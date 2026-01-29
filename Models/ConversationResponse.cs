namespace fluid_durable_agent.Models;

public class ConversationResponse
{
    public string? QuestionResponse { get; set; }
    public string? AcknowledgeInputs { get; set; }
    public string? ValidationConcerns { get; set; }
    public string? FinalThoughts { get; set; }
    public string? FieldFocus { get; set; }
}
