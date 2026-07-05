using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Registry;
using ReuMedCertificates.Application.StudentPortal;
using ReuMedCertificates.Application.Students;
using ReuMedCertificates.Infrastructure.Identity;
using ReuMedCertificates.Infrastructure.Persistence;
using ReuMedCertificates.Infrastructure.Services;

namespace ReuMedCertificates.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=reu_med_certificates;Username=postgres;Password=postgres";

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));

        services
            .AddIdentity<AppUser, IdentityRole<Guid>>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Auth/Login";
            options.AccessDeniedPath = "/Auth/Login";
            options.SlidingExpiration = true;
        });

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentTeacherProvider>();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddScoped<IRegistryQueryService, RegistryQueryService>();
        services.AddScoped<IStudentService, StudentService>();
        services.AddScoped<IStudentPortalService, StudentPortalService>();
        services.AddScoped<ICertificateService, CertificateService>();

        return services;
    }
}
