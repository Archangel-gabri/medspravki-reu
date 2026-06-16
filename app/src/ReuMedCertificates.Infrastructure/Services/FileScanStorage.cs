using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>
/// Хранилище сканов в файловой системе ВНЕ wwwroot. GUID-имена + SHA-256 по ОРИГИНАЛУ.
/// Содержимое шифруется at-rest (AES через DataProtection, P1 MED-A02) — на диске только шифртекст.
/// </summary>
public sealed class FileScanStorage : IScanStorage
{
    private readonly string _root;
    private readonly IDataProtector _protector;

    public FileScanStorage(ScanStorageOptions options, IDataProtectionProvider dataProtection)
    {
        _root = Path.IsPathRooted(options.StoragePath)
            ? options.StoragePath
            : Path.Combine(AppContext.BaseDirectory, options.StoragePath);
        Directory.CreateDirectory(_root);
        _protector = dataProtection.CreateProtector("ReuMedCertificates.Scans.v1");
    }

    public async Task<StoredFile> SaveAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        var plain = ms.ToArray();

        // SHA-256 считаем по оригиналу (контроль целостности исходного документа).
        var hash = Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant();

        var cipher = _protector.Protect(plain);
        var storedName = Guid.NewGuid().ToString("N") + ".bin";
        await File.WriteAllBytesAsync(Path.Combine(_root, storedName), cipher, cancellationToken);

        return new StoredFile(storedName, hash, plain.Length);
    }

    public async Task<Stream?> OpenReadAsync(string storedName, CancellationToken cancellationToken = default)
    {
        // Path.GetFileName отсекает любые попытки path traversal.
        var path = Path.Combine(_root, Path.GetFileName(storedName));
        if (!File.Exists(path)) return null;

        var cipher = await File.ReadAllBytesAsync(path, cancellationToken);
        var plain = _protector.Unprotect(cipher);
        return new MemoryStream(plain);
    }

    public Task<bool> DeleteAsync(string storedName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, Path.GetFileName(storedName));
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }
}
