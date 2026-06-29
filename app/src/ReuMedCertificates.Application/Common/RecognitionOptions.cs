namespace ReuMedCertificates.Application.Common;

/// <summary>Настройки ИИ-распознавания справок (план §3, §5). Дефолт — ручной режим без ИИ.</summary>
public sealed class RecognitionOptions
{
    public const string SectionName = "Recognition";

    /// <summary>Manual (без ИИ, дефолт) | LocalOllama (локальный VLM через Ollama).</summary>
    public string Provider { get; set; } = "Manual";

    /// <summary>URL Ollama (в периметре РЭУ / на ПК с GPU). Никаких внешних облаков для медданных.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Vision-модель (напр. qwen2.5vl:7b).</summary>
    public string VisionModel { get; set; } = "qwen2.5vl:7b";

    /// <summary>Таймаут запроса к модели, сек.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>DPI рендера PDF→изображение.</summary>
    public int PdfRenderDpi { get; set; } = 200;

    /// <summary>Двухэтапное авто-распознавание: после общего прохода модель сама перечитывает ключевые поля
    /// (дата выдачи/номер/группа) фокус-запросами (точнее на рукописи).</summary>
    public bool TwoStage { get; set; } = true;

    /// <summary>Сколько раз перечитывать дату выдачи для голосования (self-consistency): совпало большинство —
    /// уверенно; разошлось — поле помечается «проверьте». 1 = без голосования.</summary>
    public int VoteCount { get; set; } = 3;

    /// <summary>Предобработка изображения перед распознаванием (нормализация контраста + резкость).</summary>
    public bool Preprocess { get; set; } = true;
}
