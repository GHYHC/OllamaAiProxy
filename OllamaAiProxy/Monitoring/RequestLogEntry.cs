using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaAiProxy.Monitoring;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RequestLogEntry))]
internal sealed partial class RequestLoggingJsonSerializerContext : JsonSerializerContext;

internal sealed record RequestLogEntry(
    string Timestamp,
    string TraceIdentifier,
    string Method,
    string Path,
    string QueryString,
    int StatusCode,
    double ElapsedMs,
    string? RemoteIp,
    string UserAgent,
    string Referer,
    string Origin,
    string XForwardedFor,
    string XRealIp,
    string RequestId,
    string? ContentType,
    string? Error,
    string Url);
