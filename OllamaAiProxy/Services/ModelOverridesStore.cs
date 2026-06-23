using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaAiProxy.Services;

/// <summary>
/// 用户可编辑的模型详情覆盖字段。只有非 null 的字段才会覆盖上游值。
/// </summary>
public sealed record ModelOverride
{
    public string? DisplayName { get; init; }
    public string? Architecture { get; init; }
    public string? ParameterSize { get; init; }
    public long? ParameterCount { get; init; }
    public long? ActiveParameterCount { get; init; }
    public int? ContextLength { get; init; }
    public int? MaxOutputTokens { get; init; }
    public string? QuantizationLevel { get; init; }
    public string? Availability { get; init; }
    public bool? TextOnly { get; init; }
    public bool? Deprecated { get; init; }
    public List<string>? Capabilities { get; init; }
}

/// <summary>
/// 模型详情覆盖值的持久化存储。以 provider/model 为键，读写 model-overrides.json。
/// </summary>
public sealed class ModelOverridesStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ConcurrentDictionary<string, ModelOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public ModelOverridesStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            var dict = JsonSerializer.Deserialize(json, ModelOverridesJsonContext.Default.ConcurrentDictionaryStringModelOverride);
            if (dict is not null)
                _overrides = dict;
        }
        catch
        {
        }
        finally
        {
            _lock.Release();
        }
    }

    public ModelOverride? Get(string key) =>
        _overrides.TryGetValue(key, out var value) ? value : null;

    public ConcurrentDictionary<string, ModelOverride> GetAll() =>
        _overrides;

    public async Task SetAsync(string key, ModelOverride value, CancellationToken cancellationToken = default)
    {
        _overrides[key] = value;
        await SaveAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_overrides.TryRemove(key, out _))
            return false;
        await SaveAsync(cancellationToken);
        return true;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_overrides, ModelOverridesJsonContext.Default.ConcurrentDictionaryStringModelOverride);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConcurrentDictionary<string, ModelOverride>))]
[JsonSerializable(typeof(ModelOverride))]
[JsonSerializable(typeof(Dictionary<string, ModelOverride>))]
internal sealed partial class ModelOverridesJsonContext : JsonSerializerContext
{
}
