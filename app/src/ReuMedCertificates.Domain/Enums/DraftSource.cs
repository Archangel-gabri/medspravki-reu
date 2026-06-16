namespace ReuMedCertificates.Domain.Enums;

/// <summary>
/// Источник появления черновика справки. Все источники сходятся в один кейс
/// (<see cref="VerificationStatus"/>) — это снимает переписывание архитектуры при переходе v1→v2.
/// </summary>
public enum DraftSource
{
    /// <summary>Ручной ввод преподавателем (единственный источник в MVP / ТЗ v1).</summary>
    Manual = 0,

    /// <summary>Импорт из Excel деканата.</summary>
    ExcelImport = 1,

    /// <summary>Загрузка студентом из личного кабинета (v2, при сетевом доступе).</summary>
    StudentUpload = 2,

    /// <summary>Предзаполнение локальным OCR/ИИ (v2, поздняя фаза).</summary>
    Ocr = 3
}
