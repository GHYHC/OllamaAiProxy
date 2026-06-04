namespace OllamaAiProxy.Contracts;

/// <summary>
/// 厂商无关的模型元数据。endpoint 层只依赖这个结构，再按 OpenAI/Ollama 协议转换输出。
/// </summary>
public sealed record AiModel
{
    /// <summary>
    /// 模型 ID，也是客户端请求时传入的 model 值，例如 deepseek-v4-flash 或 gpt-4.1。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 上游模型对象类型。OpenAI/DeepSeek 通常返回 model。
    /// </summary>
    public required string Object { get; init; }

    /// <summary>
    /// 模型所属方或厂商标识，例如 deepseek、openai。
    /// </summary>
    public required string OwnedBy { get; init; }

    /// <summary>
    /// 模型可用状态。上游不提供时使用 available 作为稳定默认值。
    /// </summary>
    public required string Availability { get; init; }

    /// <summary>
    /// 面向显示的模型名称；可以比 Id 更友好。
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// 模型更新时间，用 ISO 8601 字符串表示；用于 Ollama modified_at。
    /// </summary>
    public required string ModifiedAt { get; init; }

    /// <summary>
    /// 模型创建时间，Unix 秒级时间戳；用于 OpenAI 兼容模型对象。
    /// </summary>
    public required long Created { get; init; }

    /// <summary>
    /// 本地模型文件大小。API 代理没有本地权重文件时固定为 0。
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// 稳定摘要值。API 模型没有真实文件 digest 时，由 provider 基于模型 ID 合成。
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>
    /// 映射到 Ollama details 的基础模型信息。
    /// </summary>
    public required AiModelDetails Details { get; init; }

    /// <summary>
    /// 映射到 Ollama /api/show model_info 的厂商/架构信息。
    /// </summary>
    public required AiModelInfo ModelInfo { get; init; }

    /// <summary>
    /// 模型能力列表，例如 completion、tools、thinking、vision。
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>
/// 对应 Ollama details 的模型基础信息；API 模型没有本地文件时使用稳定的 API 占位值。
/// </summary>
public sealed record AiModelDetails
{
    /// <summary>
    /// 父模型名称。API 模型通常没有父模型，使用空字符串。
    /// </summary>
    public required string ParentModel { get; init; }

    /// <summary>
    /// 模型格式。远程 API 模型统一使用 api。
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    /// 模型家族或厂商家族，例如 deepseek、openai。
    /// </summary>
    public required string Family { get; init; }

    /// <summary>
    /// 模型家族列表，用于兼容 Ollama families 字段。
    /// </summary>
    public required IReadOnlyList<string> Families { get; init; }

    /// <summary>
    /// 参数规模描述。上游未知时使用 unknown。
    /// </summary>
    public required string ParameterSize { get; init; }

    /// <summary>
    /// 量化等级。API 模型没有本地量化等级时使用 api。
    /// </summary>
    public required string QuantizationLevel { get; init; }
}

/// <summary>
/// 更偏厂商/架构的模型信息，用于 /api/show 的 model_info。
/// </summary>
public sealed record AiModelInfo
{
    /// <summary>
    /// 架构或厂商名称，用作 model_info 字段前缀，例如 deepseek、openai。
    /// </summary>
    public required string Architecture { get; init; }

    /// <summary>
    /// 总参数量。上游未知时使用 0。
    /// </summary>
    public required long ParameterCount { get; init; }

    /// <summary>
    /// 激活参数量。MoE 模型可填写真实激活参数；未知时使用 0。
    /// </summary>
    public required long ActiveParameterCount { get; init; }

    /// <summary>
    /// 上下文长度。上游未知时使用 0。
    /// </summary>
    public required int ContextLength { get; init; }

    /// <summary>
    /// 单次响应最大输出 token 数；映射到 Ollama 参数 num_predict。
    /// </summary>
    public required int MaxOutputTokens { get; init; }

    /// <summary>
    /// 是否为纯文本模型；用于决定是否允许 image_url 输入。
    /// </summary>
    public required bool TextOnly { get; init; }

    /// <summary>
    /// 模型是否已废弃或仅作为兼容别名保留。
    /// </summary>
    public required bool Deprecated { get; init; }

    /// <summary>
    /// 厂商侧可用状态，和 AiModel.Availability 保持一致。
    /// </summary>
    public required string Availability { get; init; }
}
