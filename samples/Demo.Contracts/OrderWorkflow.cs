using WorkThrough;

namespace Demo.Contracts;

public static class OrderWorkflow
{
    public const string Key = "orders.process";
    public const int Version = 2;
    public const string Validate = "validate";
    public const string Dispatch = "dispatch";
    public const string Review = "reject";

    // Only the resolved JSON names are saved; expressions are never serialized or executed by workers.
    public static FlowDefinition Definition { get; } = new FlowBuilder(Key, Version)
        .Step(Validate, OrderJobs.Validate.Identity, maxAttempts: 1)
            .BindInput(JsonField.Of<ValidateOrder, string>(x => x.OrderId), JsonField.Of<OrderRequest, string>(x => x.OrderId))
            .BindInput(JsonField.Of<ValidateOrder, decimal>(x => x.Amount), JsonField.Of<OrderRequest, decimal>(x => x.Amount))
        .Step(Dispatch, OrderJobs.Dispatch.Identity, queue: OrderJobs.ShippingQueue).After(Validate)
            .BindInput(JsonField.Of<DispatchOrder, string>(x => x.OrderId), JsonField.Of<OrderValidation, string>(x => x.OrderId), Validate)
            .WhenOutput(Validate, JsonField.Of<OrderValidation, bool>(x => x.Approved), true)
        .Step(Review, OrderJobs.Reject.Identity).After(Validate)
            .BindInput(JsonField.Of<RejectOrder, string>(x => x.OrderId), JsonField.Of<OrderValidation, string>(x => x.OrderId), Validate)
            .WhenOutput(Validate, JsonField.Of<OrderValidation, bool>(x => x.Approved), false)
        .Build();

    public static string CorrelationId(string orderId) => $"order:{orderId}";
    public static string StartKey(string orderId) => $"process-order:{orderId}";
}
