namespace OllamaAiProxy.Services;

/// <summary>
/// 图片视觉中继配置。当目标模型不支持图片时，用 VisionModel 把图片描述成文字再转发。
/// </summary>
public sealed class ImageVisionRelayOptions
{
    public const string SectionName = "ImageVisionRelay";

    /// <summary>是否启用图片视觉中继。默认 true，但未配置 VisionModel 时仍不生效。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 用于识图的视觉模型，使用 provider/model 外部格式，
    /// 例如 VolcengineCodingPlan/doubao-seed-2.0-lite。为空时中继不生效。
    /// </summary>
    public string VisionModel { get; set; } = "";

    /// <summary>
    /// 发给视觉模型的提示词。留空使用默认（提取所有可见文字再描述画面，输出结构化结果）。
    /// </summary>
    public string Prompt { get; set; } = "";
}
