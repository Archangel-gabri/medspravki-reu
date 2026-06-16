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
}
