using System.Reflection;
using System.Text.RegularExpressions;

namespace WorkThrough;

/// <summary>Gives persisted work an identity independent of CLR names.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class JobContractAttribute(string key) : Attribute
{
    public string Key { get; } = key;
    public int Version { get; set; } = 1;
}

/// <summary>A stable, explicitly versioned job identity.</summary>
public sealed record JobContractId
{
    public JobContractId(string key, int version)
    {
        if (string.IsNullOrWhiteSpace(key) || !Regex.IsMatch(key, "^[a-z][a-z0-9.-]{0,119}$"))
            throw new ArgumentException("Job keys must be lowercase identifiers, for example invoices.generate.", nameof(key));
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        Key = key;
        Version = version;
    }

    public string Key { get; }
    public int Version { get; }

    public static JobContractId For<T>() => For(typeof(T));

    public static JobContractId For(Type type)
    {
        var attribute = type.GetCustomAttribute<JobContractAttribute>()
            ?? throw new InvalidOperationException($"{type.FullName} needs an explicit [JobContract] identity.");
        return new(attribute.Key, attribute.Version);
    }

    public override string ToString() => $"{Key}/v{Version}";
}
