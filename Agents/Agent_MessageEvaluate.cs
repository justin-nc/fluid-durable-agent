using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;

namespace fluid_durable_agent.Agents;

public class Agent_MessageEvaluate
{
    private readonly Microsoft.Extensions.AI.IChatClient _chatClient;

    public Agent_MessageEvaluate(Microsoft.Extensions.AI.IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Evaluates the latest dialog turn for questions, requests, distractions, and value-providing content.
    /// </summary>
    /// <param name="priorMessages">List of prior messages (expected to include the last assistant and user messages)</param>
    /// <param name="formContext">Form context to help detect relevance or distraction</param>
    /// <returns>MessageEvaluationResult with boolean flags</returns>
    public async Task<MessageEvaluationResult> EvaluateMessageAsync(List<string> priorMessages, string formContext, String formFields,  String formSections)
    {
        var safePriorMessages = priorMessages ?? new List<string>();

        var lastAssistantMessage = safePriorMessages.Count >= 2
            ? safePriorMessages[^2]
            : string.Empty;
        var lastUserMessage = safePriorMessages.Count >= 1
            ? safePriorMessages[^1]
            : string.Empty;

        var prompt = $@"SYSTEM MESSAGE:
As a highly percise evaluator in a process that helps users complete complex forms, you evaluate incoming chat text from a user to help an AI orchestrator understand the content of an incoming message from the user. The user message will contain text as well as the name of the form input field that's currently in focus. Occasionally, the user may select a different field and provide a value for that field. This would not be considered a distraction. You need to return 4 boolean values based on what you observe in an incoming message:

contains_question: The user is asking a question about the field or the form in general.

contains_request: The user is asking you to perform some kind of action such as suggesting an answer or recalling previously entered data.

contains_distraction: The user is attempting to divert the conversation to something not relevant to the form data entry process. 

**IMPORTANT GUIDANCE ON DISTRACTIONS:**
- Review the FORM CONTEXT below to understand what topics and fields are relevant to this form
- If the user's message relates to ANY field, topic, or concept mentioned in the form context, it is NOT a distraction - even if they didn't directly answer the last question asked
- Users may volunteer information about different form fields in any order - this is helpful, not a distraction
- A user may confirm previously provided information or make corrections - this is helpful, not a distraction
- Only mark as distraction if the content is clearly unrelated to ANY aspect of the form or its subject matter
- Examples of actual distractions: asking about unrelated topics (weather, sports, personal matters), attempting to change the subject to something completely outside the form's scope
- A user asking about a certain field or section of the form is NOT a distraction
 


contains_values: The content of the message appears to answer one or multiple questions or provide value(s) that could be entered into a form field. This could include direct answers to questions, or volunteering information relevant to the form fields.   
- Simple one word answers could be field values if the last assistant messsage asked a question.

Always provide your output in json. Here are some example transactions:

Example 1:
A: What is the name of the agency
user: NC Department of Information Technology
response: {{{{""contains_question"": false, ""contains_request"": false, ""contains_distraction"": false, ""contains_values"": true}}}}

Example 2:
assistant: Can you tell me what the business case is?
user: What is the square root of pi?
response: {{{{""contains_question"": true, ""contains_request"": false, ""contains_distraction"": true, ""contains_values"": false}}}}

Example 3:
assistant: Can you tell me what the business case is?
user: 25,000.00 [inputFocus:budgetAmount]
response: {{{{""contains_question"": false, ""contains_request"": false, ""contains_distraction"": false, ""contains_values"": true}}}}

Example 4:
assistant: What is the title for this program?
user: Can you help me create one?[inputFocus:programTitle]
response: {{{{""contains_question"": false, ""contains_request"": true, ""contains_distraction"": false, ""contains_values"": false}}}}

Example 5 - User provides information about a different field (NOT a distraction):
assistant: What is the project title?
user: Actually, the budget is $50,000 and we need it by next month
response: {{{{""contains_question"": false, ""contains_request"": false, ""contains_distraction"": false, ""contains_values"": true}}}}


FORM CONTEXT: {formContext}

FORM SECTIONS: {formSections}

FORM FIELD NAMES: {formFields}

PROMPT:
Please evaluate this dialog:

assistant: {lastAssistantMessage}
user: {lastUserMessage}

Return ONLY valid JSON with the 4 boolean properties: contains_question, contains_request, contains_distraction, contains_values.";

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, prompt)
        };

        // Try up to 3 times to get valid JSON
        int maxAttempts = 5;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var content = "{}";
            try
            {
                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
                content = response?.Text ?? "{}";
                var evaluation = JsonSerializer.Deserialize<MessageEvaluationResult>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return evaluation ?? new MessageEvaluationResult();
            }
            catch (JsonException)
            {
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    try
                    {
                        var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                        var evaluation = JsonSerializer.Deserialize<MessageEvaluationResult>(jsonContent,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        return evaluation ?? new MessageEvaluationResult();
                    }
                    catch (JsonException)
                    {
                        // If this is the last attempt, return default; otherwise retry
                        if (attempt == maxAttempts - 1)
                        {
                            return new MessageEvaluationResult();
                        }
                    }
                }
                else if (attempt == maxAttempts - 1)
                {
                    // Last attempt and couldn't find JSON structure
                    return new MessageEvaluationResult();
                }
            }

            // Small delay before retry
            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(100);
            }
        }

        return new MessageEvaluationResult();
    }
}
