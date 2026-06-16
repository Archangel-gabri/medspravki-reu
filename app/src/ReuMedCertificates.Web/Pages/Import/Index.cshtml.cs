using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReuMedCertificates.Application.Roster;

namespace ReuMedCertificates.Web.Pages.Import;

public class IndexModel : PageModel
{
    private readonly IRosterImportService _import;

    public IndexModel(IRosterImportService import) => _import = import;

    public RosterPreview? Preview { get; private set; }
    public string? ErrorMessage { get; private set; }

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Preview = await _import.PreviewAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Не удалось получить данные из источника: " + ex.Message;
        }
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var r = await _import.ImportAsync(cancellationToken);
            Message = $"Импорт завершён: создано {r.Created}, пропущено {r.Skipped}, ошибок {r.Errors}, новых групп {r.NewGroups}.";
        }
        catch (Exception ex)
        {
            Message = "Ошибка импорта: " + ex.Message;
        }
        return RedirectToPage();
    }
}
