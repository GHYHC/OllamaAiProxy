using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OllamaAiProxy.Contracts;
using OllamaAiProxy.Providers;
using OllamaAiProxy.Services;

var failed = 0;

void Check(bool cond, string name)
{
    if (cond) Console.WriteLine($"  PASS  {name}");
    else { Console.WriteLine($"  FAIL  {name}"); failed++; }
}

async Task Run(string name, Func<Task> body)
{
    Console.WriteLine($"\n== {name} ==");
    try { await body(); }
    catch (Exception ex) { Console.WriteLine($"  FAIL  unexpected {ex.GetType().Name}: {ex.Message}"); failed++; }
}

static JsonDocument J(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o));

static JsonDocument SingleImg(string url) => J(new
{
    model = "stub/text",
    messages = new[] { new { role = "user", content = new object[] { new { type = "image_url", image_url = new { url } } } } }
});

static List<string> ExtractTexts(JsonDocument doc)
{
    var list = new List<string>();
    foreach (var msg in doc.RootElement.GetProperty("messages").EnumerateArray())
    {
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
        foreach (var part in content.EnumerateArray())
            if (part.TryGetProperty("type", out var t) && t.ValueEquals("text") && part.TryGetProperty("text", out var tx))
                list.Add(tx.GetString() ?? "");
    }
    return list;
}

static List<string> ExtractResponsesTexts(JsonDocument doc)
{
    var list = new List<string>();
    foreach (var item in doc.RootElement.GetProperty("input").EnumerateArray())
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
        foreach (var part in content.EnumerateArray())
            if (part.TryGetProperty("type", out var t) && t.ValueEquals("input_text") && part.TryGetProperty("text", out var tx))
                list.Add(tx.GetString() ?? "");
    }
    return list;
}

static int FreePort()
{
    using var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}

static (string Url, Action Stop) StartServer(int status, byte[] body, string contentType)
{
    for (var attempt = 0; attempt < 10; attempt++)
    {
        var port = FreePort();
        var listener = new HttpListener();
        try { listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start(); }
        catch { continue; }
        _ = Task.Run(() =>
        {
            try
            {
                var ctx = listener.GetContext();
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                if (body.Length > 0) ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
            }
            catch { }
        });
        return ($"http://localhost:{port}/img", () => { try { listener.Stop(); } catch { } });
    }
    throw new InvalidOperationException("could not start test HTTP listener");
}

ImageVisionRelay NewRelay(StubProvider p) =>
    new(new StubRegistry(p), new ImageVisionRelayOptions { Enabled = true, VisionModel = "stub/vision" }, new SimpleHttpClientFactory());

// ---- tests ----

await Run("basic translation + document order (parallel)", async () =>
{
    var p = new StubProvider(); p.EnqueueEcho(); p.EnqueueEcho();
    var relay = NewRelay(p);
    using var req = J(new
    {
        model = "stub/text",
        messages = new[] { new { role = "user", content = new object[] {
            new { type = "image_url", image_url = new { url = "data:img-1" } },
            new { type = "text", text = "hi" },
            new { type = "image_url", image_url = new { url = "data:img-2" } } } } }
    });
    using var outDoc = await relay.TranslateImagesAsync(req, default);
    Check(outDoc is not null, "relay returns a translated document");
    var texts = ExtractTexts(outDoc!);
    Check(texts.Count == 3, "three content parts after replacement");
    Check(texts[0] == "OK data:img-1", "first image replaced in order");
    Check(texts[1] == "hi", "non-image text part preserved");
    Check(texts[2] == "OK data:img-2", "second image replaced in order");
    Check(p.Calls.Count == 2, "two vision calls made (parallel)");
});

await Run("focus hint from user text (#6 positive)", async () =>
{
    var p = new StubProvider(); p.EnqueueOk("desc");
    var relay = NewRelay(p);
    using var req = J(new
    {
        model = "stub/text",
        messages = new[] { new { role = "user", content = new object[] {
            new { type = "text", text = "what does the red error dialog say" },
            new { type = "image_url", image_url = new { url = "data:x" } } } } }
    });
    using var outDoc = await relay.TranslateImagesAsync(req, default);
    Check(p.Calls[0].Prompt.Contains("what does the red error dialog say"), "user text injected as focus hint");
    Check(p.Calls[0].Prompt.Contains("Viewer intent"), "focus instruction appended to prompt");
});

await Run("focus hint ignores system/assistant text (#6 negative)", async () =>
{
    var p = new StubProvider(); p.EnqueueOk("desc");
    var relay = NewRelay(p);
    using var req = J(new
    {
        model = "stub/text",
        messages = new object[] {
            new { role = "assistant", content = new[] { new { type = "text", text = "previous answer here" } } },
            new { role = "user", content = new object[] { new { type = "image_url", image_url = new { url = "data:y" } } } } }
    });
    using var outDoc = await relay.TranslateImagesAsync(req, default);
    Check(p.Calls[0].Prompt == ImageVisionRelay.DefaultPrompt, "default prompt used when user message has no text");
    Check(!p.Calls[0].Prompt.Contains("Viewer intent"), "no focus instruction when only assistant/system had text");
});

await Run("caching: identical second call served from cache (#1)", async () =>
{
    var p = new StubProvider(); p.EnqueueOk("cached");
    var relay = NewRelay(p);
    using var req = J(new
    {
        model = "stub/text",
        messages = new[] { new { role = "user", content = new object[] {
            new { type = "text", text = "q" },
            new { type = "image_url", image_url = new { url = "data:same" } } } } }
    });
    using var a = await relay.TranslateImagesAsync(req, default);
    using var b = await relay.TranslateImagesAsync(req, default);
    Check(p.Calls.Count == 1, "second identical call did not hit the vision provider");
    using var req2 = J(new
    {
        model = "stub/text",
        messages = new[] { new { role = "user", content = new object[] {
            new { type = "text", text = "different intent" },
            new { type = "image_url", image_url = new { url = "data:same" } } } } }
    });
    p.EnqueueOk("cached2");
    using var c = await relay.TranslateImagesAsync(req2, default);
    Check(p.Calls.Count == 2, "different focus hint caused a cache miss");
});

await Run("retry on 5xx then success (#4)", async () =>
{
    var p = new StubProvider(); p.EnqueueStatus(500); p.EnqueueOk("recovered");
    var relay = NewRelay(p);
    using var req = SingleImg("data:r");
    using var outDoc = await relay.TranslateImagesAsync(req, default);
    var first = ExtractTexts(outDoc!);
    Check(first.Count > 0 && first[0] == "recovered", "recovered content returned after a 500");
    Check(p.Calls.Count == 2, "retried once after 5xx");
});

await Run("no retry on 4xx (#4)", async () =>
{
    var p = new StubProvider(); p.EnqueueStatus(404);
    var relay = NewRelay(p);
    using var req = SingleImg("data:n");
    using var outDoc = await relay.TranslateImagesAsync(req, default);
    var first = ExtractTexts(outDoc!);
    Check(first.Count > 0 && first[0].StartsWith("[IMAGE ANALYSIS]"), "failed placeholder returned on 4xx");
    Check(p.Calls.Count == 1, "did not retry on 4xx");
});

await Run("retry after simulated timeout (#4)", async () =>
{
    var p = new StubProvider(); p.EnqueueThrow(new OperationCanceledException()); p.EnqueueOk("after-cancel");
    var relay = NewRelay(p);
    using var req = SingleImg("data:c");
    using var outDoc = await relay.TranslateImagesAsync(req, default);
    var first = ExtractTexts(outDoc!);
    Check(first.Count > 0 && first[0] == "after-cancel", "recovered after a simulated timeout");
    Check(p.Calls.Count == 2, "retried after timeout");
});

await Run("remote image fetch converts to data url (#7)", async () =>
{
    var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
    var (imgUrl, stop) = StartServer(200, png, "image/png");
    try
    {
        var p = new StubProvider(); p.EnqueueEcho();
        var relay = NewRelay(p);
        using var req = SingleImg(imgUrl);
        using var outDoc = await relay.TranslateImagesAsync(req, default);
        Check(p.Calls[0].Url.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "remote url fetched and converted to data url");
        var b64 = p.Calls[0].Url.Substring("data:image/png;base64,".Length);
        Check(Convert.FromBase64String(b64).SequenceEqual(png), "data url content matches fetched bytes");
    }
    finally { stop(); }
});

await Run("remote image fetch falls back on 404 (#7)", async () =>
{
    var (badUrl, stop) = StartServer(404, Array.Empty<byte>(), "text/plain");
    try
    {
        var p = new StubProvider(); p.EnqueueEcho();
        var relay = NewRelay(p);
        using var req = SingleImg(badUrl);
        using var outDoc = await relay.TranslateImagesAsync(req, default);
        Check(p.Calls[0].Url == badUrl, "fetch failure falls back to the original url");
    }
    finally { stop(); }
});

await Run("responses API focus hint + rebuild (#6)", async () =>
{
    var p = new StubProvider(); p.EnqueueOk("r-desc");
    var relay = NewRelay(p);
    using var req = J(new
    {
        model = "stub/text",
        input = new[] { new { role = "user", content = new object[] {
            new { type = "input_text", text = "read the chart" },
            new { type = "input_image", image_url = new { url = "data:resp1" } } } } }
    });
    using var outDoc = await relay.TranslateResponsesImagesAsync(req, default);
    Check(outDoc is not null, "responses relay returns a translated document");
    Check(p.Calls[0].Prompt.Contains("read the chart"), "responses focus hint from input_text");
    var texts = ExtractResponsesTexts(outDoc!);
    Check(texts.Contains("r-desc"), "input_image replaced with description");
    Check(texts.Contains("read the chart"), "original input_text preserved");
});

await Run("disabled relay and no-image requests return null", async () =>
{
    var disabled = new ImageVisionRelay(new StubRegistry(new StubProvider()), new ImageVisionRelayOptions { Enabled = false, VisionModel = "" }, new SimpleHttpClientFactory());
    using var r1 = SingleImg("data:d");
    Check(await disabled.TranslateImagesAsync(r1, default) is null, "disabled relay returns null");

    var relay = NewRelay(new StubProvider());
    using var r2 = J(new { model = "stub/text", messages = new[] { new { role = "user", content = new object[] { new { type = "text", text = "hello" } } } } });
    Check(await relay.TranslateImagesAsync(r2, default) is null, "request without images returns null");
});

await Run("cancellation propagates without retry (#1 fix)", async () =>
{
    var p = new StubProvider(); p.EnqueueOk("desc");
    var relay = NewRelay(p);
    using var req = SingleImg("data:cancel");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try
    {
        await relay.TranslateImagesAsync(req, cts.Token);
        Check(false, "expected OperationCanceledException to be thrown");
    }
    catch (OperationCanceledException)
    {
        Check(true, "cancellation propagated immediately");
    }
    Check(p.Calls.Count == 0, "no vision call made when already cancelled");
});

Console.WriteLine($"\n==== {failed} failure(s) ====");
return failed;

// ---- shared helpers ----

static class H
{
    public static AiModel MakeModel() => new()
    {
        Id = "vision", Object = "model", OwnedBy = "stub", Availability = "available",
        DisplayName = "Vision", ModifiedAt = "2024-01-01T00:00:00Z", Created = 0, Size = 0, Digest = "d",
        Details = new AiModelDetails { ParentModel = "", Format = "api", Family = "stub", Families = Array.Empty<string>(), ParameterSize = "unknown", QuantizationLevel = "api" },
        ModelInfo = new AiModelInfo { Architecture = "stub", ParameterCount = 0, ActiveParameterCount = 0, ContextLength = 0, MaxOutputTokens = 0, TextOnly = false, Deprecated = false, Availability = "available" },
        Capabilities = Array.Empty<string>()
    };

    public static ProviderChatResponse MakeResponse(int status, string body)
    {
        var msg = new HttpResponseMessage((HttpStatusCode)status);
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return ProviderChatResponse.BufferAsync(msg, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static string OkBody(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
}

// ---- stubs ----

sealed class StubProvider : IAiProvider
{
    private readonly Queue<Func<string, string, ProviderChatResponse>> _script = new();
    public List<(string Prompt, string Url)> Calls { get; } = new();
    public string Name => "stub";
    public string Family => "stub";
    public bool SupportsImages => true;

    public void EnqueueOk(string content) => Enqueue((_, _) => H.MakeResponse(200, H.OkBody(content)));
    public void EnqueueEcho() => Enqueue((_, u) => H.MakeResponse(200, H.OkBody("OK " + u)));
    public void EnqueueStatus(int s) => Enqueue((_, _) => H.MakeResponse(s, "{\"error\":\"x\"}"));
    public void EnqueueThrow(Exception ex) => Enqueue((_, _) => throw ex);
    public void Enqueue(Func<string, string, ProviderChatResponse> f) => _script.Enqueue(f);

    public Task<ProviderChatResponse> CreateChatCompletionAsync(JsonDocument request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (prompt, url) = ParseVisionRequest(request);
        lock (Calls) Calls.Add((prompt, url));
        var f = _script.Dequeue();
        return Task.FromResult(f(prompt, url));
    }

    private static (string Prompt, string Url) ParseVisionRequest(JsonDocument request)
    {
        var root = request.RootElement;
        if (!root.TryGetProperty("messages", out var msgs) || msgs.GetArrayLength() == 0)
            return ("", "");
        var content = msgs[0].GetProperty("content");
        string prompt = "", url = "";
        foreach (var part in content.EnumerateArray())
        {
            var t = part.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if (t == "text" && part.TryGetProperty("text", out var tx))
                prompt = tx.GetString() ?? "";
            else if (t == "image_url" && part.TryGetProperty("image_url", out var iu))
            {
                if (iu.ValueKind == JsonValueKind.Object && iu.TryGetProperty("url", out var u))
                    url = u.GetString() ?? "";
                else if (iu.ValueKind == JsonValueKind.String)
                    url = iu.GetString() ?? "";
            }
        }
        return (prompt, url);
    }

    public Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AiModel>>(Array.Empty<AiModel>());
    public Task<AiModel?> GetModelAsync(string model, CancellationToken ct) => Task.FromResult<AiModel?>(null);
    public Task<ProviderChatResponse> StreamChatCompletionAsync(JsonDocument r, CancellationToken ct) => throw new NotImplementedException();
    public Task<ProviderChatResponse> CreateResponseAsync(JsonDocument r, CancellationToken ct) => throw new NotImplementedException();
    public Task<ProviderChatResponse> StreamResponseAsync(JsonDocument r, CancellationToken ct) => throw new NotImplementedException();
}

sealed class StubRegistry : IAiProviderRegistry
{
    private readonly StubProvider _p;
    public StubRegistry(StubProvider p) => _p = p;
    public IReadOnlyList<IAiProvider> Providers => new IAiProvider[] { _p };
    public IAiProvider GetRequiredProvider(string name) => _p;
    public Task<IReadOnlyList<(IAiProvider, AiModel)>> GetAllModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(IAiProvider, AiModel)>>(Array.Empty<(IAiProvider, AiModel)>());
    public Task<(IAiProvider, AiModel, string)?> ResolveModelAsync(string external, CancellationToken ct)
    {
        var sep = external.IndexOf('/');
        var upstream = sep >= 0 ? external[(sep + 1)..] : external;
        return Task.FromResult<(IAiProvider, AiModel, string)?>((_p, H.MakeModel(), upstream));
    }
}

sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new HttpClient();
}