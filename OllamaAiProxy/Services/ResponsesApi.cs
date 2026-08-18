using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaAiProxy.Contracts;
using OllamaAiProxy.Providers;

namespace OllamaAiProxy.Services;

/// <summary>
/// Responses API（/v1/responses）处理器。
/// 直接转发到上游的 /v1/responses 接口，不翻译请求和响应；只把 model 从 "provider/model"
/// 重写为上游模型名。流式响应按字节透传，保留上游 SSE 帧；非流式响应原样返回并缓存到
/// 内存，供 GET /v1/responses/{id} 查询。
/// </summary>
public static class ResponsesApi
{
    public static async Task HandleCreateAsync(HttpContext context, IAiProviderRegistry registry, ModelOverridesStore overridesStore, ImageVisionRelay imageVisionRelay, ResponsesStore store)
    {
        var cancellationToken = context.RequestAborted;

        JsonDocument request;
        try
        {
            request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, "request body must be valid JSON", StatusCodes.Status400BadRequest, cancellationToken);
            return;
        }

        using (request)
        {
            var root = request.RootElement;
            var requestedModel = TryGetString(root, "model");
            if (string.IsNullOrWhiteSpace(requestedModel))
            {
                await WriteErrorAsync(context, "model is required", StatusCodes.Status400BadRequest, cancellationToken);
                return;
            }

            if (!IsExternalModelName(requestedModel))
            {
                await WriteErrorAsync(context, "model must use 'provider/model' format", StatusCodes.Status400BadRequest, cancellationToken);
                return;
            }

            var resolved = await registry.ResolveModelAsync(requestedModel, cancellationToken);
            if (resolved is null)
            {
                await WriteErrorAsync(context, $"model '{requestedModel}' not found", StatusCodes.Status404NotFound, cancellationToken);
                return;
            }

            // 图片中继按模型显式 opt-in（与 /v1/chat/completions 一致）：只有勾选了 imageRelay
            // 的模型才走中继；未勾选时不拦截，原样转发给上游（上游可能因 input_image 自行报错）。
            var overrides = overridesStore.Get(requestedModel);
            JsonDocument? translatedRequest = null;
            if (ResponsesRequestContainsImages(root))
            {
                if (overrides?.ImageRelay == true)
                {
                    if (!imageVisionRelay.Enabled)
                    {
                        await WriteErrorAsync(
                            context,
                            "Image vision relay is enabled for this model but not configured. " +
                            "Set ImageVisionRelay:VisionModel with a vision-capable model.",
                            StatusCodes.Status400BadRequest, cancellationToken);
                        return;
                    }

                    translatedRequest = await imageVisionRelay.TranslateResponsesImagesAsync(request, cancellationToken);
                    if (translatedRequest is null)
                    {
                        await WriteErrorAsync(
                            context,
                            $"{requestedModel} image vision relay failed. " +
                            "Configure ImageVisionRelay:VisionModel with a vision-capable model.",
                            StatusCodes.Status400BadRequest, cancellationToken);
                        return;
                    }
                }
            }

            var isStream = TryGetBoolean(root, "stream");
            var effectiveRequest = translatedRequest ?? request;
            // 思考强度默认值：模型覆盖里设置了档位且客户端未显式指定时，注入 reasoning.effort。
            var injected = ThinkingStrengthInjector.Apply(effectiveRequest, overrides?.ThinkingStrength, responses: true);
            using var upstreamRequest = RewriteModel(injected ?? effectiveRequest, resolved.Value.UpstreamModel);
            injected?.Dispose();
            translatedRequest?.Dispose();
            await using var upstream = isStream
                ? await resolved.Value.Provider.StreamResponseAsync(upstreamRequest, cancellationToken)
                : await resolved.Value.Provider.CreateResponseAsync(upstreamRequest, cancellationToken);

            context.Response.StatusCode = upstream.StatusCode;
            context.Response.ContentType = upstream.ContentType;

            // 成功的流式响应按字节透传，保留上游 SSE 帧格式。
            if (isStream && upstream.StatusCode is >= 200 and < 300)
            {
                await using var stream = await upstream.ReadStreamAsync(cancellationToken);
                await stream.CopyToAsync(context.Response.Body, cancellationToken);
                return;
            }

            var body = upstream.Body ?? "";
            await context.Response.WriteAsync(body, cancellationToken);

            // 非流式成功响应缓存到内存，供 GET /v1/responses/{id} 查询。
            if (upstream.StatusCode is >= 200 and < 300 && TryGetStringProperty(body, "id") is { Length: > 0 } responseId)
                store.Save(new StoredResponse(responseId, body, DateTimeOffset.UtcNow));
        }
    }

    public static async Task HandleRetrieveAsync(HttpContext context, ResponsesStore store)
    {
        var responseId = (string?)context.Request.RouteValues["responseId"] ?? "";
        var entry = store.Get(responseId);
        if (entry is null)
        {
            await WriteErrorAsync(
                context,
                $"No response found with id '{responseId}'",
                StatusCodes.Status404NotFound,
                context.RequestAborted,
                code: "response_not_found");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(entry.ResponseJson, context.RequestAborted);
    }

    private static string? TryGetStringProperty(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(propertyName, out var prop) &&
                   prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonDocument RewriteModel(JsonDocument request, string upstreamModel)
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

    private static bool IsExternalModelName(string model)
    {
        var separatorIndex = model.IndexOf('/');
        return separatorIndex > 0 && separatorIndex < model.Length - 1;
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryGetBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.True;

    // /v1/responses 的 input 数组里是否含 input_image 块（OpenAI Responses 多模态格式）。
    private static bool ResponsesRequestContainsImages(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (TryGetString(part, "type") == "input_image")
                    return true;
            }
        }

        return false;
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        string message,
        int statusCode,
        CancellationToken cancellationToken,
        string? code = null,
        string? type = null)
    {
        var error = new JsonObject { ["message"] = message };
        if (code is not null)
            error["code"] = code;
        if (type is not null)
            error["type"] = type;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(new JsonObject { ["error"] = error }.ToJsonString(), cancellationToken);
    }
}

/// <summary>Responses 响应的内存缓存，支持 GET /v1/responses/{id} 查询。</summary>
public sealed class ResponsesStore
{
    private const int MaxStoredResponses = 200;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StoredResponse> _responses = new(StringComparer.Ordinal);
    private readonly object _evictLock = new();

    public void Save(StoredResponse response)
    {
        _responses[response.Id] = response;

        if (_responses.Count <= MaxStoredResponses)
            return;

        lock (_evictLock)
        {
            if (_responses.Count <= MaxStoredResponses)
                return;

            var overflow = _responses.Count - MaxStoredResponses;
            foreach (var oldest in _responses.Values.OrderBy(x => x.CreatedAt).Take(overflow))
                _responses.TryRemove(oldest.Id, out _);
        }
    }

    public StoredResponse? Get(string responseId) =>
        _responses.TryGetValue(responseId, out var response) ? response : null;
}

/// <summary>一条已缓存的 Responses 响应（仅非流式成功响应）。</summary>
public sealed record StoredResponse(string Id, string ResponseJson, DateTimeOffset CreatedAt);