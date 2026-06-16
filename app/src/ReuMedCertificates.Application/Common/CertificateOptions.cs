namespace ReuMedCertificates.Application.Common;

/// <summary>Настройки расчёта статусов справок (из конфигурации).</summary>
public sealed class CertificateOptions
{
    public const string SectionName = "Certificates";

    /// <summary>За сколько дней до окончания статус становится «Скоро истекает».</summary>
    public int ExpiringSoonThresholdDays { get; set; } = 7;
}
