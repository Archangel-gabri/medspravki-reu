namespace ReuMedCertificates.Application.Abstractions;

/// <summary>Текущий аутентифицированный пользователь (для аудита и проставления авторства).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string UserName { get; }
    bool IsAuthenticated { get; }

    /// <summary>IP-адрес запроса (для журнала безопасности / РСБ). null вне HTTP-контекста.</summary>
    string? IpAddress { get; }
}
