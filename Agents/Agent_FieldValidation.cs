using System.Text.Json;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;
using static fluid_durable_agent.Tools.ConversationPromptTemplates;

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

        var prompt = $@"You are a validation assistant for an intelligent form completion system. Your job is to validate field values that have been already been extracted from user input and identify any errors or warnings.

# YOUR ROLE
You validate that field values comply with the validation rules specified in each field's 'note' property. You also flag fields that may need human review or double-checking.
**FIELDS THAT DO NOT HAVE A VALUE SHOULD NOT BE VALIDATED. ONLY VALIDATE FIELDS WITH PROVIDED VALUES.**

## FIELDS: A JSON list of fields and their properties.
**Properties include:**
- 'label': Label that will be used when presenting the field. Provided for context.
- 'type': Type of input. Provided for context.
- 'id': Field identifier; the value that you will use to convey the next question.
- 'placeholder': Used for the input field. Provided for context.
- 'subLabel': Additional information shown to the user when asked to provide information. Provided for context.
- 'na_option': Indicates that ""N/A"" is an appropriate response for this field.
- 'isRequired': Indicates the field is required when not conditional or when the condition is met.
- 'choices': Options that must be used to complete this field (if applicable). If the field is a choice set and the user's response does not align with one of the choices, ask the question again.
- 'isMultiline': Indicates the field could be a paragraph or more of text. A single-word answer is not sufficient unless the response is ""N/A"" and 'na_option' is true.
- 'note': Contains important information about the field which may be helpful when determining how to complete the field based on user input. This could include instructions, definitions, or other relevant details.



# VALIDATION TYPES

## ERRORS
These are critical validation failures that MUST be corrected:
- Value does not match a required format (e.g., email, date, phone number)
- Value is not one of the valid choices for a choice field
- Required field is missing a value
- Value violates explicit constraints in the field's 'note' property
- Value type mismatch (e.g., text in a number field)
- Value exceeds length limits or other hard constraints
- Do not scrutinize number formatting unless field notes explicitly specify a required format. For example, if a field expects a numeric value, ""1000"", ""1,000"", and ""$1,000"" would all be acceptable unless the field's note says that currency symbols or commas are not allowed.
- Currency formatting does not need to be strictly validated as long as the numeric value is clear. For example, ""$1,000"" or ""1000"" would both be acceptable for a numeric field representing a budget amount.
- If the field has no issue, do not include it in the errors list

## WARNINGS
These are potential issues that should be reviewed but may be acceptable:
- Value seems unusual or unexpected based on context
- Values that are borderline acceptable
- Missing optional fields that would be helpful to complete
- Values that appear incomplete or truncated    
- Ambiguous values that could be interpreted multiple ways
- Values that conflict with or contradict other field values
- If the field has no issue, do not include it in the warnings list

## NOTES
- All values (even numeric fields) will be represented as strings. This is not an error as long as the string value represnts a number where required.  
- All dates should be today or in the future. Past dates should be flagged as errors unless the field's note explicitly allows for past dates (e.g., ""Date of Birth"").
- ERROR and WARNING outputs are only for fields that are invalid or questionable.



## FORM FIELDS
{fieldsInfo}

## COMPLETED FIELDS (EXISTING)
{completedFieldsInfo}

## NEW FIELD VALUES (TO VALIDATE)
{newFieldValuesInfo}

 

# OUTPUT FORMAT
Return a JSON object with two arrays: 'errors' and 'warnings'. Each array contains objects with 'fieldId' and 'concern' properties. Both arrays are optional. Don't provide an array if there are no items to report.

Example1 Warning and Error:
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

Example 2  - Warning only:
{{
  ""warnings"": [
    {{
      ""fieldId"": ""budget_amount"",
      ""concern"": ""Budget amount seems unusually high. Please verify this is correct.""
    }}
  ]
}}

Example 3 - No issues:
{{}}


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
If there are not errors or warnings, return an empty object: {{}}

#important context
{TodayDateContext}";

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, prompt)
        };

        var content = "";
        const int maxAttempts = 2;
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
                content = response?.Text ?? "{}";
                
                // Validate the AI response for actual errors
                var isValidError = await ValidateErrorResponseAsync(content, newFieldValuesInfo);
                
                if (isValidError)
                {
                    // Parse and return the validation result
                    var validationResult = JsonSerializer.Deserialize<ValidationResult>(content, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return validationResult ?? new ValidationResult();
                }
                else if (attempt == maxAttempts)
                {
                    // After max attempts with invalid errors, return empty arrays
                    return new ValidationResult();
                }
                
                // If not valid and not final attempt, continue to retry
            }
            catch (JsonException)
            {
                // If parsing fails, try to extract JSON from the response
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    
                    // Validate the extracted JSON response
                    var isValidError = await ValidateErrorResponseAsync(jsonContent, newFieldValuesInfo);
                    
                    if (isValidError)
                    {
                        var validationResult = JsonSerializer.Deserialize<ValidationResult>(jsonContent,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        return validationResult ?? new ValidationResult();
                    }
                    else if (attempt == maxAttempts)
                    {
                        // After max attempts with invalid errors, return empty arrays
                        return new ValidationResult();
                    }
                }
                else if (attempt == maxAttempts)
                {
                    return new ValidationResult();
                }
            }
        }
        
        return new ValidationResult();
    }
    
    /// <summary>
    /// Validates if the AI-generated errors/warnings are actually valid errors
    /// </summary>
    /// <param name="aiResponse">The AI response containing errors and warnings</param>
    /// <returns>True if the errors are valid, false otherwise</returns>
    private async Task<bool> ValidateErrorResponseAsync(string aiResponse,  string newFieldsInfo)
    {
        var validationPrompt = @$"You evaluate incoming errors / warnings based on a user input and determine if they are actually errors and if the error statements are accurate.
        For example, if the error states that a date is in the past, be sure the actual date provided is indeed in the past. 
        If a concern states that a value is acceptable, sufficient, etc. that's an invalid error.
        
        Return only a JSON {{ ""valid_error"": <boolean> }}
FIELD VALUES THAT WERE VALIDATED:
{newFieldsInfo}

#important context
{TodayDateContext}

AI Response to Validate:
{aiResponse}";

        var validationMessages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, validationPrompt)
        };
        
        try
        {
            var validationResponse = await _chatClient.GetResponseAsync(validationMessages, cancellationToken: default);
            var validationContent = validationResponse?.Text ?? "{}";
            
            // Extract JSON from response
            var jsonStart = validationContent.IndexOf('{');
            var jsonEnd = validationContent.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = validationContent.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var validationResult = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (validationResult != null && validationResult.ContainsKey("valid_error"))
                {
                    var validError = validationResult["valid_error"];
                    if (validError is JsonElement element)
                    {
                        return element.GetBoolean();
                    }
                    else if (validError is bool boolValue)
                    {
                        return boolValue;
                    }
                    else if (validError != null && bool.TryParse(validError.ToString(), out var parsedBool))
                    {
                        return parsedBool;
                    }
                }
            }
            
            // If we can't parse or find valid_error, assume it's valid to avoid infinite loops
            return true;
        }
        catch
        {
            // On error, assume valid to avoid infinite loops
            return true;
        }
    }
}
