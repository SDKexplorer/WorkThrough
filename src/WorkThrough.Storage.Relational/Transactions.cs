using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Usable only inside the owning transaction callback. Neither method commits independently.</summary>
public sealed class TransactionalJobs
{
    private readonly Func<JobRecord, bool, CancellationToken, Task<Guid>> accept;
    private bool active = true;

    internal TransactionalJobs(Func<JobRecord, bool, CancellationToken, Task<Guid>> accept) => this.accept = accept;
    internal void Close() => active = false;

    /// <summary>Enqueues in the same database transaction as the business write.</summary>
    public Task<Guid> EnqueueAsync<TInput>(JobDefinition<TInput> definition, TInput input, EnqueueOptions? options = null, CancellationToken cancellationToken = default) =>
        AcceptAsync(JobClient.CreateRecord(definition.Identity, input, options), false, cancellationToken);

    /// <summary>Persists a delivery request for a separate job store; returns the outbox receipt ID.</summary>
    public Task<Guid> SendAsync<TInput>(JobDefinition<TInput> definition, TInput input, EnqueueOptions? options = null, CancellationToken cancellationToken = default) =>
        AcceptAsync(JobClient.CreateRecord(definition.Identity, input, options), true, cancellationToken);

    private Task<Guid> AcceptAsync(JobRecord record, bool outbox, CancellationToken ct)
    {
        if (!active)
        {
            throw new InvalidOperationException("The transaction callback has ended.");
        }

        return accept(record, outbox, ct);
    }
}

public abstract partial class RelationalJobStore : IJobOutbox
{
    /// <summary>Commits business SQL and accepted jobs/outbox requests atomically in this database.</summary>
    /// <remarks>Keep callbacks short, await every command, and do not call external services or commit/dispose the supplied transaction.</remarks>
    public Task<T> InTransactionAsync<T>(Func<DbConnection, DbTransaction, TransactionalJobs, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        return WriteAsync(async (connection, transaction, ct) =>
        {
            var writer = new TransactionalJobs((job, outbox, token) => outbox
                ? AcceptOutboxAsync(connection, transaction, job, token)
                : AcceptJobAsync(connection, transaction, job, token));
            try
            {
                return await action(connection, transaction, writer, ct);
            }
            finally
            {
                writer.Close();
            }
        }, cancellationToken);
    }

    private async Task<Guid> AcceptOutboxAsync(DbConnection connection, DbTransaction transaction, JobRecord job, CancellationToken ct)
    {
        var fingerprint = JobFingerprint(job);
        if (job.IdempotencyKey is not null)
        {
            await using var command = Command(connection, transaction, "SELECT id,fingerprint FROM dj_outbox WHERE idempotency_key=@key", ("key", job.IdempotencyKey));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1) != fingerprint)
                {
                    throw new StateConflictException("Outbox idempotency key was already used for different work.");
                }

                return Guid.Parse(reader.GetString(0));
            }
        }

        var source = (string)(await ScalarAsync(connection, transaction, "SELECT id FROM dj_store", ct))!;
        var envelope = job with
        {
            IdempotencyKey = job.IdempotencyKey ?? $"outbox:{source}:{job.Id:N}"
        };
        await ExecuteAsync(connection, transaction, "INSERT INTO dj_outbox(id,request,status,idempotency_key,fingerprint) VALUES (@id,@request,'Pending',@key,@hash)", ct,
            ("id", job.Id.ToString()), ("request", JsonSerializer.Serialize(envelope, JobJson.Options)), ("key", job.IdempotencyKey), ("hash", fingerprint));

        return job.Id;
    }

    public async Task<int> DispatchOutboxAsync(IJobStore destination, int batchSize = 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (batchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var delivered = 0;
        for (var i = 0; i < batchSize; i++)
        {
            var claimed = await WriteAsync<(JobRecord Job, Guid Token)?>(async (connection, transaction, ct) =>
            {
                var now = await NowAsync(connection, transaction, ct);
                var json = await ScalarAsync(connection, transaction, $"SELECT request FROM dj_outbox WHERE status='Pending' OR (status='Delivering' AND lease_expires_at<=@now) ORDER BY id {PageSql("1")}", ct, ("now", now));
                if (json is not string payload)
                {
                    return null;
                }

                var job = JsonSerializer.Deserialize<JobRecord>(payload, JobJson.Options)!;
                var token = Guid.NewGuid();
                await ExecuteAsync(connection, transaction, "UPDATE dj_outbox SET status='Delivering',lease_token=@token,lease_expires_at=@expiry WHERE id=@id", ct,
                    ("token", token.ToString()), ("expiry", checked(now + 30000)), ("id", job.Id.ToString()));

                return (job, token);
            }, cancellationToken);
            if (claimed is not { } item)
            {
                break;
            }

            try
            {
                // No source transaction is held across the destination write.
                var accepted = await destination.EnqueueAsync(item.Job, cancellationToken);
                var acknowledged = await WriteAsync(async (connection, transaction, ct) =>
                    await ExecuteAsync(connection, transaction, "UPDATE dj_outbox SET status='Delivered',request='{}',delivered_job_id=@job,lease_token=NULL,lease_expires_at=NULL WHERE id=@id AND lease_token=@token", ct,
                        ("job", accepted.ToString()), ("id", item.Job.Id.ToString()), ("token", item.Token.ToString())) == 1, cancellationToken);
                if (acknowledged)
                {
                    delivered++;
                }
            }
            catch
            {
                // A destination commit may have succeeded. Repeating the stable envelope reconciles it.
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await WriteAsync((connection, transaction, ct) => ExecuteAsync(connection, transaction, "UPDATE dj_outbox SET status='Pending',lease_token=NULL,lease_expires_at=NULL WHERE id=@id AND lease_token=@token", ct,
                    ("id", item.Job.Id.ToString()), ("token", item.Token.ToString())), release.Token);
                throw;
            }
        }

        return delivered;
    }

    public async Task<OutboxDelivery?> OutboxDeliveryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT status,delivered_job_id FROM dj_outbox WHERE id=@id", ("id", id.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? new(id, reader.GetString(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))) : null;
    }
}
