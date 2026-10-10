using System.Data.Common;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IExecutionEvents
{
    public async Task<EventPosition> EventPositionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadEventPositionAsync(connection, null, cancellationToken);
    }

    private async Task<EventPosition> ReadEventPositionAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT value,floor FROM dj_event_sequence WHERE id=1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new(reader.GetInt64(0), reader.GetInt64(1));
    }

    public Task<long> SubscribeAsync(string subscriber, long startAfter = 0, CancellationToken cancellationToken = default)
    {
        ValidateSubscriber(subscriber);

        return WriteAsync(async (connection, transaction, ct) =>
        {
            var previous = await ScalarAsync(connection, transaction, "SELECT sequence FROM dj_subscriptions WHERE name=@name", ct, ("name", subscriber));
            if (previous is not null)
            {
                return Convert.ToInt64(previous);
            }

            var position = await ReadEventPositionAsync(connection, transaction, ct);
            if (startAfter < position.RetainedAfter || startAfter > position.LastSequence)
            {
                throw new StateConflictException("Requested subscription history is unavailable. Choose an explicit retained position.");
            }

            await ExecuteAsync(connection, transaction, "INSERT INTO dj_subscriptions(name,sequence) VALUES (@name,@sequence)", ct, ("name", subscriber), ("sequence", startAfter));

            return startAfter;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionEvent>> ReadEventsAsync(long after, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (after < 0 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginReadAsync(connection, cancellationToken);
        var position = await ReadEventPositionAsync(connection, transaction, cancellationToken);
        if (after < position.RetainedAfter)
        {
            throw new StateConflictException("Requested events have been trimmed. Resume from an explicit retained position.");
        }

        var entries = new List<ExecutionEvent>();
        await using (var command = Command(connection, transaction, $"SELECT * FROM dj_events WHERE sequence>@after ORDER BY sequence {PageSql("@limit")}", ("after", after), ("limit", limit)))
        {
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    entries.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), ParseGuid(Text(reader, "run_id")), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetString(4), reader.GetInt32(5)));
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return entries;
    }

    public Task<bool> AcknowledgeAsync(string subscriber, long expectedSequence, long sequence, CancellationToken cancellationToken = default)
    {
        ValidateSubscriber(subscriber);
        if (sequence <= expectedSequence || expectedSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return WriteAsync(async (connection, transaction, ct) =>
        {
            var position = await ReadEventPositionAsync(connection, transaction, ct);
            if (sequence > position.LastSequence || expectedSequence < position.RetainedAfter)
            {
                throw new StateConflictException("Event position is outside retained history.");
            }

            return await ExecuteAsync(connection, transaction, "UPDATE dj_subscriptions SET sequence=@next WHERE name=@name AND sequence=@expected", ct,
                ("next", sequence), ("name", subscriber), ("expected", expectedSequence)) == 1;
        }, cancellationToken);
    }

    public Task<int> TrimEventsAsync(long through, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, "events.trim", "event journal", new
        {
            through
        }, async (connection, transaction, ct) =>
        {
            var position = await ReadEventPositionAsync(connection, transaction, ct);
            if (through < position.RetainedAfter || through > position.LastSequence)
            {
                throw new StateConflictException("Invalid trim position.");
            }

            var behind = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_subscriptions WHERE sequence<@through", ct, ("through", through)));
            var unarchived = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_events e WHERE e.sequence<=@through AND EXISTS (SELECT 1 FROM dj_jobs j WHERE j.id=e.job_id)", ct, ("through", through)));
            var held = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM dj_events e WHERE e.sequence<=@through AND EXISTS (SELECT 1 FROM dj_retention_holds h WHERE h.unit_id=COALESCE(e.run_id,e.job_id) AND h.held=1)", ct, ("through", through)));
            if (behind > 0 || unarchived > 0 || held > 0)
            {
                throw new StateConflictException("Archive the affected executions, release applicable holds and advance every subscriber before trimming.");
            }

            var removed = await ExecuteAsync(connection, transaction, "DELETE FROM dj_events WHERE sequence<=@through", ct, ("through", through));
            await ExecuteAsync(connection, transaction, "UPDATE dj_event_sequence SET floor=@through WHERE id=1", ct, ("through", through));

            return removed;
        }, cancellationToken);

    private static void ValidateSubscriber(string subscriber)
    {
        if (string.IsNullOrWhiteSpace(subscriber) || subscriber.Length > 200)
        {
            throw new ArgumentException("Subscriber names contain 1–200 characters.", nameof(subscriber));
        }
    }
}
