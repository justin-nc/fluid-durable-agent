using System.Text.Json;
using System.Text.Json.Serialization;

namespace fluid_durable_agent.Models;

public sealed class FormFieldValue
{
    [JsonPropertyName("fieldName")]
    public string? FieldName { get; set; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("inferred")]
    public bool? Inferred { get; set; }

    [JsonPropertyName("drafted")]
    public bool? Drafted { get; set; }
}

public sealed class FieldValue
{
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

}