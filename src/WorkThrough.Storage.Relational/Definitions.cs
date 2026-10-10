using System.Data.Common;
using System.Text.Json;

namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    private const int MaximumDefinitionPage = 500;

    private static string VersionTarget(string key, int version) => $"{key}/v{version}";

    public Task<Result<int>> SaveDraftAsync(FlowDefinition definition, int expectedRevision, AdministrativeCommand command, CancellationToken cancellationToken = default) =>
        AdminAsync(command, AuditActions.EditDefinition, VersionTarget(definition.Key, definition.Version), new { definition, expectedRevision }, async (connection, transaction, ct) =>
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
                ("definition", JsonSerializer.Serialize(definition, JobJson.Options)), ("status", nameof(DefinitionStatus.Draft)), ("revision", revision));

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
        AdminAsync(command, action, VersionTarget(key, version), new { expectedRevision }, async (connection, transaction, token) =>
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
                    ("key", key), ("version", version), ("definition", JsonSerializer.Serialize(saved.Definition, JobJson.Options)));
            }

            await ExecuteAsync(connection, transaction, "UPDATE dj_definition_lifecycle SET status=@status,revision=revision+1 WHERE flow_key=@key AND version=@version", token,
                ("status", status.ToString()), ("key", key), ("version", version));

            return checked(expectedRevision + 1);
        }, ct);

    public async Task<StoredFlowDefinition?> GetDefinitionAsync(string key, int version, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        return await ReadDefinitionAsync(connection, null, key, version, cancellationToken);
    }

    private async Task<StoredFlowDefinition?> ReadDefinitionAsync(DbConnection connection, DbTransaction? transaction, string key, int version, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT definition,status,revision FROM dj_definition_lifecycle WHERE flow_key=@key AND version=@version",
            ("key", key), ("version", version));
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? Definition(reader) : null;
    }

    public async Task<IReadOnlyList<StoredFlowDefinition>> DefinitionsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumDefinitionPage)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, $"SELECT definition,status,revision FROM dj_definition_lifecycle ORDER BY flow_key,version DESC {PageSql("@limit")}", ("limit", limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var definitions = new List<StoredFlowDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(Definition(reader));
        }

        return definitions;
    }

    private static StoredFlowDefinition Definition(DbDataReader reader) => new(
        JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0), JobJson.Options)!, Enum.Parse<DefinitionStatus>(reader.GetString(1)), reader.GetInt32(2));

    public async Task<FlowDefinition?> GetRunDefinitionAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var saved = await ScalarAsync(connection, null, """
            SELECT d.definition FROM dj_definitions d JOIN dj_flows f ON d.flow_key=f.flow_key AND d.version=f.version WHERE f.id=@id
            """, cancellationToken, ("id", runId.ToString()));

        return saved is string json ? JsonSerializer.Deserialize<FlowDefinition>(json, JobJson.Options) : null;
    }
}
