using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReuMedCertificates.Web.Pages;

[AllowAnonymous]
public class ErrorModel : PageModel
{
    // `new` намеренно: свойство перекрывает PageModel.StatusCode(int) — это
    // код ошибки для показа на странице, а не хелпер, возвращающий результат.
    public new int? StatusCode { get; set; }

    public void OnGet(int? code = null) => StatusCode = code;
}
