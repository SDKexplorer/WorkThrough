namespace Shop;

/// <summary>Bound from the "Shop" section; environment variables such as Shop__PostgreSql override these defaults.</summary>
public sealed class ShopSettings
{
    public const string SectionName = "Shop";

    /// <summary>The store every shop process shares; by default the samples' local database (samples/compose.yaml).</summary>
    public string PostgreSql { get; init; } = "Host=127.0.0.1;Port=5432;Database=workthrough;Username=postgres;Password=workthrough;Search Path=shop";
    public int WorkerSlots { get; init; } = 4;
    public int FleetSlots { get; init; } = 12;
    public int ShippingSlots { get; init; } = 2;

    /// <summary>One tenant's backlog can hold at most this many fleet slots.</summary>
    public int SlotsPerTenant { get; init; } = 4;

    /// <summary>Payment-provider calls each tenant may start per minute.</summary>
    public int PaymentsPerTenantPerMinute { get; init; } = 120;
    public string DailyReportCron { get; init; } = "0 6 * * *";
    public string DailyReportTimeZone { get; init; } = "UTC";
}
