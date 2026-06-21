using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Abstractions;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Scans;
using ReuMedCertificates.Application.Students;
using ReuMedCertificates.Infrastructure.Identity;

namespace ReuMedCertificates.Web.Pages.Me;

/// <summary>Личный кабинет студента: показывает ТОЛЬКО свой допуск к физкультуре
/// и позволяет загрузить скан/фото медсправки на проверку (v2). Аккаунт привязан к карточке
/// студента через AppUser.StudentId — грузим всегда за себя, ID из формы не принимаем.</summary>
public class IndexModel : PageModel
{
    private readonly UserManager<AppUser> _users;
    private readonly IStudentService _students;
    private readonly IScanService _scans;
    private readonly ScanStorageOptions _scanOptions;
    private readonly IDocumentAssembler _assembler;

    public IndexModel(UserManager<AppUser> users, IStudentService students,
        IScanService scans, ScanStorageOptions scanOptions, IDocumentAssembler assembler)
    {
        _users = users;
        _students = students;
        _scans = scans;
        _scanOptions = scanOptions;
        _assembler = assembler;
    }

    public StudentDetail? Student { get; private set; }

    /// <summary>Аккаунт студента не привязан к карточке (StudentId == null) — показать подсказку.</summary>
    public bool NotLinked { get; private set; }

    /// <summary>Заявки студента (загруженные сканы) — без медицинского содержимого, только факт и дата.</summary>
    public IReadOnlyList<ScanListItem> MyScans { get; private set; } = Array.Empty<ScanListItem>();

    public string? Message { get; private set; }
    public string? ErrorMessage { get; private set; }

    public string MaxUploadMb => (_scanOptions.MaxUploadBytes / 1024.0 / 1024.0).ToString("0.#");

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(List<IFormFile>? files, CancellationToken cancellationToken)
    {
        var studentId = await ResolveStudentIdAsync();
        if (studentId is null)
        {
            NotLinked = true;
            return Page();
        }

        files = files?.Where(f => f is { Length: > 0 }).ToList() ?? new List<IFormFile>();

        async Task<IActionResult> Fail(string msg)
        {
            ErrorMessage = msg;
            await LoadAsync(cancellationToken);
            return Page();
        }

        if (files.Count == 0) return await Fail("Файл не выбран.");
        if (files.Count > 5) return await Fail("Слишком много файлов (максимум 5).");

        // Валидация каждого файла (размер / тип / реальная сигнатура).
        foreach (var f in files)
        {
            if (f.Length > _scanOptions.MaxUploadBytes)
                return await Fail($"Файл «{f.FileName}» больше {MaxUploadMb} МБ.");
            if (!_scanOptions.AllowedContentTypes.Contains(f.ContentType))
                return await Fail($"Недопустимый тип файла ({f.ContentType}). Разрешено: PDF, JPG, PNG.");
            if (!await HasAllowedSignatureAsync(f, cancellationToken))
                return await Fail($"Файл «{f.FileName}» не прошёл проверку сигнатуры — это не PDF/JPG/PNG.");
        }

        var hasPdf = files.Any(f => f.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase));
        if (files.Count > 1 && hasPdf)
            return await Fail("Выберите либо один PDF, либо несколько фотографий — не вперемешку.");

        if (files.Count == 1)
        {
            var f = files[0];
            await using var stream = f.OpenReadStream();
            await _scans.UploadAsync(
                new ScanUploadRequest(studentId.Value, stream, Path.GetFileName(f.FileName), f.ContentType),
                cancellationToken);
        }
        else
        {
            // Несколько фото (лицо + оборот) → склеиваем в один PDF, чтобы ИИ видел все стороны.
            var images = new List<byte[]>();
            foreach (var f in files)
            {
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, cancellationToken);
                images.Add(ms.ToArray());
            }
            var pdf = await _assembler.ImagesToPdfAsync(images, cancellationToken);
            await using var pdfStream = new MemoryStream(pdf);
            await _scans.UploadAsync(
                new ScanUploadRequest(studentId.Value, pdfStream, "Справка.pdf", "application/pdf"),
                cancellationToken);
        }

        Message = "Справка отправлена на проверку. ИИ распознаёт её, результат появится в таблице ниже.";
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<Guid?> ResolveStudentIdAsync()
    {
        var user = await _users.GetUserAsync(User);
        return user?.StudentId;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var studentId = await ResolveStudentIdAsync();
        if (studentId is null)
        {
            NotLinked = true;
            return;
        }

        Student = await _students.GetDetailAsync(studentId.Value, cancellationToken);
        if (Student is null)
        {
            NotLinked = true;
            return;
        }

        MyScans = await _scans.ListForStudentAsync(studentId.Value, cancellationToken);
    }

    // Проверка реальной сигнатуры (magic bytes), а не клиентского MIME — анти-XSS на загрузке.
    private static async Task<bool> HasAllowedSignatureAsync(IFormFile file, CancellationToken ct)
    {
        var buf = new byte[8];
        await using var s = file.OpenReadStream();
        var n = await s.ReadAsync(buf.AsMemory(0, 8), ct);
        bool pdf = n >= 4 && buf[0] == 0x25 && buf[1] == 0x50 && buf[2] == 0x44 && buf[3] == 0x46; // %PDF
        bool png = n >= 8 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47; // PNG
        bool jpg = n >= 3 && buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF;                    // JPEG
        return pdf || png || jpg;
    }
}
