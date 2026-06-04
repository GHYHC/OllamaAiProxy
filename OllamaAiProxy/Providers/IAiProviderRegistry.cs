namespace OllamaAiProxy.Providers;

using OllamaAiProxy.Contracts;

/// <summary>按配置中的厂商名称解析 provider。</summary>
public interface IAiProviderRegistry
{
    /// <summary>所有已配置的 provider。</summary>
    IReadOnlyList<IAiProvider> Providers { get; }

    /// <summary>获取指定 provider；未注册时抛出异常，让启动/配置错误尽早暴露。</summary>
    IAiProvider GetRequiredProvider(string name);

    /// <summary>获取所有 provider 的模型。</summary>
    Task<IReadOnlyList<(IAiProvider Provider, AiModel Model)>> GetAllModelsAsync(CancellationToken cancellationToken);

    /// <summary>按 provider-name/model 外部模型名解析 provider 和上游原始模型名。</summary>
    Task<(IAiProvider Provider, AiModel Model, string UpstreamModel)?> ResolveModelAsync(string externalModel, CancellationToken cancellationToken);
}
