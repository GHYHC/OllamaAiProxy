namespace OllamaAiProxy.Providers;

/// <summary>
/// OpenAI provider 配置。ApiKeys 可以来自配置数组，也可以来自 OPENAI_API_KEY 环境变量。
/// 配置中可以指定多个 Key，当一个 Key 返回 HTTP 429 时会自动切换到下一个 Key。
/// </summary>
public sealed class OpenAIOptions
{
    public const string SectionName = "Providers:OpenAI";

    public string Name { get; set; } = "openai";

    public string BaseUrl { get; set; } = "https://api.openai.com";

    /// <summary>
    /// 可配置多个 ApiKey，429 时自动轮换。为空时从环境变量 OPENAI_API_KEY 读取。
    /// </summary>
    public string[] ApiKeys { get; set; } = [];
}
