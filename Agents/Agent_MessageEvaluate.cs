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
    public async Task<MessageEvaluationResult> EvaluateMessageAsync(List<string> priorMessages, string formContext, String formFields,  String formSections, String? tools=null)
    {
        var safePriorMessages = priorMessages ?? new List<string>();

        var lastAssistantMessage = safePriorMessages.Count >= 2
            ? safePriorMessages[^2]
            : string.Empty;
        var lastUserMessage = safePriorMessages.Count >= 1
            ? safePriorMessages[^1]
            : string.Empty;
        var toolList=tools != null ? "- " + tools.Replace("\n", "\n- ") : "";
        var prompt = $@"SYSTEM MESSAGE:
As a highly percise evaluator in a process that helps users complete complex forms, you evaluate incoming chat text from a user to help an AI orchestrator understand the content of an incoming message from the user. The user message will contain text as well as the name of the form input field that's currently in focus. Occasionally, the user may select a different field and provide a value for that field. This would not be considered a distraction. You need to return 4 boolean values based on what you observe in an incoming message:

contains_distraction: The user is attempting to divert the conversation to something not relevant to the form data entry process. If they ask irrelevant, non-business questions, this is a distraction.
contains_question: The user is asking a question about the field or the form in general.  If the question is not relevant to the form or any of the fields, then this should not be considered a question.  If the user is asking for help or guidance on how to answer a question, this should be considered a question. If they are asking for technical help with how to use the form, this should also be considered a question.  
contains_request: The user is asking you to perform some kind of action such as suggesting an answer or recalling previously entered data. If the user is requesting navigation to or selection of a field, this should not be considered a request.
contains_values: The content of the message appears to answer one or multiple questions or provide value(s) that could be entered into a form field. This could include direct answers to questions, or volunteering information relevant to the form fields.
requires_tool (string): The user is asking for help with something for which there is a tool, such as recalling previously entered information, suggesting possible values, or looking up information. (See tool list to determine what qualifies as a tool request).  

- Simple one word answers could be field values if the last assistant messsage asked a question.

**NOTE ON contains_question vs. contains_request:** 
A user asking ""What are you looking for here?"" is asking a question. 
A user asking ""Can you [do something]?"" is making a request. 
The first example would set contains_question to true, the second example would set contains_request to true.
Nuance is critical here.  For example, if the user is asking if we can skip a field, this is a request, not a question, because they are asking for an action to be taken, not asking for information. 

Conversley if the user says ""Explain"" or ""Help Me Choose"", this should be considered a question, as they are asking for guidance and not an action to be performed.


**IMPORTANT GUIDANCE ON DISTRACTIONS:**
- Review the FORM CONTEXT below to understand what topics and fields are relevant to this form.
- If the user's message relates to ANY field, topic, or concept mentioned in the form context, it is NOT a distraction - even if they didn't directly answer the last question asked.
- Users may volunteer information about different form fields in any order - this is helpful, not a distraction.
- Users may start talking about something related to the form and then attempt to divert the conversation to an unrelated topic - in this case, the whole message should be considered a distraction, because the user's intent appears to be to divert the conversation, even if they included some relevant information in the message.

 
Always provide your output in json. Here are some example transactions:

Example 1:
assistant: What is the name of the agency?
user: NC Department of Information Technology
response: {{{{""contains_question"": false, ""contains_request"": false, ""contains_distraction"": false, ""contains_values"": true}}}}

Example 2:
assistant: Can you tell me what the business case is?
user: What is the square root of pi?
response: {{{{""contains_question"": true, ""contains_request"": false, ""contains_distraction"": true, ""contains_values"": false}}}}

Example 3:
assistant: Can you tell me what the business case is?
user: Who was the 16th president of the United States?
response: {{{{""contains_question"": true, ""contains_request"": false, ""contains_distraction"": true, ""contains_values"": false}}}}

Example 4 - User provides information about a different field (NOT a distraction):
assistant: Can you tell me what the business case is?
user: 25,000.00 [inputFocus:budgetAmount]
response: {{{{""contains_question"": false, ""contains_request"": false, ""contains_distraction"": false, ""contains_values"": true}}}}

Example 5:
assistant: What is the title for this program?
user: Can you help me create one?[inputFocus:programTitle]
response: {{{{""contains_question"": false, ""contains_request"": true, ""contains_distraction"": false, ""contains_values"": false}}}}

Example 5 - User provides information about a different field (NOT a distraction):
assistant: What is the project title?
user: Actually, the budget is $50,000 and we need it by next month
response: {{{{""contains_question"": false, ""contains_request"": false, ""contains_distraction"": false, ""contains_values"": true}}}}

Example 6 - User asks for a save (tool use):
assistant: What is the project title?
user: [inputFocus:projectTitle] Can you save my progress so far?
response: {{{{""contains_question"": false, ""contains_request"": true, ""contains_distraction"": false, ""contains_values"": false, ""requires_tool"": ""#SAVE#""}}}}

Example 7 - User asks for lookup (tool use):
assistant: What is the owner's name?
user: [inputFocus:ownerName] Can you look up the owner's information?
response: {{{{""contains_question"": false, ""contains_request"": true, ""contains_distraction"": false, ""contains_values"": false, ""requires_tool"": ""LOOKUP_EMPLOYEE""}}}}

Example 8 - User asking for a field (tool use):
assistant: What is the project title?
user: [inputFocus:projectTitle] Can you show me the budget for this project?
response: {{{{""contains_question"": false, ""contains_request"": true, ""contains_distraction"": false, ""contains_values"": false, ""requires_tool"": ""INTERNAL_FORM_NAV""}}}}

TOOL LIST:
- #SAVE#: User must be explicitly asking that you save their progress to use this tool. They need to actually use the word ""save"" or a close synonym in their request.  If they are asking for help with something but don't explicitly ask to save, do not select this tool.
- #SUBMIT#: User is asking to submit the form. User 
- INTERNAL_FORM_NAVIGATION (VERY SPECIFIC USE CASE): User is asking to see a specific field.  This is the only condition under which tool can be invoked. 
{toolList}

SPECIAL NOTE ON TOOL SELECTION:
**Tool selection must be very deliberate.  Only select a tool if there is overwhelming evidence that it will help the user.** 
**Keep things conversational.  If the user is asking why a value was chosen or what the fields means, this is probably not the time to execute the tool. **
**BE CAUTIOUS ABOUT EXCECUTING A TOOL IN A LOOP. IF THE LATEST USER MESSAGE INDICATES A TOOL WAS ACTIVATED, AVOID CALLING IT IMMEDIATELY AGAIN**
Calling the wrong tool can lead to a very bad user experience, so if you are not certain that a tool is needed, don't include it.


FORM CONTEXT: {formContext}

FORM SECTIONS: {formSections}

FORM FIELD NAMES: {formFields}

PROMPT:
Please evaluate this dialog:

assistant: {lastAssistantMessage}
user: {lastUserMessage}

IF A USER IS ASKING FOR HELP WITH A FIELD AND A TOOL EXISTS, USE THE TOOL.  HOWEVER, IF THE TOOL EXPLICITLY PROVIDES THE FIELDS TAHT IT CAN HELP WITH, MAKE SURE THE USER IS ASKING FOR HELP WITH ONE OF THOSE FIELDS BEFORE SELECTING THE TOOL.  IF THE USER IS ASKING FOR HELP BUT IT'S NOT CLEAR WHAT THEY ARE ASKING FOR HELP WITH, DO NOT SELECT A TOOL.
DO NOT MAKE UP TOOLS.  IF THE TOOL NAME CANNOT BE FOUND IN THE LIST, IT ISN'T A REAL TOOL, SO DO NOT SELECT IT. 
WHENEVER NO TOOL IS SELECTED RETURN AN EMPTY STRING FOR requires_tool. 
MAKE SURE THAT YOU FLAG DISTRACTIONS ACCURATELY.  IF THE USER IS TALKING ABOUT ANY TOPIC RELATED TO THE FORM OR ANY FIELD, THIS IS NOT A DISTRACTION, EVEN IF THEY AREN'T ANSWERING THE CURRENT QUESTION.  IF THEY ARE TALKING ABOUT SOMETHING UNRELATED TO THE FORM, THIS IS A DISTRACTION.

Return ONLY valid JSON with the 4 boolean properties: contains_question, contains_request, contains_distraction, contains_values and the string property requires_tool.";

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
                //When another value is true, contains_distraction should not be true. This is a sanity check to catch cases where the model may have misunderstood the instructions and marked something as a distraction when it actually contains relevant content.
               /* if (evaluation.ContainsDistraction && (evaluation.ContainsQuestion || evaluation.ContainsRequest || evaluation.ContainsValues))
                {
                    // If contains_distraction is true, the other three should be false. If not, this is likely a misinterpretation.
                    evaluation.ContainsDistraction = false;
                }*/
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
