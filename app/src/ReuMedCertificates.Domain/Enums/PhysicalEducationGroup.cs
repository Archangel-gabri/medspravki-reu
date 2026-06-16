namespace ReuMedCertificates.Domain.Enums;

/// <summary>
/// Физкультурная группа — определяет режим занятий и оценивание. Основное поле для физрука.
/// По приказу Минздрава (мед. заключение для занятий физической культурой).
/// </summary>
public enum PhysicalEducationGroup
{
    /// <summary>Не определена.</summary>
    None = 0,

    /// <summary>Основная.</summary>
    Basic = 1,

    /// <summary>Подготовительная.</summary>
    Preparatory = 2,

    /// <summary>Специальная «А».</summary>
    SpecialA = 3,

    /// <summary>Специальная «Б».</summary>
    SpecialB = 4,

    /// <summary>Освобождение (полное).</summary>
    Exempt = 5
}
