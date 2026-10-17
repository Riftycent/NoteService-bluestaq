using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace NoteService.Infrastructure.Storage;

public sealed record BlobInfo(string Key, DateTimeOffset LastModified);

/// <summary>Stores attachment bytes outside the database. Swap the implementation for Azure Blob or S3 in production.</summary>
public interface IBlobStore
{
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);

    /// <returns>The content stream, or null when the key does not exist. The caller disposes it.</returns>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    IAsyncEnumerable<BlobInfo> ListAsync(CancellationToken ct = default);
}

public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    public string RootPath { get; set; } = Path.Combine("App_Data", "blobs");
}

public sealed class LocalDiskBlobStore(IOptions<BlobStorageOptions> options) : IBlobStore
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var key = Path.GetRelativePath(_root, file).Replace('\\', '/');
            yield return new BlobInfo(key, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero));
        }
    }

    /// <summary>Maps a key to a file path, rejecting anything that would escape the storage root.</summary>
    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Blob key is required.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Blob key escapes the storage root.", nameof(key));
        }

        return full;
    }
}
