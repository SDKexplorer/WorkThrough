using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkThrough.Storage;

/// <summary>The query and its kind that a history cursor is bound to, named as earlier releases wrote them.</summary>
internal sealed record HistoryFingerprint<T>([property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("query")] T Query);

/// <summary>
/// Cursors and the fingerprints that bind them, serialized with the default settings earlier releases used, so existing cursors still read.
/// </summary>
[JsonSerializable(typeof(RelationalJobStore.ExecutionCursor))]
[JsonSerializable(typeof(ExecutionQuery))]
[JsonSerializable(typeof(HistoryFingerprint<AuditQuery>))]
[JsonSerializable(typeof(HistoryFingerprint<ArchiveQuery>))]
internal sealed partial class CursorJsonContext : JsonSerializerContext;

/// <summary>
/// Named values written in order, each by its own converter: the JSON an anonymous object with those properties produced, which stored
/// fingerprints were hashed from. Or a single value written as itself.
/// </summary>
[JsonConverter(typeof(FieldsConverter))]
internal sealed class Fields
{
    private Fields(IReadOnlyList<(string Name, object? Value)>? properties, object? value)
    {
        Properties = properties;
        Value = value;
    }

    public IReadOnlyList<(string Name, object? Value)>? Properties { get; }
    public object? Value { get; }

    public static Fields Named(params (string Name, object? Value)[] properties) => new(properties, null);

    public static Fields Of(object value) => new(null, value);
}

internal sealed class FieldsConverter : JsonConverter<Fields>
{
    public override Fields Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Fields are written for fingerprints and audit records only.");

    public override void Write(Utf8JsonWriter writer, Fields value, JsonSerializerOptions options)
    {
        if (value.Properties is null)
        {
            WriteValue(writer, value.Value, options);

            return;
        }

        writer.WriteStartObject();
        foreach (var (name, property) in value.Properties)
        {
            writer.WritePropertyName(name);
            WriteValue(writer, property, options);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();

            return;
        }

        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
    }
}

/// <summary>The storage layer's own records, in the engine's JSON settings.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Fields))]
[JsonSerializable(typeof(RelationalJobStore.ChildOutcome))]
[JsonSerializable(typeof(RelationalJobStore.MaintenanceBatch))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
