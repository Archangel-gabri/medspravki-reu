namespace ReuMedCertificates.Application.Abstractions;

public sealed record StoredFile(string StoredName, string Sha256, long SizeBytes);

/// <summary>Файловое хранилище сканов ВНЕ wwwroot (план §7). Имена — GUID, контроль целостности — SHA-256.</summary>
public interface IScanStorage
{
    Task<StoredFile> SaveAsync(Stream content, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string storedName, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string storedName, CancellationToken cancellationToken = default);
}
