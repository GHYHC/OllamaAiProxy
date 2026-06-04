namespace OllamaAiProxy.Providers;

/// <summary>
/// OpenAI provider 配置。ApiKey 可以来自配置，也可以来自 OPENAI_API_KEY 环境变量。
/// </summary>
public sealed class OpenAIOptions
{
    public const string SectionName = "Providers:OpenAI";

    public string Name { get; set; } = "openai";

    public string BaseUrl { get; set; } = "https://api.openai.com";

    public string? ApiKey { get; set; }
}
