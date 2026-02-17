namespace fluid_durable_agent.Models;

public class SessionState
{
    // Core orchestration state - lightweight and kept in custom status
    public string FormCode { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string NewMessage { get; set; } = string.Empty;
    public string? Command { get; set; }
    public string? ClientAccessToken { get; set; }
    public DateTime? TokenExpiration { get; set; }
    
    // Entity references - heavy state is stored in durable entities
    // History is stored in SessionHistoryEntity_{instanceId}
    // CompletedFieldValues is stored in FormFieldsEntity_{instanceId}
}
