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
    private readonly ApiKeyManager _apiKeyManager;
    private readonly SemaphoreSlim _modelsCacheLock = new(1, 1);
    private ModelsCache? _modelsCache;

    public DeepSeekProvider(HttpClient httpClient, DeepSeekOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _apiKeyManager = CreateApiKeyManager(options);
        _httpClient.BaseAddress = EnsureTrailingSlash(options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static ApiKeyManager CreateApiKeyManager(DeepSeekOptions options)
    {
        // 优先使用配置中的 ApiKeys；空白占位 Key（如 appsettings 中的空字符串）会被过滤，
        // 过滤后为空则尝试环境变量（作为单 Key）。
        var keys = options.ApiKeys.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (keys.Length == 0)
            keys = TryGetEnvKeys();

        if (keys.Length == 0)
            throw new InvalidOperationException(
                $"No API key configured for {options.Name}. Set ApiKeys in configuration or " +
                $"the {ApiKeyEnvironmentVariable} environment variable.");

        return new ApiKeyManager(keys);
    }

    private static string[] TryGetEnvKeys()
    {
        var env = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(env) ? [] : [env];
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

            var apiKey = _apiKeyManager.GetCurrentKey();
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
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
            else if ((int)response.StatusCode == 429)
            {
                // /models 请求的 429 也标记 Key 不可用
                _apiKeyManager.MarkCurrentBlocked();
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

    public Task<ProviderChatResponse> CreateChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "v1/chat/completions", stream: false, cancellationToken);

    public Task<ProviderChatResponse> StreamChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "v1/chat/completions", stream: true, cancellationToken);

    /// <summary>转发非流式 OpenAI Responses 请求（/v1/responses）。</summary>
    public Task<ProviderChatResponse> CreateResponseAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "v1/responses", stream: false, cancellationToken);

    /// <summary>转发流式 OpenAI Responses 请求（/v1/responses）。</summary>
    public Task<ProviderChatResponse> StreamResponseAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "v1/responses", stream: true, cancellationToken);

    private async Task<ProviderChatResponse> SendCoreAsync(JsonDocument request, string path, bool stream, CancellationToken cancellationToken)
    {
        // 最多重试所有 Key 的次数
        var maxAttempts = _apiKeyManager.KeyCount;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var content = CreateJsonContent(request);
            var upstreamRequest = CreateRequest(content, path);
            if (stream)
                upstreamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await _httpClient.SendAsync(
                upstreamRequest,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cancellationToken);

            if (stream && response.IsSuccessStatusCode)
                return ProviderChatResponse.Stream(response);

            if ((int)response.StatusCode != 429)
                return await ProviderChatResponse.BufferAsync(response, cancellationToken);

            // 429: 标记当前 Key 不可用，切换到下一个 Key 重试
            var (_, hasAvailable) = _apiKeyManager.MarkCurrentBlocked();
            if (!hasAvailable)
                return await ProviderChatResponse.BufferAsync(response, cancellationToken);

            response.Dispose();
        }

        // 所有 Key 都尝试完仍 429，返回最后的 429 响应
        using var finalContent = CreateJsonContent(request);
        var finalRequest = CreateRequest(finalContent, path);
        if (stream)
            finalRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var finalResponse = await _httpClient.SendAsync(
            finalRequest,
            stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (stream && finalResponse.IsSuccessStatusCode)
            return ProviderChatResponse.Stream(finalResponse);
        return await ProviderChatResponse.BufferAsync(finalResponse, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpContent content, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content
        };

        var apiKey = _apiKeyManager.GetCurrentKey();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return request;
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

    private static Uri EnsureTrailingSlash(string url) => new(
        url.EndsWith('/') ? url : url + "/");}

