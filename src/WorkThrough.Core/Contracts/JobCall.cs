using System.ComponentModel;

namespace WorkThrough;

/// <summary>One attempt of a job registered as a delegate: its input, its context and the attempt's scoped services.</summary>
public sealed class JobCall<TInput>(TInput input, JobExecutionContext context, IServiceProvider services, CancellationToken cancellationToken)
{
    public TInput Input { get; } = input;
    public JobExecutionContext Context { get; } = context;
    public IServiceProvider Services { get; } = services;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>A service from the attempt's scope; throws when it is not registered.</summary>
    public T Service<T>() where T : notnull =>
        (T?)Services.GetService(typeof(T)) ?? throw new InvalidOperationException($"No service of type {typeof(T).FullName} is registered.");
}

/// <summary>The scoped handler behind every delegate job. Hosts register it; applications do not use it directly.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class DelegateJobHandler(IServiceProvider services)
{
    internal IServiceProvider Services { get; } = services;
}
