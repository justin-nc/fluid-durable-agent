using System.Text.Json;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;

namespace fluid_durable_agent.Agents;

public class Agent_FieldValidation
{
    private readonly Microsoft.Extensions.AI.IChatClient _chatClient;

    public Agent_FieldValidation(Microsoft.Extensions.AI.IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Validates field values against form validation rules and flags potential concerns
    /// </summary>
    /// <param name="userMessage">The user's chat message</param>
    /// <param name="form">The form containing fields and validation rules</param>
    /// <param name="completedFields">Field values that have been completed</param>
    /// <param name="newFieldValues">New field values to validate</param>
    /// <returns>ValidationResult containing errors and warnings</returns>
    public async Task<ValidationResult> ValidateFieldValuesAsync(
        string userMessage, 
        Form form, 
        Dictionary<string, FieldValue>? completedFields = null,
        List<FormFieldValue>? newFieldValues = null)
    {
        if (form?.Body == null || form.Body.Count == 0 || newFieldValues == null || newFieldValues.Count == 0)
        {
            return new ValidationResult();
        }

        // Build field information as JSON
        var fieldsInfoObject = form.Body.Select(field => new
        {
            id = field.Id,
            label = field.Label,
            type = field.Type,
            subLabel = field.SubLabel,
            note = field.Note,
            placeholder = field.Placeholder,
            isRequired = field.IsRequired,
            isMultiline = field.IsMultiline,
            na_option = field.NaOption,
            choices = field.Choices?.Select(c => new { value = c.Value, title = c.Title }).ToList()
        }).ToList();
        
        var fieldsInfo = JsonSerializer.Serialize(fieldsInfoObject, new JsonSerializerOptions { WriteIndented = true });

        // Build completed fields information as JSON if present
        var completedFieldsInfo = string.Empty;
        if (completedFields != null && completedFields.Count > 0)
        {
            var completedFieldsObject = completedFields.Select(kvp => new
            {
                fieldId = kvp.Key,
                value = kvp.Value.Value?.ToString() ?? "",
                note = kvp.Value.Note
            }).ToList();
            
            completedFieldsInfo = JsonSerializer.Serialize(completedFieldsObject, new JsonSerializerOptions { WriteIndented = true });
        }

        // Build new field values information as JSON
        var newFieldValuesInfo = JsonSerializer.Serialize(newFieldValues, new JsonSerializerOptions { WriteIndented = true });

        var prompt = $@"You are a validation assistant for an intelligent form completion system. Your job is to validate field values that have been extracted from user input and identify any errors or warnings.

# YOUR ROLE
You validate that field values comply with the validation rules specified in each field's 'note' property. You also flag fields that may need human review or double-checking.

# VALIDATION TYPES

## ERRORS
These are critical validation failures that MUST be corrected:
- Value does not match a required format (e.g., email, date, phone number)
- Value is not one of the valid choices for a choice field
- Required field is missing a value
- Value violates explicit constraints in the field's 'note' property
- Value type mismatch (e.g., text in a number field)
- Value exceeds length limits or other hard constraints

## WARNINGS
These are potential issues that should be reviewed but may be acceptable:
- Value seems unusual or unexpected based on context
- Values that are borderline acceptable
- Missing optional fields that would be helpful to complete
- Values that appear incomplete or truncated    
- Ambiguous values that could be interpreted multiple ways
- Values that conflict with or contradict other field values

# INPUT DATA

## FORM FIELDS
{fieldsInfo}

## COMPLETED FIELDS (EXISTING)
{completedFieldsInfo}

## NEW FIELD VALUES (TO VALIDATE)
{newFieldValuesInfo}

## USER MESSAGE
{userMessage}

# OUTPUT FORMAT
Return a JSON object with two arrays: 'errors' and 'warnings'. Each array contains objects with 'fieldId' and 'concern' properties. Both arrays are optional. Don't provide an array if there are no items to report.

Example:
{{
  ""errors"": [
    {{
      ""fieldId"": ""email_address"",
      ""concern"": ""Email format is invalid. Expected format: user@domain.com""
    }}
  ],
  ""warnings"": [
    {{
      ""fieldId"": ""budget_amount"",
      ""concern"": ""Budget amount seems unusually high. Please verify this is correct.""
    }}
  ]
}}

# INSTRUCTIONS
1. Review each field value in NEW FIELD VALUES 
2. Check against the field's validation rules in the 'note' property
3. Verify the value matches the field type and any choice constraints
4. Consider context from the user message and existing completed fields
5. Return ONLY errors for true validation failures
6. Return warnings for questionable values that merit review
7. If everything validates correctly, return empty arrays for both errors and warnings
8. Be specific in your concern descriptions - explain what's wrong and what's expected

Return ONLY valid JSON matching the format above.
If there are not errors or warnings, return an empty object: {{}}";

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, prompt)
        };

        var content = "";
        
        try
        {
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
            content = response?.Text ?? "{}";
            
            var validationResult = JsonSerializer.Deserialize<ValidationResult>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return validationResult ?? new ValidationResult();
        }
        catch (JsonException)
        {
            // If parsing fails, try to extract JSON from the response
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var validationResult = JsonSerializer.Deserialize<ValidationResult>(jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return validationResult ?? new ValidationResult();
            }
            return new ValidationResult();
        }
    }
}
