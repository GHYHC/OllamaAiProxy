using System.Text;
using System.Text.Json;
using OllamaAiProxy.Contracts;
using OllamaAiProxy.Providers;

namespace OllamaAiProxy.Services;

/// <summary>
/// 图片视觉中继：当目标模型不支持图片输入时，先用一个视觉模型把图片描述成文字
/// （OCR 文字 + 画面描述），再把消息里的 image_url 块替换成文本块，让纯文本模型也能"看图"。
/// 视觉模型通过 IAiProviderRegistry 解析，复用已有 provider 的 Key 轮换与转发逻辑。
/// </summary>
public sealed class ImageVisionRelay
{
    /// <summary>默认识图提示词：提取所有可见文字再描述画面，输出结构化结果。</summary>
    public const string DefaultPrompt =
        "Extract ALL visible text precisely, then describe. Output:\n" +
        "[IMAGE ANALYSIS]\n" +
        "Text content: <text or none>\n" +
        "Visual description: <description>";

    private const string FailedDescription =
        "[IMAGE ANALYSIS]\nText content: (recognition failed)\nVisual description: (recognition failed)";

    private readonly IAiProviderRegistry _registry;
    private readonly ImageVisionRelayOptions _options;

    public ImageVisionRelay(IAiProviderRegistry registry, ImageVisionRelayOptions options)
    {
        _registry = registry;
        _options = options;
    }

    /// <summary>中继是否可用：已启用且配置了视觉模型。</summary>
    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.VisionModel);

    /// <summary>当前配置的视觉模型（provider/model 格式），用于启动日志。</summary>
    public string VisionModel => _options.VisionModel;

    private string Prompt => string.IsNullOrWhiteSpace(_options.Prompt) ? DefaultPrompt : _options.Prompt;

    /// <summary>
    /// 把请求里的 image_url 块替换成视觉模型生成的文字描述。没有图片或中继不可用时返回 null。
    /// </summary>
    public async Task<JsonDocument?> TranslateImagesAsync(JsonDocument request, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return null;

        var root = request.RootElement;
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        var imageUrls = CollectImageUrls(messages);
        if (imageUrls.Count == 0)
            return null;

        var descriptions = new string[imageUrls.Count];
        for (var i = 0; i < imageUrls.Count; i++)
            descriptions[i] = await AnalyzeImageAsync(imageUrls[i], cancellationToken);

        return RebuildRequest(root, messages, descriptions);
    }

    /// <summary>
    /// /v1/responses 版本的图片中继：把 input 数组里的 input_image 块替换成视觉模型生成的文字描述。
    /// input 为字符串、没有图片或中继不可用时返回 null。
    /// </summary>
    public async Task<JsonDocument?> TranslateResponsesImagesAsync(JsonDocument request, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return null;

        var root = request.RootElement;
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
            return null;

        var imageUrls = CollectResponsesImageUrls(input);
        if (imageUrls.Count == 0)
            return null;

        var descriptions = new string[imageUrls.Count];
        for (var i = 0; i < imageUrls.Count; i++)
            descriptions[i] = await AnalyzeImageAsync(imageUrls[i], cancellationToken);

        return RebuildResponsesRequest(root, input, descriptions);
    }

    // 按文档顺序收集 input 数组里所有 input_image 块的图片地址，用于逐个识图。
    private static List<string> CollectResponsesImageUrls(JsonElement input)
    {
        var urls = new List<string>();
        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (TryGetString(part, "type") == "input_image")
                    urls.Add(ExtractImageUrl(part));
            }
        }
        return urls;
    }

    // 重建请求：复制所有字段，仅把 input 里的 input_image 块替换成对应文字描述。
    private static JsonDocument RebuildResponsesRequest(JsonElement root, JsonElement input, IReadOnlyList<string> descriptions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("input"))
                    WriteResponsesInput(writer, input, descriptions);
                else
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static void WriteResponsesInput(Utf8JsonWriter writer, JsonElement input, IReadOnlyList<string> descriptions)
    {
        writer.WriteStartArray("input");
        var descIndex = 0;
        foreach (var item in input.EnumerateArray())
        {
            writer.WriteStartObject();
            foreach (var property in item.EnumerateObject())
            {
                if (property.NameEquals("content") && property.Value.ValueKind == JsonValueKind.Array)
                    WriteResponsesContent(writer, property.Value, descriptions, ref descIndex);
                else
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteResponsesContent(Utf8JsonWriter writer, JsonElement content, IReadOnlyList<string> descriptions, ref int descIndex)
    {
        writer.WriteStartArray("content");
        foreach (var part in content.EnumerateArray())
        {
            if (TryGetString(part, "type") == "input_image")
            {
                var description = descIndex < descriptions.Count ? descriptions[descIndex] : FailedDescription;
                descIndex++;
                writer.WriteStartObject();
                writer.WriteString("type", "input_text");
                writer.WriteString("text", description);
                writer.WriteEndObject();
            }
            else
            {
                part.WriteTo(writer);
            }
        }
        writer.WriteEndArray();
    }

    private async Task<string> AnalyzeImageAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return FailedDescription;

        var resolved = await _registry.ResolveModelAsync(_options.VisionModel, cancellationToken);
        if (resolved is null)
            return FailedDescription;

        var (provider, _, upstreamModel) = resolved.Value;
        using var visionRequest = BuildVisionRequest(upstreamModel, imageUrl, Prompt);
        await using var response = await provider.CreateChatCompletionAsync(visionRequest, cancellationToken);

        if (response.StatusCode is < 200 or >= 300 || string.IsNullOrEmpty(response.Body))
            return FailedDescription;

        return ExtractContent(response.Body) ?? FailedDescription;
    }

    private static JsonDocument BuildVisionRequest(string model, string imageUrl, string prompt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", imageUrl);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("stream", false);
            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static string? ExtractContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;

            if (!choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
                return null;

            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => ConcatTextParts(content),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ConcatTextParts(JsonElement content)
    {
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var type) && type.ValueEquals("text") &&
                part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }

    // 按文档顺序收集所有 image_url 块的图片地址，用于逐个识图。
    private static List<string> CollectImageUrls(JsonElement messages)
    {
        var urls = new List<string>();
        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (TryGetString(part, "type") == "image_url")
                    urls.Add(ExtractImageUrl(part));
            }
        }
        return urls;
    }

    private static string ExtractImageUrl(JsonElement part)
    {
        if (!part.TryGetProperty("image_url", out var imageUrl))
            return "";

        if (imageUrl.ValueKind == JsonValueKind.Object &&
            imageUrl.TryGetProperty("url", out var url) &&
            url.ValueKind == JsonValueKind.String)
            return url.GetString() ?? "";

        if (imageUrl.ValueKind == JsonValueKind.String)
            return imageUrl.GetString() ?? "";

        return "";
    }

    // 重建请求：复制所有字段，仅把 messages 里的 image_url 块替换成对应文字描述。
    private static JsonDocument RebuildRequest(JsonElement root, JsonElement messages, IReadOnlyList<string> descriptions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("messages"))
                    WriteMessages(writer, messages, descriptions);
                else
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static void WriteMessages(Utf8JsonWriter writer, JsonElement messages, IReadOnlyList<string> descriptions)
    {
        writer.WriteStartArray("messages");
        var descIndex = 0;
        foreach (var message in messages.EnumerateArray())
        {
            writer.WriteStartObject();
            foreach (var property in message.EnumerateObject())
            {
                if (property.NameEquals("content") && property.Value.ValueKind == JsonValueKind.Array)
                    WriteContent(writer, property.Value, descriptions, ref descIndex);
                else
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteContent(Utf8JsonWriter writer, JsonElement content, IReadOnlyList<string> descriptions, ref int descIndex)
    {
        writer.WriteStartArray("content");
        foreach (var part in content.EnumerateArray())
        {
            if (TryGetString(part, "type") == "image_url")
            {
                var description = descIndex < descriptions.Count ? descriptions[descIndex] : FailedDescription;
                descIndex++;
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", description);
                writer.WriteEndObject();
            }
            else
            {
                part.WriteTo(writer);
            }
        }
        writer.WriteEndArray();
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
