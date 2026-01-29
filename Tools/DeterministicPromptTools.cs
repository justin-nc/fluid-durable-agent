using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace fluid_durable_agent.Tools;

public sealed class DeterministicPromptTools
{
    [Description("Builds a deterministic prompt by parsing JSON values and incorporating them into custom prompt text. Use {path.to.key} syntax to reference JSON values.")]
    public string BuildPromptFromJson(string json, string promptTemplate)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("json is required", nameof(json));
        }
        if (string.IsNullOrWhiteSpace(promptTemplate))
        {
            throw new ArgumentException("promptTemplate is required", nameof(promptTemplate));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, string> values = new Dictionary<string, string>();
        ExtractValues(document.RootElement, values, path: string.Empty);

        string result = promptTemplate;
        foreach (var kvp in values.OrderByDescending(x => x.Key.Length))
        {
            result = result.Replace($"{{{kvp.Key}}}", kvp.Value);
        }
        return result;
    }

    private static void ExtractValues(JsonElement element, Dictionary<string, string> values, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string nextPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                    ExtractValues(property.Value, values, nextPath);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string nextPath = string.IsNullOrEmpty(path) ? index.ToString() : $"{path}[{index}]";
                    ExtractValues(item, values, nextPath);
                    index++;
                }
                break;
            default:
                if (!string.IsNullOrEmpty(path))
                {
                    values[path] = ScalarToString(element);
                }
                break;
        }
    }

    private static string ScalarToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.ToString()
        };
    }
}
