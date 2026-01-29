using System.Text.Json.Serialization;

namespace fluid_durable_agent.Models;

public sealed class FormFieldsState
{
    [JsonPropertyName("fields")]
    public Dictionary<string, FormFieldValue> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
