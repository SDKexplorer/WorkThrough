namespace WorkThrough;

/// <summary>
/// A step captured with <see cref="FlowBuilder.As"/>. It converts to the step's ID, so <c>After</c>, <c>OnFailureOf</c>, <c>WhenOutput</c>,
/// <c>BindInput</c> and <c>ForEach</c> take it in place of a string, and the ID is written only where the step is declared.
/// </summary>
public readonly record struct FlowStepRef(string Id)
{
    public static implicit operator string(FlowStepRef step) => step.Id;

    public override string ToString() => Id;
}
