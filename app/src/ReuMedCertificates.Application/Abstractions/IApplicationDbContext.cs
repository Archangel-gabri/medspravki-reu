using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Domain.Entities;

namespace ReuMedCertificates.Application.Abstractions;

/// <summary>Контракт доступа к данным для Application-слоя (реализуется ApplicationDbContext в Infrastructure).</summary>
public interface IApplicationDbContext
{
    DbSet<Department> Departments { get; }
    DbSet<Teacher> Teachers { get; }
    DbSet<StudyGroup> StudyGroups { get; }
    DbSet<Student> Students { get; }
    DbSet<MedicalCertificate> Certificates { get; }
    DbSet<CertificateScan> Scans { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<ImportBatch> ImportBatches { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
