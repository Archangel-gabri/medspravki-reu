namespace ReuMedCertificates.Application.Abstractions;

/// <summary>Прямоугольник выделения на странице скана (доли 0..1 относительно изображения страницы).</summary>
public sealed record RegionRect(double X, double Y, double W, double H);

/// <summary>Отрендеренная страница скана (JPEG для показа) + общее число страниц.</summary>
public sealed record RenderedPage(byte[] Jpeg, int PageCount);

/// <summary>
/// Двухэтапное распознавание (zoom по полям). Препод выделяет на скане область (напр. «дата выдачи»),
/// мы вырезаем её С УВЕЛИЧЕНИЕМ и шлём модели узкий запрос по одному полю — точнее, чем вся страница.
/// </summary>
public interface IRegionRecognizer
{
    /// <summary>Рендерит страницу скана в JPEG (для показа в браузере) + число страниц.</summary>
    Task<RenderedPage> RenderPageAsync(byte[] file, string contentType, int page, CancellationToken cancellationToken = default);

    /// <summary>Число страниц в скане (PDF → реальное, изображение → 1).</summary>
    Task<int> GetPageCountAsync(byte[] file, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Вырезает выделенную область (увеличивает) и распознаёт ОДНО поле по ключу
    /// (issue_date | number | health_group | name | text). Возвращает текст или null.</summary>
    Task<string?> RecognizeRegionAsync(byte[] file, string contentType, int page, RegionRect rect, string fieldKey, CancellationToken cancellationToken = default);
}
