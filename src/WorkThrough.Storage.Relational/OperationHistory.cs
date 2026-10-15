using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore : IOperationHistoryQueries
{
    // Actor JSON larger than this is not parsed inside the database.
    private const int MaximumParsedActorLength = 16384;

    protected virtual string AuditActorValueSql(string field) => throw new NotSupportedException("This provider does not project audit actors.");

    private string ArchiveMetadataColumns => $"""
        id,kind,{ClippedLabelSql("correlation_id")} AS correlation_id,archived_at,jobs,
        CASE WHEN body IS NULL THEN 0 ELSE 1 END AS has_body,
        CASE WHEN {LabelTooLongSql("correlation_id")} THEN 1 ELSE 0 END AS clipped
        """;

    public async Task<AuditPage> SearchAuditAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateOperationLimit(query.Limit);
        ValidateFilterLengths(query.Cursor, query.Action, query.Target);

        return await ReadOperationAsync<AuditPage>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            var page = HistoryWindow("audit", query with { Cursor = null }, query.Cursor, query.From, query.Until, storeId, observed);
            var conditions = new List<string> { "a.at>=@from AND a.at<@until" };
            var values = HistoryParameters(page, query.Limit);
            AddHistoryFilter("a.action", "action", query.Action, conditions, values);
            AddHistoryFilter("a.target", "target", query.Target, conditions, values);
            AddHistoryPosition("a.at", "a.command_id", query.Cursor is not null, conditions);
            var issuer = AuditActorValueSql("issuer");
            var subject = AuditActorValueSql("subject");
            var entries = new List<AuditMetadata>();
            await using (var command = OperationCommand(connection, transaction, $"""
                SELECT a.command_id,a.at,{ClippedLabelSql(issuer)} AS issuer,{ClippedLabelSql(subject)} AS subject,
                  {ClippedLabelSql("a.action")} AS action,{ClippedLabelSql("a.target")} AS target,{ClippedLabelSql("a.outcome")} AS outcome,
                  CASE WHEN {LengthSql("a.actor")}>{MaximumParsedActorLength} OR {LabelTooLongSql(issuer)} OR {LabelTooLongSql(subject)}
                    OR {LabelTooLongSql("a.action")} OR {LabelTooLongSql("a.target")} OR {LabelTooLongSql("a.outcome")} THEN 1 ELSE 0 END AS clipped
                FROM dj_audit a WHERE {string.Join(" AND ", conditions)} ORDER BY a.at DESC,a.command_id {PageSql("@limit")}
                """, values.ToArray()))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    entries.Add(new(Guid.Parse(reader.GetString(0)), Date(reader, "at")!.Value,
                        Text(reader, "issuer"), Text(reader, "subject"), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        !reader.IsDBNull(2) && !reader.IsDBNull(3), reader.GetInt32(7) != 0));
                }
            }

            var more = TrimPage(entries, query.Limit);
            var next = more ? HistoryContinuation(page, entries[^1].At, entries[^1].CommandId) : null;

            return new(storeId, observed, DateTimeOffset.FromUnixTimeMilliseconds(page.From),
                DateTimeOffset.FromUnixTimeMilliseconds(page.Until), entries, next);
        }, cancellationToken);
    }

    public async Task<ArchivePage> SearchArchivesAsync(ArchiveQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateOperationLimit(query.Limit);
        ValidateFilterLengths(query.Cursor, query.CorrelationId);
        if (query.Kind is { } kind && !Enum.IsDefined(kind))
        {
            throw new ArgumentException("Archive kind must be Job or Flow.", nameof(query));
        }

        return await ReadOperationAsync<ArchivePage>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            var page = HistoryWindow("archives", query with { Cursor = null }, query.Cursor, query.From, query.Until, storeId, observed);
            var conditions = new List<string> { "archived_at>=@from AND archived_at<@until" };
            var values = HistoryParameters(page, query.Limit);
            AddHistoryFilter("correlation_id", "correlation", query.CorrelationId, conditions, values);
            AddHistoryFilter("kind", "kind", query.Kind?.ToString(), conditions, values);
            AddHistoryPosition("archived_at", "id", query.Cursor is not null, conditions);
            await using var command = OperationCommand(connection, transaction,
                $"SELECT {ArchiveMetadataColumns} FROM dj_archives WHERE {string.Join(" AND ", conditions)} ORDER BY archived_at DESC,id {PageSql("@limit")}", values.ToArray());
            var items = await ReadArchiveMetadataAsync(command, ct);
            var more = TrimPage(items, query.Limit);
            var next = more ? HistoryContinuation(page, items[^1].ArchivedAt, items[^1].Id) : null;

            return new(storeId, observed, DateTimeOffset.FromUnixTimeMilliseconds(page.From), DateTimeOffset.FromUnixTimeMilliseconds(page.Until), items, next);
        }, cancellationToken);
    }

    public async Task<ArchiveLookup> FindArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await ReadOperationAsync<ArchiveLookup>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            await using var command = OperationCommand(connection, transaction,
                $"SELECT {ArchiveMetadataColumns} FROM dj_archives WHERE id=@id", ("id", id.ToString()));
            var items = await ReadArchiveMetadataAsync(command, ct);

            return new(storeId, observed, items.SingleOrDefault());
        }, cancellationToken);
    }

    public async Task<RetentionPreview> PreviewRetentionAsync(ArchivePolicy policy, CancellationToken cancellationToken = default)
    {
        ValidateArchivePolicy(policy);

        return await ReadOperationAsync<RetentionPreview>(async (connection, transaction, ct) =>
        {
            var (storeId, observed) = await ReadOperationContextAsync(connection, transaction, ct);
            var candidates = new List<RetentionCandidate>();
            await using (var command = OperationCommand(connection, transaction, $"""
                SELECT id,kind,{ClippedLabelSql("correlation_id")} AS correlation_id,completed,jobs,
                  CASE WHEN {LabelTooLongSql("correlation_id")} THEN 1 ELSE 0 END AS clipped
                FROM ({EligibleArchiveUnitsSql}) candidates ORDER BY completed,id {PageSql("@limit")}
                """, ("success", policy.SucceededBefore.ToUnixTimeMilliseconds()),
                ("failure", policy.FailedBefore?.ToUnixTimeMilliseconds()), ("limit", policy.BatchSize + 1)))
            {
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    candidates.Add(new(Guid.Parse(reader.GetString(0)), Enum.Parse<ArchiveKind>(reader.GetString(1)), Text(reader, "correlation_id"),
                        Date(reader, "completed")!.Value, Convert.ToInt32(reader.GetValue(4)), reader.GetInt32(5) != 0));
                }
            }

            var more = TrimPage(candidates, policy.BatchSize);

            return new(storeId, observed, policy, candidates, more);
        }, cancellationToken);
    }

    private async Task<List<ArchiveMetadata>> ReadArchiveMetadataAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ArchiveMetadata>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new(Guid.Parse(reader.GetString(0)), Enum.Parse<ArchiveKind>(reader.GetString(1)), Text(reader, "correlation_id"),
                Date(reader, "archived_at")!.Value, reader.GetInt32(4), reader.GetInt32(5) != 0, reader.GetInt32(6) != 0));
        }

        return items;
    }

    private static ExecutionCursor HistoryWindow<T>(string kind, T query, string? cursor, DateTimeOffset? from,
        DateTimeOffset? until, string storeId, DateTimeOffset observed)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new HistoryFingerprint<T>(kind, query),
            (JsonTypeInfo<HistoryFingerprint<T>>)CursorJsonContext.Default.GetTypeInfo(typeof(HistoryFingerprint<T>))!)));
        var previous = cursor is null ? null : ReadExecutionCursor(cursor);
        var upper = previous?.Until ?? CeilingMilliseconds(until ?? observed.AddMilliseconds(1));
        var lower = previous?.From ?? CeilingMilliseconds(from ?? DateTimeOffset.FromUnixTimeMilliseconds(upper) - OperationQueryLimits.DefaultWindow);
        ValidateWindow(lower, upper);
        if (previous is not null && (previous.StoreId != storeId || previous.Fingerprint != fingerprint))
        {
            throw new ArgumentException("The cursor belongs to a different store or query.");
        }

        return previous ?? new(CursorVersion, storeId, fingerprint, lower, upper, upper, Guid.Empty);
    }

    private static List<(string, object?)> HistoryParameters(ExecutionCursor page, int limit) =>
        [("from", page.From), ("until", page.Until), ("limit", limit + 1), ("position", page.Position), ("id", page.Id.ToString())];

    private static void AddHistoryPosition(string timestamp, string id, bool continuing, List<string> conditions)
    {
        if (continuing)
        {
            conditions.Add($"({timestamp}<@position OR ({timestamp}=@position AND {id}>@id))");
        }
    }

    private static void AddHistoryFilter(string column, string parameter, string? value, List<string> conditions, List<(string, object?)> values)
    {
        if (value is not null)
        {
            conditions.Add($"{column}=@{parameter}");
            values.Add((parameter, value));
        }
    }

    private static string HistoryContinuation(ExecutionCursor page, DateTimeOffset at, Guid id) =>
        EncodeCursor(page with { Position = at.ToUnixTimeMilliseconds(), Id = id });
}
