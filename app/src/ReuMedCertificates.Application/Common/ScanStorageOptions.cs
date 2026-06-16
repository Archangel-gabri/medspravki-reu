namespace ReuMedCertificates.Application.Common;

/// <summary>Настройки загрузки и хранения сканов справок.</summary>
public sealed class ScanStorageOptions
{
    public const string SectionName = "Scans";

    /// <summary>Каталог хранения файлов ВНЕ wwwroot.</summary>
    public string StoragePath { get; set; } = "App_Data/scans";

    /// <summary>Максимальный размер загружаемого файла (по умолчанию 10 МБ).</summary>
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Разрешённые типы файлов.</summary>
    public string[] AllowedContentTypes { get; set; } = { "application/pdf", "image/jpeg", "image/png" };
}
