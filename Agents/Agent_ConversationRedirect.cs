using System.Text.Json;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;

namespace fluid_durable_agent.Agents;

public class Agent_ConversationRedirect
{
    private readonly Microsoft.Extensions.AI.IChatClient _chatClient;

    public Agent_ConversationRedirect(Microsoft.Extensions.AI.IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Generates a redirection response when the user appears distracted, providing encouraging words and guidance back to form completion
    /// </summary>
    /// <param name="conversationHistory">The conversation history (list of messages)</param>
    /// <param name="form">The form containing fields</param>
    /// <param name="completedFields">Field values that have been completed</param>
    /// <param name="focusFieldId">The field ID that the user is currently focused on/looking at</param>
    /// <returns>ConversationResponse with encouraging redirection</returns>
    public async Task<ConversationResponse> GenerateRedirectResponseAsync(
        Form form,
        Dictionary<string, FieldValue>? completedFields = null,
        string? focusFieldId = null)
    {
        if (form?.Body == null || form.Body.Count == 0)
        {
            return new ConversationResponse
            {
                FinalThoughts = "I'm here to help you complete this form. Let's get back on track!"
            };
        }

        // Build field information as JSON
        var fieldsInfoObject = form.Body.Select(field => new
        {
            id = field.Id,
            label = field.Label,
            type = field.Type,
            isRequired = field.IsRequired,
            subLabel = field.SubLabel
        }).ToList();
        
        var fieldsInfo = JsonSerializer.Serialize(fieldsInfoObject, new JsonSerializerOptions { WriteIndented = true });

        // Build completed fields information as JSON if present
        var completedFieldsInfo = "{}";
        if (completedFields != null && completedFields.Count > 0)
        {
            var completedFieldsObject = completedFields.Select(kvp => new
            {
                fieldId = kvp.Key,
                value = kvp.Value.Value?.ToString() ?? ""
            }).ToList();
            
            completedFieldsInfo = JsonSerializer.Serialize(completedFieldsObject, new JsonSerializerOptions { WriteIndented = true });
        }

        // Get focused field information if available
        var focusedFieldInfo = "null";
        if (!string.IsNullOrEmpty(focusFieldId))
        {
            var focusedField = form.Body.FirstOrDefault(f => f.Id == focusFieldId);
            if (focusedField != null)
            {
                focusedFieldInfo = JsonSerializer.Serialize(new
                {
                    id = focusedField.Id,
                    label = focusedField.Label,
                    type = focusedField.Type,
                    subLabel = focusedField.SubLabel
                }, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        var prompt = $@"You are a friendly and encouraging conversational assistant helping users complete a form. The user has gotten distracted or off-topic, and you need to gently redirect them back to completing the form.

# YOUR RESPONSIBILITIES

1. **BE ENCOURAGING** - Acknowledge that you understand their input, but gently redirect
2. **BE POLITE** - Never be dismissive or rude about their distraction
3. **GUIDE BACK TO FORM** - Help them refocus on the next field that needs to be completed
4. **PROVIDE CONTEXT** - Give them a clear next step

# OUTPUT REQUIREMENTS

Return a JSON object with the following properties:

- **FinalThoughts**: (string, required) A friendly, encouraging message that:
  - Politely acknowledges their message without directly answering off-topic questions
  - Gently redirects back to the form completion task
  - Identifies the next logical field to complete (start from top, move down)
  - Asks the question for that next field in a natural, conversational way
  - Uses markdown formatting with paragraph breaks for readability

- **FieldFocus**: (string, optional) The field ID of the next logical field to focus on. Choose based on:
  - Fields that haven't been completed yet
  - Start from the top of the form and work down logically
  - Required fields take priority

- **ResponseOptions**: (array of strings, optional) When asking about the next field, if that field has choices or allows N/A:
  - Include all available choice titles/values from the field's choices array
  - If the field has na_option set to true, add ""N/A"" or ""Not Applicable"" as an option
  - Only provide this when you're asking the user about a specific field that has predefined options

# FORMATTING GUIDELINES

- Use markdown formatting for text
- Mention the field label when asking for input and bold it
- Include paragraph breaks (double newlines) for readability
- Be warm, friendly, and patient
- Keep the redirect brief but effective
- Focus on moving forward positively
- Use line breaks to improve readability

# EXAMPLES

Example 1 - Off-topic question:
{{{{
  ""FinalThoughts"": ""I appreciate your curiosity! However, I'm here specifically to help you complete this form.\\n\\nLet's get back to it. Could you tell me the name of your agency?"",
  ""FieldFocus"": ""agency_name""
}}}}

Example 2 - General distraction:
{{{{
  ""FinalThoughts"": ""I understand! Let's focus on getting this form completed for you.\\n\\nWhat is the title of this IT need or project?"",
  ""FieldFocus"": ""titleOfITNeed""
}}}}

Example 3 - With progress acknowledgment:
{{{{
  ""FinalThoughts"": ""Thanks for that input! To keep things moving, let's continue with the form.\\n\\nYou've made good progress so far. Next, I need to know: what is the business case or justification for this request?"",
  ""FieldFocus"": ""businessCase""
}}}}

Example 4 - Field with choice options:
{{{{
  ""FinalThoughts"": ""I understand! Let's focus on getting this form completed for you.\\n\\nDoes this procurement involve software?"",
  ""FieldFocus"": ""includesSoftware"",
  ""ResponseOptions"": [""Yes"", ""No"", ""Not Applicable""]
}}}}

# IMPORTANT NOTES
- Do NOT answer off-topic questions directly
- Stay focused on redirecting to form completion
- Be encouraging and positive
- Always provide a clear next step
- Identify which field needs to be completed next

Return ONLY valid JSON matching the structure above.

----------

## CURRENT FIELD FOCUS
{focusedFieldInfo}

## FORM FIELDS
{fieldsInfo}

## COMPLETED FIELDS
{completedFieldsInfo}

#END OF PROMPT";

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, prompt)
        };

        var content = "";
        
        try
        {
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
            content = response?.Text ?? "{{}}";
            
            var conversationResponse = JsonSerializer.Deserialize<ConversationResponse>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return conversationResponse ?? new ConversationResponse
            {
                FinalThoughts = "I'm here to help you complete this form. Let's get back on track!"
            };
        }
        catch (JsonException)
        {
            // If parsing fails, try to extract JSON from the response
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var conversationResponse = JsonSerializer.Deserialize<ConversationResponse>(jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return conversationResponse ?? new ConversationResponse
                {
                    FinalThoughts = "I'm here to help you complete this form. Let's get back on track!"
                };
            }
            
            // Fallback response
            return new ConversationResponse
            {
                FinalThoughts = "I'm here to help you complete this form. Let's get back on track!"
            };
        }
    }
}
