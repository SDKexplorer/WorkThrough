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
    void Register<TJob, THandler>() where THandler : class, IJobHandler<TJob>;
    void RegisterProcessor<TInput, TProcessor>() where TProcessor : class, IProcessor<TInput>;
}
