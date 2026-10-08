using WorkThrough;

namespace Demo.Contracts;

// These addresses are persisted contracts. Keep them when moving processor code.
public static class OrderJobs
{
    public const string ValidateKey = "orders.validate";
    public const string DispatchKey = "orders.dispatch";
    public const string RejectKey = "orders.reject";
    public const string DefaultQueue = "default";
    public const string ShippingQueue = "shipping";
    public static JobDefinition<ValidateOrder> Validate { get; } = new(ValidateKey, displayName: "Validate order");
    public static JobDefinition<DispatchOrder> Dispatch { get; } = new(DispatchKey, displayName: "Dispatch order");
    public static JobDefinition<RejectOrder> Reject { get; } = new(RejectKey, displayName: "Route order for review");
}
