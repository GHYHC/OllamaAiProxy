namespace OllamaAiProxy.Providers;

/// <summary>
/// 火山方舟（VolcengineCodingPlan）provider 配置。ApiKeys 可以来自配置数组，也可以来自 VOLCENGINE_CODING_PLAN_API_KEY 环境变量。
/// 配置中可以指定多个 Key，当一个 Key 返回 HTTP 429 时会自动切换到下一个 Key。
/// </summary>
public sealed class VolcengineCodingPlanOptions
{
    public const string SectionName = "Providers:VolcengineCodingPlan";

    public string Name { get; set; } = "VolcengineCodingPlan";

    /// <summary>
    /// 火山方舟 OpenAI 兼容接口基址。默认使用编程场景的 coding 入口：
    /// https://ark.cn-beijing.volces.com/api/coding/v3
    /// </summary>
    public string BaseUrl { get; set; } = "https://ark.cn-beijing.volces.com/api/coding/v3";

    /// <summary>
    /// 可配置多个 ApiKey，429 时自动轮换。为空时从环境变量 VOLCENGINE_CODING_PLAN_API_KEY 读取。
    /// </summary>
    public string[] ApiKeys { get; set; } = [];
}