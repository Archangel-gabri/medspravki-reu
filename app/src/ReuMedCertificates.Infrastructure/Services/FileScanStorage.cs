using System.Security.Cryptography;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>Хранилище сканов в файловой системе ВНЕ wwwroot. GUID-имена + SHA-256.</summary>
public sealed class FileScanStorage : IScanStorage
{
    private readonly string _root;

    public FileScanStorage(ScanStorageOptions options)
    {
        _root = Path.IsPathRooted(options.StoragePath)
            ? options.StoragePath
            : Path.Combine(AppContext.BaseDirectory, options.StoragePath);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var storedName = Guid.NewGuid().ToString("N") + ".bin";
        var path = Path.Combine(_root, storedName);

        using var sha = SHA256.Create();
        long size = 0;
        var buffer = new byte[81920];

        await using (var fs = File.Create(path))
        {
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.TransformBlock(buffer, 0, read, null, 0);
                size += read;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }

        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        return new StoredFile(storedName, hash, size);
    }

    public Task<Stream?> OpenReadAsync(string storedName, CancellationToken cancellationToken = default)
    {
        // Path.GetFileName отсекает любые попытки path traversal.
        var path = Path.Combine(_root, Path.GetFileName(storedName));
        Stream? stream = File.Exists(path) ? File.OpenRead(path) : null;
        return Task.FromResult(stream);
    }

    public Task<bool> DeleteAsync(string storedName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, Path.GetFileName(storedName));
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }
}
