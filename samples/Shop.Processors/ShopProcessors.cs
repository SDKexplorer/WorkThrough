using WorkThrough;

namespace Shop.Processors;

public static class ShopProcessors
{
    /// <summary>
    /// Binds each persisted address to its current implementation. Moving or renaming a class changes only this mapping;
    /// work already stored keeps its address and runs on the new code.
    /// </summary>
    public static WorkThroughBuilder AddShopProcessors(this WorkThroughBuilder builder) => builder
        .AddProcessor<OrderPlaced, StartCheckout>(ShopJobs.StartCheckout)
        .AddProcessor<ReserveStock, ReserveStockProcessor>(ShopJobs.ReserveStock)
        .AddProcessor<ChargePayment, ChargePaymentProcessor>(ShopJobs.ChargePayment)
        .AddProcessor<ShipOrder, ShipOrderProcessor>(ShopJobs.ShipOrder)
        .AddProcessor<NotifyCustomer, NotifyCustomerProcessor>(ShopJobs.NotifyCustomer)
        .AddProcessor<DailyReport, DailyReportProcessor>(ShopJobs.DailyReport);

    public static readonly string[] Queues = [JobDefaults.Queue, ShopJobs.ShippingQueue];
}
