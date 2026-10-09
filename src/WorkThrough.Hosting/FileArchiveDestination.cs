using System.Security.Cryptography;
using System.Text;

namespace WorkThrough;

/// <summary>Immutable files in an operator-controlled directory. Use durable external storage, separate from the job database.</summary>
public sealed class FileArchiveDestination : IArchiveDestination
{
    private readonly string directory;
    public string Id { get; }

    public FileArchiveDestination(string id, string directory)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("Archive destinations need a stable ID and an absolute directory path.");
        }

        Id = id;
        this.directory = Path.GetFullPath(directory);
    }

    public async Task<VerifiedArchiveExport> StoreAndVerifyAsync(string storeId, ArchiveExport archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!Guid.TryParse(storeId, out var store))
        {
            throw new ArgumentException("Invalid store ID.", nameof(storeId));
        }

        var bytes = Encoding.UTF8.GetBytes(archive.Json);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length is < 1 or > 32_000_000 || hash != archive.Summary.Sha256)
        {
            throw new InvalidDataException("Archive content does not match its recorded hash or size bounds.");
        }

        var folder = Path.Combine(directory, store.ToString("N"));
        var version = $"{archive.Summary.Id:N}-{hash}.json";
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, version);
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.upload");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, path, overwrite: false);
                }
                catch (IOException) when (File.Exists(path))
                { /* A competing copy must pass the same read-back check. */
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        await using var saved = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (saved.Length != bytes.Length || Convert.ToHexString(await SHA256.HashDataAsync(saved, cancellationToken)) != hash)
        {
            throw new InvalidDataException("The stored archive failed read-back verification. Keep the database body.");
        }

        return new(archive.Summary.Id, hash, Id, version, bytes.LongLength);
    }
}
