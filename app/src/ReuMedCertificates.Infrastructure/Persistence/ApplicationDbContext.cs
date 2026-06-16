using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Infrastructure.Identity;

namespace ReuMedCertificates.Infrastructure.Persistence;

public class ApplicationDbContext
    : IdentityDbContext<AppUser, AppRole, Guid>, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<StudyGroup> StudyGroups => Set<StudyGroup>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<MedicalCertificate> Certificates => Set<MedicalCertificate>();
    public DbSet<CertificateScan> Scans => Set<CertificateScan>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Триграммный поиск по ФИО.
        builder.HasPostgresExtension("pg_trgm");

        builder.Entity<Department>(e =>
        {
            e.ToTable("departments");
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.Code).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        builder.Entity<Teacher>(e =>
        {
            e.ToTable("teachers");
            e.Property(x => x.FullName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ShortName).HasMaxLength(255);
            e.Property(x => x.Position).HasMaxLength(255);
            e.HasIndex(x => x.FullName);
        });

        builder.Entity<StudyGroup>(e =>
        {
            e.ToTable("study_groups");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Department).WithMany(d => d.Groups)
                .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany(t => t.Groups)
                .HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.Name, x.DepartmentId }).IsUnique();
            e.HasIndex(x => x.TeacherId);
        });

        builder.Entity<Student>(e =>
        {
            e.ToTable("students");
            e.Property(x => x.FullName).HasMaxLength(255).IsRequired();
            e.Property(x => x.NormalizedFullName).HasMaxLength(255).IsRequired();
            e.HasOne(x => x.Department).WithMany()
                .HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StudyGroup).WithMany(g => g.Students)
                .HasForeignKey(x => x.StudyGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany(t => t.Students)
                .HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.SetNull);

            // GIN trgm индекс для быстрого частичного поиска по ФИО.
            e.HasIndex(x => x.NormalizedFullName)
                .HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(x => x.StudyGroupId);
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.TeacherId);
            e.HasIndex(x => new { x.DepartmentId, x.Course, x.StudyGroupId });

            // Optimistic concurrency через системный столбец xmin (PostgreSQL).
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        builder.Entity<MedicalCertificate>(e =>
        {
            e.ToTable("medical_certificates");
            e.Property(x => x.CertificateNumber).HasMaxLength(100);
            e.Property(x => x.MedicalOrganization).HasMaxLength(255);
            e.Property(x => x.RejectionReason).HasMaxLength(500);
            e.HasOne(x => x.Student).WithMany(s => s.Certificates)
                .HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.StudentId);
            e.HasIndex(x => x.StartDate);
            e.HasIndex(x => x.EndDate);
            e.HasIndex(x => x.IsDeleted);
            e.HasIndex(x => new { x.StudentId, x.StartDate, x.EndDate });

            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        builder.Entity<CertificateScan>(e =>
        {
            e.ToTable("certificate_scans");
            e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(100).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.RecognitionStatus).HasMaxLength(20);
            e.Property(x => x.RecognitionModel).HasMaxLength(100);
            e.Property(x => x.RecognitionJson).HasColumnType("text"); // text, т.к. хранится шифртекст (P1 MED-A02)
            e.HasOne(x => x.Student).WithMany()
                .HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Certificate).WithMany()
                .HasForeignKey(x => x.CertificateId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.StudentId);
        });

        builder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActionType).HasMaxLength(50).IsRequired();
            e.Property(x => x.UserNameSnapshot).HasMaxLength(255);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.BeforeJson).HasColumnType("jsonb");
            e.Property(x => x.AfterJson).HasColumnType("jsonb");
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        builder.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches");
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50);
            e.Property(x => x.ReportJson).HasColumnType("jsonb");
        });
    }
}
