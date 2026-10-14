using WorkThrough;

namespace Shop.Jobs;

public static class ShopJobRegistration
{
    /// <summary>
    /// Binds each persisted address to its current job class, with the job's own limits and schedule. Moving or renaming a class
    /// changes only this mapping; work already stored keeps its address and runs on the new code.
    /// </summary>
    public static WorkThroughBuilder AddShopJobs(this WorkThroughBuilder builder, ShopSettings? settings = null)
    {
        settings ??= new();

        return builder
            .Job(ShopJobs.StartCheckout, job => job.RunWith<StartCheckoutJob>())
            .Job(ShopJobs.ReserveStock, job => job.RunWith<ReserveStockJob>())
            .Job(ShopJobs.ChargePayment, job => job
                .RunWith<ChargePaymentJob>()
                .AtMostPerPartition(1)
                .RateLimit(settings.PaymentsPerTenantPerMinute, TimeSpan.FromMinutes(1), KeyScope.Tenant))
            .Job(ShopJobs.ShipOrder, job => job
                .RunWith<ShipOrderJob>()
                .AtMostRunning(settings.ShippingSlots))
            .Job(ShopJobs.NotifyCustomer, job => job.RunWith<NotifyCustomerJob>())
            .Job(ShopJobs.DailyReport, job => job
                .RunWith<DailyReportJob>()
                .Every(settings.DailyReportCron, new DailyReport(ShopSetup.Region), settings.DailyReportTimeZone));
    }

    public static readonly string[] Queues = [JobDefaults.Queue, ShopJobs.ShippingQueue];
}
