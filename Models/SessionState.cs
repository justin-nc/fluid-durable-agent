namespace fluid_durable_agent.Models;

public class SessionState
{
    public string FormCode { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<string> History { get; set; } = new List<string>();
    public string NewMessage { get; set; } = string.Empty;
    public Dictionary<string, FieldValue> CompletedFieldValues { get; set; } = new Dictionary<string, FieldValue>();
}
