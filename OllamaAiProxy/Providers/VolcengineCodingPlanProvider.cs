using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OllamaAiProxy.Contracts;

namespace OllamaAiProxy.Providers;

/// <summary>
/// 火山方舟 Coding Plan（VolcengineCodingPlan）provider。Coding Plan 提供兼容 OpenAI 的聊天补全接口，
/// Base URL 为 https://ark.cn-beijing.volces.com/api/coding/v3。模型列表固定为 Coding Plan 支持的模型，
/// 不再从 /models 接口拉取，避免误用非套餐模型产生额外费用。
/// </summary>
public sealed class VolcengineCodingPlanProvider : IAiProvider
{
    private const string ApiKeyEnvironmentVariable = "VOLCENGINE_CODING_PLAN_API_KEY";
    private const long ModelCreated = 1_776_988_800;
    private const string ModelModifiedAt = "2026-07-06T00:00:00Z";

    private readonly HttpClient _httpClient;
    private readonly VolcengineCodingPlanOptions _options;
    private readonly ApiKeyManager _apiKeyManager;

    public VolcengineCodingPlanProvider(HttpClient httpClient, VolcengineCodingPlanOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _apiKeyManager = CreateApiKeyManager(options);
        _httpClient.BaseAddress = EnsureTrailingSlash(_options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static ApiKeyManager CreateApiKeyManager(VolcengineCodingPlanOptions options)
    {
        // 优先使用配置中的 ApiKeys；如果数组为空则尝试环境变量（作为单 Key）。
        var keys = options.ApiKeys is { Length: > 0 }
            ? options.ApiKeys
            : TryGetEnvKeys();

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

    public string Name => string.IsNullOrWhiteSpace(_options.Name) ? "VolcengineCodingPlan" : _options.Name;

    public string Family => Name;

    // Coding Plan 同时包含纯文本和视觉模型，图片输入是否可用交由上游按模型校验。
    public bool SupportsImages => true;

    // 模型列表固定为 Coding Plan 支持的模型，不调用上游 /models 接口。
    public Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiModel>>(GetKnownModels(Family));

    public Task<AiModel?> GetModelAsync(string model, CancellationToken cancellationToken)
    {
        var found = GetKnownModels(Family).FirstOrDefault(x => string.Equals(x.Id, model, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(found);
    }

    public async Task<ProviderChatResponse> CreateChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken)
    {
        // 最多重试所有 Key 的次数
        var maxAttempts = _apiKeyManager.KeyCount;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var content = CreateJsonContent(request);
            var upstreamRequest = CreateChatRequest(content);
            var response = await _httpClient.SendAsync(upstreamRequest, cancellationToken);

            if ((int)response.StatusCode != 429)
                return await ProviderChatResponse.BufferAsync(response, cancellationToken);

            // 429: 标记当前 Key 不可用，切换到下一个 Key 重试
            var (_, hasAvailable) = _apiKeyManager.MarkCurrentBlocked();
            if (!hasAvailable)
                return await ProviderChatResponse.BufferAsync(response, cancellationToken);
        }

        // 所有 Key 都尝试完仍 429，返回最后的 429 响应
        using var finalContent = CreateJsonContent(request);
        var finalRequest = CreateChatRequest(finalContent);
        var finalResponse = await _httpClient.SendAsync(finalRequest, cancellationToken);
        return await ProviderChatResponse.BufferAsync(finalResponse, cancellationToken);
    }

    public async Task<ProviderChatResponse> StreamChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken)
    {
        var maxAttempts = _apiKeyManager.KeyCount;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var content = CreateJsonContent(request);
            var upstreamRequest = CreateChatRequest(content);
            upstreamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await _httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 429)
                {
                    var (_, hasAvailable) = _apiKeyManager.MarkCurrentBlocked();
                    if (hasAvailable)
                        continue; // 换 Key 重试

                    // 所有 Key 都用完了，返回最后这个 429
                    return await ProviderChatResponse.BufferAsync(response, cancellationToken);
                }

                // 非 429 错误，缓存并返回
                return await ProviderChatResponse.BufferAsync(response, cancellationToken);
            }

            return ProviderChatResponse.Stream(response);
        }

        // 兜底（不应到达此处）
        using var fallbackContent = CreateJsonContent(request);
        var fallbackRequest = CreateChatRequest(fallbackContent);
        fallbackRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var fallbackResponse = await _httpClient.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!fallbackResponse.IsSuccessStatusCode)
            return await ProviderChatResponse.BufferAsync(fallbackResponse, cancellationToken);

        return ProviderChatResponse.Stream(fallbackResponse);
    }

    private HttpRequestMessage CreateChatRequest(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
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

    // Coding Plan 支持的模型（来源：方舟 Coding Plan 快速开始「模型配置」）。
    // 上下文长度/最大输出按官方文档与模型目录整理；视觉/思考能力按文档说明标注。
    private static IReadOnlyList<AiModel> GetKnownModels(string family)
    {
        return new[]
        {
            CreateModel("doubao-seed-2.0-code", "Doubao Seed 2.0 Code", 262144, 131072, vision: true, family),
            CreateModel("doubao-seed-2.0-pro", "Doubao Seed 2.0 Pro", 262144, 131072, vision: true, family),
            CreateModel("doubao-seed-2.0-lite", "Doubao Seed 2.0 Lite", 262144, 131072, vision: true, family),
            CreateModel("doubao-seed-code", "Doubao Seed Code", 262144, 32768, vision: true, family),
            CreateModel("minimax-m2.7", "MiniMax M2.7", 204800, 131072, vision: false, family),
            CreateModel("minimax-m3", "MiniMax M3", 524288, 131072, vision: true, family),
            CreateModel("glm-5.2", "GLM 5.2", 1048576, 131072, vision: false, family),
            CreateModel("deepseek-v4-flash", "DeepSeek V4 Flash", 1048576, 393216, vision: false, family),
            CreateModel("deepseek-v4-pro", "DeepSeek V4 Pro", 1048576, 393216, vision: false, family),
            CreateModel("kimi-k2.6", "Kimi K2.6", 262144, 32768, vision: true, family),
            CreateModel("kimi-k2.7-code", "Kimi K2.7 Code", 262144, 32768, vision: true, family)
        };
    }

    private static AiModel CreateModel(string id, string displayName, int contextLength, int maxOutputTokens, bool vision, string family)
    {
        // Coding Plan 模型均支持函数调用与深度思考，视觉能力按模型标注。
        var capabilities = new List<string> { "completion", "tools", "thinking" };
        if (vision)
            capabilities.Add("vision");

        return new AiModel
        {
            Id = id,
            Object = "model",
            OwnedBy = "volcengine",
            Availability = "available",
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
                ParameterSize = "unknown",
                QuantizationLevel = "api"
            },
            ModelInfo = new AiModelInfo
            {
                Architecture = "volcengine",
                ParameterCount = 0L,
                ActiveParameterCount = 0L,
                ContextLength = contextLength,
                MaxOutputTokens = maxOutputTokens,
                TextOnly = !vision,
                Deprecated = false,
                Availability = "available"
            },
            Capabilities = capabilities
        };
    }

    private static string CreateDigest(string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"volcengine-api:{model}")))
            .ToLowerInvariant();

    private static Uri EnsureTrailingSlash(string url) =>
        new(url.EndsWith('/') ? url : url + "/");
}