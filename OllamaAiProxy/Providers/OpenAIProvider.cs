using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OllamaAiProxy.Contracts;

namespace OllamaAiProxy.Providers;

public sealed class OpenAIProvider : IAiProvider
{
    private const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    private const long ModelCreatedFallback = 1_700_000_000;
    private static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly OpenAIOptions _options;
    private readonly SemaphoreSlim _modelsCacheLock = new(1, 1);
    private ModelsCache? _modelsCache;

    public OpenAIProvider(HttpClient httpClient, OpenAIOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Name => string.IsNullOrWhiteSpace(_options.Name) ? "openai" : _options.Name;

    public string Family => Name;

    public bool SupportsImages => true;

    public async Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _modelsCache;
        if (cached is not null && cached.ExpiresAt > now)
            return cached.Models;

        await _modelsCacheLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _modelsCache;
            if (cached is not null && cached.ExpiresAt > now)
                return cached.Models;

            if (TryGetApiKey() is { } apiKey)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
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
            // 模型发现失败时不伪造默认模型，只返回仍有效的缓存或空列表。
        }
        finally
        {
            _modelsCacheLock.Release();
        }

        return cached?.Models ?? Array.Empty<AiModel>();
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
        var json = JsonSerializer.Serialize(request.RootElement);
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

                var obj = TryGetString(item, "object") ?? "model";
                var ownedBy = TryGetString(item, "owned_by") ?? "openai";
                var created = TryGetInt64(item, "created") ?? ModelCreatedFallback;
                models.Add(CreateModel(id, family, obj, ownedBy, created));
            }

            return models;
        }
        catch
        {
            return Array.Empty<AiModel>();
        }
    }

    private static AiModel CreateModel(string id, string family, string obj, string ownedBy, long created = ModelCreatedFallback)
    {
        // OpenAI /models 只提供模型基础信息，token 限制需要按官方模型规格做本地映射。
        var tokenLimits = GetTokenLimits(id);

        return new AiModel
        {
            Id = id,
            Object = obj,
            OwnedBy = ownedBy,
            Availability = "available",
            DisplayName = id,
            ModifiedAt = DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime.ToString("o"),
            Created = created,
            Size = 0L,
            Digest = CreateDigest(id),
            Details = new AiModelDetails
            {
                ParentModel = "",
                Format = "api",
                Family = family,
                Families = new[] { family },
                ParameterSize = "unknown",
                QuantizationLevel = "api"
            },
            ModelInfo = new AiModelInfo
            {
                Architecture = "openai",
                ParameterCount = 0L,
                ActiveParameterCount = 0L,
                ContextLength = tokenLimits.ContextLength,
                MaxOutputTokens = tokenLimits.MaxOutputTokens,
                TextOnly = false,
                Deprecated = false,
                Availability = "available"
            },
            Capabilities = new[] { "completion", "tools", "vision" }
        };
    }

    private static ModelTokenLimits GetTokenLimits(string id)
    {
        var normalizedId = id.ToLowerInvariant();

        // 先处理精确模型，再处理带日期后缀的快照模型，例如 gpt-4.1-2025-04-14。
        if (OpenAIModelTokenLimits.TryGetValue(normalizedId, out var exactLimits))
            return exactLimits;

        foreach (var mapping in OpenAIModelTokenLimits)
        {
            if (normalizedId.StartsWith($"{mapping.Key}-", StringComparison.Ordinal))
                return mapping.Value;
        }

        return ModelTokenLimits.Unknown;
    }

    // 常用 OpenAI 模型的官方 token 限制。未知模型仍保留 0，避免伪造不确定数据。
    private static readonly IReadOnlyDictionary<string, ModelTokenLimits> OpenAIModelTokenLimits =
        new Dictionary<string, ModelTokenLimits>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.5"] = new(1_050_000, 128_000),
            ["gpt-5.5-pro"] = new(1_050_000, 128_000),
            ["gpt-5.4"] = new(1_050_000, 128_000),
            ["gpt-5"] = new(400_000, 128_000),
            ["gpt-5-mini"] = new(400_000, 128_000),
            ["gpt-5-nano"] = new(400_000, 128_000),
            ["gpt-5-chat-latest"] = new(400_000, 128_000),
            ["gpt-4.1"] = new(1_047_576, 32_768),
            ["gpt-4.1-mini"] = new(1_047_576, 32_768),
            ["gpt-4.1-nano"] = new(1_047_576, 32_768),
            ["gpt-4o"] = new(128_000, 16_384),
            ["gpt-4o-mini"] = new(128_000, 16_384),
            ["o3"] = new(200_000, 100_000),
            ["o3-mini"] = new(200_000, 100_000),
            ["o4-mini"] = new(200_000, 100_000)
        };

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static long? TryGetInt64(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.Number &&
        prop.TryGetInt64(out var value)
            ? value
            : null;

    private static string CreateDigest(string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"openai-api:{model}")))
            .ToLowerInvariant();

    private sealed record ModelsCache(IReadOnlyList<AiModel> Models, DateTimeOffset ExpiresAt);

    private sealed record ModelTokenLimits(int ContextLength, int MaxOutputTokens)
    {
        public static ModelTokenLimits Unknown { get; } = new(0, 0);
    }
}

