namespace OllamaAiProxy.Contracts;

/// <summary>
/// 持有上游聊天响应。非流式和错误响应会缓存为文本，成功的流式响应保留为可复制的流。
/// endpoint 在把响应写回客户端后负责释放这个对象。
/// </summary>
public sealed class ProviderChatResponse : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;

    private ProviderChatResponse(HttpResponseMessage response, string contentType)
    {
        _response = response;
        StatusCode = (int)response.StatusCode;
        ContentType = contentType;
    }

    public int StatusCode { get; }

    public string ContentType { get; }

    public static async Task<ProviderChatResponse> BufferAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var result = new ProviderChatResponse(response, response.Content.Headers.ContentType?.ToString() ?? "application/json");
        result.Body = await response.Content.ReadAsStringAsync(cancellationToken);
        return result;
    }

    public static ProviderChatResponse Stream(HttpResponseMessage response) =>
        new(response, response.Content.Headers.ContentType?.ToString() ?? "text/event-stream");

    public string? Body { get; private set; }

    public Task<Stream> ReadStreamAsync(CancellationToken cancellationToken) =>
        _response.Content.ReadAsStreamAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
