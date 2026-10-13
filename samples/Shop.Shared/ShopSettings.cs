namespace Shop;

/// <summary>Bound from the "Shop" section; environment variables such as Shop__PostgreSql override these defaults.</summary>
public sealed class ShopSettings
{
    public const string SectionName = "Shop";

    /// <summary>Used unless <see cref="PostgreSql"/> is set. Separate local processes need the same absolute file path.</summary>
    public string Sqlite { get; init; } = "Data Source=shop.db";
    public string? PostgreSql { get; init; }
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
