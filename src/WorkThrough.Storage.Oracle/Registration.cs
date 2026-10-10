
namespace WorkThrough.Storage;

public static class OracleRegistration
{
    public static WorkThroughBuilder UseOracle(this WorkThroughBuilder builder, string connectionString, OracleStorageOptions? options = null) =>
        builder.UseStore(new OracleJobStore(connectionString, options));
}
