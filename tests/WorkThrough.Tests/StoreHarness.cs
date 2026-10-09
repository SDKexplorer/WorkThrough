using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using System.Data.Common;
using Npgsql;
using WorkThrough.Storage;

namespace WorkThrough.Tests;

internal sealed class StoreHarness : IAsyncDisposable
{
    private string? directory;
    private string? schema;
    private string? adminConnection;
    private string? database;
    public string? ReadOnlyConnectionString { get; private set; }
    public required Func<RelationalJobStore> NewStore { get; init; }
    public required string ConnectionString { get; init; }
    public RelationalJobStore Store => NewStore();

    public DbConnection CreateConnection() => Store switch
    {
        SqlServerJobStore => new SqlConnection(ConnectionString),
        PostgresJobStore => new NpgsqlConnection(ConnectionString),
        SqliteJobStore => new SqliteConnection(ConnectionString),
        _ => throw new InvalidOperationException("Unknown test provider.")
    };

    public WorkThroughBuilder Register(WorkThroughBuilder builder) => Store switch
    {
        SqlServerJobStore => builder.UseSqlServer(ConnectionString),
        PostgresJobStore => builder.UsePostgreSql(ConnectionString),
        SqliteJobStore => builder.UseSqlite(ConnectionString),
        _ => throw new InvalidOperationException("Unknown test provider.")
    };

    public static async Task<StoreHarness> CreateAsync(string provider, bool initialize = true)
    {
        StoreHarness harness;
        if (provider == "sqlserver")
        {
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_SQLSERVER")
                ?? throw new InvalidOperationException("SQL Server tests require a disposable server connection with database creation privileges.");
            var database = "dj_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new SqlConnection(admin);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON;";
            await command.ExecuteNonQueryAsync();
            var cs = new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ToString();
            harness = new()
            {
                database = database,
                adminConnection = admin,
                ConnectionString = cs,
                NewStore = () => new SqlServerJobStore(cs)
            };
        }
        else if (provider == "postgres")
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
        else if (provider == "sqlite")
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
        else
        {
            throw new ArgumentException("Unknown test provider.", nameof(provider));
        }

        if (initialize)
        {
            await harness.Store.InitializeAsync();
        }

        return harness;
    }

    public async Task ProvisionReadOnlySqlServerAsync()
    {
        if (database is null || ReadOnlyConnectionString is not null)
        {
            return;
        }

        var login = database + "_reader";
        var password = "Test_" + Guid.NewGuid().ToString("N") + "!";
        await using var connection = new SqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=OFF; USE [{database}]; CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT TO [{login}];";
        await command.ExecuteNonQueryAsync();
        ReadOnlyConnectionString = new SqlConnectionStringBuilder(ConnectionString)
        {
            UserID = login,
            Password = password,
            Pooling = false
        }.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (database is not null)
        {
            SqlConnection.ClearPool(new SqlConnection(ConnectionString));
            await using var connection = new SqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];";
            if (ReadOnlyConnectionString is not null)
            {
                command.CommandText += $"DROP LOGIN [{database}_reader];";
            }

            await command.ExecuteNonQueryAsync();
        }

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
