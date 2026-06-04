namespace OllamaAiProxy.Providers;

/// <summary>
/// DeepSeek provider 配置。ApiKey 可以来自配置，也可以来自 DEEPSEEK_API_KEY 环境变量。
/// </summary>
public sealed class DeepSeekOptions
{
    public const string SectionName = "Providers:DeepSeek";

    public string Name { get; set; } = "deepseek";

    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string? ApiKey { get; set; }
}
