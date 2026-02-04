using System.Text.Json.Serialization;

namespace fluid_durable_agent.Models;

public sealed class MessageEvaluationResult
{
    [JsonPropertyName("contains_question")]
    public bool ContainsQuestion { get; set; }

    [JsonPropertyName("contains_request")]
    public bool ContainsRequest { get; set; }

    [JsonPropertyName("contains_distraction")]
    public bool ContainsDistraction { get; set; }

    [JsonPropertyName("contains_values")]
    public bool ContainsValues { get; set; }
}
