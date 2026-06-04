namespace OllamaAiProxy.Monitoring;

public sealed class RequestLoggingOptions
{
    public const string SectionName = "RequestLogging";

    public bool Enabled { get; set; }

    public string Directory { get; set; } = "logs";
}
