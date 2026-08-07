using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using OllamaAiProxy.Contracts;
using OllamaAiProxy.Monitoring;
using OllamaAiProxy.Providers;
using OllamaAiProxy.Services;

const int DefaultPort = 11434;
const string OllamaVersion = "0.24.0";

var builder = WebApplication.CreateSlimBuilder(args);
var port = builder.Configuration.GetValue("PORT", DefaultPort);
var testPageUrl = $"http://localhost:{port}/";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.Configure<RequestLoggingOptions>(options =>
{
    var section = builder.Configuration.GetSection(RequestLoggingOptions.SectionName);
    options.Enabled = section.GetValue(nameof(RequestLoggingOptions.Enabled), options.Enabled);
    options.Directory = section.GetValue(nameof(RequestLoggingOptions.Directory), options.Directory) ?? options.Directory;
});
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonSerializerContext.Default);
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IReadOnlyList<IAiProvider>>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    return CreateProviders(configuration, httpClientFactory);
});
builder.Services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
builder.Services.AddSingleton<ResponsesStore>();
builder.Services.AddSingleton(sp =>
{
    var overridesPath = Path.Combine(AppContext.BaseDirectory, "model-overrides.json");
    return new ModelOverridesStore(overridesPath);
});

builder.Services.AddSingleton<ImageVisionRelay>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var registry = sp.GetRequiredService<IAiProviderRegistry>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var section = configuration.GetSection(ImageVisionRelayOptions.SectionName);
    var options = new ImageVisionRelayOptions
    {
        Enabled = section.GetValue(nameof(ImageVisionRelayOptions.Enabled), true),
        VisionModel = section.GetValue(nameof(ImageVisionRelayOptions.VisionModel), "") ?? ""
    };
    return new ImageVisionRelay(registry, options, httpClientFactory);
});

var app = builder.Build();
// 加载用户自定义的模型详情覆盖数据。
await app.Services.GetRequiredService<ModelOverridesStore>().LoadAsync();

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

// 健康检查只返回本地状态，不访问上游，避免健康探测消耗模型厂商 API。
app.MapGet("/health", (IAiProviderRegistry registry) =>
{
    return Results.Json(new HealthResponse(
        "ok",
        registry.Providers.Select(x => new ProviderSummary(x.Name, x.Family)).ToArray()),
        ApiJsonSerializerContext.Default.HealthResponse);
});

// Ollama 版本接口：用于客户端探测服务版本，响应保持 Ollama /api/version 的简洁格式。
app.MapGet("/api/version", () =>
{
    return Results.Json(
        new VersionResponse(OllamaVersion),
        ApiJsonSerializerContext.Default.VersionResponse);
});

// Ollama 模型列表接口：把厂商无关的模型元数据转换成 Ollama /api/tags 响应格式。
app.MapGet("/api/tags", async (IAiProviderRegistry registry, CancellationToken cancellationToken) =>
{
    var models = await registry.GetAllModelsAsync(cancellationToken);
    return Results.Json(new OllamaTagsResponse(
        models.Select(x => ToOllamaTag(x.Provider, x.Model)).ToArray()),
        ApiJsonSerializerContext.Default.OllamaTagsResponse);
});

// Ollama 模型详情接口：模型不存在时返回 404，避免客户端误以为已经使用指定模型。
app.MapPost("/api/show", async (HttpContext context, IAiProviderRegistry registry, ModelOverridesStore overridesStore) =>
{
    var cancellationToken = context.RequestAborted;
    var modelName = await ReadModelName(context.Request, cancellationToken);
    if (string.IsNullOrWhiteSpace(modelName))
        return Error("model is required", StatusCodes.Status400BadRequest);

    if (!IsExternalModelName(modelName))
        return Error("model must use 'provider/model' format", StatusCodes.Status400BadRequest);

    var resolved = await registry.ResolveModelAsync(modelName, cancellationToken);
    if (resolved is null)
        return Error($"model '{modelName}' not found", StatusCodes.Status404NotFound);

    var overrides = overridesStore.Get(modelName);
    return Results.Json(
        ToOllamaShow(resolved.Value.Provider, resolved.Value.Model, overrides),
        ApiJsonSerializerContext.Default.OllamaShowResponse);
});

// 模型详情覆盖接口
app.MapGet("/api/model-overrides", (ModelOverridesStore overridesStore) =>
{
    return Results.Json(overridesStore.GetAll(), ModelOverridesJsonContext.Default.ConcurrentDictionaryStringModelOverride);
});

app.MapPut("/api/model-overrides/{provider}/{model}", async (string provider, string model, ModelOverride body, ModelOverridesStore overridesStore) =>
{
    var key = $"{provider}/{Uri.UnescapeDataString(model)}";
    await overridesStore.SetAsync(key, body);
    return Results.Ok(body);
});

app.MapDelete("/api/model-overrides/{provider}/{model}", async (string provider, string model, ModelOverridesStore overridesStore) =>
{
    var key = $"{provider}/{Uri.UnescapeDataString(model)}";
    return await overridesStore.RemoveAsync(key) ? Results.Ok() : Results.NotFound();
});

// OpenAI 兼容模型列表接口：把厂商无关的模型元数据转换成 OpenAI /v1/models 响应格式。
app.MapGet("/v1/models", async (IAiProviderRegistry registry, CancellationToken cancellationToken) =>
{
    var models = await registry.GetAllModelsAsync(cancellationToken);
    return Results.Json(new OpenAiModelsResponse(
        "list",
        models.Select(x => ToOpenAiModel(x.Provider, x.Model)).ToArray()),
        ApiJsonSerializerContext.Default.OpenAiModelsResponse);
});

// OpenAI 兼容单个模型查询接口：按 provider/model 路径探测指定模型是否存在。
app.MapGet("/v1/models/{*model}", async (string model, IAiProviderRegistry registry, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(model) || !IsExternalModelName(model))
        return Error("model must use 'provider/model' format", StatusCodes.Status400BadRequest);

    var resolved = await registry.ResolveModelAsync(model, cancellationToken);
    if (resolved is null)
        return Error($"model '{model}' not found", StatusCodes.Status404NotFound);

    return Results.Json(
        ToOpenAiModel(resolved.Value.Provider, resolved.Value.Model),
        ApiJsonSerializerContext.Default.OpenAiModel);
});

// OpenAI 兼容聊天接口：provider 负责厂商转发，这一层只做通用校验和响应透传。
app.MapPost("/v1/chat/completions", async (HttpContext context, IAiProviderRegistry registry, ImageVisionRelay imageVisionRelay, ModelOverridesStore overridesStore) =>
{
    JsonDocument request;
    try
    {
        request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    }
    catch (JsonException)
    {
        return Error("request body must be valid JSON", StatusCodes.Status400BadRequest);
    }

    using (request)
    {
        var root = request.RootElement;
        var requestedModel = TryGetString(root, "model");
        if (string.IsNullOrWhiteSpace(requestedModel))
            return Error("model is required", StatusCodes.Status400BadRequest);

        if (!IsExternalModelName(requestedModel))
            return Error("model must use 'provider/model' format", StatusCodes.Status400BadRequest);

        var resolved = await registry.ResolveModelAsync(requestedModel, context.RequestAborted);
        if (resolved is null)
            return Error($"model '{requestedModel}' not found", StatusCodes.Status404NotFound);

        var provider = resolved.Value.Provider;
        // 图片中继按模型显式 opt-in：只有在该模型覆盖值里勾选了 imageRelay 才走中继
        // （勾选时 vision 仅作标记，不参与判断）；未勾选时不拦截，原样转发给上游
        // （视觉模型原生支持图片；纯文本模型上游可能因 image_url 自行报错）。
        JsonDocument? translatedRequest = null;
        if (RequestContainsImages(root))
        {
            var overrides = overridesStore.Get(requestedModel);
            if (overrides?.ImageRelay == true)
            {
                if (!imageVisionRelay.Enabled)
                    return Error(
                        "Image vision relay is enabled for this model but not configured. " +
                        "Set ImageVisionRelay:VisionModel with a vision-capable model.",
                        StatusCodes.Status400BadRequest);

                translatedRequest = await imageVisionRelay.TranslateImagesAsync(request, context.RequestAborted);
                if (translatedRequest is null)
                {
                    return Error(
                        $"{requestedModel} image vision relay failed. " +
                        "Configure ImageVisionRelay:VisionModel with a vision-capable model.",
                        StatusCodes.Status400BadRequest);
                }
            }
        }

        var effectiveRequest = translatedRequest ?? request;
        var isStream = TryGetBoolean(effectiveRequest.RootElement, "stream");
        using var upstreamRequest = RewriteModel(effectiveRequest, resolved.Value.UpstreamModel);
        translatedRequest?.Dispose();
        await using var response = isStream
            ? await provider.StreamChatCompletionAsync(upstreamRequest, context.RequestAborted)
            : await provider.CreateChatCompletionAsync(upstreamRequest, context.RequestAborted);

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;

        // 成功的流式响应按字节透传，保留上游 SSE 帧格式。
        if (isStream && response.StatusCode is >= 200 and < 300)
        {
            await using var stream = await response.ReadStreamAsync(context.RequestAborted);
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
            return Results.Empty;
        }

        await context.Response.WriteAsync(response.Body ?? "", context.RequestAborted);
        return Results.Empty;
    }
});

// Responses API 兼容接口：直接转发到上游 /v1/responses，仅把 model 重写为上游模型名，
// 请求按需经图片中继翻译（勾选 imageRelay 的模型），响应原样透传；流式按字节透传。
app.MapPost("/v1/responses", (HttpContext context, IAiProviderRegistry registry, ModelOverridesStore overridesStore, ImageVisionRelay imageVisionRelay, ResponsesStore store) =>
    ResponsesApi.HandleCreateAsync(context, registry, overridesStore, imageVisionRelay, store));

// Responses 查询接口：读取内存中缓存的非流式响应（最多保留 200 条）。
app.MapGet("/v1/responses/{responseId}", (HttpContext context, ResponsesStore store) =>
    ResponsesApi.HandleRetrieveAsync(context, store));
Console.WriteLine("Ollama AI Proxy API");
var startupRegistry = app.Services.GetRequiredService<IAiProviderRegistry>();
Console.WriteLine($"Providers: {string.Join(", ", startupRegistry.Providers.Select(x => $"{x.Name}/{x.Family}"))}");
Console.WriteLine($"URL:      {testPageUrl}");
var startupRelay = app.Services.GetRequiredService<ImageVisionRelay>();
Console.WriteLine($"Vision relay: {(startupRelay.Enabled ? $"on -> {startupRelay.VisionModel}" : "off")}");

if (ShouldOpenTestPage(app.Configuration, app.Environment))
{
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(testPageUrl));
}

app.Run();

static bool ShouldOpenTestPage(IConfiguration configuration, IWebHostEnvironment environment) =>
    configuration.GetValue("OpenTestPageOnStart", environment.IsDevelopment());

static IResult Error(string message, int statusCode) =>
    Results.Json(
        new ApiErrorResponse(message),
        ApiJsonSerializerContext.Default.ApiErrorResponse,
        statusCode: statusCode);

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not open test page automatically: {ex.Message}");
    }
}

static IReadOnlyList<IAiProvider> CreateProviders(IConfiguration configuration, IHttpClientFactory httpClientFactory)
{
    var providers = new List<IAiProvider>();

    foreach (var options in ReadDeepSeekOptions(configuration.GetSection(DeepSeekOptions.SectionName)))
    {
        TryAddProvider(providers, () => new DeepSeekProvider(CreateProviderHttpClient(httpClientFactory), options));
    }

    foreach (var options in ReadOpenAIOptions(configuration.GetSection(OpenAIOptions.SectionName)))
    {
        TryAddProvider(providers, () => new OpenAIProvider(CreateProviderHttpClient(httpClientFactory), options));
    }

    foreach (var options in ReadVolcengineCodingPlanOptions(configuration.GetSection(VolcengineCodingPlanOptions.SectionName)))
    {
        TryAddProvider(providers, () => new VolcengineCodingPlanProvider(CreateProviderHttpClient(httpClientFactory), options));
    }

    if (providers.Count == 0)
        throw new InvalidOperationException("At least one AI provider must be configured.");

    return providers;
}

// 配置了 provider 但没有任何有效 API Key（含 appsettings 中的空白占位）时跳过该 provider，
// 避免单个未配置 Key 的 provider 让整个代理无法启动。
static void TryAddProvider(List<IAiProvider> providers, Func<IAiProvider> factory)
{
    try
    {
        providers.Add(factory());
    }
    catch (InvalidOperationException ex) when (ex.Message.StartsWith("No API key configured", StringComparison.Ordinal))
    {
        Console.WriteLine($"Skipping provider: {ex.Message}");
    }
}

static HttpClient CreateProviderHttpClient(IHttpClientFactory httpClientFactory)
{
    var client = httpClientFactory.CreateClient();
    client.Timeout = TimeSpan.FromMinutes(5);
    return client;
}

static IReadOnlyList<DeepSeekOptions> ReadDeepSeekOptions(IConfigurationSection section)
{
    if (!section.Exists())
        return Array.Empty<DeepSeekOptions>();

    if (section.GetChildren().Any(x => int.TryParse(x.Key, out _)))
        return section.GetChildren().Select(ReadDeepSeekOption).ToArray();

    return new[] { ReadDeepSeekOption(section) };
}

static DeepSeekOptions ReadDeepSeekOption(IConfiguration section)
{
    var options = new DeepSeekOptions();
    options.Name = section.GetValue(nameof(DeepSeekOptions.Name), options.Name) ?? options.Name;
    options.BaseUrl = section.GetValue(nameof(DeepSeekOptions.BaseUrl), options.BaseUrl) ?? options.BaseUrl;
    options.ApiKeys = section.GetSection("ApiKeys").Get<string[]>() ?? [];
    return options;
}

static IReadOnlyList<OpenAIOptions> ReadOpenAIOptions(IConfigurationSection section)
{
    if (!section.Exists())
        return Array.Empty<OpenAIOptions>();

    if (section.GetChildren().Any(x => int.TryParse(x.Key, out _)))
        return section.GetChildren().Select(ReadOpenAIOption).ToArray();

    return new[] { ReadOpenAIOption(section) };
}

static OpenAIOptions ReadOpenAIOption(IConfiguration section)
{
    var options = new OpenAIOptions();
    options.Name = section.GetValue(nameof(OpenAIOptions.Name), options.Name) ?? options.Name;
    options.BaseUrl = section.GetValue(nameof(OpenAIOptions.BaseUrl), options.BaseUrl) ?? options.BaseUrl;
    options.ApiKeys = section.GetSection("ApiKeys").Get<string[]>() ?? [];
    return options;
}

static IReadOnlyList<VolcengineCodingPlanOptions> ReadVolcengineCodingPlanOptions(IConfigurationSection section)
{
    if (!section.Exists())
        return Array.Empty<VolcengineCodingPlanOptions>();

    if (section.GetChildren().Any(x => int.TryParse(x.Key, out _)))
        return section.GetChildren().Select(ReadVolcengineCodingPlanOption).ToArray();

    return new[] { ReadVolcengineCodingPlanOption(section) };
}

static VolcengineCodingPlanOptions ReadVolcengineCodingPlanOption(IConfiguration section)
{
    var options = new VolcengineCodingPlanOptions();
    options.Name = section.GetValue(nameof(VolcengineCodingPlanOptions.Name), options.Name) ?? options.Name;
    options.BaseUrl = section.GetValue(nameof(VolcengineCodingPlanOptions.BaseUrl), options.BaseUrl) ?? options.BaseUrl;
    options.ApiKeys = section.GetSection("ApiKeys").Get<string[]>() ?? [];
    return options;
}

// 读取 Ollama /api/show 可选的 model 字段；非法 JSON 在这里按“未提供 model”处理。
static async Task<string?> ReadModelName(HttpRequest request, CancellationToken cancellationToken)
{
    if (request.ContentLength is 0 or null)
        return null;

    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        return TryGetString(doc.RootElement, "model");
    }
    catch (JsonException)
    {
        return null;
    }
}

// 将厂商无关模型信息转换成 Ollama /api/tags 中的单个模型项。
static OllamaTag ToOllamaTag(IAiProvider provider, AiModel model) => new(
    ToExternalModelName(provider, model),
    ToExternalModelName(provider, model),
    model.ModifiedAt,
    model.Size,
    model.Digest,
    ToOllamaDetails(model.Details));

// 将厂商无关模型信息转换成 OpenAI /v1/models 中的单个模型对象。
static OpenAiModel ToOpenAiModel(IAiProvider provider, AiModel model) => new(
    ToExternalModelName(provider, model),
    model.Object,
    model.Created,
    model.OwnedBy);

// 将厂商无关模型信息转换成 Ollama /api/show 的模型详情响应。
static OllamaShowResponse ToOllamaShow(IAiProvider provider, AiModel model, ModelOverride? overrides = null)
{
    var arch = overrides?.Architecture ?? model.ModelInfo.Architecture;
    var ctxLen = overrides?.ContextLength ?? model.ModelInfo.ContextLength;
    var maxOut = overrides?.MaxOutputTokens ?? model.ModelInfo.MaxOutputTokens;
    var paramCount = overrides?.ParameterCount ?? model.ModelInfo.ParameterCount;
    var activeCount = overrides?.ActiveParameterCount ?? model.ModelInfo.ActiveParameterCount;
    var textOnly = overrides?.TextOnly ?? model.ModelInfo.TextOnly;
    var deprecated = overrides?.Deprecated ?? model.ModelInfo.Deprecated;
    var imageRelay = overrides?.ImageRelay ?? false;
    var availability = overrides?.Availability ?? model.ModelInfo.Availability;
    var displayName = overrides?.DisplayName ?? model.DisplayName;
    var capabilities = overrides?.Capabilities ?? model.Capabilities;

    return new OllamaShowResponse(
        "",
        "",
        $"num_ctx {ctxLen}\nnum_predict {maxOut}",
        "",
        ToOllamaDetails(model.Details, overrides),
        new Dictionary<string, JsonElement>
        {
            ["general.name"] = JsonString(model.Id),
            ["general.display_name"] = JsonString(displayName),
            ["general.architecture"] = JsonString(arch),
            ["general.parameter_count"] = JsonLong(paramCount),
            [$"{arch}.active_parameter_count"] = JsonLong(activeCount),
            [$"{arch}.context_length"] = JsonInt(ctxLen),
            [$"{arch}.max_output_tokens"] = JsonInt(maxOut),
            [$"{arch}.text_only"] = JsonBool(textOnly),
            [$"{arch}.deprecated"] = JsonBool(deprecated),
            [$"{arch}.availability"] = JsonString(availability),
            [$"{arch}.image_relay"] = JsonBool(imageRelay)
        },
        capabilities,
        model.ModifiedAt);
}

static string ToExternalModelName(IAiProvider provider, AiModel model) => $"{provider.Name}/{model.Id}";

static bool IsExternalModelName(string model)
{
    var separatorIndex = model.IndexOf('/');
    return separatorIndex > 0 && separatorIndex < model.Length - 1;
}

static JsonDocument RewriteModel(JsonDocument request, string upstreamModel)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        foreach (var property in request.RootElement.EnumerateObject())
        {
            if (property.NameEquals("model"))
                writer.WriteString("model", upstreamModel);
            else
                property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    stream.Position = 0;
    return JsonDocument.Parse(stream);
}

static OllamaDetails ToOllamaDetails(AiModelDetails details, ModelOverride? overrides = null) => new(
    details.ParentModel,
    details.Format,
    details.Family,
    details.Families,
    overrides?.ParameterSize ?? details.ParameterSize,
    overrides?.QuantizationLevel ?? details.QuantizationLevel);

static JsonElement JsonString(string value) =>
    JsonSerializer.SerializeToElement(value, ApiJsonSerializerContext.Default.String);

static JsonElement JsonLong(long value) =>
    JsonSerializer.SerializeToElement(value, ApiJsonSerializerContext.Default.Int64);

static JsonElement JsonInt(int value) =>
    JsonSerializer.SerializeToElement(value, ApiJsonSerializerContext.Default.Int32);

static JsonElement JsonBool(bool value) =>
    JsonSerializer.SerializeToElement(value, ApiJsonSerializerContext.Default.Boolean);

// OpenAI 兼容的多模态消息会在 content parts 里使用 type=image_url。
static bool RequestContainsImages(JsonElement root)
{
    if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        return false;

    foreach (var message in messages.EnumerateArray())
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            continue;

        foreach (var part in content.EnumerateArray())
        {
            if (TryGetString(part, "type") == "image_url")
                return true;
        }
    }

    return false;
}

static string? TryGetString(JsonElement element, string propertyName) =>
    element.ValueKind == JsonValueKind.Object &&
    element.TryGetProperty(propertyName, out var prop) &&
    prop.ValueKind == JsonValueKind.String
        ? prop.GetString()
        : null;

static bool TryGetBoolean(JsonElement element, string propertyName) =>
    element.ValueKind == JsonValueKind.Object &&
    element.TryGetProperty(propertyName, out var prop) &&
    prop.ValueKind == JsonValueKind.True;
