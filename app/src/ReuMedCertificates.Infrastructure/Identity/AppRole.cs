using Microsoft.AspNetCore.Identity;

namespace ReuMedCertificates.Infrastructure.Identity;

/// <summary>Роль пользователя. В MVP активна одна (Teacher); остальные заведены под будущее расширение.</summary>
public class AppRole : IdentityRole<Guid>
{
    public AppRole() { }
    public AppRole(string name) : base(name) { }
}

/// <summary>Имена ролей системы (Codex: уже в v1 нужны минимум три).</summary>
public static class AppRoles
{
    public const string Teacher = "Teacher";
    public const string HeadOfDepartment = "HeadOfDepartment";
    public const string Admin = "Admin";
    /// <summary>Медработник — единственный, кто даёт медвердикт по справке (323-ФЗ ст.13).</summary>
    public const string MedicalStaff = "MedicalStaff";
    /// <summary>Студент — видит только свой допуск (портал студента — фаза v2).</summary>
    public const string Student = "Student";

    public static readonly string[] All = { Teacher, HeadOfDepartment, Admin, MedicalStaff, Student };
}
