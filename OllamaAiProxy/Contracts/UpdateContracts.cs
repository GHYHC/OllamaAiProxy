namespace OllamaAiProxy.Contracts;

/// <summary>
/// GitHub Releases /releases/latest 接口响应的最小模型。
/// 属性命名采用 snake_case 策略（与 ApiJsonSerializerContext 一致），
/// 对应 tag_name / assets[].name / assets[].browser_download_url / assets[].digest。
/// </summary>
public sealed record GitHubRelease(
    string TagName,
    IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed record GitHubReleaseAsset(
    string Name,
    string BrowserDownloadUrl,
    string Digest);
