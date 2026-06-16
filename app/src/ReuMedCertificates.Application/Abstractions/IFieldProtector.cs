namespace ReuMedCertificates.Application.Abstractions;

/// <summary>
/// Шифрование чувствительных строковых полей (спецкатегория ПДн) at-rest, P1 MED-A02.
/// Реализация в Infrastructure — DataProtection/AES. Application не зависит от ASP.NET.
/// </summary>
public interface IFieldProtector
{
    string Protect(string plaintext);

    /// <summary>Расшифровать; если значение не зашифровано (legacy/повреждено) — вернуть как есть, не падая.</summary>
    string Unprotect(string value);
}
