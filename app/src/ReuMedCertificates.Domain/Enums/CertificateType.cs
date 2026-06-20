namespace ReuMedCertificates.Domain.Enums;

/// <summary>Тип медицинской справки/назначения по физкультуре.</summary>
public enum CertificateType
{
    /// <summary>086/у — общий допуск к физкультуре.</summary>
    Standard086 = 0,
    /// <summary>Справка для бассейна (занятия в воде).</summary>
    Pool = 1,
    /// <summary>Освобождение от физкультуры.</summary>
    Exemption = 2,
    /// <summary>Иной тип.</summary>
    Other = 3
}
