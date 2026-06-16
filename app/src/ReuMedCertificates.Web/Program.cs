using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
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
    // Всё под авторизацией; страница входа и ошибки — анонимны.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Auth/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services.AddInfrastructure(builder.Configuration);

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

app.MapGet("/", () => Results.Redirect("/registry"));

// Просмотр скана/PDF справки. Доступ только сотрудникам (роли), НЕ студентам — разграничение 152-ФЗ.
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

app.MapRazorPages();

app.Run();

// Точка входа доступна интеграционным тестам (WebApplicationFactory<Program>).
public partial class Program;
