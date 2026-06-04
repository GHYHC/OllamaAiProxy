using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OllamaAiProxy.Contracts;

namespace OllamaAiProxy.Providers;

public sealed class DeepSeekProvider : IAiProvider
{
    private const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";
    private const int ContextLength = 1_000_000;
    private const int MaxOutputTokens = 384_000;
    private const long ModelCreated = 1_776_988_800;
    private const string ModelModifiedAt = "2026-04-24T00:00:00Z";
    private static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly DeepSeekOptions _options;
    private readonly SemaphoreSlim _modelsCacheLock = new(1, 1);
    private ModelsCache? _modelsCache;

    public DeepSeekProvider(HttpClient httpClient, DeepSeekOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress = new Uri(options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Name => string.IsNullOrWhiteSpace(_options.Name) ? "deepseek" : _options.Name;

    public string Family => Name;

    public bool SupportsImages => false;

    public async Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _modelsCache;
        if (cached is not null && cached.ExpiresAt > now)
            return cached.Models;

        // 防止冷缓存时大量 Copilot 探测请求同时打到 DeepSeek /models。
        await _modelsCacheLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _modelsCache;
            if (cached is not null && cached.ExpiresAt > now)
                return cached.Models;

            if (TryGetApiKey() is { } apiKey)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var models = ParseModels(body, Family);
                    if (models.Count > 0)
                    {
                        _modelsCache = new ModelsCache(models, now.Add(ModelsCacheTtl));
                        return models;
                    }
                }
            }
        }
        catch
        {
            // 模型发现短暂不可用时不伪造默认模型，只返回缓存或内置已知模型列表。
        }
        finally
        {
            _modelsCacheLock.Release();
        }

        return cached?.Models ?? GetKnownModels(Family);
    }

    public async Task<AiModel?> GetModelAsync(string model, CancellationToken cancellationToken)
    {
        var models = await GetModelsAsync(cancellationToken);
        return models.FirstOrDefault(x => string.Equals(x.Id, model, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProviderChatResponse> CreateChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken)
    {
        using var content = CreateJsonContent(request);
        using var upstreamRequest = CreateChatRequest(content);
        var response = await _httpClient.SendAsync(upstreamRequest, cancellationToken);
        return await ProviderChatResponse.BufferAsync(response, cancellationToken);
    }

    public async Task<ProviderChatResponse> StreamChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken)
    {
        using var content = CreateJsonContent(request);
        using var upstreamRequest = CreateChatRequest(content);
        upstreamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 错误响应先缓存为文本，endpoint 才能正常返回上游状态码和错误体。
            return await ProviderChatResponse.BufferAsync(response, cancellationToken);
        }

        return ProviderChatResponse.Stream(response);
    }

    private HttpRequestMessage CreateChatRequest(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = content
        };

        if (TryGetApiKey() is { } apiKey)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return request;
    }

    private string? TryGetApiKey()
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable)
            : _options.ApiKey;
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    private static StringContent CreateJsonContent(JsonDocument request)
    {
        var json = request.RootElement.GetRawText();
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static IReadOnlyList<AiModel> ParseModels(string json, string family)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<AiModel>();

            var models = new List<AiModel>();
            foreach (var item in data.EnumerateArray())
            {
                var id = TryGetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                models.Add(CreateKnownModel(
                    id,
                    family,
                    TryGetString(item, "object") ?? "model",
                    TryGetString(item, "owned_by") ?? "deepseek",
                    TryGetString(item, "availability") ?? "available"));
            }

            return models;
        }
        catch
        {
            return Array.Empty<AiModel>();
        }
    }

    private static IReadOnlyList<AiModel> GetKnownModels(string family) => new[]
    {
        // DeepSeek 官方模型清单不可用时，返回项目内维护的已知 DeepSeek 模型列表。
        CreateKnownModel("deepseek-v4-flash", family),
        CreateKnownModel("deepseek-v4-pro", family),
        CreateKnownModel("deepseek-chat", family),
        CreateKnownModel("deepseek-reasoner", family)
    };

    private static AiModel CreateKnownModel(
        string id,
        string family,
        string obj = "model",
        string ownedBy = "deepseek",
        string availability = "available") =>
        id switch
        {
            "deepseek-v4-pro" => CreateModel(id, family, obj, ownedBy, availability, "DeepSeek V4 Pro", "1.6T total / 49B active", 1_600_000_000_000L, 49_000_000_000L, true, false),
            "deepseek-chat" => CreateModel(id, family, obj, ownedBy, availability, "DeepSeek Chat (deprecated alias for V4 Flash non-thinking mode)", "284B total / 13B active", 284_000_000_000L, 13_000_000_000L, false, true),
            "deepseek-reasoner" => CreateModel(id, family, obj, ownedBy, availability, "DeepSeek Reasoner (deprecated alias for V4 Flash thinking mode)", "284B total / 13B active", 284_000_000_000L, 13_000_000_000L, true, true),
            "deepseek-v4-flash" => CreateModel(id, family, obj, ownedBy, availability, "DeepSeek V4 Flash", "284B total / 13B active", 284_000_000_000L, 13_000_000_000L, true, false),
            _ => CreateModel(id, family, obj, ownedBy, availability, id, "unknown", 0L, 0L, false, false)
        };

    private static AiModel CreateModel(
        string id,
        string family,
        string obj,
        string ownedBy,
        string availability,
        string displayName,
        string parameterSize,
        long parameterCount,
        long activeParameterCount,
        bool thinking,
        bool deprecated)
    {
        var capabilities = new List<string> { "completion", "tools" };
        if (thinking) capabilities.Add("thinking");

        // Ollama 期望 digest、quantization 等本地模型字段；API 模型使用稳定合成值，
        // 并把厂商细节保留在 model_info 中。
        return new AiModel
        {
            Id = id,
            Object = obj,
            OwnedBy = ownedBy,
            Availability = availability,
            DisplayName = displayName,
            ModifiedAt = ModelModifiedAt,
            Created = ModelCreated,
            Size = 0L,
            Digest = CreateDigest(id),
            Details = new AiModelDetails
            {
                ParentModel = "",
                Format = "api",
                Family = family,
                Families = new[] { family },
                ParameterSize = parameterSize,
                QuantizationLevel = "api"
            },
            ModelInfo = new AiModelInfo
            {
                Architecture = "deepseek",
                ParameterCount = parameterCount,
                ActiveParameterCount = activeParameterCount,
                ContextLength = ContextLength,
                MaxOutputTokens = MaxOutputTokens,
                TextOnly = true,
                Deprecated = deprecated,
                Availability = availability
            },
            Capabilities = capabilities
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string CreateDigest(string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"deepseek-api:{model}")))
            .ToLowerInvariant();

    private sealed record ModelsCache(IReadOnlyList<AiModel> Models, DateTimeOffset ExpiresAt);
}

