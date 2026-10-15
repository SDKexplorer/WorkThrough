using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkThrough;

/// <summary>
/// Encrypts business data at rest (ADR-027): job inputs and outputs, flow inputs and signal payloads. Stores protect on write and
/// unprotect on read, so the engine and jobs always see the original JSON.
/// </summary>
public interface IPayloadProtector
{
    /// <summary>The JSON envelope that stores <paramref name="value"/> encrypted under the current key.</summary>
    JsonElement Protect(JsonElement value);

    /// <summary>
    /// The original JSON of an envelope, or <paramref name="stored"/> unchanged when it is not one. An envelope under a key this host
    /// does not hold is also returned unchanged; <see cref="PayloadEnvelope.IsProtected"/> tells the caller.
    /// </summary>
    JsonElement Unprotect(JsonElement stored);
}

/// <summary>A store that can encrypt business data at rest with a configured protector.</summary>
public interface IPayloadProtection
{
    IPayloadProtector? PayloadProtector { get; set; }
}

/// <summary>The stored form of an encrypted value: <c>{"$wt":"aes-gcm-v1","key":"…","nonce":"…","data":"…"}</c>.</summary>
public static class PayloadEnvelope
{
    public const string MarkerProperty = "$wt";
    public const string Algorithm = "aes-gcm-v1";
    public const string KeyProperty = "key";
    public const string NonceProperty = "nonce";
    public const string DataProperty = "data";

    /// <summary>True when <paramref name="value"/> is an envelope, which a host without its key cannot read.</summary>
    public static bool IsProtected(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(MarkerProperty, out var marker) && marker.ValueKind == JsonValueKind.String && marker.GetString() == Algorithm;

    /// <summary>The key an envelope was written with.</summary>
    public static string? KeyOf(JsonElement value) => IsProtected(value) && value.TryGetProperty(KeyProperty, out var key) ? key.GetString() : null;
}

/// <summary>
/// AES-256-GCM with a key ring: new values use the current key; older keys stay to read what they wrote, so keys rotate without
/// rewriting stored work. Each value gets a fresh 96-bit nonce.
/// </summary>
public sealed class AesGcmPayloadProtector : IPayloadProtector
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly IReadOnlyDictionary<string, byte[]> keys;
    private readonly string current;

    public AesGcmPayloadProtector(IReadOnlyDictionary<string, byte[]> keys, string current)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || keys.Any(key => string.IsNullOrWhiteSpace(key.Key) || key.Value is not { Length: KeyBytes }))
        {
            throw new ArgumentException("Payload keys need non-empty IDs and 32 random bytes each.", nameof(keys));
        }

        if (!keys.ContainsKey(current))
        {
            throw new ArgumentException($"The current payload key {current} is not in the key ring.", nameof(current));
        }

        this.keys = keys.ToDictionary(key => key.Key, key => key.Value.ToArray(), StringComparer.Ordinal);
        this.current = current;
    }

    public JsonElement Protect(JsonElement value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value.GetRawText());
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var data = new byte[plaintext.Length + TagBytes];
        using (var aes = new AesGcm(keys[current], TagBytes))
        {
            aes.Encrypt(nonce, plaintext, data.AsSpan(0, plaintext.Length), data.AsSpan(plaintext.Length), Encoding.UTF8.GetBytes(current));
        }

        return JobJson.ToElement(new JsonObject
        {
            [PayloadEnvelope.MarkerProperty] = PayloadEnvelope.Algorithm,
            [PayloadEnvelope.KeyProperty] = current,
            [PayloadEnvelope.NonceProperty] = Convert.ToBase64String(nonce),
            [PayloadEnvelope.DataProperty] = Convert.ToBase64String(data)
        });
    }

    public JsonElement Unprotect(JsonElement stored)
    {
        if (PayloadEnvelope.KeyOf(stored) is not { } id || !keys.TryGetValue(id, out var key))
        {
            return stored;
        }

        var nonce = Convert.FromBase64String(stored.GetProperty(PayloadEnvelope.NonceProperty).GetString()!);
        var data = Convert.FromBase64String(stored.GetProperty(PayloadEnvelope.DataProperty).GetString()!);
        var plaintext = new byte[data.Length - TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
        {
            // The key ID is authenticated, so an envelope cannot be relabeled to another key.
            aes.Decrypt(nonce, data.AsSpan(0, plaintext.Length), data.AsSpan(plaintext.Length), plaintext, Encoding.UTF8.GetBytes(id));
        }

        using var document = JsonDocument.Parse(plaintext);

        return document.RootElement.Clone();
    }
}

/// <summary>
/// Stored work is encrypted under a key this host does not hold. Nothing is settled from it; once the key is configured, a retry or
/// another host completes the work.
/// </summary>
public sealed class PayloadKeyMissingException(string? key)
    : InvalidOperationException($"Stored work is encrypted with payload key '{key}', which this host does not hold. Add it to ProtectPayloads.");
