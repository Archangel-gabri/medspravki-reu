using System.Data.Common;
using Npgsql;
using ReuMedCertificates.Application.Common;
using ReuMedCertificates.Application.Roster;

namespace ReuMedCertificates.Infrastructure.Services;

/// <summary>
/// Источник реестра из внешней SQL-базы (PostgreSQL/Npgsql). Подходит для 1С на SQL-СУБД
/// или реплики/витрины деканата. Запрос и строка подключения — из конфигурации (раздел Roster:Sql).
/// </summary>
public sealed class SqlRosterSource : IRosterSource
{
    private readonly RosterOptions _options;

    public SqlRosterSource(RosterOptions options) => _options = options;

    public string SourceName => $"SQL ({_options.Sql.Description})";

    public async Task<IReadOnlyList<RosterRecord>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RosterRecord>();

        await using var conn = new NpgsqlConnection(_options.Sql.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(_options.Sql.Query, conn);
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RosterRecord(
                GetString(reader, "external_id"),
                GetString(reader, "full_name") ?? string.Empty,
                GetString(reader, "department") ?? string.Empty,
                ToShort(reader["course"]),
                GetString(reader, "group_name") ?? string.Empty,
                GetString(reader, "teacher")));
        }

        return result;
    }

    private static string? GetString(DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
    }

    private static short ToShort(object? value) =>
        value is null || value is DBNull ? (short)0 : Convert.ToInt16(value);
}
