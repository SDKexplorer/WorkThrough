using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using WorkThrough.Storage;

namespace WorkThrough.Exercises;

internal sealed class ExerciseDatabase(string provider, string connectionString, string? schema, string directory) : IAsyncDisposable
{
    private const int CleanupAttempts = 5;

    private OracleTestDatabase? oracleDatabase;
    public string Provider { get; } = provider;
    public string ConnectionString { get; } = connectionString;
    public string DirectoryPath { get; } = directory;
    public string? Schema => schema;
    public RelationalJobStore Store { get; } = OpenStore(provider, connectionString);
    public static RelationalJobStore OpenStore(string provider, string connection) =>
        provider switch
        {
            "oracle" => new OracleJobStore(connection),
            "sqlserver" => new SqlServerJobStore(connection),
            "postgres" => new PostgresJobStore(connection),
            "mysql" or "mariadb" => new MySqlJobStore(connection),
            _ => throw new ArgumentException("Unknown provider.", nameof(provider))
        };

    public static async Task<ExerciseDatabase> CreateAsync(string provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workthrough-exercises", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? schema = null;
        string connection;
        OracleTestDatabase? oracle = null;
        if (provider == "oracle")
        {
            oracle = await OracleTestDatabase.CreateAsync(Environment.GetEnvironmentVariable("DURABLEJOBS_ORACLE")
                ?? throw new InvalidOperationException("A disposable Oracle PDB administrator is required."));
            connection = oracle.ConnectionString;
        }
        else if (provider == "sqlserver")
        {
            schema = "wt_exercise_" + Guid.NewGuid().ToString("N");
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_SQLSERVER") ?? throw new InvalidOperationException("A disposable SQL Server connection is required.");
            await using var database = new SqlConnection(admin);
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            // WT_EXERCISE_SQLSERVER_VERSIONED_READS=1 measures the row-locked path that READ_COMMITTED_SNAPSHOT enables (ADR-020).
            command.CommandText = $"CREATE DATABASE [{schema}]; ALTER DATABASE [{schema}] SET ALLOW_SNAPSHOT_ISOLATION ON;" +
                (Environment.GetEnvironmentVariable("WT_EXERCISE_SQLSERVER_VERSIONED_READS") == "1" ? $" ALTER DATABASE [{schema}] SET READ_COMMITTED_SNAPSHOT ON;" : "");
            await command.ExecuteNonQueryAsync();
            connection = new SqlConnectionStringBuilder(admin) { InitialCatalog = schema }.ToString();
        }
        else if (provider == "postgres")
        {
            schema = "wt_exercise_" + Guid.NewGuid().ToString("N");
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_POSTGRES") ?? throw new InvalidOperationException("A disposable PostgreSQL connection is required.");
            await using var database = new NpgsqlConnection(admin);
            await database.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", database);
            await command.ExecuteNonQueryAsync();
            connection = new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema }.ToString();
        }
        else if (provider is not ("mysql" or "mariadb"))
        {
            throw new ArgumentException("Unknown provider.", nameof(provider));
        }
        else
        {
            schema = "wt_exercise_" + Guid.NewGuid().ToString("N");
            await using var database = new MySqlConnection(MySqlAdministrator(provider));
            await database.OpenAsync();
            await using var command = new MySqlCommand($"CREATE DATABASE {schema}", database);
            await command.ExecuteNonQueryAsync();
            connection = new MySqlConnectionStringBuilder(MySqlAdministrator(provider)) { Database = schema }.ConnectionString;
        }

        var result = new ExerciseDatabase(provider, connection, schema, directory) { oracleDatabase = oracle };
        await result.Store.InitializeAsync();

        return result;
    }

    private static string MySqlAdministrator(string provider)
    {
        var variable = provider == "mysql" ? "DURABLEJOBS_MYSQL" : "DURABLEJOBS_MARIADB";

        return Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"A disposable {provider} server connection is required in {variable}.");
    }

    public DbConnection OpenConnection() => Provider switch
    {
        "oracle" => new OracleConnection(ConnectionString),
        "sqlserver" => new SqlConnection(ConnectionString),
        "postgres" => new NpgsqlConnection(ConnectionString),
        _ => new MySqlConnection(ConnectionString)
    };

    public async Task<string> VersionAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Provider switch
        {
            "oracle" => "SELECT banner_full FROM v$version WHERE banner_full LIKE 'Oracle%'",
            "sqlserver" => "SELECT @@VERSION",
            "postgres" => "SELECT version()",
            _ => "SELECT CONCAT(VERSION(),' ',@@version_comment)"
        };

        return (string)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        if (oracleDatabase is not null)
        {
            await oracleDatabase.DisposeAsync();
        }
        else if (Provider == "sqlserver")
        {
            SqlConnection.ClearPool(new SqlConnection(ConnectionString));
            await using var connection = new SqlConnection(Environment.GetEnvironmentVariable("DURABLEJOBS_SQLSERVER"));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{schema}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{schema}];";
            await command.ExecuteNonQueryAsync();
        }
        else if (Provider is "mysql" or "mariadb")
        {
            await MySqlConnection.ClearAllPoolsAsync();
            await using var connection = new MySqlConnection(MySqlAdministrator(Provider));
            await connection.OpenAsync();
            await using var command = new MySqlCommand($"DROP DATABASE {schema}", connection);
            await command.ExecuteNonQueryAsync();
        }
        else if (schema is not null)
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("DURABLEJOBS_POSTGRES"));
            await connection.OpenAsync();

            // A killed worker's server session can still be finishing a statement; dropping its tables then deadlocks with it.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
                    await command.ExecuteNonQueryAsync();

                    break;
                }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DeadlockDetected && attempt < CleanupAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt));
                }
            }
        }

        Directory.Delete(DirectoryPath, true);
    }
}
