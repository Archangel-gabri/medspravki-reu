using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>
/// Дефолтный провайдер распознавания: ИИ нет, всегда требуется ручная проверка.
/// Гарантирует, что система полна БЕЗ GPU/Python (план §3). Локальный OCR/VLM-провайдер
/// добавляется позже отдельной реализацией без изменения домена.
/// </summary>
public sealed class ManualRecognitionProvider : IDocumentRecognitionService
{
    public Task<RecognitionResult> RecognizeAsync(ScanInput scan, CancellationToken cancellationToken = default)
        => Task.FromResult(RecognitionResult.ManualOnly());
}
