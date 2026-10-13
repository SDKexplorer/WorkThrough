using Microsoft.Extensions.Configuration;
using WorkThrough;

namespace Shop;

public static class ShopSetup
{
    public const string Region = "global";

    public static ShopSettings Settings(IConfiguration configuration) =>
        configuration.GetSection(ShopSettings.SectionName).Get<ShopSettings>() ?? new();

    /// <summary>Every shop process points at the same store; nothing else about it is shared.</summary>
    public static WorkThroughBuilder UseShopStorage(this WorkThroughBuilder builder, ShopSettings settings) =>
        settings.PostgreSql is { Length: > 0 } postgres ? builder.UsePostgreSql(postgres) : builder.UseSqlite(settings.Sqlite);

    /// <summary>
    /// Starting values for the fleet-wide policies. They are created once; operators change them afterwards in the dashboard, and
    /// restarts or redeployments never overwrite those changes. Each job's own limits and schedule sit with the job in AddShopJobs.
    /// </summary>
    public static WorkThroughBuilder SeedShopPolicies(this WorkThroughBuilder builder, ShopSettings settings) => builder
        .SeedClusterLimits(new ConcurrencyLimit(null, settings.FleetSlots))
        .SeedKeyedLimits(new KeyedLimit(KeyScope.Tenant, null, settings.SlotsPerTenant));
}
