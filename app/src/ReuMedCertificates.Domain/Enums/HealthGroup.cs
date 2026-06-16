namespace ReuMedCertificates.Domain.Enums;

/// <summary>
/// Группа здоровья (I–V) — общая медицинская оценка. НЕ путать с физкультурной группой
/// (<see cref="PhysicalEducationGroup"/>). Медполе: видно не всем ролям (152-ФЗ / 323-ФЗ).
/// </summary>
public enum HealthGroup
{
    Unknown = 0,
    I = 1,
    II = 2,
    III = 3,
    IV = 4,
    V = 5
}
