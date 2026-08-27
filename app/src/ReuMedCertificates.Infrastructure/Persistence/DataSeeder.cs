using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReuMedCertificates.Domain.Entities;
using ReuMedCertificates.Domain.Enums;
using ReuMedCertificates.Infrastructure.Identity;

namespace ReuMedCertificates.Infrastructure.Persistence;

/// <summary>Применяет миграции и засевает роли, bootstrap-пользователя и справочники кафедры.</summary>
public static class DataSeeder
{
    public static async Task SeedIdentityAsync(this IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ApplicationDbContext>();

        await db.Database.MigrateAsync();
        await EnsureAuditAppendOnlyAsync(db);

        var roleManager = sp.GetRequiredService<RoleManager<AppRole>>();
        foreach (var role in AppRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new AppRole(role));

        // Роль медработника удалена (2026-06-18): её функции переданы преподавателю.
        // Идемпотентная зачистка для существующих БД — убрать демо-юзера medic и роль MedicalStaff.
        var cleanupUm = sp.GetRequiredService<UserManager<AppUser>>();
        if (await cleanupUm.FindByNameAsync("medic") is { } medicUser)
            await cleanupUm.DeleteAsync(medicUser);
        if (await roleManager.FindByNameAsync("MedicalStaff") is { } medRole)
            await roleManager.DeleteAsync(medRole);

        var bootstrap = configuration.GetSection("BootstrapUser");
        if (bootstrap.GetValue<bool>("Enabled"))
        {
            var userManager = sp.GetRequiredService<UserManager<AppUser>>();
            var login = bootstrap["Login"] ?? "teacher";
            if (await userManager.FindByNameAsync(login) is null)
            {
                var now = DateTime.UtcNow;
                var user = new AppUser
                {
                    UserName = login,
                    Email = $"{login}@rea.ru",
                    EmailConfirmed = true,
                    FullName = bootstrap["FullName"] ?? login,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                // Пароля по умолчанию нет намеренно: литерал в коде переживал любую
                // смену конфига и уезжал в прод вместе со сборкой.
                var bootstrapPassword = bootstrap["Password"];
                if (string.IsNullOrWhiteSpace(bootstrapPassword))
                {
                    throw new InvalidOperationException(
                        "BootstrapUser:Enabled=true, но BootstrapUser:Password не задан. " +
                        "Задайте его через переменную окружения BootstrapUser__Password " +
                        "или user-secrets — в конфиг репозитория пароль не кладём.");
                }

                var result = await userManager.CreateAsync(user, bootstrapPassword);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(user, AppRoles.Admin);
                    await userManager.AddToRoleAsync(user, AppRoles.Teacher);
                }
            }
        }

        await SeedReferenceDataAsync(db);

        if (configuration.GetValue<bool>("SeedDemoData"))
        {
            // Демо-пользователи СТРОГО по одной роли (логичное разграничение прав).
            var userMgr = sp.GetRequiredService<UserManager<AppUser>>();
            await EnsureDemoUserAsync(userMgr, "teacher", "Преподаватель 57", AppRoles.Teacher);
            await EnsureDemoUserAsync(userMgr, "head", "Преподаватель 01", AppRoles.HeadOfDepartment);
            await EnsureDemoUserAsync(userMgr, "admin", "Администратор системы", AppRoles.Admin);

            await SeedDemoDataAsync(db);
            // Демо-студент-пользователь (ФИО как в ЛКС РЭУ — для совпадения ФИО на справке).
            var demoStudent = await db.Students.FirstOrDefaultAsync(s => s.FullName == "Кубрак Вадим Андреевич")
                ?? await db.Students.FirstOrDefaultAsync(s => s.FullName == "Иванов Иван Сергеевич");
            if (demoStudent is not null)
            {
                await EnsureDemoUserAsync(userMgr, "student", "Кубрак Вадим Андреевич", AppRoles.Student);
                var su = await userMgr.FindByNameAsync("student");
                if (su is not null && su.StudentId != demoStudent.Id)
                {
                    su.StudentId = demoStudent.Id;
                    await userMgr.UpdateAsync(su);
                }
            }
            await SeedReviewQueueDemoAsync(db);
            await SeedAuditDemoAsync(db);
            await SeedOnecDemoTableAsync(db);
        }
    }

    /// <summary>
    /// Локальная «1С-подобная» таблица-источник для демонстрации импорта (Инкр. 4).
    /// В проде вместо неё — реальная 1С (OData) или SQL-витрина деканата (см. Roster:* в конфиге).
    /// Сбрасывается при каждом старте — это источник, а не пользовательские данные.
    /// </summary>
    private static async Task SeedOnecDemoTableAsync(ApplicationDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS onec_roster_demo (
                external_id text,
                full_name   text NOT NULL,
                department  text NOT NULL,
                course      smallint NOT NULL,
                group_name  text NOT NULL,
                teacher     text
            );");

        await db.Database.ExecuteSqlRawAsync("DELETE FROM onec_roster_demo;");

        await db.Database.ExecuteSqlRawAsync(@"
            INSERT INTO onec_roster_demo (external_id, full_name, department, course, group_name, teacher) VALUES
            ('OD-1001','Кузьмин Артур Олегович','Высшая школа права',2,'15.30Д-Ю01/24б','Преподаватель 57'),
            ('OD-1002','Соколова Вероника Павловна','Высшая школа финансов',2,'15.25Д-ЭКФ01/24б','Преподаватель 40'),
            ('OD-1003','Иванов Иван Сергеевич','Высшая школа права',2,'15.30Д-Ю01/24б','Преподаватель 57'),
            ('OD-1004','Преподаватель 47','Высшая школа менеджмента',2,'15.26Д-ММО01/24б','Преподаватель 20'),
            ('OD-1005','Преподаватель 18','Высшая школа менеджмента',2,'15.26Д-ММО01/24б','Преподаватель 20');");
    }

    /// <summary>Демо-записи журнала, чтобы раздел «Журнал» был не пустым на старте.</summary>
    private static async Task SeedAuditDemoAsync(ApplicationDbContext db)
    {
        if (await db.AuditLogs.AnyAsync())
            return;

        var now = DateTime.UtcNow;
        db.AuditLogs.AddRange(
            new AuditLog { EntityType = "AppUser", ActionType = "Login", UserNameSnapshot = "teacher", OccurredAt = now.AddMinutes(-42), Description = "Вход в систему" },
            new AuditLog { EntityType = "MedicalCertificate", ActionType = "Create", UserNameSnapshot = "teacher", OccurredAt = now.AddMinutes(-40), Description = "Добавлена справка (ручной ввод)" },
            new AuditLog { EntityType = "MedicalCertificate", ActionType = "Approve", UserNameSnapshot = "teacher", OccurredAt = now.AddMinutes(-20), Description = "Справка подтверждена" });

        await db.SaveChangesAsync();
    }

    /// <summary>Демо для очереди «На проверке»: пара справок, ожидающих подтверждения (имитация загрузки/OCR).</summary>
    private static async Task SeedReviewQueueDemoAsync(ApplicationDbContext db)
    {
        // Идемпотентно: если уже есть справки из источников загрузки/OCR — ничего не делаем.
        if (await db.Certificates.AnyAsync(c => c.Source == DraftSource.Ocr || c.Source == DraftSource.StudentUpload))
            return;

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var pending = new (string Name, DraftSource Source, PhysicalEducationGroup Pg, HealthGroup Hg, string? Restr)[]
        {
            ("Кузнецова Мария Павловна", DraftSource.Ocr,           PhysicalEducationGroup.Preparatory, HealthGroup.II,  "Освобождение от кроссовых дистанций"),
            ("Морозов Артём Сергеевич",  DraftSource.StudentUpload, PhysicalEducationGroup.SpecialA,     HealthGroup.III, "Щадящий режим, без прыжков"),
        };

        foreach (var p in pending)
        {
            var student = await db.Students.FirstOrDefaultAsync(s => s.FullName == p.Name);
            if (student is null)
                continue;

            db.Certificates.Add(new MedicalCertificate
            {
                StudentId = student.Id,
                IssueDate = today.AddDays(-3),
                StartDate = today.AddDays(-3),
                EndDate = today.AddDays(180),
                CertificateNumber = "086/у-" + Random.Shared.Next(1000, 9999),
                MedicalOrganization = "Студенческая поликлиника №1, г. Москва",
                HealthGroup = p.Hg,
                PhysicalGroup = p.Pg,
                Restrictions = p.Restr,
                Source = p.Source,
                VerificationStatus = VerificationStatus.NeedsReview,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedReferenceDataAsync(ApplicationDbContext db)
    {
        var now = DateTime.UtcNow;

        if (!await db.Departments.AnyAsync())
        {
            db.Departments.AddRange(DepartmentNames.Select(name => new Department
            {
                Name = name,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            }));
        }

        if (!await db.Teachers.AnyAsync())
        {
            db.Teachers.AddRange(TeacherSeed.Select(t => new Teacher
            {
                FullName = t.Name,
                Position = t.Position,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            }));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Демо-данные для разработки/демо: группы, студенты, справки с разными статусами.</summary>
    private static async Task SeedDemoDataAsync(ApplicationDbContext db)
    {
        if (await db.Students.AnyAsync())
            return;

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var law = await db.Departments.FirstAsync(d => d.Name.Contains("права"));
        var finance = await db.Departments.FirstAsync(d => d.Name.Contains("финансов"));

        var petrova = await db.Teachers.FirstAsync(t => t.FullName.StartsWith("Петрова"));
        var loginov = await db.Teachers.FirstAsync(t => t.FullName.StartsWith("Логинов"));

        // Реальные коды групп РЭУ (источник: rasp.rea.ru): право=15.30, финансы=15.25.
        var groupLaw = new StudyGroup { Name = "15.30Д-Ю01/24б", Course = 2, DepartmentId = law.Id, TeacherId = petrova.Id, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var groupFin = new StudyGroup { Name = "15.25Д-ЭКФ01/24б", Course = 2, DepartmentId = finance.Id, TeacherId = loginov.Id, IsActive = true, CreatedAt = now, UpdatedAt = now };
        db.StudyGroups.AddRange(groupLaw, groupFin);

        // (ФИО, группа, преподаватель, кафедра, справка?)
        var specs = new (string Name, StudyGroup Group, Teacher Teacher, Department Dept,
            (int StartOffset, int EndOffset, PhysicalEducationGroup Pg, HealthGroup Hg, string? Restr)? Cert)[]
        {
            ("Иванов Иван Сергеевич",       groupLaw, petrova, law,     (-30, 90, PhysicalEducationGroup.Basic,       HealthGroup.I,   null)),
            ("Иванова Алина Романовна",     groupLaw, petrova, law,     (-60,  5, PhysicalEducationGroup.Preparatory, HealthGroup.II,  "Освобождение от силовых упражнений")),
            ("Сидоров Дмитрий Николаевич",  groupLaw, petrova, law,     (-90,-10, PhysicalEducationGroup.Basic,       HealthGroup.I,   null)),
            ("Кузнецова Мария Павловна",    groupLaw, petrova, law,      null),
            ("Смирнов Олег Викторович",     groupLaw, petrova, law,     (  5, 200, PhysicalEducationGroup.Basic,       HealthGroup.I,   null)),
            ("Фёдорова Анна Дмитриевна",    groupLaw, petrova, law,     (-15, 350, PhysicalEducationGroup.Exempt,      HealthGroup.IV,  "Полное освобождение")),
            ("Петров Никита Андреевич",     groupFin, loginov, finance, (-20, 120, PhysicalEducationGroup.Basic,       HealthGroup.I,   null)),
            ("Васильева Екатерина Игоревна",groupFin, loginov, finance, (-40,  3, PhysicalEducationGroup.SpecialA,    HealthGroup.III, "Щадящий режим, без прыжков")),
            ("Морозов Артём Сергеевич",     groupFin, loginov, finance,  null),
            ("Новикова Ольга Алексеевна",   groupFin, loginov, finance, (-70,-25, PhysicalEducationGroup.Preparatory, HealthGroup.II,  "Освобождение от бега")),
            ("Алексеев Павел Романович",    groupFin, loginov, finance, (-10, 360, PhysicalEducationGroup.Basic,       HealthGroup.I,   null)),
            ("Зайцева Дарья Максимовна",    groupFin, loginov, finance, (-50,  20, PhysicalEducationGroup.SpecialB,    HealthGroup.III, "Только ЛФК")),
        };

        foreach (var s in specs)
        {
            var student = new Student
            {
                FullName = s.Name,
                NormalizedFullName = Student.Normalize(s.Name),
                DepartmentId = s.Dept.Id,
                Course = s.Group.Course,
                StudyGroupId = s.Group.Id,
                TeacherId = s.Teacher.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Students.Add(student);

            if (s.Cert is { } c)
            {
                db.Certificates.Add(new MedicalCertificate
                {
                    StudentId = student.Id,
                    Student = student,
                    IssueDate = today.AddDays(c.StartOffset),
                    StartDate = today.AddDays(c.StartOffset),
                    EndDate = today.AddDays(c.EndOffset),
                    CertificateNumber = "086/у-" + Random.Shared.Next(1000, 9999),
                    MedicalOrganization = "Городская поликлиника №5, г. Москва",
                    HealthGroup = c.Hg,
                    PhysicalGroup = c.Pg,
                    Restrictions = c.Restr,
                    Source = DraftSource.Manual,
                    VerificationStatus = VerificationStatus.Verified,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>P1 (РСБ-целостность): делает audit_logs append-only на уровне БД — блокирует UPDATE/DELETE
    /// триггером (срабатывает даже для суперпользователя). Идемпотентно.</summary>
    private static async Task EnsureAuditAppendOnlyAsync(ApplicationDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE OR REPLACE FUNCTION audit_logs_no_modify() RETURNS trigger AS $func$
            BEGIN
                RAISE EXCEPTION 'audit_logs — журнал только для добавления (INSERT-only)';
            END;
            $func$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_audit_logs_no_modify ON audit_logs;
            CREATE TRIGGER trg_audit_logs_no_modify
                BEFORE UPDATE OR DELETE ON audit_logs
                FOR EACH ROW EXECUTE FUNCTION audit_logs_no_modify();");

        // RecognitionJson хранит шифртекст (P1 MED-A02) — тип колонки text, а не jsonb. Идемпотентно.
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE certificate_scans ALTER COLUMN ""RecognitionJson"" TYPE text USING ""RecognitionJson""::text;");

        // Связь аккаунта со студентом (роль Student видит свой допуск). Идемпотентно.
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE ""AspNetUsers"" ADD COLUMN IF NOT EXISTS ""StudentId"" uuid;");

        // Отклонение заявки-скана (v2): причина + дата. Идемпотентно.
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE certificate_scans ADD COLUMN IF NOT EXISTS ""RejectionReason"" text;");
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE certificate_scans ADD COLUMN IF NOT EXISTS ""RejectedAt"" timestamptz;");

        // Срок действия, указанный студентом при загрузке (v2). Идемпотентно.
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE certificate_scans ADD COLUMN IF NOT EXISTS ""ProposedStartDate"" date;");
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE certificate_scans ADD COLUMN IF NOT EXISTS ""ProposedEndDate"" date;");

        // Тип справки + итог авто-проверки ИИ (v2). Идемпотентно.
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE medical_certificates ADD COLUMN IF NOT EXISTS ""Type"" integer NOT NULL DEFAULT 0;");
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE certificate_scans ADD COLUMN IF NOT EXISTS ""AiNotes"" text;");

        // Медвердикт «допущен/не допущен» (v2): не допущен — валидная справка-недопуск. Идемпотентно.
        await db.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE medical_certificates ADD COLUMN IF NOT EXISTS ""Admitted"" boolean NOT NULL DEFAULT true;");
    }

    /// <summary>Создаёт демо-пользователя (если нет) и приводит его роли РОВНО к одной нужной (минимизация прав).</summary>
    private static async Task EnsureDemoUserAsync(UserManager<AppUser> um, string login, string fullName, string role)
    {
        var user = await um.FindByNameAsync(login);
        if (user is null)
        {
            var now = DateTime.UtcNow;
            user = new AppUser
            {
                UserName = login, Email = $"{login}@rea.ru", EmailConfirmed = true,
                FullName = fullName, IsActive = true, CreatedAt = now, UpdatedAt = now
            };
            // Демо-пользователи заводятся только под SeedDemoData; пароль берётся
            // из окружения, чтобы в репозитории не лежало ничего, чем можно войти.
            var demoPassword = Environment.GetEnvironmentVariable("DEMO_USER_PASSWORD");
            if (string.IsNullOrWhiteSpace(demoPassword)) return;
            if (!(await um.CreateAsync(user, demoPassword)).Succeeded) return;
        }
        // Держим ФИО актуальным (показывается в шапке как в ЛК РЭУ).
        if (user.FullName != fullName)
        {
            user.FullName = fullName;
            user.UpdatedAt = DateTime.UtcNow;
            await um.UpdateAsync(user);
        }
        var current = await um.GetRolesAsync(user);
        var extra = current.Where(r => r != role).ToList();
        if (extra.Count > 0) await um.RemoveFromRolesAsync(user, extra);
        if (!current.Contains(role)) await um.AddToRoleAsync(user, role);
    }

    // Реальные подразделения кафедры (с фото-справочника «Расписание»).
    private static readonly string[] DepartmentNames =
    {
        "Высшая инженерная школа «Новые материалы и технологии»",
        "Высшая школа кибертехнологий, математики и статистики",
        "Высшая школа креативных индустрий",
        "Высшая школа менеджмента",
        "Высшая школа права",
        "Высшая школа социально-гуманитарных наук",
        "Высшая школа финансов",
        "Высшая школа экономики и бизнеса",
        "Факультет «Плехановская школа бизнеса \"Интеграл\"»"
    };

    // Реальный преподавательский состав кафедры физического воспитания РЭУ
    // (источник: rasp.rea.ru, 76 преподавателей — проверено 2026-06-16).
    private static readonly (string Name, string Position)[] TeacherSeed =
    {
        ("Преподаватель 01", "Профессор, зав. кафедрой"),
        ("Преподаватель 02", "Преподаватель"),
        ("Преподаватель 03", "Преподаватель"),
        ("Преподаватель 04", "Преподаватель"),
        ("Преподаватель 05", "Преподаватель"),
        ("Преподаватель 06", "Преподаватель"),
        ("Преподаватель 07", "Преподаватель"),
        ("Преподаватель 08", "Доцент"),
        ("Преподаватель 09", "Преподаватель"),
        ("Преподаватель 10", "Преподаватель"),
        ("Преподаватель 11", "Преподаватель"),
        ("Преподаватель 12", "Преподаватель"),
        ("Преподаватель 13", "Преподаватель"),
        ("Преподаватель 14", "Преподаватель"),
        ("Преподаватель 15", "Преподаватель"),
        ("Преподаватель 16", "Преподаватель"),
        ("Преподаватель 17", "Преподаватель"),
        ("Преподаватель 19", "Преподаватель"),
        ("Преподаватель 20", "Доцент"),
        ("Преподаватель 21", "Преподаватель"),
        ("Преподаватель 22", "Преподаватель"),
        ("Преподаватель 23", "Преподаватель"),
        ("Преподаватель 24", "Преподаватель"),
        ("Преподаватель 25", "Преподаватель"),
        ("Преподаватель 26", "Преподаватель"),
        ("Преподаватель 27", "Преподаватель"),
        ("Преподаватель 28", "Преподаватель"),
        ("Преподаватель 29", "Преподаватель"),
        ("Преподаватель 30", "Доцент"),
        ("Преподаватель 31", "Преподаватель"),
        ("Преподаватель 32", "Преподаватель"),
        ("Преподаватель 33", "Преподаватель"),
        ("Преподаватель 34", "Преподаватель"),
        ("Преподаватель 35", "Преподаватель"),
        ("Преподаватель 36", "Преподаватель"),
        ("Преподаватель 37", "Преподаватель"),
        ("Преподаватель 38", "Преподаватель"),
        ("Преподаватель 39", "Преподаватель"),
        ("Преподаватель 40", "Доцент"),
        ("Преподаватель 41", "Доцент"),
        ("Преподаватель 42", "Преподаватель"),
        ("Преподаватель 43", "Преподаватель"),
        ("Преподаватель 44", "Преподаватель"),
        ("Преподаватель 45", "Доцент"),
        ("Преподаватель 46", "Преподаватель"),
        ("Преподаватель 48", "Преподаватель"),
        ("Преподаватель 49", "Преподаватель"),
        ("Преподаватель 50", "Преподаватель"),
        ("Преподаватель 51", "Доцент"),
        ("Преподаватель 52", "Преподаватель"),
        ("Преподаватель 53", "Преподаватель"),
        ("Преподаватель 54", "Преподаватель"),
        ("Преподаватель 55", "Преподаватель"),
        ("Преподаватель 56", "Преподаватель"),
        ("Преподаватель 57", "Доцент"),
        ("Преподаватель 58", "Преподаватель"),
        ("Преподаватель 59", "Преподаватель"),
        ("Преподаватель 60", "Преподаватель"),
        ("Преподаватель 61", "Преподаватель"),
        ("Преподаватель 62", "Преподаватель"),
        ("Преподаватель 63", "Доцент"),
        ("Преподаватель 64", "Доцент"),
        ("Преподаватель 66", "Преподаватель"),
        ("Преподаватель 67", "Доцент"),
        ("Преподаватель 68", "Преподаватель"),
        ("Преподаватель 69", "Преподаватель"),
        ("Преподаватель 70", "Преподаватель"),
        ("Преподаватель 71", "Преподаватель"),
        ("Преподаватель 72", "Преподаватель"),
        ("Фарзалиев Джавид Аллахверди оглы", "Преподаватель"),
        ("Преподаватель 73", "Профессор"),
        ("Преподаватель 74", "Преподаватель"),
        ("Преподаватель 75", "Преподаватель"),
        ("Преподаватель 76", "Преподаватель"),
        ("Преподаватель 77", "Преподаватель"),
        ("Преподаватель 78", "Преподаватель")
    };
}
