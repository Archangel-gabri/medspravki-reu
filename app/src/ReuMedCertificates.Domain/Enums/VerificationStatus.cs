namespace ReuMedCertificates.Domain.Enums;

/// <summary>
/// Жизненный цикл КЕЙСА справки (совет Codex): официальным фактом становится только
/// человеком подтверждённое решение (<see cref="Verified"/>). Источник черновика — отдельно
/// (<see cref="DraftSource"/>): ручной ввод, импорт Excel, загрузка студентом, OCR.
/// </summary>
public enum VerificationStatus
{
    /// <summary>Черновик: данные внесены/распознаны, но ещё не отправлены на проверку.</summary>
    Draft = 0,

    /// <summary>Отправлено и ожидает ручной проверки уполномоченным лицом.</summary>
    NeedsReview = 1,

    /// <summary>Проверено и подтверждено человеком — официальный факт.</summary>
    Verified = 2,

    /// <summary>Отклонено при проверке (с обязательной причиной).</summary>
    Rejected = 3,

    /// <summary>Срок действия истёк (переводится фоновым процессом по EndDate).</summary>
    Expired = 4,

    /// <summary>Отозвано/аннулировано после подтверждения.</summary>
    Revoked = 5
}
