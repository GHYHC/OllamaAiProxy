using System.Text.Json;

namespace OllamaAiProxy.Services;

/// <summary>
/// 思考强度（reasoning effort）档位注入器。
/// 把每个模型在覆盖值里配置的「思考强度」默认值写进转发给上游的请求，
/// 仅在客户端没有显式指定对应思考参数时生效。
/// 档位词表与 DeepSeek「思考模式」文档的 reasoning_effort 取值一致：
/// none（关闭思考）、low（低）、medium（中）、high（高）。
/// </summary>
public static class ThinkingStrengthInjector
{
    /// <summary>合法档位取值。none 只对支持它的上游（如 DeepSeek）有意义。</summary>
    public static readonly string[] Levels = { "none", "low", "medium", "high" };

    /// <summary>判断档位取值是否合法；null 或未知值返回 false。</summary>
    public static bool IsValidLevel(string? level) =>
        level is not null && Array.IndexOf(Levels, level) >= 0;

    /// <summary>
    /// 按端点格式把档位注入请求。chat 写顶层 reasoning_effort，responses 写 reasoning.effort。
    /// 无需注入（档位为空/非法，或请求已显式指定）时返回 null，调用方应原样转发原请求。
    /// 返回值是非 null 的新 JsonDocument，调用方负责 Dispose。
    /// </summary>
    public static JsonDocument? Apply(JsonDocument request, string? level, bool responses)
    {
        if (!IsValidLevel(level))
            return null;

        var root = request.RootElement;
        // 客户端已显式指定思考参数时尊重客户端，不注入默认值。
        if (responses)
        {
            if (root.TryGetProperty("reasoning", out _))
                return null;
        }
        else if (root.TryGetProperty("reasoning_effort", out _))
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            if (responses)
            {
                writer.WritePropertyName("reasoning");
                writer.WriteStartObject();
                writer.WriteString("effort", level);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteString("reasoning_effort", level);
            }

            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }
}
