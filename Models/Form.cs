using System.Text.Json.Serialization;

namespace fluid_durable_agent.Models;

public class Form
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "AdaptiveCard";

    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://adaptivecards.io/schemas/adaptive-card.json";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.5";

    [JsonPropertyName("body")]
    public List<FormField> Body { get; set; } = new();
}
