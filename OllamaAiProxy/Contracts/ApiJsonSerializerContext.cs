using System.Text.Json.Serialization;
using OllamaAiProxy.Services;

namespace OllamaAiProxy.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaShowResponse))]
[JsonSerializable(typeof(OpenAiModelsResponse))]
[JsonSerializable(typeof(OpenAiModel))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ModelOverride))]
[JsonSerializable(typeof(string))]
internal sealed partial class ApiJsonSerializerContext : JsonSerializerContext;
