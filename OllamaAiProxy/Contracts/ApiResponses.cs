using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaAiProxy.Contracts;

public sealed record ApiErrorResponse(string Error);

public sealed record HealthResponse(
    string Status,
    IReadOnlyList<ProviderSummary> Providers);

public sealed record ProviderSummary(
    string Name,
    string Family);

public sealed record OllamaTagsResponse(
    IReadOnlyList<OllamaTag> Models);

public sealed record OllamaTag(
    string Name,
    string Model,
    string ModifiedAt,
    long Size,
    string Digest,
    OllamaDetails Details);

public sealed record OllamaDetails(
    string ParentModel,
    string Format,
    string Family,
    IReadOnlyList<string> Families,
    string ParameterSize,
    string QuantizationLevel);

public sealed record OllamaShowResponse(
    string License,
    string Modelfile,
    string Parameters,
    string Template,
    OllamaDetails Details,
    IReadOnlyDictionary<string, JsonElement> ModelInfo,
    IReadOnlyList<string> Capabilities,
    string ModifiedAt);
