namespace ReuMedCertificates.Domain.Enums;

/// <summary>
/// Вычисляемый статус действия справки по сроку (НЕ хранится в БД — считается на лету).
/// </summary>
public enum CertificateStatus
{
    /// <summary>Дата начала действия в будущем.</summary>
    Upcoming = 0,

    /// <summary>Текущая дата внутри периода действия.</summary>
    Active = 1,

    /// <summary>До даты окончания осталось не больше порога (ExpiringSoonThresholdDays).</summary>
    ExpiringSoon = 2,

    /// <summary>Последний день действия — истекает сегодня.</summary>
    EndsToday = 3,

    /// <summary>Дата окончания уже прошла.</summary>
    Expired = 4
}
