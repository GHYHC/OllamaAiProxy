using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OllamaAiProxy.Monitoring;

public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _environment;
    private readonly RequestLoggingOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RequestLoggingMiddleware(RequestDelegate next, IWebHostEnvironment environment, IOptions<RequestLoggingOptions> options)
    {
        _next = next;
        _environment = environment;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 中间件始终在管道里；关闭时只透传请求，开启时才写本地 JSONL 日志。
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        Exception? exception = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            await WriteLogAsync(context, startedAt, sw.Elapsed, exception);
        }
    }

    private async Task WriteLogAsync(HttpContext context, DateTimeOffset startedAt, TimeSpan elapsed, Exception? exception)
    {
        try
        {
            var request = context.Request;
            var host = request.Host.Value ?? "";
            var entry = new RequestLogEntry(
                Timestamp: startedAt.ToString("o"),
                TraceIdentifier: context.TraceIdentifier,
                Method: request.Method,
                Path: request.Path.Value ?? "",
                QueryString: request.QueryString.Value ?? "",
                StatusCode: exception is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError,
                ElapsedMs: elapsed.TotalMilliseconds,
                RemoteIp: context.Connection.RemoteIpAddress?.ToString(),
                UserAgent: request.Headers.UserAgent.ToString(),
                Referer: request.Headers.Referer.ToString(),
                Origin: request.Headers.Origin.ToString(),
                XForwardedFor: request.Headers["X-Forwarded-For"].ToString(),
                XRealIp: request.Headers["X-Real-IP"].ToString(),
                RequestId: request.Headers["X-Request-Id"].ToString(),
                ContentType: request.ContentType,
                Error: exception?.GetType().Name,
                Url: $"{request.Scheme}://{host}{request.PathBase}{request.Path}{request.QueryString}");

            var logDir = Path.Combine(_environment.ContentRootPath, _options.Directory);
            Directory.CreateDirectory(logDir);
            var file = Path.Combine(logDir, $"requests-{startedAt:yyyyMMdd}.jsonl");
            var line = JsonSerializer.Serialize(entry, RequestLoggingJsonSerializerContext.Default.RequestLogEntry) + Environment.NewLine;

            Console.WriteLine(
                $"[{entry.Timestamp}] {entry.Method} {entry.Path}{entry.QueryString} " +
                $"-> {entry.StatusCode} ua=\"{entry.UserAgent}\" " +
                $"xff=\"{entry.XForwardedFor}\" xrip=\"{entry.XRealIp}\" trace={entry.TraceIdentifier}");

            // 同一进程内串行写文件，确保每个请求完整落在一行 JSONL。
            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                await File.AppendAllTextAsync(file, line, Encoding.UTF8, CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // 监控失败不能影响代理主流程。
        }
    }

}
