using System.Diagnostics;
using System.Text.Json;
using OllamaAiProxy.Contracts;
using OllamaAiProxy.Monitoring;
using OllamaAiProxy.Providers;

const int DefaultPort = 11434;

var builder = WebApplication.CreateSlimBuilder(args);
var port = builder.Configuration.GetValue("PORT", DefaultPort);
var testPageUrl = $"http://localhost:{port}/";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.Configure<RequestLoggingOptions>(builder.Configuration.GetSection(RequestLoggingOptions.SectionName));

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IReadOnlyList<IAiProvider>>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    return CreateProviders(configuration, httpClientFactory);
});
builder.Services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();

var app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

// 健康检查只返回本地状态，不访问上游，避免健康探测消耗模型厂商 API。
app.MapGet("/health", (IAiProviderRegistry registry) =>
{
    return Results.Ok(new
    {
        status = "ok",
        providers = registry.Providers.Select(x => new { name = x.Name, family = x.Family })
    });
});

// Ollama 模型列表接口：把厂商无关的模型元数据转换成 Ollama /api/tags 响应格式。
app.MapGet("/api/tags", async (IAiProviderRegistry registry, CancellationToken cancellationToken) =>
{
    var models = await registry.GetAllModelsAsync(cancellationToken);
    return Results.Json(new
    {
        models = models.Select(x => ToOllamaTag(x.Provider, x.Model))
    });
});

// Ollama 模型详情接口：模型不存在时返回 404，避免客户端误以为已经使用指定模型。
app.MapPost("/api/show", async (HttpContext context, IAiProviderRegistry registry) =>
{
    var cancellationToken = context.RequestAborted;
    var modelName = await ReadModelName(context.Request, cancellationToken);
    if (string.IsNullOrWhiteSpace(modelName))
        return Results.Json(new { error = "model is required" }, statusCode: StatusCodes.Status400BadRequest);

    if (!IsExternalModelName(modelName))
        return Results.Json(new { error = "model must use 'provider/model' format" }, statusCode: StatusCodes.Status400BadRequest);

    var resolved = await registry.ResolveModelAsync(modelName, cancellationToken);
    if (resolved is null)
        return Results.Json(new { error = $"model '{modelName}' not found" }, statusCode: StatusCodes.Status404NotFound);

    return Results.Json(ToOllamaShow(resolved.Value.Provider, resolved.Value.Model));
});

// OpenAI 兼容聊天接口：provider 负责厂商转发，这一层只做通用校验和响应透传。
app.MapPost("/v1/chat/completions", async (HttpContext context, IAiProviderRegistry registry) =>
{
    JsonDocument request;
    try
    {
        request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.Json(new { error = "request body must be valid JSON" }, statusCode: StatusCodes.Status400BadRequest);
    }

    using (request)
    {
        var root = request.RootElement;
        var requestedModel = TryGetString(root, "model");
        if (string.IsNullOrWhiteSpace(requestedModel))
            return Results.Json(new { error = "model is required" }, statusCode: StatusCodes.Status400BadRequest);

        if (!IsExternalModelName(requestedModel))
            return Results.Json(new { error = "model must use 'provider/model' format" }, statusCode: StatusCodes.Status400BadRequest);

        var resolved = await registry.ResolveModelAsync(requestedModel, context.RequestAborted);
        if (resolved is null)
            return Results.Json(new { error = $"model '{requestedModel}' not found" }, statusCode: StatusCodes.Status404NotFound);

        var provider = resolved.Value.Provider;
        // 图片输入是否允许由 provider 能力决定：OpenAI 可放行，DeepSeek 仍拒绝。
        if (RequestContainsImages(root) && !provider.SupportsImages)
            return Results.Json(new { error = $"{provider.Name} provider does not support image inputs." }, statusCode: StatusCodes.Status400BadRequest);

        var isStream = TryGetBoolean(root, "stream");
        using var upstreamRequest = RewriteModel(request, resolved.Value.UpstreamModel);
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

Console.WriteLine("Ollama AI Proxy API");
var startupRegistry = app.Services.GetRequiredService<IAiProviderRegistry>();
Console.WriteLine($"Providers: {string.Join(", ", startupRegistry.Providers.Select(x => $"{x.Name}/{x.Family}"))}");
Console.WriteLine($"URL:      {testPageUrl}");

if (ShouldOpenTestPage(app.Configuration, app.Environment))
{
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(testPageUrl));
}

app.Run();

static bool ShouldOpenTestPage(IConfiguration configuration, IWebHostEnvironment environment) =>
    configuration.GetValue("OpenTestPageOnStart", environment.IsDevelopment());

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

    foreach (var options in BindOptionsList<DeepSeekOptions>(configuration.GetSection(DeepSeekOptions.SectionName), new DeepSeekOptions()))
    {
        providers.Add(new DeepSeekProvider(CreateProviderHttpClient(httpClientFactory), options));
    }

    foreach (var options in BindOptionsList<OpenAIOptions>(configuration.GetSection(OpenAIOptions.SectionName), new OpenAIOptions()))
    {
        providers.Add(new OpenAIProvider(CreateProviderHttpClient(httpClientFactory), options));
    }

    if (providers.Count == 0)
        throw new InvalidOperationException("At least one AI provider must be configured.");

    return providers;
}

static HttpClient CreateProviderHttpClient(IHttpClientFactory httpClientFactory)
{
    var client = httpClientFactory.CreateClient();
    client.Timeout = TimeSpan.FromMinutes(5);
    return client;
}

static IReadOnlyList<TOptions> BindOptionsList<TOptions>(IConfigurationSection section, TOptions fallback)
    where TOptions : class, new()
{
    if (!section.Exists())
        return Array.Empty<TOptions>();

    if (section.GetChildren().Any(x => int.TryParse(x.Key, out _)))
        return section.Get<TOptions[]>() ?? Array.Empty<TOptions>();

    var single = new TOptions();
    section.Bind(single);
    return new[] { single };
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
static object ToOllamaTag(IAiProvider provider, AiModel model) => new
{
    name = ToExternalModelName(provider, model),
    model = ToExternalModelName(provider, model),
    modified_at = model.ModifiedAt,
    size = model.Size,
    digest = model.Digest,
    details = ToOllamaDetails(model.Details)
};

// 将厂商无关模型信息转换成 Ollama /api/show 的模型详情响应。
static object ToOllamaShow(IAiProvider provider, AiModel model) => new
{
    license = "",
    modelfile = "",
    parameters = $"num_ctx {model.ModelInfo.ContextLength}\nnum_predict {model.ModelInfo.MaxOutputTokens}",
    template = "",
    details = ToOllamaDetails(model.Details),
    model_info = new Dictionary<string, object?>
    {
        ["general.name"] = model.Id,
        ["general.display_name"] = model.DisplayName,
        ["general.architecture"] = model.ModelInfo.Architecture,
        ["general.parameter_count"] = model.ModelInfo.ParameterCount,
        [$"{model.ModelInfo.Architecture}.active_parameter_count"] = model.ModelInfo.ActiveParameterCount,
        [$"{model.ModelInfo.Architecture}.context_length"] = model.ModelInfo.ContextLength,
        [$"{model.ModelInfo.Architecture}.max_output_tokens"] = model.ModelInfo.MaxOutputTokens,
        [$"{model.ModelInfo.Architecture}.text_only"] = model.ModelInfo.TextOnly,
        [$"{model.ModelInfo.Architecture}.deprecated"] = model.ModelInfo.Deprecated,
        [$"{model.ModelInfo.Architecture}.availability"] = model.ModelInfo.Availability
    },
    capabilities = model.Capabilities,
    modified_at = model.ModifiedAt
};

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

static object ToOllamaDetails(AiModelDetails details) => new
{
    parent_model = details.ParentModel,
    format = details.Format,
    family = details.Family,
    families = details.Families,
    parameter_size = details.ParameterSize,
    quantization_level = details.QuantizationLevel
};

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
