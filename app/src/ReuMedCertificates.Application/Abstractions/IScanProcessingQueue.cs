namespace ReuMedCertificates.Application.Abstractions;

/// <summary>Очередь сканов на фоновую ИИ-проверку (загрузка возвращается сразу, ИИ работает в фоне).</summary>
public interface IScanProcessingQueue
{
    void Enqueue(Guid scanId);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
