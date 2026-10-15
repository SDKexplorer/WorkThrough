using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumDefinitionPage = 500;

    private static string VersionTarget(string key, int version) => $"{key}/v{version}";

    public Task<Result<int>> SaveDraftAsync(FlowDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.EditDefinition, VersionTarget(definition.Key, definition.Version), Args(("definition", definition), ("expectedRevision", expectedRevision)), async (connection, transaction, ct) =>
        {
            var saved = await ReadDefinitionAsync(connection, transaction, definition.Key, definition.Version, ct);
            if (saved is null ? expectedRevision != 0 : saved.Status != DefinitionStatus.Draft || saved.Revision != expectedRevision)
            {
                throw new StateConflictException("Draft changed or is immutable. Use its current revision, or create a new graph version.");
            }

            var revision = checked(expectedRevision + 1);
            var upsert = UpsertSql("dj_definition_lifecycle", "flow_key,version,definition,status,revision",
                "@key,@version,@definition,@status,@revision", "flow_key,version", "definition,revision");
            await ExecuteAsync(connection, transaction, upsert, ct, ("key", definition.Key), ("version", definition.Version),
                ("definition", JobJson.Serialize(definition)), ("status", nameof(DefinitionStatus.Draft)), ("revision", revision));

            return revision;
        }, cancellationToken, validate: () => ValidateDraft(definition, expectedRevision));

    private static void ValidateDraft(FlowDefinition definition, int expectedRevision)
    {
        FlowValidator.ValidateDraftShape(definition);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
    }

    public Task<Result<int>> PublishAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        TransitionDefinitionAsync(key, version, expectedRevision, DefinitionStatus.Draft, DefinitionStatus.Published, AuditActions.PublishDefinition, command, cancellationToken);

    public Task<Result<int>> RetireAsync(string key, int version, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        TransitionDefinitionAsync(key, version, expectedRevision, DefinitionStatus.Published, DefinitionStatus.Retired, AuditActions.RetireDefinition, command, cancellationToken);

    private Task<Result<int>> TransitionDefinitionAsync(string key, int version, int expectedRevision, DefinitionStatus from, DefinitionStatus status,
        string action, AdministrativeCommand command, CancellationToken ct) =>
        AdminAsync(command, action, VersionTarget(key, version), Args(("expectedRevision", expectedRevision)), async (connection, transaction, token) =>
        {
            var saved = await ReadDefinitionAsync(connection, transaction, key, version, token);
            if (saved is null || saved.Revision != expectedRevision || saved.Status != from)
            {
                throw new StateConflictException("Definition lifecycle or revision changed.");
            }

            if (status == DefinitionStatus.Published)
            {
                FlowValidator.Validate(saved.Definition);
                await ExecuteAsync(connection, transaction, "INSERT INTO dj_definitions(flow_key,version,definition) VALUES (@key,@version,@definition)", token,
                    ("key", key), ("version", version), ("definition", JobJson.Serialize(saved.Definition)));
            }

            await ExecuteAsync(connection, transaction, "UPDATE dj_definition_lifecycle SET status=@status,revision=revision+1 WHERE flow_key=@key AND version=@version", token,
                ("status", status.ToString()), ("key", key), ("version", version));

            return checked(expectedRevision + 1);
        }, ct);

    public Task<StoredFlowDefinition?> GetDefinitionAsync(string key, int version, CancellationToken cancellationToken = default)
    {
        return ReadAsync<StoredFlowDefinition?>(async (connection, transaction, ct) =>
        {
            return await ReadDefinitionAsync(connection, transaction, key, version, ct);
        }, cancellationToken);
    }

    private async Task<StoredFlowDefinition?> ReadDefinitionAsync(DbConnection connection, DbTransaction? transaction, string key, int version, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT definition,status,revision FROM dj_definition_lifecycle WHERE flow_key=@key AND version=@version",
            ("key", key), ("version", version));
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? Definition(reader) : null;
    }

    public Task<IReadOnlyList<StoredFlowDefinition>> DefinitionsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumDefinitionPage)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return ReadAsync<IReadOnlyList<StoredFlowDefinition>>(async (connection, transaction, ct) =>
        {
            await using var command = Command(connection, transaction, $"SELECT definition,status,revision FROM dj_definition_lifecycle ORDER BY flow_key,version DESC {PageSql("@limit")}", ("limit", limit));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var definitions = new List<StoredFlowDefinition>();
            while (await reader.ReadAsync(ct))
            {
                definitions.Add(Definition(reader));
            }

            return definitions;
        }, cancellationToken);
    }

    private static StoredFlowDefinition Definition(DbDataReader reader) => new(
        JobJson.Deserialize<FlowDefinition>(reader.GetString(0))!, Enum.Parse<DefinitionStatus>(reader.GetString(1)), reader.GetInt32(2));

    public Task<FlowDefinition?> GetRunDefinitionAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return ReadAsync<FlowDefinition?>(async (connection, transaction, ct) =>
        {
            var saved = await ScalarAsync(connection, transaction, """
                SELECT d.definition FROM dj_definitions d JOIN dj_flows f ON d.flow_key=f.flow_key AND d.version=f.version WHERE f.id=@id
                """, ct, ("id", runId.ToString()));

            return saved is string json ? JobJson.Deserialize<FlowDefinition>(json) : null;
        }, cancellationToken);
    }
}
