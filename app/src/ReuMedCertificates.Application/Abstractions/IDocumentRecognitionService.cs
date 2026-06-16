namespace ReuMedCertificates.Application.Abstractions;

/// <summary>Входные данные скана справки для распознавания.</summary>
public sealed record ScanInput(byte[] Content, string ContentType, string FileName);

/// <summary>Одно распознанное поле с оценкой уверенности (0..1).</summary>
public sealed record RecognizedField(string Name, string? Value, double Confidence);

/// <summary>Результат распознавания. В MVP всегда RequiresManualReview = true (нет ИИ).</summary>
public sealed record RecognitionResult(
    bool RequiresManualReview,
    IReadOnlyList<RecognizedField> Fields,
    string? RawJson = null)
{
    public static RecognitionResult ManualOnly() =>
        new(true, Array.Empty<RecognizedField>());
}

/// <summary>
/// Подключаемое распознавание документа (план §3, совет Codex). Реализации в Infrastructure:
/// ManualProvider (дефолт, без ИИ) → LocalProvider (офлайн OCR/VLM) → опц. провайдеры.
/// Переключение НЕ затрагивает домен — это разрешает конфликт «офлайн vs облако».
/// </summary>
public interface IDocumentRecognitionService
{
    Task<RecognitionResult> RecognizeAsync(ScanInput scan, CancellationToken cancellationToken = default);
}
