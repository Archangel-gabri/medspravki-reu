using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>Текущий пользователь из cookie-сессии (ASP.NET Core Identity).</summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string UserName => Principal?.Identity?.Name ?? "system";

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
}
