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
    private const int MaximumOutboxBatch = 100;
    private static readonly TimeSpan OutboxDeliveryLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OutboxReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Commits business SQL and accepted jobs/outbox requests atomically in this database.</summary>
    /// <remarks>Keep callbacks short and DML-only. Await every command; do not run DDL, autonomous transactions, external calls, or commit/dispose the supplied transaction.</remarks>
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
        await ExecuteAsync(connection, transaction, "INSERT INTO dj_outbox(id,request,status,idempotency_key,fingerprint) VALUES (@id,@request,@status,@key,@hash)", ct,
            ("id", job.Id.ToString()), ("request", JsonSerializer.Serialize(envelope, JobJson.Options)), ("status", OutboxStatuses.Pending),
            ("key", job.IdempotencyKey), ("hash", fingerprint));

        return job.Id;
    }

    public async Task<int> DispatchOutboxAsync(IJobStore destination, int batchSize = 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (batchSize is < 1 or > MaximumOutboxBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var delivered = 0;
        for (var i = 0; i < batchSize; i++)
        {
            if (await ClaimOutboxAsync(cancellationToken) is not { } item)
            {
                break;
            }

            try
            {
                // No source transaction is held across the destination write.
                var accepted = await destination.EnqueueAsync(item.Job, cancellationToken);
                if (await AcknowledgeOutboxAsync(item, accepted, cancellationToken))
                {
                    delivered++;
                }
            }
            catch
            {
                // A destination commit may have succeeded. Repeating the stable envelope reconciles it.
                await ReleaseOutboxAsync(item);
                throw;
            }
        }

        return delivered;
    }

    private sealed record OutboxClaim(JobRecord Job, Guid Token);

    private Task<OutboxClaim?> ClaimOutboxAsync(CancellationToken cancellationToken) => WriteAsync<OutboxClaim?>(async (connection, transaction, ct) =>
    {
        var now = await NowAsync(connection, transaction, ct);
        var json = await ScalarAsync(connection, transaction, $"""
            SELECT request FROM dj_outbox WHERE status=@pending OR (status=@delivering AND lease_expires_at<=@now) ORDER BY id {PageSql(1)}
            """, ct, ("pending", OutboxStatuses.Pending), ("delivering", OutboxStatuses.Delivering), ("now", now));
        if (json is not string payload)
        {
            return null;
        }

        var claim = new OutboxClaim(JsonSerializer.Deserialize<JobRecord>(payload, JobJson.Options)!, Guid.NewGuid());
        await ExecuteAsync(connection, transaction, "UPDATE dj_outbox SET status=@status,lease_token=@token,lease_expires_at=@expiry WHERE id=@id", ct,
            ("status", OutboxStatuses.Delivering), ("token", claim.Token.ToString()),
            ("expiry", checked(now + (long)OutboxDeliveryLease.TotalMilliseconds)), ("id", claim.Job.Id.ToString()));

        return claim;
    }, cancellationToken);

    private Task<bool> AcknowledgeOutboxAsync(OutboxClaim claim, Guid jobId, CancellationToken cancellationToken) =>
        WriteAsync(async (connection, transaction, ct) => await ExecuteAsync(connection, transaction, """
            UPDATE dj_outbox SET status=@status,request='{}',delivered_job_id=@job,lease_token=NULL,lease_expires_at=NULL
            WHERE id=@id AND lease_token=@token
            """, ct, ("status", OutboxStatuses.Delivered), ("job", jobId.ToString()), ("id", claim.Job.Id.ToString()), ("token", claim.Token.ToString())) == 1,
            cancellationToken);

    /// <summary>Returns the claim for prompt redelivery. If this fails too, the delivery lease expires and the claim is retried.</summary>
    private async Task ReleaseOutboxAsync(OutboxClaim claim)
    {
        try
        {
            using var timeout = new CancellationTokenSource(OutboxReleaseTimeout);
            await WriteAsync((connection, transaction, ct) => ExecuteAsync(connection, transaction, """
                UPDATE dj_outbox SET status=@status,lease_token=NULL,lease_expires_at=NULL WHERE id=@id AND lease_token=@token
                """, ct, ("status", OutboxStatuses.Pending), ("id", claim.Job.Id.ToString()), ("token", claim.Token.ToString())), timeout.Token);
        }
        catch (Exception)
        {
            // The caller rethrows the delivery failure, which is the actionable error.
        }
    }

    public Task<OutboxDelivery?> OutboxDeliveryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync<OutboxDelivery?>(async (connection, transaction, ct) =>
        {
            await using var command = Command(connection, transaction, "SELECT status,delivered_job_id FROM dj_outbox WHERE id=@id", ("id", id.ToString()));
            await using var reader = await command.ExecuteReaderAsync(ct);

            return await reader.ReadAsync(ct) ? new(id, reader.GetString(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))) : null;
        }, cancellationToken);
    }
}
