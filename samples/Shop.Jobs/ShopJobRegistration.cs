using WorkThrough;

namespace Shop.Jobs;

public static class ShopJobRegistration
{
    /// <summary>
    /// Binds each persisted address to its current job class. Moving or renaming a class changes only this mapping; work
    /// already stored keeps its address and runs on the new code.
    /// </summary>
    public static WorkThroughBuilder AddShopJobs(this WorkThroughBuilder builder) => builder
        .AddJob<OrderPlaced, StartCheckoutJob>(ShopJobs.StartCheckout)
        .AddJob<ReserveStock, ReserveStockJob>(ShopJobs.ReserveStock)
        .AddJob<ChargePayment, ChargePaymentJob>(ShopJobs.ChargePayment)
        .AddJob<ShipOrder, ShipOrderJob>(ShopJobs.ShipOrder)
        .AddJob<NotifyCustomer, NotifyCustomerJob>(ShopJobs.NotifyCustomer)
        .AddJob<DailyReport, DailyReportJob>(ShopJobs.DailyReport);

    public static readonly string[] Queues = [JobDefaults.Queue, ShopJobs.ShippingQueue];
}
