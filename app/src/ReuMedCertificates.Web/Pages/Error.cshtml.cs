using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReuMedCertificates.Web.Pages;

[AllowAnonymous]
public class ErrorModel : PageModel
{
    public int? StatusCode { get; set; }

    public void OnGet(int? code = null) => StatusCode = code;
}
