using FluentAssertions;
using ReuMedCertificates.Application.Scans;
using Xunit;

namespace ReuMedCertificates.UnitTests;

/// <summary>
/// Тесты доменных правил авто-ревью — те «шаги человека», что не зависят от мощности модели.
/// Кейсы взяты из реальных ошибок распознавания на живых справках.
/// Даты и фамилии обезличены: это медицинские данные студентов.
/// </summary>
public class RecognitionRulesTests
{
    // ── Печать ИЛИ электронная подпись (случай A: бассейн с ЭЦП без физпечати) ──
    [Theory]
    [InlineData(true, null, true)]   // обычная справка с печатью
    [InlineData(false, true, true)]  // е-подписанная: печати нет, но есть ЭЦП → ОК
    [InlineData(null, true, true)]
    [InlineData(false, false, false)] // ни печати, ни подписи → брак
    [InlineData(null, null, false)]
    public void StampOrSignaturePresent_works(bool? stamp, bool? esign, bool expected) =>
        RecognitionRules.StampOrSignaturePresent(stamp, esign).Should().Be(expected);

    // ── Дата выдачи ≠ дата рождения (случай B: распознанная дата оказалась рождением) ──
    [Fact]
    public void ResolveIssueDate_drops_birthdate_mistaken_as_issue()
    {
        var birth = new DateOnly(2006, 3, 2);
        var issueEqualsBirth = new DateOnly(2006, 3, 2);
        RecognitionRules.ResolveIssueDate(issueEqualsBirth, birth).Should().BeNull();
    }

    [Fact]
    public void ResolveIssueDate_keeps_valid_issue_far_from_birth()
    {
        var birth = new DateOnly(2006, 5, 21);          // случай C
        var issue = new DateOnly(2025, 11, 22);
        RecognitionRules.ResolveIssueDate(issue, birth).Should().Be(issue);
    }

    [Fact]
    public void ResolveIssueDate_passthrough_when_no_birth_and_null()
    {
        RecognitionRules.ResolveIssueDate(null, null).Should().BeNull();
        var issue = new DateOnly(2025, 10, 16);
        RecognitionRules.ResolveIssueDate(issue, null).Should().Be(issue);
    }

    // ── Срок обязателен только для допуска ──
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)] // «не допущен» действует без срока
    public void ValidityRequired_only_for_admitted(bool admitted, bool required) =>
        RecognitionRules.ValidityRequired(admitted).Should().Be(required);

    // ── Правдоподобность даты выдачи (мягкий флаг «проверьте год»: 2023 вместо 2025) ──
    [Fact]
    public void IssueDatePlausible_flags_too_old_and_future()
    {
        var today = new DateOnly(2026, 6, 29);
        RecognitionRules.IssueDatePlausible(null, today).Should().BeTrue();              // нет даты — отдельная ветка
        RecognitionRules.IssueDatePlausible(new DateOnly(2025, 4, 30), today).Should().BeTrue();  // свежая — ОК
        RecognitionRules.IssueDatePlausible(new DateOnly(2023, 11, 25), today).Should().BeFalse(); // старее 18 мес → проверить год
        RecognitionRules.IssueDatePlausible(new DateOnly(2027, 1, 1), today).Should().BeFalse();   // будущее → подозрительно
    }
}
