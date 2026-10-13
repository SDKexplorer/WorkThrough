using System.Reflection;

namespace WorkThrough;

/// <summary>
/// Executes one attempt of a job. Delivery is at least once, so an attempt can repeat: pass
/// <see cref="JobExecutionContext.IdempotencyKey"/> to external services that must not apply an effect twice.
/// </summary>
/// <remarks>Several jobs may accept the same input type; each has its own address.</remarks>
public interface IJob<in TInput>
{
    Task<JobResult> ExecuteAsync(TInput input, JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>A job class's permanent address, independent of its name, namespace, assembly and input type.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class JobAttribute(string key) : Attribute
{
    public string Key { get; } = key;
    public int Version { get; set; } = 1;
    public string? DisplayName { get; set; }
}

/// <summary>Excludes a job class from generated registration because the composition root assigns its address.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ManualJobRegistrationAttribute : Attribute;

/// <summary>Declares a compatible historical address. Activation requires a coordinated registry update.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class JobAliasAttribute(string key) : Attribute
{
    public string Key { get; } = key;
    public int Version { get; set; } = 1;
}

/// <summary>Resolves a job class's address: its <see cref="JobAttribute"/>, otherwise the <see cref="JobContractAttribute"/> on its input.</summary>
public static class JobIdentity
{
    public static JobContractId For<TJob>() => For(typeof(TJob));

    public static JobContractId For(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        if (jobType.GetCustomAttribute<JobAttribute>() is { } job)
        {
            return new(job.Key, job.Version);
        }

        var inputs = jobType.GetInterfaces().Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IJob<>))
            .Select(type => type.GetGenericArguments()[0]).Where(input => input.GetCustomAttribute<JobContractAttribute>() is not null).ToArray();

        return inputs.Length == 1 ? JobContractId.For(inputs[0])
            : throw new InvalidOperationException($"{jobType.FullName} needs a [Job] address, or exactly one IJob<T> input with a [JobContract] address.");
    }

    internal static string? DisplayName(Type jobType) => jobType.GetCustomAttribute<JobAttribute>()?.DisplayName;
}
