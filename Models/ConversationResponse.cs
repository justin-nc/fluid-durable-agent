namespace fluid_durable_agent.Models;

public class ConversationResponse
{
    public string? QuestionResponse { get; set; }
    public string? AcknowledgeInputs { get; set; }
    public string? ValidationConcerns { get; set; }
    public string? FinalThoughts { get; set; }
    public string? FieldFocusMessage { get; set; }
    public DraftedField? DraftedField { get; set; }

    public bool IncludesFieldUpdates { get; set; } = false;
}

public class DraftedField
{
    public string? FieldName { get; set; }
    public string? Value { get; set; }
}
