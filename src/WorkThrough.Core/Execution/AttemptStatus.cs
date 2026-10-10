using System.Text.Json.Serialization;

namespace WorkThrough;

/// <summary>How one attempt ended. Persisted and archived by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttemptStatus>))]
public enum AttemptStatus
{
    Running, Succeeded, Failed, Rejected, Deferred, Interrupted, Abandoned, Cancelled
}
