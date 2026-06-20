using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Scans;

namespace ReuMedCertificates.Web.Services;

/// <summary>Фоновый воркер: берёт сканы из очереди и гоняет ИИ-автопроверку (в своём scope).</summary>
public sealed class ScanProcessingBackgroundService : BackgroundService
{
    private readonly IScanProcessingQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScanProcessingBackgroundService> _logger;

    public ScanProcessingBackgroundService(
        IScanProcessingQueue queue, IServiceScopeFactory scopeFactory, ILogger<ScanProcessingBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var scanId in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scans = scope.ServiceProvider.GetRequiredService<IScanService>();
                await scans.AutoReviewAsync(scanId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Авто-проверка скана {ScanId} не удалась", scanId);
            }
        }
    }
}
