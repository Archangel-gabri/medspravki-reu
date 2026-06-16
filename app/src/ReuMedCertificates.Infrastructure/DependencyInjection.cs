using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Audit;
using ReuMedCertificates.Application.Certificates;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Lookups;
using ReuMedCertificates.Application.Registry;
using ReuMedCertificates.Application.Roster;
using ReuMedCertificates.Application.Scans;
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

        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services
            .AddIdentity<AppUser, AppRole>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.User.RequireUniqueEmail = false;
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

        services.AddSingleton(new CertificateOptions
        {
            ExpiringSoonThresholdDays = configuration.GetValue("ExpiringSoonThresholdDays", 7)
        });

        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        // Хранилище сканов (файлы вне wwwroot) + сервис сканов.
        var scanOptions = configuration.GetSection(ScanStorageOptions.SectionName).Get<ScanStorageOptions>()
            ?? new ScanStorageOptions();
        services.AddSingleton(scanOptions);
        services.AddScoped<IScanStorage, FileScanStorage>();
        services.AddScoped<IScanService, ScanService>();

        // Выбор провайдера распознавания по конфигу. План §3:
        //   Manual (дефолт, без ИИ) | LocalOllama (локальный VLM в периметре РЭУ / на ПК с GPU).
        var recognitionOptions = configuration.GetSection(RecognitionOptions.SectionName).Get<RecognitionOptions>()
            ?? new RecognitionOptions();
        services.AddSingleton(recognitionOptions);

        if (string.Equals(recognitionOptions.Provider, "LocalOllama", StringComparison.OrdinalIgnoreCase))
            services.AddHttpClient<IDocumentRecognitionService, LocalOllamaRecognitionProvider>();
        else
            services.AddScoped<IDocumentRecognitionService, ManualRecognitionProvider>();

        services.AddScoped<IRegistryQueryService, RegistryQueryService>();
        services.AddScoped<IStudentService, StudentService>();
        services.AddScoped<ICertificateService, CertificateService>();
        services.AddScoped<ILookupService, LookupService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();

        // Источник импорта реестра (1С OData / SQL) + сервис импорта.
        // По умолчанию — SQL на базе приложения (локальная демо-таблица onec_roster_demo).
        var rosterOptions = configuration.GetSection(RosterOptions.SectionName).Get<RosterOptions>() ?? new RosterOptions();
        if (string.IsNullOrWhiteSpace(rosterOptions.Sql.ConnectionString))
            rosterOptions.Sql.ConnectionString = connectionString;
        services.AddSingleton(rosterOptions);

        if (string.Equals(rosterOptions.Provider, "OneC", StringComparison.OrdinalIgnoreCase))
            services.AddHttpClient<IRosterSource, OneCODataRosterSource>();
        else
            services.AddScoped<IRosterSource, SqlRosterSource>();

        services.AddScoped<IRosterImportService, RosterImportService>();

        return services;
    }
}
