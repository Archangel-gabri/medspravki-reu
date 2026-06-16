using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Roster;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>
/// Источник реестра из 1С через OData-интерфейс (REST). 1С Предприятие отдаёт справочники по адресу
/// вида .../odata/standard.odata/Catalog_Студенты?$format=json (Basic-аутентификация).
/// Маппинг полей и URL — из конфигурации (раздел Roster:OData). Требует реального endpoint 1С.
/// </summary>
public sealed class OneCODataRosterSource : IRosterSource
{
    private readonly HttpClient _http;
    private readonly RosterOptions _options;

    public OneCODataRosterSource(HttpClient http, RosterOptions options)
    {
        _http = http;
        _options = options;

        if (!string.IsNullOrWhiteSpace(_options.OData.Username))
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.OData.Username}:{_options.OData.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public string SourceName => "1С (OData)";

    public async Task<IReadOnlyList<RosterRecord>> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.OData.Url))
            throw new InvalidOperationException("Не задан адрес OData-интерфейса 1С (Roster:OData:Url).");

        var o = _options.OData;
        var json = await _http.GetStringAsync(o.Url, cancellationToken);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(o.ValueProperty, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<RosterRecord>();

        var list = new List<RosterRecord>();
        foreach (var item in arr.EnumerateArray())
        {
            list.Add(new RosterRecord(
                Str(item, o.ExternalIdField),
                Str(item, o.FullNameField) ?? string.Empty,
                Str(item, o.DepartmentField) ?? string.Empty,
                ShortVal(item, o.CourseField),
                Str(item, o.GroupField) ?? string.Empty,
                Str(item, o.TeacherField)));
        }
        return list;
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.ToString() : null;

    private static short ShortVal(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt16(out var n)) return n;
        return short.TryParse(v.ToString(), out var p) ? p : (short)0;
    }
}
