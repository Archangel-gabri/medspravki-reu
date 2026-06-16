using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReuMedCertificates.Infrastructure.Persistence;

/// <summary>Фабрика для design-time операций EF Core (dotnet ef migrations) без запуска Web-хоста.</summary>
public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("REU_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=reu_med_certificates;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}
