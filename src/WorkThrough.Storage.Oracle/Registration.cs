using WorkThrough.Storage;

// Builder extensions live in the builder's namespace, so registering storage needs no extra using directive.
namespace WorkThrough;

public static class OracleRegistration
{
    public static WorkThroughBuilder UseOracle(this WorkThroughBuilder builder, string connectionString, OracleStorageOptions? options = null) =>
        builder.UseStore(new OracleJobStore(connectionString, options));
}
