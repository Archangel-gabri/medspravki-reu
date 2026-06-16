using Microsoft.AspNetCore.Identity;

namespace ReuMedCertificates.Infrastructure.Identity;

/// <summary>Учётная запись для входа в систему (ASP.NET Core Identity).</summary>
public class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
