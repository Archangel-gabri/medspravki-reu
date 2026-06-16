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

    public static readonly string[] All = { Teacher, HeadOfDepartment, Admin };
}
