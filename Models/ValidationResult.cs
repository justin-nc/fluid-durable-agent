namespace fluid_durable_agent.Models;

public class ValidationResult
{
    public List<FieldConcern>? Errors { get; set; } = new List<FieldConcern>();
    public List<FieldConcern>? Warnings { get; set; } = new List<FieldConcern>();
}
