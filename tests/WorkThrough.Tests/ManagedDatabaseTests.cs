using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace WorkThrough.Tests;

/// <summary>
/// Evidence for a managed database run (scripts/check-managed-db.sh): every configured provider connection must be encrypted, so a
/// qualification against a cloud server cannot pass over plain TCP.
/// </summary>
public sealed class ManagedDatabaseTests
{
    public const string RequireTlsVariable = "WT_REQUIRE_TLS";

    private static readonly (string Provider, string Variable)[] Providers =
    [
        ("sqlserver", "DURABLEJOBS_SQLSERVER"), ("postgres", "DURABLEJOBS_POSTGRES"), ("mysql", "DURABLEJOBS_MYSQL"),
        ("mariadb", "DURABLEJOBS_MARIADB"), ("oracle", "DURABLEJOBS_ORACLE")
    ];

    public static TheoryData<string> ConfiguredProviders()
    {
        var data = new TheoryData<string>();
        foreach (var (provider, variable) in Providers)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                data.Add(provider);
            }
        }

        return data;
    }

    [ManagedDatabaseTheory]
    [MemberData(nameof(ConfiguredProviders))]
    public async Task Provider_connections_are_encrypted(string provider)
    {
        var connectionString = Environment.GetEnvironmentVariable(Providers.Single(p => p.Provider == provider).Variable)!;
        await using DbConnection connection = provider switch
        {
            "sqlserver" => new SqlConnection(connectionString),
            "postgres" => new NpgsqlConnection(connectionString),
            "oracle" => new OracleConnection(connectionString),
            _ => new MySqlConnection(connectionString)
        };
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = provider switch
        {
            "sqlserver" => "SELECT encrypt_option FROM sys.dm_exec_connections WHERE session_id=@@SPID",
            "postgres" => "SELECT CASE WHEN ssl THEN 'TRUE' ELSE 'FALSE' END FROM pg_stat_ssl WHERE pid=pg_backend_pid()",
            "oracle" => "SELECT CASE WHEN LOWER(SYS_CONTEXT('USERENV','NETWORK_PROTOCOL'))='tcps' THEN 'TRUE' ELSE 'FALSE' END FROM dual",
            _ => "SHOW SESSION STATUS LIKE 'Ssl_cipher'"
        };
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var encrypted = reader.FieldCount == 1 ? reader.GetString(0) == "TRUE" : !string.IsNullOrEmpty(reader.GetString(1));
        Assert.True(encrypted, $"The {provider} connection is not encrypted.");
    }
}

internal sealed class ManagedDatabaseTheoryAttribute : TheoryAttribute
{
    public ManagedDatabaseTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable(ManagedDatabaseTests.RequireTlsVariable) != "1")
        {
            Skip = $"Set {ManagedDatabaseTests.RequireTlsVariable}=1 for a managed database run; see scripts/check-managed-db.sh.";
        }
    }
}
