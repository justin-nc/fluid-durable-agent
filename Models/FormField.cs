using System.Text.Json.Serialization;

namespace fluid_durable_agent.Models;

public class FormField
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; set; }

    [JsonPropertyName("isRequired")]
    public bool? IsRequired { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("subLabel")]
    public string? SubLabel { get; set; }

    [JsonPropertyName("isMultiline")]
    public bool? IsMultiline { get; set; }

    [JsonPropertyName("na_option")]
    public bool? NaOption { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("codeSnippet")]
    public string? CodeSnippet { get; set; }

    [JsonPropertyName("choices")]
    public List<FieldChoice>? Choices { get; set; }

    [JsonPropertyName("decisionTree")]
    public List<DecisionTreeNode>? DecisionTree { get; set; }
}

public class FieldChoice
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public class DecisionTreeNode
{
    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public List<DecisionTreeOption> Options { get; set; } = new();
}

public class DecisionTreeOption
{
    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;
}
