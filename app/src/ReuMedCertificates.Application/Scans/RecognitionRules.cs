namespace ReuMedCertificates.Application.Scans;

/// <summary>
/// Детерминированные доменные правила авто-ревью справок — те «шаги человека», что НЕ зависят
/// от мощности модели: дисамбигуация даты выдачи/рождения, «печать ИЛИ электронная подпись»,
/// нужен ли срок, правдоподобность даты. Вынесено отдельно ради чистых юнит-тестов и переноса
/// логики на любую (в т.ч. слабую локальную) модель: модель только «видит», правила — здесь.
/// </summary>
public static class RecognitionRules
{
    /// <summary>
    /// Студенту минимум ~15 лет: дата выдачи валидной справки не может лежать в пределах 10 лет
    /// от даты рождения. Если «дата выдачи» так близка к рождению — модель спутала поля (баг 086/у,
    /// где дата рождения стоит сразу после ФИО).
    /// </summary>
    public const int MinYearsBetweenBirthAndIssue = 10;

    /// <summary>Печать ИЛИ электронная подпись. У е-подписанных справок физической печати нет — это норма.</summary>
    public static bool StampOrSignaturePresent(bool? hasStamp, bool? electronicSignature) =>
        hasStamp == true || electronicSignature == true;

    /// <summary>
    /// Возвращает надёжную дату выдачи или null, отбрасывая случай, когда модель приняла за дату
    /// выдачи дату рождения (issue ≈ birth).
    /// </summary>
    public static DateOnly? ResolveIssueDate(DateOnly? issueDate, DateOnly? birthDate)
    {
        if (issueDate is null) return null;
        if (birthDate is null) return issueDate;
        if (issueDate.Value < birthDate.Value.AddYears(MinYearsBetweenBirthAndIssue)) return null;
        return issueDate;
    }

    /// <summary>Срок действия обязателен только для справки-ДОПУСКА. Для «не допущен» — не требуется.</summary>
    public static bool ValidityRequired(bool admitted) => admitted;

    /// <summary>
    /// Дата выдачи правдоподобна для свежей загрузки: не в будущем и не старше maxAgeMonths.
    /// Это МЯГКИЙ сигнал «проверьте дату» (частая ошибка — год: 2023 вместо 2025), а не жёсткий отказ.
    /// </summary>
    public static bool IssueDatePlausible(DateOnly? issueDate, DateOnly today, int maxAgeMonths = 18)
    {
        if (issueDate is null) return true;
        if (issueDate.Value > today) return false;
        return issueDate.Value >= today.AddMonths(-maxAgeMonths);
    }
}
