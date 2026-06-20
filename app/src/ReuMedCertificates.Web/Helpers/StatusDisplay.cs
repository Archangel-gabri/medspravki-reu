using ReuMedCertificates.Domain.Enums;

namespace ReuMedCertificates.Web.Helpers;

/// <summary>Человекочитаемые подписи и CSS-классы статусов/групп (всегда цвет + текст для дальтоников).</summary>
public static class StatusDisplay
{
    public static string CertType(CertificateType type) => type switch
    {
        CertificateType.Standard086 => "086/у",
        CertificateType.Pool => "Бассейн",
        CertificateType.Exemption => "Освобождение",
        _ => "Иная"
    };

    public static string CertText(CertificateStatus? status) => status switch
    {
        null => "Нет справки",
        CertificateStatus.Active => "Действует",
        CertificateStatus.ExpiringSoon => "Истекает",
        CertificateStatus.EndsToday => "Истекает сегодня",
        CertificateStatus.Upcoming => "Будет активна",
        CertificateStatus.Expired => "Истекла",
        _ => "—"
    };

    /// <summary>CSS-класс статуса: ok / warn / danger / muted.</summary>
    public static string CertClass(CertificateStatus? status) => status switch
    {
        null => "danger",
        CertificateStatus.Active => "ok",
        CertificateStatus.ExpiringSoon => "warn",
        CertificateStatus.EndsToday => "warn",
        CertificateStatus.Upcoming => "muted",
        CertificateStatus.Expired => "danger",
        _ => "muted"
    };

    /// <summary>Числовая «тяжесть» для сортировки «Перед парой» (хуже — выше).</summary>
    public static int Severity(CertificateStatus? status, PhysicalEducationGroup group) => status switch
    {
        null => 0,
        CertificateStatus.Expired => 1,
        CertificateStatus.EndsToday => 2,
        CertificateStatus.ExpiringSoon => 3,
        CertificateStatus.Upcoming => 4,
        _ => group == PhysicalEducationGroup.Exempt ? 6 : 5
    };

    public static string PhysicalGroup(PhysicalEducationGroup group) => group switch
    {
        PhysicalEducationGroup.Basic => "Основная",
        PhysicalEducationGroup.Preparatory => "Подготовительная",
        PhysicalEducationGroup.SpecialA => "Специальная «А»",
        PhysicalEducationGroup.SpecialB => "Специальная «Б»",
        PhysicalEducationGroup.Exempt => "Освобождение",
        _ => "—"
    };

    public static string HealthGroup(HealthGroup group) => group switch
    {
        Domain.Enums.HealthGroup.I => "I",
        Domain.Enums.HealthGroup.II => "II",
        Domain.Enums.HealthGroup.III => "III",
        Domain.Enums.HealthGroup.IV => "IV",
        Domain.Enums.HealthGroup.V => "V",
        _ => "—"
    };

    public static string Verification(VerificationStatus status) => status switch
    {
        VerificationStatus.Draft => "Черновик",
        VerificationStatus.NeedsReview => "На проверке",
        VerificationStatus.Verified => "Проверена",
        VerificationStatus.Rejected => "Отклонена",
        VerificationStatus.Expired => "Истекла",
        VerificationStatus.Revoked => "Аннулирована",
        _ => "—"
    };
}
