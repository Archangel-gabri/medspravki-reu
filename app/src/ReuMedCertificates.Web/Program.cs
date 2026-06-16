using Microsoft.AspNetCore.Http.Features;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Scans;
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
app.MapGet("/scans/{id:guid}/file", async (Guid id, IScanService scans, CancellationToken ct) =>
{
    var content = await scans.OpenAsync(id, ct);
    return content is null
        ? Results.NotFound()
        : Results.File(content.Stream, content.ContentType, enableRangeProcessing: true);
}).RequireAuthorization(policy => policy.RequireRole("Teacher", "HeadOfDepartment", "Admin"));

app.MapRazorPages();

app.Run();

// Точка входа доступна интеграционным тестам (WebApplicationFactory<Program>).
public partial class Program;
