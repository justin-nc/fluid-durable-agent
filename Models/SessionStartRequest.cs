namespace fluid_durable_agent.Models;

public class SessionStartRequest
{
    public string FormCode { get; set; } = string.Empty;
    public string? Version { get; set; } = string.Empty;
    public Dictionary<string, string>? FormData { get; set; }
}
