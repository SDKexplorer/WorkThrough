namespace WorkThrough;

/// <summary>Identifies a generated registration manifest in a deployed assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class JobModuleAttribute(Type moduleType) : Attribute
{
    public Type ModuleType { get; } = moduleType;
}

public interface IJobModule
{
    void Register(IJobRegistrar registrar);
}

public interface IJobRegistrar
{
    /// <summary>Registers a job at the address its class or input type declares.</summary>
    void Register<TInput, TJob>() where TJob : class, IJob<TInput>;
}
