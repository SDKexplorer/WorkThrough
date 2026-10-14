using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WorkThrough;

/// <summary>
/// Serializer settings for plain contract data; CLR type metadata is never persisted. Type metadata comes from source-generated contexts
/// — the engine's, and any an application registers with <see cref="AddContext"/> — and, where the runtime allows it, from reflection.
/// Native AOT applications register a context for their job inputs and outputs.
/// </summary>
public static class JobJson
{
    private static readonly ConcurrentQueue<IJsonTypeInfoResolver> Contexts = new([EngineJsonContext.Default]);

    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>
    /// Adds a source-generated context whose types WorkThrough can then serialize without reflection. Register before the types are used.
    /// </summary>
    /// <example><c>JobJson.AddContext(ShopJsonContext.Default);</c></example>
    public static void AddContext(IJsonTypeInfoResolver context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Contexts.Enqueue(context);
    }

    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Info<T>());

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Info<T>());

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Info<T>());

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, Info<T>());

    public static T? Deserialize<T>(JsonElement json) => json.Deserialize(Info<T>());

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new Registered()
        };
        options.MakeReadOnly();

        return options;
    }

    /// <summary>Asks each registered context in turn, then reflection when the runtime allows it.</summary>
    private sealed class Registered : IJsonTypeInfoResolver
    {
        private readonly IJsonTypeInfoResolver? reflection = Reflection();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            foreach (var context in Contexts)
            {
                if (context.GetTypeInfo(type, options) is { } info)
                {
                    return info;
                }
            }

            return reflection?.GetTypeInfo(type, options)
                ?? throw new NotSupportedException($"No JSON metadata for {type.FullName}. Add a JsonSerializerContext for it with JobJson.AddContext.");
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Used only when the runtime enables reflection-based serialization.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Used only when the runtime enables reflection-based serialization.")]
        private static IJsonTypeInfoResolver? Reflection() => JsonSerializer.IsReflectionEnabledByDefault ? new DefaultJsonTypeInfoResolver() : null;
    }
}

/// <summary>The engine's own types, serialized without reflection.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(IReadOnlyList<Guid>))]
[JsonSerializable(typeof(JobContractId))]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(FlowDefinition))]
[JsonSerializable(typeof(FlowFailure))]
[JsonSerializable(typeof(ScheduleDefinition))]
[JsonSerializable(typeof(OperationActor))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(TimeSpan?))]
[JsonSerializable(typeof(KeyScope))]
[JsonSerializable(typeof(KeyScope?))]
[JsonSerializable(typeof(ArchiveKind))]
[JsonSerializable(typeof(WorkerHeartbeat))]
[JsonSerializable(typeof(ExecutionArchive))]
[JsonSerializable(typeof(MaintenanceResult))]
[JsonSerializable(typeof(RetentionPolicy))]
[JsonSerializable(typeof(RetentionHold))]
[JsonSerializable(typeof(ArchivePolicy))]
[JsonSerializable(typeof(ConcurrencyLimit))]
[JsonSerializable(typeof(KeyedLimit))]
[JsonSerializable(typeof(RateLimit))]
[JsonSerializable(typeof(ExecutionQuery))]
[JsonSerializable(typeof(ProcessorAliasMapping))]
[JsonSerializable(typeof(IReadOnlyList<ProcessorAliasMapping>))]
[JsonSerializable(typeof(ProcessorAliasMapping[]))]
internal sealed partial class EngineJsonContext : JsonSerializerContext;
