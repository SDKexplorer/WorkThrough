using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;

namespace WorkThrough;

/// <summary>Resolves a direct public DTO property to its serialized name without persisting code.</summary>
public static class JsonField
{
    public static string Of<T, TValue>(Expression<Func<T, TValue>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Body is not MemberExpression { Member: PropertyInfo info } member ||
            member.Expression != property.Parameters[0] || info.GetMethod?.IsPublic != true ||
            info.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
        {
            throw new ArgumentException("Select a direct, public, serialized property.", nameof(property));
        }

        return info.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? JobJson.Options.PropertyNamingPolicy?.ConvertName(info.Name) ?? info.Name;
    }
}
