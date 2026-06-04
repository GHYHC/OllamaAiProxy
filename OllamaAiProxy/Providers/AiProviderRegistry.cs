namespace OllamaAiProxy.Providers;

using OllamaAiProxy.Contracts;

public sealed class AiProviderRegistry : IAiProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IAiProvider> _providers;

    public AiProviderRegistry(IReadOnlyList<IAiProvider> providers)
    {
        EnsureUniqueProviderNames(providers);
        _providers = providers.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IAiProvider> Providers => _providers.Values.ToArray();

    public IAiProvider GetRequiredProvider(string name)
    {
        if (_providers.TryGetValue(name, out var provider))
            return provider;

        throw new InvalidOperationException($"AI provider '{name}' is not registered.");
    }

    public async Task<IReadOnlyList<(IAiProvider Provider, AiModel Model)>> GetAllModelsAsync(CancellationToken cancellationToken)
    {
        var tasks = Providers.Select(async provider =>
        {
            var models = await provider.GetModelsAsync(cancellationToken);
            return models.Select(model => (provider, model)).ToArray();
        });

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(x => x).ToArray();
    }

    public async Task<(IAiProvider Provider, AiModel Model, string UpstreamModel)?> ResolveModelAsync(string externalModel, CancellationToken cancellationToken)
    {
        var separatorIndex = externalModel.IndexOf('/');
        if (separatorIndex <= 0 || separatorIndex == externalModel.Length - 1)
            return null;

        var providerName = externalModel[..separatorIndex];
        var upstreamModel = externalModel[(separatorIndex + 1)..];
        if (!_providers.TryGetValue(providerName, out var provider))
            return null;

        var model = await provider.GetModelAsync(upstreamModel, cancellationToken);
        return model is null ? null : (provider, model, upstreamModel);
    }

    private static void EnsureUniqueProviderNames(IReadOnlyList<IAiProvider> providers)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
                throw new InvalidOperationException("AI provider name cannot be empty.");

            if (!names.Add(provider.Name))
                throw new InvalidOperationException($"AI provider name '{provider.Name}' is configured more than once.");
        }
    }
}
