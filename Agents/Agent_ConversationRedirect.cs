using System.Text.Json;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;
using static fluid_durable_agent.Tools.ConversationPromptTemplates;

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
        string? focusFieldId = null,
        bool distraction_detected = true)
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

        string promptContext = distraction_detected
            ? "The user appears to be distracted or off-topic. Your goal is to gently redirect them back to completing the form while being encouraging and positive."
            : "The user needs help getting back to completing the form. Present the next logical step in a friendly and encouraging way.";
        var prompt = $@"You are a friendly and encouraging conversational assistant helping users complete a form.
        {promptContext}

# YOUR RESPONSIBILITIES

1. **BE ENCOURAGING** - Acknowledge that you understand their input, but gently redirect
2. **BE POLITE** - Never be dismissive or rude about their distraction
3. **GUIDE BACK TO FORM** - Help them refocus on the next field that needs to be completed
4. **PROVIDE CONTEXT** - Give them a clear next step
5. **SELECT THE NEXT FIELD** - Identify the next logical field to complete based on:
   - Fields that haven't been completed yet
   - Start from the top of the form and work down logically
   - Required fields take priority

# OUTPUT REQUIREMENTS

Return a JSON object with the following properties:

- **FinalThoughts**: (string, required) A friendly, encouraging message that:
  - Politely acknowledges their message without directly answering off-topic questions
  - Gently redirects back to the form completion task
  - Identifies the next logical field to complete (start from top, move down)
  - Ask the question for that next field in a natural, conversational way
  - Uses markdown formatting with paragraph breaks for readability
  - If the next question is open ended (not a choice field), ask the question related to the next logical field to complete.
  - If the next question has choices, **DO NOT incorporate the question into FinalThoughts.** Providing the field focus and response options is sufficient. Immediately following your final thought, the user will be presented with the question and options. 

- **FieldFocus**: (string, optional) The field ID of the next logical field to focus on. Choose based on:
  - Fields that haven't been completed yet
  - Start from the top of the form and work down logically
  - Required fields take priority

# FORMATTING GUIDELINES

- Use markdown formatting for text
- Mention the field label when asking for input be sure the field name is bolded.
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
  ""FinalThoughts"": ""I understand! Let's focus on getting this form completed for you.\\n\\nWhat is the **title of this IT need or project**?"",
  ""FieldFocus"": ""titleOfITNeed""
}}}}

Example 3 - With progress acknowledgment:
{{{{
  ""FinalThoughts"": ""Thanks for that input! To keep things moving, let's continue with the form.\\n\\nYou've made good progress so far. Next, I need to know: what is the **business case or justification for this request**?"",
  ""FieldFocus"": ""businessCase""
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
        const int maxAttempts = 3;
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
                content = response?.Text ?? "{{}}";
                
                var conversationResponse = JsonSerializer.Deserialize<ConversationResponse>(content, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (conversationResponse != null)
                {
                    return conversationResponse;
                }
            }
            catch (JsonException)
            {
                // If parsing fails, try to extract JSON from the response
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    try
                    {
                        var conversationResponse = JsonSerializer.Deserialize<ConversationResponse>(jsonContent,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (conversationResponse != null)
                        {
                            return conversationResponse;
                        }
                    }
                    catch (JsonException)
                    {
                        // Continue to next attempt
                    }
                }
            }
            
            // If this isn't the last attempt, continue to retry
            if (attempt < maxAttempts)
            {
                continue;
            }
        }
        
        // Fallback response after all attempts exhausted
        return new ConversationResponse
        {
            FinalThoughts = "I'm here to help you complete this form. Let's get back on track!"
        };
    }
}
