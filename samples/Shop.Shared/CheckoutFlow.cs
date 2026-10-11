using WorkThrough;

namespace Shop;

/// <summary>
/// reserve → charge → (review, only when the payment needs it) → ship → notify. A step runs once all its predecessors have
/// succeeded or been skipped, so shipping waits for an approval only when the review step is active.
/// </summary>
public static class CheckoutFlow
{
    public const string Key = "shop.checkout";
    public const int Version = 1;
    public const decimal ReviewThreshold = 500m;
    public static readonly TimeSpan ReviewTimeout = TimeSpan.FromDays(3);
    public static SignalDefinition<ReviewApproved> ReviewApproved { get; } = new("shop.review.approved");

    private const string Reserve = "reserve";
    private const string Charge = "charge";
    private const string Review = "review";
    private const string Ship = "ship";
    private const string Notify = "notify";

    // Bindings and conditions resolve to JSON property names now; no expression or code is persisted.
    public static FlowDefinition Definition { get; } = new FlowBuilder(Key, Version)
        .Step(Reserve, ShopJobs.ReserveStock.Identity).Named("Reserve stock")
            .BindInput((ReserveStock step) => step.OrderId, (OrderPlaced order) => order.OrderId)
        .Step(Charge, ShopJobs.ChargePayment.Identity).Named("Charge payment").After(Reserve)
            .BindInput((ChargePayment step) => step.OrderId, (OrderPlaced order) => order.OrderId)
            .BindInput((ChargePayment step) => step.Amount, (OrderPlaced order) => order.Amount)
        .WaitForSignal(Review, ReviewApproved, ReviewTimeout).Named("Manual review").After(Charge)
            .WhenOutput(Charge, (Payment payment) => payment.NeedsReview, true)
        .Step(Ship, ShopJobs.ShipOrder.Identity, queue: ShopJobs.ShippingQueue).Named("Ship order").After(Charge, Review)
            .BindInput((ShipOrder step) => step.OrderId, (OrderPlaced order) => order.OrderId)
        .Step(Notify, ShopJobs.NotifyCustomer.Identity).Named("Notify customer").After(Ship)
            .BindInput((NotifyCustomer step) => step.OrderId, (OrderPlaced order) => order.OrderId)
        .Build();

    public static string Correlation(string orderId) => $"order:{orderId}";

    /// <summary>One checkout per order, even when the starting job is retried.</summary>
    public static FlowStart Start(OrderPlaced order) => new(Correlation(order.OrderId), $"checkout:{order.OrderId}",
        PartitionKey: CustomerPartition(order), TenantId: order.TenantId);

    /// <summary>Partition keys are global, so the tenant namespaces the customer.</summary>
    public static string CustomerPartition(OrderPlaced order) => $"{order.TenantId}/customer/{order.Customer}";
}
