namespace OllamaAiProxy.Services;

/// <summary>
/// 自动更新配置。对应 appsettings.json 中的 "AutoUpdate" 节。
/// </summary>
public sealed record AutoUpdateOptions
{
    public const string SectionName = "AutoUpdate";

    /// <summary>总开关。默认开启：每次启动时检查 GitHub 是否有新版本。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>GitHub 仓库，格式 "owner/repo"。</summary>
    public string Repository { get; init; } = "GHYHC/OllamaAiProxy";

    /// <summary>版本检查请求的超时秒数（仅用于查询最新版本，下载不受此限制）。</summary>
    public int CheckTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// 更新后是否自动重启到新版本。在 systemd 等进程管理器托管场景建议设为 false，
    /// 避免脚本内的后台重启与进程管理器的拉起产生双实例。
    /// </summary>
    public bool RestartAfterUpdate { get; init; } = true;

    /// <summary>GitHub API 根地址，默认官方；测试或自建 GitHub Enterprise 时可覆盖。</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>
    /// 平台 RID 覆盖，例如 "linux-arm64"。留空则按当前 OS + 架构自动检测。
    /// 用于特殊环境、交叉运行或手工测试。
    /// </summary>
    public string PlatformRid { get; init; } = "";
}
