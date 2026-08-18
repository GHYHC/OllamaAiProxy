using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OllamaAiProxy.Contracts;

namespace OllamaAiProxy.Services;

/// <summary>
/// 启动时自更新的结果。
/// </summary>
public enum AutoUpdateOutcome
{
    /// <summary>配置关闭，未检查。</summary>
    Disabled,

    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>无需更新（非单文件运行等场景）。</summary>
    NoUpdate,

    /// <summary>检查或安装失败，继续使用当前版本启动。</summary>
    Failed,

    /// <summary>已下载新版本并启动更新脚本，进程即将退出，由脚本替换并重启。</summary>
    Applying,
}

/// <summary>
/// 启动时自动更新：检查 GitHub 最新稳定版，有更新则下载匹配当前平台的安装包、
/// 校验 SHA256、解压暂存，并生成辅助脚本在进程退出后替换可执行文件并重启。
/// </summary>
public sealed class AutoUpdater
{
    /// <summary>这些文件是用户数据，更新时永不覆盖（解压后从暂存目录删除）。</summary>
    private static readonly string[] PreservedOnInstall =
    {
        "appsettings.json",
        "model-overrides.json"
    };

    private readonly HttpClient _http;
    private readonly AutoUpdateOptions _options;
    private readonly Version _currentVersion;
    private readonly string _appDir;
    private readonly string _exeName;
    private readonly bool _isWindows;
    private readonly Action<string, string>? _launcher;
    private readonly Func<bool>? _isDeployableCheck;

    /// <summary>
    /// 构造函数。除 http/options 外均为可选注入，便于单元测试：
    /// currentVersion/appDir/exeName 默认取自运行时；launcher 默认真实启动更新脚本，
    /// 测试可注入记录器拦截；isDeployable 默认真实检测，测试可注入恒定 true。
    /// </summary>
    public AutoUpdater(
        HttpClient http,
        AutoUpdateOptions options,
        Version? currentVersion = null,
        string? appDir = null,
        string? exeName = null,
        Action<string, string>? launcher = null,
        Func<bool>? isDeployable = null)
    {
        _http = http;
        _options = options;
        _currentVersion = currentVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        _appDir = appDir ?? Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        _exeName = exeName ?? Path.GetFileName(Environment.ProcessPath ?? "OllamaAiProxy");
        _isWindows = OperatingSystem.IsWindows();
        _launcher = launcher;
        _isDeployableCheck = isDeployable;
    }

    /// <summary>
    /// 执行一次自更新检查并（如有新版本）完成下载与暂存、启动更新脚本。
    /// 返回 <see cref="AutoUpdateOutcome.Applying"/> 时，调用方应退出当前进程，
    /// 由更新脚本替换可执行文件并重启。
    /// </summary>
    public async Task<AutoUpdateOutcome> CheckAndApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_options.Enabled)
            {
                Log("disabled");
                return AutoUpdateOutcome.Disabled;
            }

            CleanupLeftovers();

            var deployable = _isDeployableCheck is null ? IsDeployable() : _isDeployableCheck();
            if (!deployable)
            {
                Log("非单文件运行（dotnet run / 调试），跳过自更新");
                return AutoUpdateOutcome.NoUpdate;
            }

            if (string.IsNullOrWhiteSpace(_options.Repository))
            {
                Log("未配置 Repository，跳过更新检查");
                return AutoUpdateOutcome.NoUpdate;
            }

            var release = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                Log($"获取最新版本失败（网络或 API 异常），继续使用当前版本 v{_currentVersion}");
                return AutoUpdateOutcome.Failed;
            }

            if (!TryParseVersion(release.TagName, out var latestVersion))
            {
                Log($"无法解析最新版本号 '{release.TagName}'，跳过更新");
                return AutoUpdateOutcome.Failed;
            }

            if (latestVersion <= _currentVersion)
            {
                Log($"当前已是最新版本 v{_currentVersion}");
                return AutoUpdateOutcome.UpToDate;
            }

            Log($"发现新版本 v{latestVersion}（当前 v{_currentVersion}），开始更新");

            var rid = string.IsNullOrWhiteSpace(_options.PlatformRid)
                ? DetectRid()
                : _options.PlatformRid.Trim().ToLowerInvariant();
            if (rid is null)
            {
                Log("无法识别当前平台架构，跳过更新");
                return AutoUpdateOutcome.Failed;
            }

            var assetName = BuildAssetName(release.TagName, rid);
            var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                Log($"发行版中未找到资产 {assetName}，跳过更新");
                return AutoUpdateOutcome.Failed;
            }

            var updateDir = Path.Combine(_appDir, ".update");
            Directory.CreateDirectory(updateDir);
            var archivePath = Path.Combine(updateDir, assetName);

            try
            {
                Log($"下载 {asset.Name} ...");
                await DownloadFileAsync(asset.BrowserDownloadUrl, archivePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"下载失败：{ex.Message}");
                CleanupDir(updateDir);
                return AutoUpdateOutcome.Failed;
            }

            if (!await VerifyChecksumAsync(asset, assetName, archivePath, release.Assets, cancellationToken).ConfigureAwait(false))
            {
                Log("完整性校验失败，中止更新，继续使用当前版本");
                CleanupDir(updateDir);
                return AutoUpdateOutcome.Failed;
            }

            var stagingDir = Path.Combine(updateDir, "staging");
            try
            {
                ExtractArchive(archivePath, stagingDir);
                ExcludePreservedFiles(stagingDir);
            }
            catch (Exception ex)
            {
                Log($"解压或整理安装文件失败：{ex.Message}");
                CleanupDir(updateDir);
                return AutoUpdateOutcome.Failed;
            }

            if (!File.Exists(Path.Combine(stagingDir, _exeName)))
            {
                Log($"安装包中未找到可执行文件 {_exeName}，中止更新");
                CleanupDir(updateDir);
                return AutoUpdateOutcome.Failed;
            }

            var scriptPath = WriteUpdateScript(updateDir);
            if (scriptPath is null)
            {
                CleanupDir(updateDir);
                return AutoUpdateOutcome.Failed;
            }

            LaunchScript(scriptPath, Environment.ProcessId, _options.RestartAfterUpdate ? 1 : 0);
            Log($"已下载并暂存新版本 v{latestVersion}，更新脚本已启动，进程即将退出重启...");
            return AutoUpdateOutcome.Applying;
        }
        catch (Exception ex)
        {
            Log($"自更新检查异常：{ex.GetType().Name}: {ex.Message}");
            return AutoUpdateOutcome.Failed;
        }
    }

    /// <summary>解析 release tag 版本号：去掉前导 v/V，兼容带 -/+ 后缀的预发布 tag。</summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        var separator = s.IndexOfAny(new[] { '-', '+' });
        if (separator > 0)
            s = s[..separator];

        if (Version.TryParse(s, out var parsed) && parsed is not null)
        {
            // 归一化：无 Revision 的 "1.0.9" 解析为 Revision=-1，统一按 0 处理，
            // 避免与程序集版本（1.0.9.0，Revision=0）比较时出现 -1 < 0 的误判。
            version = new Version(parsed.Major, parsed.Minor, parsed.Build, parsed.Revision < 0 ? 0 : parsed.Revision);
            return true;
        }

        version = new Version();
        return false;
    }

    /// <summary>
    /// 按发布产物命名规则生成资产名：OllamaAiProxy-{tag}-{rid}.{zip|tar.gz}。
    /// </summary>
    public static string BuildAssetName(string tag, string rid)
    {
        var ext = rid.StartsWith("win", StringComparison.Ordinal) ? "zip" : "tar.gz";
        return $"OllamaAiProxy-{tag}-{rid}.{ext}";
    }

    /// <summary>
    /// 检测当前运行平台，返回与 release.yml 一致的 RID（win-x64/win-arm64/
    /// linux-x64/linux-arm64/osx-x64/osx-arm64）。未知架构返回 null。
    /// </summary>
    public static string? DetectRid()
    {
        var isX64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        var isArm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return isX64 ? "win-x64" : isArm ? "win-arm64" : null;
        if (OperatingSystem.IsLinux())
            return isX64 ? "linux-x64" : isArm ? "linux-arm64" : null;
        if (OperatingSystem.IsMacOS())
            return isX64 ? "osx-x64" : isArm ? "osx-arm64" : null;
        return null;
    }

    /// <summary>校验文件的 SHA256 是否与期望的十六进制串一致。</summary>
    public static bool VerifySha256(string filePath, string expectedHex)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHex.Trim());
            byte[] actual;
            using (var stream = File.OpenRead(filePath))
            using (var sha = SHA256.Create())
            {
                actual = sha.ComputeHash(stream);
            }
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 SHA256SUMS.txt 内容中解析指定文件的校验和（兼容 "hash  name" 与 "hash *name"）。</summary>
    public static string? ParseChecksumLine(string sha256SumsContent, string fileName)
    {
        foreach (var rawLine in sha256SumsContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var name = parts[^1].TrimStart('*');
            if (string.Equals(name, fileName, StringComparison.Ordinal))
                return parts[0];
        }
        return null;
    }

    /// <summary>解压 zip 或 tar.gz 安装包到指定目录。</summary>
    public static void ExtractArchive(string archivePath, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, stagingDir, overwriteFiles: true);
        }
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, stagingDir, overwriteFiles: true);
        }
        else
        {
            throw new InvalidOperationException($"不支持的安装包格式：{archivePath}");
        }
    }

    /// <summary>从暂存目录删除不应覆盖的用户数据文件。</summary>
    public static void ExcludePreservedFiles(string stagingDir)
    {
        foreach (var name in PreservedOnInstall)
        {
            var path = Path.Combine(stagingDir, name);
            if (File.Exists(path))
                File.Delete(path);
        }

        foreach (var dirName in new[] { "logs", ".update" })
        {
            var dir = Path.Combine(stagingDir, dirName);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }

        foreach (var old in Directory.EnumerateFiles(stagingDir, "*.old", SearchOption.TopDirectoryOnly))
            File.Delete(old);
    }

    /// <summary>生成 Windows 更新脚本内容（update.cmd）。exe 名按实际可执行文件名生成。</summary>
    public static string BuildWindowsScript(string exeName)
    {
        return
            "@echo off\r\n" +
            "setlocal\r\n" +
            ":wait\r\n" +
            "tasklist /FI \"PID eq %1\" 2>nul | findstr /C:\"%1\" >nul\r\n" +
            "if %errorlevel%==0 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
            "set \"UPD=%~dp0\"\r\n" +
            "for %%I in (\"%~dp0..\") do set \"APP=%%~fI\"\r\n" +
            "set \"EXE=" + exeName + "\"\r\n" +
            "if exist \"%APP%\\%EXE%.old\" del /q \"%APP%\\%EXE%.old\"\r\n" +
            "if exist \"%APP%\\%EXE%\" move /y \"%APP%\\%EXE%\" \"%APP%\\%EXE%.old\" >nul\r\n" +
            "xcopy /y /e /q \"%UPD%staging\\*\" \"%APP%\\\" >nul\r\n" +
            "if exist \"%APP%\\%EXE%.old\" del /q \"%APP%\\%EXE%.old\"\r\n" +
            // 必须先重启再清理：删除正在执行的 update.cmd 会让 cmd 中止，导致 start 永不执行。
            "if \"%2\"==\"1\" start \"\" \"%APP%\\%EXE%\"\r\n" +
            "if exist \"%UPD%\" rmdir /s /q \"%UPD%\" 2>nul\r\n" +
            "exit /b 0\r\n";
    }

    /// <summary>生成 Unix 更新脚本内容（update.sh）。exe 名按实际可执行文件名生成。</summary>
    public static string BuildUnixScript(string exeName)
    {
        return
            "#!/bin/sh\r\n" +
            "trap '' HUP\r\n" +
            "PID=$1\r\n" +
            "while kill -0 \"$PID\" 2>/dev/null; do sleep 1; done\r\n" +
            "UPD=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\r\n" +
            "APP=\"$(dirname -- \"$UPD\")\"\r\n" +
            "EXE=\"" + exeName + "\"\r\n" +
            "[ -e \"$APP/$EXE.old\" ] && rm -f \"$APP/$EXE.old\"\r\n" +
            "[ -e \"$APP/$EXE\" ] && mv -f \"$APP/$EXE\" \"$APP/$EXE.old\"\r\n" +
            "cp -rf \"$UPD/staging/.\" \"$APP/\"\r\n" +
            "chmod +x \"$APP/$EXE\"\r\n" +
            "[ -e \"$APP/$EXE.old\" ] && rm -f \"$APP/$EXE.old\"\r\n" +
            // 必须先重启再清理：删除正在执行的 update.sh 可能让 shell 中止。
            "if [ \"$2\" = \"1\" ]; then\r\n" +
            "  cd \"$APP\"\r\n" +
            "  nohup \"./$EXE\" >> \"$APP/server.log\" 2>&1 &\r\n" +
            "fi\r\n" +
            "rm -rf \"$UPD\"\r\n" +
            "exit 0\r\n";
    }

    /// <summary>是否处于"可自更新"的部署态：不是由 dotnet 宿主运行，且可执行文件与基目录同目录。</summary>
    public static bool IsDeployable()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            return false;

        // dotnet run / dotnet dll 启动时，进程路径是 dotnet 宿主，跳过自更新。
        var name = Path.GetFileNameWithoutExtension(processPath);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return false;

        // 单文件发布时 AppContext.BaseDirectory 与可执行文件同目录，且文件真实存在。
        var exePath = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(processPath));
        return File.Exists(exePath);
    }

    private async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/repos/{_options.Repository}/releases/latest";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.CheckTimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"OllamaAiProxy/{_currentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Log($"GitHub API 返回 {(int)response.StatusCode}（{url}）");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(json, ApiJsonSerializerContext.Default.GitHubRelease);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> VerifyChecksumAsync(
        GitHubReleaseAsset asset,
        string assetName,
        string archivePath,
        IReadOnlyList<GitHubReleaseAsset> allAssets,
        CancellationToken cancellationToken)
    {
        // 优先使用发行版附带的 SHA256SUMS.txt。
        var sumsAsset = allAssets.FirstOrDefault(a =>
            string.Equals(a.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (sumsAsset is not null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.CheckTimeoutSeconds)));
                var content = await _http.GetStringAsync(sumsAsset.BrowserDownloadUrl, cts.Token).ConfigureAwait(false);
                var expected = ParseChecksumLine(content, assetName);
                if (expected is not null)
                    return VerifySha256(archivePath, expected);
                Log("SHA256SUMS.txt 中未找到对应条目，尝试使用资产 digest");
            }
            catch (Exception ex)
            {
                Log($"获取 SHA256SUMS.txt 失败：{ex.Message}");
            }
        }

        // 兜底：GitHub 新版 API 附带的资产 digest 字段（sha256:...）。
        if (!string.IsNullOrWhiteSpace(asset.Digest) &&
            asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return VerifySha256(archivePath, asset.Digest["sha256:".Length..]);
        }

        Log("无可用校验和，跳过完整性校验");
        return true;
    }

    private async Task DownloadFileAsync(string url, string destPath, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private string? WriteUpdateScript(string updateDir)
    {
        try
        {
            var path = Path.Combine(updateDir, _isWindows ? "update.cmd" : "update.sh");
            var content = _isWindows ? BuildWindowsScript(_exeName) : BuildUnixScript(_exeName);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception ex)
        {
            Log($"生成更新脚本失败：{ex.Message}");
            return null;
        }
    }

    private void LaunchScript(string scriptPath, int processId, int restart)
    {
        if (_launcher is not null)
        {
            _launcher(scriptPath, $"{processId} {restart}");
            return;
        }

        if (_isWindows)
        {
            // 更新脚本必须以可见窗口运行：隐藏窗口下 cmd 的 `start` 无法创建新进程，
            // 会导致替换后无法自动重启新版本。
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                Arguments = $"{processId} {restart}",
                UseShellExecute = true,
                WorkingDirectory = _appDir
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{scriptPath}\" {processId} {restart}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _appDir
            });
        }
    }

    private void CleanupLeftovers()
    {
        try
        {
            var updateDir = Path.Combine(_appDir, ".update");
            if (Directory.Exists(updateDir))
            {
                Directory.Delete(updateDir, recursive: true);
                Log("清理上次未完成的更新残留 .update");
            }

            var oldFile = Path.Combine(_appDir, _exeName + ".old");
            if (File.Exists(oldFile))
            {
                File.Delete(oldFile);
                Log("清理上次更新遗留的备份 .old");
            }
        }
        catch (Exception ex)
        {
            Log($"清理更新残留失败：{ex.Message}");
        }
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AutoUpdate] 清理 {dir} 失败：{ex.Message}");
        }
    }

    private void Log(string message) => Console.WriteLine($"[AutoUpdate] {message}");
}
