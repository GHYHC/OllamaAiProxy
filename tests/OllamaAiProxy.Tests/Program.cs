using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

// 生成 tar.gz 安装包（与 release.yml 的 tar -czf 产物一致，不包含顶层目录）。
static void CreateTarGz(string srcDir, string destPath)
{
    using var outFile = new FileStream(destPath, FileMode.Create);
    using var gzip = new GZipStream(outFile, CompressionLevel.Optimal);
    TarFile.CreateFromDirectory(srcDir, gzip, includeBaseDirectory: false);
}

// 构造一个模拟 GitHub Release 服务器：/releases/latest 返回新版本 JSON，
// 附带一个指向本地 zip 的资产与 SHA256SUMS.txt。
// correctChecksum=false 时提供错误的校验和，用于负向测试。
static (MockHttpServer Server, string AppDir, string Work) StartMockRelease(
    bool correctChecksum, string tag = "v9.9.9", string exeName = "OllamaAiProxy.exe")
{
    var work = Path.Combine(Path.GetTempPath(), "autoupdate-e2e-" + Guid.NewGuid().ToString("N"));
    var appDir = Path.Combine(work, "app");
    Directory.CreateDirectory(appDir);
    var pkgDir = Path.Combine(work, "pkg");
    Directory.CreateDirectory(Path.Combine(pkgDir, "wwwroot"));
    File.WriteAllText(Path.Combine(pkgDir, exeName), "new exe content");
    File.WriteAllText(Path.Combine(pkgDir, "wwwroot", "index.html"), "<html>new</html>");
    File.WriteAllText(Path.Combine(pkgDir, "appsettings.json"), "{ user config }");
    File.WriteAllText(Path.Combine(pkgDir, "model-overrides.json"), "{}");

    var assetName = AutoUpdater.BuildAssetName(tag, "win-x64");
    var zipPath = Path.Combine(work, assetName);
    ZipFile.CreateFromDirectory(pkgDir, zipPath);
    var zipBytes = File.ReadAllBytes(zipPath);
    var hash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
    var expectedHash = correctChecksum ? hash : new string('0', 64);

    var server = new MockHttpServer();
    var json = $$"""
    {
      "tag_name": "{{tag}}",
      "name": "{{tag}}",
      "assets": [
        { "name": "{{assetName}}", "browser_download_url": "{{server.BaseUrl}}asset.bin", "digest": "" },
        { "name": "SHA256SUMS.txt", "browser_download_url": "{{server.BaseUrl}}SHA256SUMS.txt", "digest": "" }
      ]
    }
    """;
    server.Add("/repos/GHYHC/OllamaAiProxy/releases/latest", 200, "application/json", Encoding.UTF8.GetBytes(json));
    server.Add("/asset.bin", 200, "application/zip", zipBytes);
    server.Add("/SHA256SUMS.txt", 200, "text/plain", Encoding.UTF8.GetBytes($"{expectedHash}  {assetName}"));
    return (server, appDir, work);
}

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

// ---- 思考强度（ThinkingStrengthInjector） ----

await Run("thinking strength injector", async () =>
{
    using var chat = J(new { model = "stub/x", stream = false, messages = new[] { new { role = "user", content = "hi" } } });
    using var chatHigh = ThinkingStrengthInjector.Apply(chat, "high", responses: false);
    Check(chatHigh is not null, "chat: injects when no explicit effort");
    var chatRoot = chatHigh!.RootElement;
    Check(chatRoot.GetProperty("reasoning_effort").GetString() == "high", "chat: reasoning_effort=high");
    Check(chatRoot.GetProperty("model").GetString() == "stub/x", "chat: model preserved");
    Check(chatRoot.GetProperty("stream").GetBoolean() == false, "chat: stream preserved");
    Check(chatRoot.GetProperty("messages").GetArrayLength() == 1, "chat: messages preserved");
    chatHigh.Dispose();

    using var resp = J(new { model = "stub/x", input = new[] { new { role = "user", content = "hi" } } });
    using var respMid = ThinkingStrengthInjector.Apply(resp, "medium", responses: true);
    Check(respMid is not null, "responses: injects when no explicit reasoning");
    var respRoot = respMid!.RootElement;
    Check(respRoot.GetProperty("reasoning").GetProperty("effort").GetString() == "medium", "responses: reasoning.effort=medium");
    Check(respRoot.GetProperty("model").GetString() == "stub/x", "responses: model preserved");
    respMid.Dispose();

    using var chatExplicit = J(new { model = "stub/x", reasoning_effort = "low", messages = new object[0] });
    using var chatNo = ThinkingStrengthInjector.Apply(chatExplicit, "high", responses: false);
    Check(chatNo is null, "chat: explicit reasoning_effort respected (no inject)");

    using var respExplicit = J(new { model = "stub/x", reasoning = new { effort = "low" }, input = new object[0] });
    using var respNo = ThinkingStrengthInjector.Apply(respExplicit, "high", responses: true);
    Check(respNo is null, "responses: explicit reasoning respected (no inject)");

    using var chatBase = J(new { model = "stub/x" });
    Check(ThinkingStrengthInjector.Apply(chatBase, null, responses: false) is null, "null level -> no inject");
    Check(ThinkingStrengthInjector.Apply(chatBase, "", responses: false) is null, "empty level -> no inject");
    Check(ThinkingStrengthInjector.Apply(chatBase, "extreme", responses: false) is null, "invalid level -> no inject");
    using var chatNone = ThinkingStrengthInjector.Apply(chatBase, "none", responses: false);
    Check(chatNone is not null && chatNone.RootElement.GetProperty("reasoning_effort").GetString() == "none", "none maps to reasoning_effort=none");
    chatNone?.Dispose();

    Check(ThinkingStrengthInjector.IsValidLevel("high") && ThinkingStrengthInjector.IsValidLevel("none")
        && ThinkingStrengthInjector.IsValidLevel("low") && ThinkingStrengthInjector.IsValidLevel("medium"), "IsValidLevel accepts all levels");
    Check(!ThinkingStrengthInjector.IsValidLevel(null) && !ThinkingStrengthInjector.IsValidLevel("") && !ThinkingStrengthInjector.IsValidLevel("max"), "IsValidLevel rejects invalid values");
});

await Run("thinking strength override round-trips as camelCase", async () =>
{
    var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var ov = new ModelOverride
    {
        DisplayName = "X",
        ImageRelay = true,
        ThinkingStrength = "high"
    };
    var json = JsonSerializer.Serialize(ov, opts);
    Check(json.Contains("\"thinkingStrength\":\"high\""), $"thinkingStrength serialized camelCase: {json}");
    Check(json.Contains("\"imageRelay\":true"), "imageRelay still camelCase");

    var back = JsonSerializer.Deserialize<ModelOverride>(json, opts);
    Check(back is not null && back!.ThinkingStrength == "high" && back.ImageRelay == true && back.DisplayName == "X",
        "camelCase thinkingStrength deserializes back");
});

// 复现 Program.cs 对最小 API 请求体的 JsonOptions 配置（Web 默认 camelCase 反射解析器 +
// snake_case source-gen 上下文插入 TypeInfoResolverChain 首位），确认 PUT 请求体里的
// camelCase 键（displayName/imageRelay/thinkingStrength 等）仍能正常绑定到 ModelOverride。
// 这保证测试页「编辑->保存」的覆盖值写入（含新加的 thinkingStrength）在 HEAD 上可用。
await Run("json options chain: camelCase body binding with snake_case source-gen context", async () =>
{
    var json = """{"displayName":"X","imageRelay":true,"thinkingStrength":"high","maxOutputTokens":123}""";
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    opts.TypeInfoResolverChain.Insert(0, SnakeCaseBindingJsonContext.Default);
    var ov = JsonSerializer.Deserialize(json, typeof(BindingBody), opts) as BindingBody;
    Check(ov is not null, "body deserialized");
    Check(ov!.DisplayName == "X", $"camelCase displayName binds (got '{ov.DisplayName}')");
    Check(ov.ImageRelay == true, "camelCase imageRelay binds");
    Check(ov.ThinkingStrength == "high", "camelCase thinkingStrength binds");
    Check(ov.MaxOutputTokens == 123, "camelCase maxOutputTokens binds");
});

// ---- 自动更新（AutoUpdate） ----

await Run("autoupdate: version parse & compare", async () =>
{
    Check(AutoUpdater.TryParseVersion("v1.0.9", out var v1) && v1 == new Version(1, 0, 9, 0), "v1.0.9 parsed");
    Check(AutoUpdater.TryParseVersion("1.0.10", out var v2) && v2 == new Version(1, 0, 10, 0), "1.0.10 parsed");
    Check(AutoUpdater.TryParseVersion("v1.0.8", out var v3) && v3 == new Version(1, 0, 8, 0), "v1.0.8 parsed");
    Check(AutoUpdater.TryParseVersion("v1.0.9-rc1", out var v4) && v4 == new Version(1, 0, 9, 0), "pre-release suffix stripped");
    Check(!AutoUpdater.TryParseVersion("abc", out _), "malformed rejected");
    Check(!AutoUpdater.TryParseVersion("", out _), "empty rejected");
    Check(!AutoUpdater.TryParseVersion(null, out _), "null rejected");
    Check(new Version(1, 0, 9, 0) > new Version(1, 0, 8, 0), "1.0.9 > 1.0.8 (update needed)");
    Check(new Version(1, 0, 10, 0) > new Version(1, 0, 9, 0), "1.0.10 > 1.0.9 (update needed)");
    Check(!(new Version(1, 0, 8, 0) > new Version(1, 0, 8, 0)), "equal is not newer");
    Check(!(new Version(1, 0, 7, 0) > new Version(1, 0, 8, 0)), "older is not newer");
});

await Run("autoupdate: asset name for all 6 platforms", async () =>
{
    Check(AutoUpdater.BuildAssetName("v1.0.9", "win-x64") == "OllamaAiProxy-v1.0.9-win-x64.zip", "win-x64 -> zip");
    Check(AutoUpdater.BuildAssetName("v1.0.9", "win-arm64") == "OllamaAiProxy-v1.0.9-win-arm64.zip", "win-arm64 -> zip");
    Check(AutoUpdater.BuildAssetName("v1.0.9", "linux-x64") == "OllamaAiProxy-v1.0.9-linux-x64.tar.gz", "linux-x64 -> tar.gz");
    Check(AutoUpdater.BuildAssetName("v1.0.9", "linux-arm64") == "OllamaAiProxy-v1.0.9-linux-arm64.tar.gz", "linux-arm64 -> tar.gz");
    Check(AutoUpdater.BuildAssetName("v1.0.9", "osx-x64") == "OllamaAiProxy-v1.0.9-osx-x64.tar.gz", "osx-x64 -> tar.gz");
    Check(AutoUpdater.BuildAssetName("v1.0.9", "osx-arm64") == "OllamaAiProxy-v1.0.9-osx-arm64.tar.gz", "osx-arm64 -> tar.gz");
});

await Run("autoupdate: detect rid for current platform", async () =>
{
    var rid = AutoUpdater.DetectRid();
    var arch = RuntimeInformation.ProcessArchitecture;
    string? expected = null;
    if (OperatingSystem.IsWindows())
        expected = arch == Architecture.Arm64 ? "win-arm64" : arch == Architecture.X64 ? "win-x64" : null;
    else if (OperatingSystem.IsLinux())
        expected = arch == Architecture.Arm64 ? "linux-arm64" : arch == Architecture.X64 ? "linux-x64" : null;
    else if (OperatingSystem.IsMacOS())
        expected = arch == Architecture.Arm64 ? "osx-arm64" : arch == Architecture.X64 ? "osx-x64" : null;
    Check(rid == expected, $"DetectRid matches current platform ({rid})");
});

await Run("autoupdate: github release JSON parse (snake_case)", async () =>
{
    const string json = """
    {
      "tag_name": "v9.9.9",
      "name": "v9.9.9",
      "prerelease": false,
      "assets": [
        { "name": "OllamaAiProxy-v9.9.9-win-x64.zip", "browser_download_url": "https://example.com/a.zip", "digest": "sha256:abcd" }
      ]
    }
    """;
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    var release = JsonSerializer.Deserialize<GitHubRelease>(json, options);
    Check(release is not null, "release parsed");
    Check(release!.TagName == "v9.9.9", "tag_name parsed");
    Check(release.Assets.Count == 1, "one asset");
    Check(release.Assets[0].Name == "OllamaAiProxy-v9.9.9-win-x64.zip", "asset name parsed");
    Check(release.Assets[0].BrowserDownloadUrl == "https://example.com/a.zip", "asset url parsed");
    Check(release.Assets[0].Digest == "sha256:abcd", "asset digest parsed");
});

await Run("autoupdate: sha256 verify + checksum line parse", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "autoupdate-sha-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var file = Path.Combine(dir, "OllamaAiProxy-v9.9.9-win-x64.zip");
        await File.WriteAllTextAsync(file, "hello autoupdate");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant();

        Check(AutoUpdater.VerifySha256(file, hash), "checksum matches");
        Check(!AutoUpdater.VerifySha256(file, new string('0', 64)), "checksum mismatch detected");
        Check(!AutoUpdater.VerifySha256(file, "nothex"), "invalid hex rejected");

        var sums = $"{hash}  OllamaAiProxy-v9.9.9-win-x64.zip\n{hash}  OllamaAiProxy-v9.9.9-win-arm64.zip";
        Check(AutoUpdater.ParseChecksumLine(sums, "OllamaAiProxy-v9.9.9-win-x64.zip") == hash, "checksum line parsed");
        Check(AutoUpdater.ParseChecksumLine(sums, "nope.zip") is null, "missing file returns null");
    }
    finally { Directory.Delete(dir, recursive: true); }
});

await Run("autoupdate: zip/tar.gz extraction excludes user files", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "autoupdate-extract-" + Guid.NewGuid().ToString("N"));
    var src = Path.Combine(dir, "src");
    Directory.CreateDirectory(Path.Combine(src, "wwwroot"));
    await File.WriteAllTextAsync(Path.Combine(src, "OllamaAiProxy.exe"), "fake exe");
    await File.WriteAllTextAsync(Path.Combine(src, "wwwroot", "index.html"), "<html></html>");
    await File.WriteAllTextAsync(Path.Combine(src, "appsettings.json"), "{ \"Providers\": {} }");
    await File.WriteAllTextAsync(Path.Combine(src, "model-overrides.json"), "{}");
    try
    {
        var zipPath = Path.Combine(dir, "pkg.zip");
        ZipFile.CreateFromDirectory(src, zipPath);
        var stagingZip = Path.Combine(dir, "staging-zip");
        AutoUpdater.ExtractArchive(zipPath, stagingZip);
        AutoUpdater.ExcludePreservedFiles(stagingZip);
        Check(File.Exists(Path.Combine(stagingZip, "OllamaAiProxy.exe")), "zip: exe extracted");
        Check(File.Exists(Path.Combine(stagingZip, "wwwroot", "index.html")), "zip: wwwroot extracted");
        Check(!File.Exists(Path.Combine(stagingZip, "appsettings.json")), "zip: appsettings.json excluded");
        Check(!File.Exists(Path.Combine(stagingZip, "model-overrides.json")), "zip: model-overrides.json excluded");

        var tarGzPath = Path.Combine(dir, "pkg.tar.gz");
        CreateTarGz(src, tarGzPath);
        var stagingTar = Path.Combine(dir, "staging-tar");
        AutoUpdater.ExtractArchive(tarGzPath, stagingTar);
        AutoUpdater.ExcludePreservedFiles(stagingTar);
        Check(File.Exists(Path.Combine(stagingTar, "OllamaAiProxy.exe")), "tar.gz: exe extracted");
        Check(File.Exists(Path.Combine(stagingTar, "wwwroot", "index.html")), "tar.gz: wwwroot extracted");
        Check(!File.Exists(Path.Combine(stagingTar, "appsettings.json")), "tar.gz: appsettings.json excluded");
    }
    finally { Directory.Delete(dir, recursive: true); }
});

await Run("autoupdate: update scripts generated for both platforms", async () =>
{
    var win = AutoUpdater.BuildWindowsScript("OllamaAiProxy.exe");
    Check(win.Contains("set \"EXE=OllamaAiProxy.exe\""), "win script bakes exe name");
    Check(win.Contains("tasklist /FI \"PID eq %1\""), "win script waits for pid");
    Check(win.Contains("%EXE%.old"), "win script renames old exe");
    Check(win.Contains("xcopy /y /e /q \"%UPD%staging\\*\""), "win script copies staging");
    Check(win.Contains("start \"\" \"%APP%\\%EXE%\""), "win script restarts");
    Check(win.Contains("if \"%2\"==\"1\""), "win script honors restart flag");
    Check(win.IndexOf("start \"\" \"%APP%\\%EXE%\"") < win.IndexOf("rmdir /s /q \"%UPD%\""),
        "win relaunch happens before cleanup (self-delete must not abort start)");

    var unix = AutoUpdater.BuildUnixScript("OllamaAiProxy");
    Check(unix.Contains("trap '' HUP"), "unix script ignores HUP");
    Check(unix.Contains("kill -0 \"$PID\""), "unix script waits for pid");
    Check(unix.Contains("mv -f \"$APP/$EXE\" \"$APP/$EXE.old\""), "unix script renames old exe");
    Check(unix.Contains("cp -rf \"$UPD/staging/.\" \"$APP/\""), "unix script copies staging");
    Check(unix.Contains("chmod +x \"$APP/$EXE\""), "unix script chmods");
    Check(unix.Contains("nohup \"./$EXE\" >> \"$APP/server.log\" 2>&1 &"), "unix script restarts detached");
    Check(unix.IndexOf("nohup") < unix.IndexOf("rm -rf \"$UPD\""),
        "unix relaunch happens before cleanup (self-delete must not abort shell)");
});

await Run("autoupdate: end-to-end download + verify + stage (mock GitHub)", async () =>
{
    var (server, appDir, work) = StartMockRelease(correctChecksum: true);
    var launched = new List<string>();
    try
    {
        var updater = new AutoUpdater(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            new AutoUpdateOptions { ApiBaseUrl = server.BaseUrl, CheckTimeoutSeconds = 5, PlatformRid = "win-x64" },
            currentVersion: new Version(1, 0, 8),
            appDir: appDir,
            exeName: "OllamaAiProxy.exe",
            launcher: (p, a) => launched.Add($"{p}|{a}"),
            isDeployable: () => true);

        var outcome = await updater.CheckAndApplyAsync(CancellationToken.None);
        Check(outcome == AutoUpdateOutcome.Applying, "outcome is Applying");

        var staging = Path.Combine(appDir, ".update", "staging");
        Check(File.Exists(Path.Combine(staging, "OllamaAiProxy.exe")), "staging has exe");
        Check(File.Exists(Path.Combine(staging, "wwwroot", "index.html")), "staging has wwwroot");
        Check(!File.Exists(Path.Combine(staging, "appsettings.json")), "staging excluded appsettings.json");
        Check(!File.Exists(Path.Combine(staging, "model-overrides.json")), "staging excluded model-overrides.json");

        var scripts = Directory.GetFiles(Path.Combine(appDir, ".update"), "update.*");
        Check(scripts.Length == 1, "update script written");
        Check(launched.Count == 1 && launched[0].StartsWith(Path.Combine(appDir, ".update")), "launcher invoked with script path");
        Check(launched[0].EndsWith(" 1"), "restart flag passed");
    }
    finally
    {
        server.Dispose();
        try { Directory.Delete(work, recursive: true); } catch { }
    }
});

await Run("autoupdate: checksum mismatch aborts and cleans up", async () =>
{
    var (server, appDir, work) = StartMockRelease(correctChecksum: false);
    try
    {
        var updater = new AutoUpdater(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            new AutoUpdateOptions { ApiBaseUrl = server.BaseUrl, CheckTimeoutSeconds = 5, PlatformRid = "win-x64" },
            currentVersion: new Version(1, 0, 8),
            appDir: appDir,
            exeName: "OllamaAiProxy.exe",
            launcher: (_, _) => { },
            isDeployable: () => true);
        var outcome = await updater.CheckAndApplyAsync(CancellationToken.None);
        Check(outcome == AutoUpdateOutcome.Failed, "checksum mismatch -> Failed");
        Check(!Directory.Exists(Path.Combine(appDir, ".update")), ".update cleaned up after failure");
    }
    finally
    {
        server.Dispose();
        try { Directory.Delete(work, recursive: true); } catch { }
    }
});

await Run("autoupdate: older release is up to date (no download)", async () =>
{
    var (server, appDir, work) = StartMockRelease(correctChecksum: true, tag: "v1.0.7");
    try
    {
        var updater = new AutoUpdater(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            new AutoUpdateOptions { ApiBaseUrl = server.BaseUrl, CheckTimeoutSeconds = 5, PlatformRid = "win-x64" },
            currentVersion: new Version(1, 0, 8),
            appDir: appDir,
            exeName: "OllamaAiProxy.exe",
            launcher: (_, _) => { },
            isDeployable: () => true);
        var outcome = await updater.CheckAndApplyAsync(CancellationToken.None);
        Check(outcome == AutoUpdateOutcome.UpToDate, "older release -> UpToDate");
        Check(!Directory.Exists(Path.Combine(appDir, ".update")), "no .update created");
    }
    finally
    {
        server.Dispose();
        try { Directory.Delete(work, recursive: true); } catch { }
    }
});

await Run("autoupdate: disabled option skips check", async () =>
{
    var updater = new AutoUpdater(
        new HttpClient(),
        new AutoUpdateOptions { Enabled = false },
        currentVersion: new Version(1, 0, 8),
        appDir: Path.GetTempPath(),
        exeName: "OllamaAiProxy.exe",
        isDeployable: () => true);
    var outcome = await updater.CheckAndApplyAsync(CancellationToken.None);
    Check(outcome == AutoUpdateOutcome.Disabled, "disabled -> Disabled");
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

// 用于复现 Program.cs 的 JsonOptions 配置（snake_case source-gen 上下文插入 chain 首位），
// 验证最小 API 请求体绑定是否仍接受 camelCase 键。
sealed record BindingBody
{
    public string? DisplayName { get; init; }
    public bool? ImageRelay { get; init; }
    public string? ThinkingStrength { get; init; }
    public int? MaxOutputTokens { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BindingBody))]
internal sealed partial class SnakeCaseBindingJsonContext : JsonSerializerContext
{
}

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

// 多路由的本地模拟 HTTP 服务器，用于端到端测试自更新（模拟 GitHub API 与附件下载）。
sealed class MockHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, (int Status, string ContentType, byte[] Body)> _routes =
        new(StringComparer.Ordinal);

    public string BaseUrl { get; }

    public MockHttpServer()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = NextFreePort();
            var listener = new HttpListener();
            try { listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start(); }
            catch { continue; }
            _listener = listener;
            BaseUrl = $"http://127.0.0.1:{port}/";
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (_routes.TryGetValue(ctx.Request.Url!.AbsolutePath, out var route))
                            {
                                ctx.Response.StatusCode = route.Status;
                                ctx.Response.ContentType = route.ContentType;
                                if (route.Body.Length > 0) ctx.Response.OutputStream.Write(route.Body, 0, route.Body.Length);
                            }
                            else
                            {
                                ctx.Response.StatusCode = 404;
                            }
                            ctx.Response.Close();
                        }
                        catch { }
                    });
                }
            });
            return;
        }
        throw new InvalidOperationException("could not start mock HTTP server");
    }

    private static int NextFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Add(string path, int status, string contentType, byte[] body) =>
        _routes[path] = (status, contentType, body);

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}