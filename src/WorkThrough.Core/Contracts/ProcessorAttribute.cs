using System.Reflection;

namespace WorkThrough;

/// <summary>Permanent execution identity on an implementation, independent of its location and input CLR type.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProcessorAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public int Version { get; set; } = 1;
    public string? DisplayName { get; set; }
}

/// <summary>Executes a processor invocation. Several processors may consume the same input type.</summary>
public interface IProcessor<in TInput>
{
    Task<JobResult> ProcessAsync(TInput input, JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>Stable processor identity helpers for code that references an implementation.</summary>
public static class ProcessorId
{
    public static JobContractId For<TProcessor>() => For(typeof(TProcessor));

    public static JobContractId For(Type processorType)
    {
        var attribute = processorType.GetCustomAttribute<ProcessorAttribute>()
            ?? throw new InvalidOperationException($"{processorType.FullName} needs an explicit [Processor] identity.");
        return new(attribute.Id, attribute.Version);
    }
}
