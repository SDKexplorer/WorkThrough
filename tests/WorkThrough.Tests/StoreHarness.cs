using Microsoft.Data.Sqlite;
using Npgsql;
using WorkThrough.Storage;

namespace WorkThrough.Tests;

internal sealed class StoreHarness : IAsyncDisposable
{
    private string? directory;
    private string? schema;
    private string? adminConnection;
    public required Func<RelationalJobStore> NewStore { get; init; }
    public required string ConnectionString { get; init; }
    public RelationalJobStore Store => NewStore();

    public static async Task<StoreHarness> CreateAsync(string provider, bool initialize = true)
    {
        StoreHarness harness;
        if (provider == "postgres")
        {
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_POSTGRES")
                ?? throw new InvalidOperationException("PostgreSQL tests require a disposable database connection.");
            var schema = "dj_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
            await command.ExecuteNonQueryAsync();
            var cs = new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema }.ToString();
            harness = new()
            {
                schema = schema,
                adminConnection = admin,
                ConnectionString = cs,
                NewStore = () => new PostgresJobStore(cs)
            };
        }
        else
        {
            var directory = Path.Combine(Path.GetTempPath(), "durablejobs-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "jobs.db"), DefaultTimeout = 10 }.ToString();
            harness = new()
            {
                directory = directory,
                ConnectionString = cs,
                NewStore = () => new SqliteJobStore(cs)
            };
        }

        if (initialize)
        {
            await harness.Store.InitializeAsync();
        }

        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        if (schema is not null)
        {
            // Every test's search path creates a separate pool. Release only this test's idle connections.
            await using var pooledConnection = new NpgsqlConnection(ConnectionString);
            NpgsqlConnection.ClearPool(pooledConnection);
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }

        if (directory is not null)
        {
            SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
            Directory.Delete(directory, true);
        }
    }
}
