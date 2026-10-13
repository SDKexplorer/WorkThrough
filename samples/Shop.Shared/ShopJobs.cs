using WorkThrough;

namespace Shop;

/// <summary>
/// Typed addresses of the shop's work. The keys and versions are persisted: they stay fixed when job classes move
/// between namespaces, projects or deployments. Producers need only these definitions, never the job assemblies.
/// </summary>
public static class ShopJobs
{
    public const string ShippingQueue = "shipping";
    public static JobDefinition<OrderPlaced> StartCheckout { get; } = new("shop.checkout.start", displayName: "Start checkout");
    public static JobDefinition<ReserveStock> ReserveStock { get; } = new("shop.stock.reserve", displayName: "Reserve stock");
    public static JobDefinition<ChargePayment> ChargePayment { get; } = new("shop.payment.charge", displayName: "Charge payment");
    public static JobDefinition<ShipOrder> ShipOrder { get; } = new("shop.shipping.ship", displayName: "Ship order");
    public static JobDefinition<NotifyCustomer> NotifyCustomer { get; } = new("shop.customer.notify", displayName: "Notify customer");
    public static JobDefinition<DailyReport> DailyReport { get; } = new("shop.reports.daily", displayName: "Daily sales report");
}

public sealed record OrderPlaced(string OrderId, string TenantId, string Customer, decimal Amount);
public sealed record ReserveStock(string OrderId);
public sealed record Reservation(string OrderId, bool Reserved);
public sealed record ChargePayment(string OrderId, decimal Amount);
public sealed record Payment(string OrderId, bool NeedsReview);
public sealed record ReviewApproved(string OrderId, string Reviewer);
public sealed record ShipOrder(string OrderId);
public sealed record Shipment(string OrderId, string TrackingNumber);
public sealed record NotifyCustomer(string OrderId);
public sealed record DailyReport(string Region);
