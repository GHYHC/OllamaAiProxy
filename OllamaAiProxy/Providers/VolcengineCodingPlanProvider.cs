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
    private const string ModelModifiedAt = "2026-08-31T00:00:00Z";

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

    public Task<ProviderChatResponse> CreateChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "chat/completions", stream: false, cancellationToken);

    public Task<ProviderChatResponse> StreamChatCompletionAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "chat/completions", stream: true, cancellationToken);

    /// <summary>转发非流式 OpenAI Responses 请求（/responses）。</summary>
    public Task<ProviderChatResponse> CreateResponseAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "responses", stream: false, cancellationToken);

    /// <summary>转发流式 OpenAI Responses 请求（/responses）。</summary>
    public Task<ProviderChatResponse> StreamResponseAsync(JsonDocument request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, "responses", stream: true, cancellationToken);

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

    // Coding Plan 支持的模型（来源：方舟 Coding Plan 文档「支持的模型」，2026-08-31 核对）。
    // 上下文长度/最大输出按官方文档整理；视觉/思考能力按文档说明标注。
    // 已移除文档标注「即将下线/已下线」的模型：doubao-seed-2.0-code、doubao-seed-2.0-pro、
    // doubao-seed-code、minimax-m2.7、kimi-k2.6、glm-5.2。
    // 注：Auto 模式仅可通过控制台切换，Model Name 不支持配置，故不列入。
    private static IReadOnlyList<AiModel> GetKnownModels(string family)
    {
        return new[]
        {
            // ark-code-latest 为控制台路由别名，使用控制台中当前选定的模型；上下文/输出随所选模型而定，
            // 这里取保守估值，实际以上游为准。
            CreateModel("ark-code-latest", "Ark Code Latest", 262144, 65536, vision: false, family),
            CreateModel("doubao-seed-2.1-turbo", "Doubao Seed 2.1 Turbo", 262144, 65536, vision: true, family),
            CreateModel("doubao-seed-evolving", "Doubao Seed Evolving", 1048576, 262144, vision: true, family),
            CreateModel("doubao-seed-2.0-lite", "Doubao Seed 2.0 Lite", 262144, 131072, vision: true, family),
            CreateModel("minimax-m3", "MiniMax M3", 1048576, 131072, vision: true, family),
            CreateModel("kimi-k2.7-code", "Kimi K2.7 Code", 262144, 32768, vision: true, family),
            CreateModel("glm-5.3", "GLM 5.3", 1048576, 131072, vision: false, family),
            CreateModel("glm-5.3-flash", "GLM 5.3 Flash", 1048576, 131072, vision: true, family),
            CreateModel("deepseek-v4-flash", "DeepSeek V4 Flash", 1048576, 393216, vision: false, family),
            CreateModel("deepseek-v4-pro", "DeepSeek V4 Pro", 1048576, 393216, vision: false, family)
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
