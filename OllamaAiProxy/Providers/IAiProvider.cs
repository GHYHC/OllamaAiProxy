using System.Text.Json;
using OllamaAiProxy.Contracts;

namespace OllamaAiProxy.Providers;

/// <summary>
/// 模型发现和 OpenAI 兼容聊天补全的厂商边界。
/// 后续新增其他上游厂商时，实现这个接口即可。
/// </summary>
public interface IAiProvider
{
    /// <summary>配置中使用的稳定厂商标识，例如 "deepseek"。</summary>
    string Name { get; }

    /// <summary>对外模型名前缀和 Ollama family；当前等于 provider Name。</summary>
    string Family { get; }

    /// <summary>当前 provider 是否允许 OpenAI 兼容消息里的 image_url 输入。</summary>
    bool SupportsImages { get; }

    /// <summary>获取模型列表，并转换成厂商无关的元数据结构。</summary>
    Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>按模型 id 查找模型；模型不存在时返回 null。</summary>
    Task<AiModel?> GetModelAsync(string model, CancellationToken cancellationToken);

    /// <summary>转发非流式 OpenAI 兼容聊天补全请求。</summary>
    Task<ProviderChatResponse> CreateChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken);

    /// <summary>转发流式 OpenAI 兼容聊天补全请求。</summary>
    Task<ProviderChatResponse> StreamChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken);
}
