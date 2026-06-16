using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Infrastructure.Identity;

namespace ReuMedCertificates.Web.Pages.Auth;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<AppUser> _signInManager;
    private readonly UserManager<AppUser> _userManager;
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;

    public LoginModel(SignInManager<AppUser> signInManager, UserManager<AppUser> userManager,
        IApplicationDbContext db, IDateTimeProvider clock)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _db = db;
        _clock = clock;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Введите логин")]
        public string Login { get; set; } = string.Empty;

        [Required(ErrorMessage = "Введите пароль")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; } = true;
    }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect("/registry");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await _signInManager.PasswordSignInAsync(
            Input.Login, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (result.Succeeded)
        {
            var user = await _userManager.FindByNameAsync(Input.Login);
            if (user is not null)
            {
                user.LastLoginAt = DateTime.UtcNow;
                await _userManager.UpdateAsync(user);
            }
            await AuditAsync("Login", $"Успешный вход: {Input.Login}", ip);
            return LocalRedirect(returnUrl ?? "/registry");
        }

        await AuditAsync("LoginFailed",
            result.IsLockedOut ? $"Блокировка после неудачных попыток: {Input.Login}" : $"Неудачная попытка входа: {Input.Login}",
            ip);

        ErrorMessage = result.IsLockedOut
            ? "Учётная запись временно заблокирована после нескольких неудачных попыток."
            : "Неверный логин или пароль.";
        return Page();
    }

    private async Task AuditAsync(string action, string description, string? ip)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = "AppUser",
            ActionType = action,
            UserNameSnapshot = Input.Login,
            OccurredAt = _clock.UtcNow,
            Description = description,
            IpAddress = ip
        });
        await _db.SaveChangesAsync();
    }
}
