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
    /// Starting values for shared policies and the recurring report. They are created once; operators change them afterwards in
    /// the dashboard, and restarts or redeployments never overwrite those changes.
    /// </summary>
    public static WorkThroughBuilder SeedShopPolicies(this WorkThroughBuilder builder, ShopSettings settings) => builder
        .SeedClusterLimits(new(null, settings.FleetSlots), new(ShopJobs.ShipOrder.Identity.Key, settings.ShippingSlots))
        .SeedKeyedLimits(new(KeyScope.Tenant, null, settings.SlotsPerTenant), new(KeyScope.Partition, ShopJobs.ChargePayment.Identity.Key, 1))
        .SeedRateLimits(new RateLimit(ShopJobs.ChargePayment.Identity.Key, settings.PaymentsPerTenantPerMinute, TimeSpan.FromMinutes(1), KeyScope.Tenant))
        .SeedSchedules(ScheduleDefinition.Create("shop.reports.daily", settings.DailyReportCron, ShopJobs.DailyReport, new DailyReport(Region),
            settings.DailyReportTimeZone));
}
