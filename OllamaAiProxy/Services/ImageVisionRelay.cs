using System.Diagnostics;
using System.Security.Cryptography;
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

    /// <summary>聚焦提示最长保留字符数，避免把整段用户消息灌进视觉提示词。</summary>
    private const int MaxFocusHintLength = 500;

    /// <summary>追加在基础提示词后的意图说明，提醒模型仍要提取全部文字并保持输出格式。</summary>
    private const string FocusHintInstruction =
        "\n\nViewer intent for this image (focus the description on what matters for this intent, " +
        "but still extract ALL visible text and keep the output format above):\n";

    /// <summary>单张图片识图超时；超时后取消并按可重试处理。</summary>
    private static readonly TimeSpan VisionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>识图失败后的最大重试次数（不含首次），仅对 5xx 和超时重试。</summary>
    private const int VisionMaxRetries = 2;

    /// <summary>重试之间的退避间隔，随尝试递增。</summary>
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];

    /// <summary>远程图片拉取超时。</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>远程图片拉取的最大字节数，超过则放弃转 data URL。</summary>
    private const int MaxFetchBytes = 10 * 1024 * 1024;

    private readonly IAiProviderRegistry _registry;
    private readonly ImageVisionRelayOptions _options;
    private readonly HttpClient _httpClient;
    private readonly DescriptionCache _cache = new();

    public ImageVisionRelay(IAiProviderRegistry registry, ImageVisionRelayOptions options, IHttpClientFactory httpClientFactory)
    {
        _registry = registry;
        _options = options;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>中继是否可用：已启用且配置了视觉模型。</summary>
    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.VisionModel);

    /// <summary>当前配置的视觉模型（provider/model 格式），用于启动日志。</summary>
    public string VisionModel => _options.VisionModel;

    /// <summary>
    /// 实际发给视觉模型的提示词：有当轮意图时在基础提示词后追加聚焦说明，否则原样返回。
    /// </summary>
    private string EffectivePrompt(string focusHint)
    {
        if (string.IsNullOrWhiteSpace(focusHint))
            return DefaultPrompt;

        var hint = focusHint.Trim();
        if (hint.Length > MaxFocusHintLength)
            hint = hint[..MaxFocusHintLength] + "...";

        return DefaultPrompt + FocusHintInstruction + hint;
    }

    // 中继日志统一带前缀写到控制台，和 RequestLoggingMiddleware 保持一致。
    private static void Log(string message) => Console.WriteLine($"[ImageVisionRelay] {message}");

    // 日志里图片地址的简短标记：data URL 只显示前缀，长 URL 截断，避免刷屏。
    private static string ImageLabel(string imageUrl)
    {
        if (imageUrl.StartsWith("data:", StringComparison.Ordinal))
            return imageUrl.Length <= 32 ? imageUrl : "data:" + imageUrl.Substring(5, 27) + "...";
        return imageUrl.Length <= 96 ? imageUrl : imageUrl.Substring(0, 96) + "...";
    }

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

        var images = CollectImagesWithFocus(messages);
        if (images.Count == 0)
            return null;

        var tasks = new Task<string>[images.Count];
        for (var i = 0; i < images.Count; i++)
            tasks[i] = AnalyzeImageAsync(images[i].Url, images[i].FocusHint, cancellationToken);
        var descriptions = await Task.WhenAll(tasks);

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

        var images = CollectResponsesImagesWithFocus(input);
        if (images.Count == 0)
            return null;

        var tasks = new Task<string>[images.Count];
        for (var i = 0; i < images.Count; i++)
            tasks[i] = AnalyzeImageAsync(images[i].Url, images[i].FocusHint, cancellationToken);
        var descriptions = await Task.WhenAll(tasks);

        return RebuildResponsesRequest(root, input, descriptions);
    }

    // 按文档顺序收集 input 数组里所有 input_image 块及其聚焦提示：优先取图片所在用户条目的文字，
    // 否则回退到最近一条用户条目文字（system/assistant 不参与，避免污染意图）。
    private static List<(string Url, string FocusHint)> CollectResponsesImagesWithFocus(JsonElement input)
    {
        var result = new List<(string, string)>();
        var lastUserText = "";

        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            var isUser = TryGetString(item, "role") == "user";
            var itemText = ConcatResponsesTextParts(content);
            if (isUser && !string.IsNullOrWhiteSpace(itemText))
                lastUserText = itemText;

            foreach (var part in content.EnumerateArray())
            {
                if (TryGetString(part, "type") == "input_image")
                {
                    var hint = (isUser && !string.IsNullOrWhiteSpace(itemText)) ? itemText : lastUserText;
                    result.Add((ExtractImageUrl(part), hint));
                }
            }
        }
        return result;
    }

    // 拼接 responses 内容数组里的 input_text 文字，用作聚焦提示来源。
    private static string ConcatResponsesTextParts(JsonElement content)
    {
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (TryGetString(part, "type") == "input_text" &&
                part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                sb.Append(text.GetString());
        }
        return sb.ToString();
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

    private async Task<string> AnalyzeImageAsync(string imageUrl, string focusHint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return FailedDescription;

        var key = CacheKey(imageUrl, focusHint);
        if (_cache.TryGet(key, out var cached))
        {
            Log($"cache hit {ImageLabel(imageUrl)}");
            return cached;
        }

        (IAiProvider Provider, AiModel Model, string UpstreamModel)? resolved;
        try
        {
            resolved = await _registry.ResolveModelAsync(_options.VisionModel, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 客户端已取消：传播，不记 error
        }
        catch (Exception ex)
        {
            Log($"vision model resolve error: {ex.GetType().Name}");
            return FailedDescription;
        }
        if (resolved is null)
        {
            Log($"vision model not resolved: {_options.VisionModel}");
            return FailedDescription;
        }

        var (provider, _, upstreamModel) = resolved.Value;
        var effectiveUrl = await ResolveImageUrlAsync(imageUrl, cancellationToken);

        var description = FailedDescription;
        for (var attempt = 0; attempt <= VisionMaxRetries; attempt++)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(VisionTimeout);
                using var visionRequest = BuildVisionRequest(upstreamModel, effectiveUrl, EffectivePrompt(focusHint));
                await using var response = await provider.CreateChatCompletionAsync(visionRequest, cts.Token);

                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (response.StatusCode is >= 200 and < 300 && !string.IsNullOrEmpty(response.Body))
                {
                    description = ExtractContent(response.Body) ?? FailedDescription;
                    if (description != FailedDescription)
                        Log($"vision ok {ImageLabel(imageUrl)} attempt={attempt + 1} {ms:F0}ms");
                    break;
                }

                Log($"vision fail {ImageLabel(imageUrl)} attempt={attempt + 1} status={response.StatusCode} {ms:F0}ms");
                if (response.StatusCode is < 500 or >= 600)
                    break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Log($"vision timeout {ImageLabel(imageUrl)} attempt={attempt + 1} {ms:F0}ms");
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 客户端已取消请求：立即传播，不重试也不记 error
            }
            catch (Exception ex)
            {
                Log($"vision error {ImageLabel(imageUrl)} attempt={attempt + 1}: {ex.GetType().Name}");
            }

            if (attempt < VisionMaxRetries)
                await Task.Delay(RetryDelays[attempt], cancellationToken);
        }

        if (description != FailedDescription)
            _cache.Set(key, description);
        else
            Log($"vision gave up {ImageLabel(imageUrl)}");
        return description;
    }

    // 远程图片主动拉取并转 data URL，保证视觉模型能访问内网/localhost 等上游不可达的地址；
    // data URL 直接放行；拉取失败或过大则回退原地址交给上游处理。
    private async Task<string> ResolveImageUrlAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (!imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return imageUrl;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FetchTimeout);
            using var response = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log($"fetch fail {ImageLabel(imageUrl)} status={(int)response.StatusCode}");
                return imageUrl;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length > MaxFetchBytes)
            {
                Log($"fetch too large {ImageLabel(imageUrl)} {bytes.Length}B");
                return imageUrl;
            }

            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/png";
            Log($"fetched {ImageLabel(imageUrl)} {bytes.Length}B -> data url");
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"fetch timeout {ImageLabel(imageUrl)}");
            return imageUrl;
        }
        catch (Exception ex)
        {
            Log($"fetch error {ImageLabel(imageUrl)}: {ex.GetType().Name}");
            return imageUrl;
        }
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

    // 按文档顺序收集所有 image_url 块及其聚焦提示：优先取图片所在用户消息的文字，
    // 否则回退到最近一条用户消息文字（system/assistant 不参与，避免污染意图）。
    private static List<(string Url, string FocusHint)> CollectImagesWithFocus(JsonElement messages)
    {
        var result = new List<(string, string)>();
        var lastUserText = "";

        foreach (var message in messages.EnumerateArray())
        {
            var isUser = TryGetString(message, "role") == "user";
            var messageText = ExtractMessageText(message);
            if (isUser && !string.IsNullOrWhiteSpace(messageText))
                lastUserText = messageText;

            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (TryGetString(part, "type") == "image_url")
                {
                    var hint = (isUser && !string.IsNullOrWhiteSpace(messageText)) ? messageText : lastUserText;
                    result.Add((ExtractImageUrl(part), hint));
                }
            }
        }
        return result;
    }

    // 提取单条消息的文字内容：字符串 content 直接返回，数组 content 拼接所有 text 块。
    private static string ExtractMessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return "";

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => ConcatTextParts(content),
            _ => ""
        };
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

    // 缓存键：图片地址 + 聚焦提示的 SHA256，避免把超长 data URL 当 key 占内存。
    private static string CacheKey(string imageUrl, string focusHint)
    {
        var raw = imageUrl + "\u0001" + focusHint;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>识图结果内存缓存：带 TTL 与条数上限，多轮对话避免对同一图片重复识图。</summary>
    private sealed class DescriptionCache
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
        private const int MaxEntries = 200;
        private readonly Dictionary<string, (string Desc, DateTime Expires)> _items = new();
        private readonly object _lock = new();

        public bool TryGet(string key, out string value)
        {
            lock (_lock)
            {
                if (_items.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow)
                {
                    value = entry.Desc;
                    return true;
                }
            }
            value = "";
            return false;
        }

        public void Set(string key, string value)
        {
            lock (_lock)
            {
                if (_items.Count >= MaxEntries && !_items.ContainsKey(key))
                {
                    string? oldestKey = null;
                    var oldestExpires = DateTime.MaxValue;
                    foreach (var kvp in _items)
                    {
                        if (kvp.Value.Expires < oldestExpires)
                        {
                            oldestExpires = kvp.Value.Expires;
                            oldestKey = kvp.Key;
                        }
                    }
                    if (oldestKey != null)
                        _items.Remove(oldestKey);
                }
                _items[key] = (value, DateTime.UtcNow + Ttl);
            }
        }
    }
}
