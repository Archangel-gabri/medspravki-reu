using System.Threading.Channels;
using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Web.Services;

/// <summary>In-process очередь сканов на ИИ-проверку (Channel). Загрузка не ждёт ИИ.</summary>
public sealed class ScanProcessingQueue : IScanProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid scanId) => _channel.Writer.TryWrite(scanId);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
