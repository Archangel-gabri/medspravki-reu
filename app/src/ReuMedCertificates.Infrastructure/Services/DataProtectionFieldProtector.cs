using Microsoft.AspNetCore.DataProtection;
using ReuMedCertificates.Application.Abstractions;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>Шифрование строковых полей через ASP.NET DataProtection (AES, ключи персистентны).</summary>
public sealed class DataProtectionFieldProtector : IFieldProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionFieldProtector(IDataProtectionProvider dataProtection)
        => _protector = dataProtection.CreateProtector("ReuMedCertificates.Fields.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string value)
    {
        try { return _protector.Unprotect(value); }
        catch { return value; } // незашифрованное/legacy — не валим чтение
    }
}
