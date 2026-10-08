using WorkThrough;

namespace Demo.Lifecycle;

public static class OrderDefinitions
{
    public static JobDefinition<OrderPlaced> Notify { get; } = new("orders.notify", displayName: "Notify customer");
    public static SignalDefinition<Approval> Approved { get; } = new("orders.approved");
    public const string ApprovalStep = "approval";
    public const string NotificationStep = "notification";
    public const string FlowKey = "orders.approve-and-notify";
    public static FlowDefinition ApprovalFlow { get; } = new FlowBuilder(FlowKey, 1)
        .WaitForSignal(ApprovalStep, Approved, TimeSpan.FromHours(24))
        .Step(NotificationStep, Notify.Identity).After(ApprovalStep)
            .BindInput(JsonField.Of<OrderPlaced, string>(o => o.OrderId), JsonField.Of<Approval, string>(a => a.OrderId), ApprovalStep)
            .BindInput(JsonField.Of<OrderPlaced, string>(o => o.CustomerEmail), JsonField.Of<Approval, string>(a => a.CustomerEmail), ApprovalStep)
        .Build();
    public static string Correlation(string orderId) => $"order:{orderId}";
    public static string AcceptanceKey(string orderId) => $"order-accepted:{orderId}";
}

public sealed record OrderPlaced(string OrderId, string CustomerEmail);
public sealed record Approval(string OrderId, string CustomerEmail);

public sealed class LifecycleSettings
{
    public const string SectionName = "Lifecycle";
    public string BusinessConnection { get; init; } = "Data Source=lifecycle-business.db";
    public string JobsConnection { get; init; } = "Data Source=lifecycle-jobs.db";
    public string OrderId { get; init; } = Guid.NewGuid().ToString("N");
    public string CustomerEmail { get; init; } = "customer@example.test";
}
