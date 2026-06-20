using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using ReuMedCertificates.Infrastructure.Identity;
using ReuMedCertificates.Web.Services;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Scans;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Infrastructure;
using ReuMedCertificates.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console();
});

builder.Services.AddRazorPages(options =>
{
    // Вход/ошибка — анонимны. Остальное закрыто FallbackPolicy (любая страница требует входа),
    // а доступ по ролям задаётся явно ниже — чтобы кабинет студента и разделы сотрудников не пересекались.
    options.Conventions.AllowAnonymousToPage("/Auth/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
    // Рабочие разделы сотрудников (физрук/завкаф/админ/медработник): реестр, «Перед парой», карточки, справки.
    options.Conventions.AuthorizeFolder("/Registry", "StaffOnly");
    options.Conventions.AuthorizeFolder("/BeforeClass", "StaffOnly");
    options.Conventions.AuthorizeFolder("/Students", "StaffOnly");
    options.Conventions.AuthorizeFolder("/Certificates", "StaffOnly");
    // Журнал аудита и импорт реестра — администрация (завкафедрой/админ), не физрук/медработник.
    options.Conventions.AuthorizePage("/Journal/Index", "AdminOrHead");
    options.Conventions.AuthorizePage("/Import/Index", "AdminOrHead");
    // Очередь заявок студентов и подтверждение справки — сотрудники кафедры (физрук/завкаф/админ).
    options.Conventions.AuthorizeFolder("/Review", "StaffOnly");
    // Сканы/справки — сотрудники кафедры (физрук обрабатывает сканы и оформляет справки).
    options.Conventions.AuthorizeFolder("/Scans", "StaffOnly");
    // Личный кабинет студента — ТОЛЬКО роль Student (видит свой допуск, не чужие данные).
    options.Conventions.AuthorizePage("/Me/Index", "StudentOnly");
});

// Ролевые политики — логичное разграничение по ролям (минимизация прав).
builder.Services.AddAuthorization(options =>
{
    // Базовый доступ (реестр, «Перед парой», карточки, сканы, проверка) — любой сотрудник кафедры.
    options.AddPolicy("StaffOnly", p => p.RequireRole("Teacher", "HeadOfDepartment", "Admin"));
    // Администрирование (журнал, импорт) — завкафедрой/админ.
    options.AddPolicy("AdminOrHead", p => p.RequireRole("HeadOfDepartment", "Admin"));
    // Личный кабинет — только студент.
    options.AddPolicy("StudentOnly", p => p.RequireRole("Student"));
    // Безопасный дефолт: любая страница без явной политики всё равно требует входа (deny-by-default).
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddInfrastructure(builder.Configuration);

// Фоновая ИИ-проверка загруженных сканов (очередь + воркер) — загрузка возвращается сразу.
builder.Services.AddSingleton<IScanProcessingQueue, ScanProcessingQueue>();
builder.Services.AddHostedService<ScanProcessingBackgroundService>();

// Шифрование спецкатегории ПДн at-rest (P1 MED-A02): ключи персистентны, чтобы расшифровывать после рестарта.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "App_Data", "dpkeys")))
    .SetApplicationName("ReuMedCertificates");

// Лимит размера загружаемого файла (скана справки) — чуть выше прикладного предела.
var maxUpload = builder.Configuration.GetValue<long?>("Scans:MaxUploadBytes") ?? (10L * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload + 512 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUpload + 1024 * 1024);

var app = builder.Build();

// Доверяем X-Forwarded-* только от известного прокси (по умолчанию — loopback); защищает от спуфинга IP/scheme.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Security-заголовки на все ответы: анти-clickjacking, анти-MIME-sniffing, referrer, CSP.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; object-src 'self'; frame-ancestors 'none'; base-uri 'self'";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Применяет миграции и засевает роли/справочники при старте.
await app.Services.SeedIdentityAsync(app.Configuration);

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Корень роутит по роли: студент — в свой кабинет, сотрудник — в реестр.
app.MapGet("/", (HttpContext ctx) =>
    Results.Redirect(ctx.User.IsInRole("Student") ? "/me" : "/registry"));

// Просмотр скана/PDF справки. Доступ сотрудникам (физрук/медработник/админ/завкаф), НЕ студентам.
// Безопасность: серверный MIME, no-store, inline-просмотр под nosniff (анти-XSS), аудит просмотра (РСБ/323-ФЗ).
app.MapGet("/scans/{id:guid}/file", async (
    Guid id, HttpContext http, IScanService scans,
    IApplicationDbContext db, ICurrentUser user, IDateTimeProvider clock, CancellationToken ct) =>
{
    var content = await scans.OpenAsync(id, ct);
    if (content is null) return Results.NotFound();

    // Логируем факт просмотра медскана один раз на открытие (range-догрузки не дублируем).
    if (!http.Request.Headers.ContainsKey("Range"))
    {
        db.AuditLogs.Add(AuditEntryFactory.Create(
            user, clock, nameof(CertificateScan), id, "ScanView", $"Просмотр скана {id:N}"));
        await db.SaveChangesAsync(ct);
    }

    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["Content-Disposition"] = "inline";
    return Results.File(content.Stream, content.ContentType, enableRangeProcessing: true);
}).RequireAuthorization(policy => policy.RequireRole("Teacher", "HeadOfDepartment", "Admin"));

// Студент смотрит ТОЛЬКО свой загруженный файл (проверка владения по AppUser.StudentId).
app.MapGet("/me/scans/{id:guid}/file", async (
    Guid id, HttpContext http, IScanService scans, UserManager<AppUser> users, CancellationToken ct) =>
{
    var user = await users.GetUserAsync(http.User);
    if (user?.StudentId is not { } studentId) return Results.Forbid();

    var detail = await scans.GetAsync(id, ct);
    if (detail is null || detail.StudentId != studentId) return Results.NotFound();

    var content = await scans.OpenAsync(id, ct);
    if (content is null) return Results.NotFound();

    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["Content-Disposition"] = "inline";
    return Results.File(content.Stream, content.ContentType, enableRangeProcessing: true);
}).RequireAuthorization("StudentOnly");

app.MapRazorPages();

app.Run();

// Точка входа доступна интеграционным тестам (WebApplicationFactory<Program>).
public partial class Program;
