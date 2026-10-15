using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace WorkThrough.Storage;

/// <summary>Encryption of business data at rest (ADR-027). Writes protect only once engine protocol 7 is active.</summary>
public abstract partial class RelationalJobStore : IPayloadProtection
{
    /// <summary>How long a store trusts its last reading of a protocol below payload protection before reading it again.</summary>
    private static readonly TimeSpan ProtocolRecheck = TimeSpan.FromSeconds(5);

    private int observedProtocol;
    private long protocolObservedAt;

    /// <summary>
    /// Encrypts job inputs and outputs, flow inputs and signal payloads when set, and decrypts what it can read. Set before the store
    /// is used; <c>WorkThroughBuilder.ProtectPayloads</c> does this.
    /// </summary>
    public IPayloadProtector? PayloadProtector { get; set; }

    /// <summary>
    /// A business value as it should be stored: an encrypted envelope once protection may be written, otherwise the value. A value still
    /// encrypted under a key this host lacks, such as one copied by a fork, is kept as it is.
    /// </summary>
    private async Task<JsonElement> ProtectedAsync(DbConnection connection, DbTransaction? transaction, JsonElement value, CancellationToken ct) =>
        PayloadProtector is { } protector && !PayloadEnvelope.IsProtected(value) && await WritesProtectedAsync(connection, transaction, ct)
            ? protector.Protect(value)
            : value;

    private async Task<string> StoredAsync(DbConnection connection, DbTransaction? transaction, JsonElement value, CancellationToken ct) =>
        (await ProtectedAsync(connection, transaction, value, ct)).GetRawText();

    private async Task<string?> StoredAsync(DbConnection connection, DbTransaction? transaction, JsonElement? value, CancellationToken ct) =>
        value is { } present ? await StoredAsync(connection, transaction, present, ct) : null;

    /// <summary>A stored business value, decrypted when this host holds its key.</summary>
    private JsonElement Revealed(JsonElement stored) => PayloadProtector?.Unprotect(stored) ?? stored;

    private JsonElement Revealed(string stored) => Revealed(Json(stored));

    /// <summary>
    /// A stored value the engine must read to make progress, such as a flow input it plans steps from. A host without its key refuses,
    /// so it never plans from or copies ciphertext as if it were the value; a host that holds the key completes the work.
    /// </summary>
    private JsonElement Required(JsonElement stored)
    {
        var value = Revealed(stored);
        if (PayloadEnvelope.IsProtected(value))
        {
            throw new PayloadKeyMissingException(PayloadEnvelope.KeyOf(value));
        }

        return value;
    }

    /// <summary>
    /// Whether this store may write envelopes. The protocol only rises, so once it reaches payload protection it is not read again;
    /// below it, a reading is reused for a few seconds, since writing plain JSON a little longer is always safe.
    /// </summary>
    private async Task<bool> WritesProtectedAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        if (Volatile.Read(ref observedProtocol) >= EngineProtocol.PayloadProtection)
        {
            return true;
        }

        var observedAt = Volatile.Read(ref protocolObservedAt);
        if (observedAt != 0 && Stopwatch.GetElapsedTime(observedAt) < ProtocolRecheck)
        {
            return false;
        }

        Observe(await ActiveProtocolAsync(connection, transaction, ct));

        return Volatile.Read(ref observedProtocol) >= EngineProtocol.PayloadProtection;
    }

    private void Observe(int protocol)
    {
        Volatile.Write(ref observedProtocol, protocol);
        Volatile.Write(ref protocolObservedAt, Stopwatch.GetTimestamp());
    }
}
