using System.Text.Json;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;

namespace fluid_durable_agent.Agents;

public class Agent_FieldIdentification
{
    private readonly Microsoft.Extensions.AI.IChatClient _chatClient;

    public Agent_FieldIdentification(Microsoft.Extensions.AI.IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Identifies which form fields can be answered by the user's input message
    /// </summary>
    /// <param name="fields">List of form fields with labels and IDs</param>
    /// <param name="priorDialog">The conversation history; last item is the latest user message, second-to-last is the latest assistant message</param>
    /// <returns>List of FormField objects that the message can answer</returns>
    public async Task<List<FormField>> IdentifyAnswerableFieldsAsync(List<FormField> fields, List<string> priorDialog)
    {
        var safeDialog = priorDialog ?? new List<string>();
        //r lastUserMessage = safeDialog.Count >= 1 ? safeDialog[^1] : string.Empty;
        //r lastAssistantMessage = safeDialog.Count >= 2 ? safeDialog[^2] : string.Empty;

       /*.if (fields == null || fields.Count == 0 || string.IsNullOrWhiteSpace(lastUserMessage))
        {
            return new List<FormField>();
        }*/

        // Build field information as simplified JSON (just label and id)
        var fieldsInfoObject = fields.Where(field => !string.IsNullOrEmpty(field.Id)).Select(field => new
        {
            id = field.Id,
            label = field.Label
        }).ToList();
        
        var fieldsInfo = JsonSerializer.Serialize(fieldsInfoObject, new JsonSerializerOptions { WriteIndented = false });

      /* r assistantContext = string.IsNullOrWhiteSpace(lastAssistantMessage)
            ? string.Empty
            : $"\nLast assistant message (for context): {lastAssistantMessage}"; */

        var prompt = $@"You are a planning agent which is part of a larger team of agents which are helping a user complete a very long form.
You have this list of form fields and their id:
{fieldsInfo}

Your job is to take the message stream for context and then the last user message and provide the id for fields that the user text would be capable of answering. Provide the list as a json array.
An example of this would be if the assistant's last message said ""[Next focus field: divisionOfficeName]"" and the user then said ""Office of Awesomeness"", you would return the id for the divisionOfficeName field, because the user's message appears to be providing an answer to that field.

Prior Dialog:
{string.Join("\n", safeDialog)}

Return ONLY a JSON array of field IDs (strings) that can be answered by the user's input.
If no fields can be answered, return an empty array: []
Example response: [""{fieldsInfoObject[0]}"", ""{fieldsInfoObject[1]}""]";
        // Try up to 5 times with 200ms delay between attempts
        int maxAttempts = 5;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, prompt)
                };

                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
                var responseText =  response?.Text ?? "{}";
                // Clean up markdown code blocks if present
                if (responseText.StartsWith("```json"))
                {
                    responseText = responseText.Substring(7);
                }
                if (responseText.StartsWith("```"))
                {
                    responseText = responseText.Substring(3);
                }
                if (responseText.EndsWith("```"))
                {
                    responseText = responseText.Substring(0, responseText.Length - 3);
                }
                responseText = responseText.Trim();

                // Parse the JSON array
                var fieldIds = JsonSerializer.Deserialize<List<string>>(responseText);
                
                // Validate that we got a valid response
                if (fieldIds != null)
                {
                    // Filter the original fields list to only include identified field IDs
                    var identifiedFields = fields.Where(f => fieldIds.Contains(f.Id)).ToList();
                    
                    // If we got valid field IDs that match actual fields, return them
                    if (identifiedFields.Count > 0 || attempt == maxAttempts - 1)
                    {
                        return identifiedFields;
                    }
                }
                
                // If we got an empty or invalid response and it's not the last attempt, retry
                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(200);
                }
            }
            catch (JsonException ex)
            {
                // Log the error
                Console.WriteLine($"Error parsing response in IdentifyAnswerableFieldsAsync (attempt {attempt + 1}): {ex.Message}");
                
                // If this is the last attempt, return empty list
                if (attempt == maxAttempts - 1)
                {
                    return new List<FormField>();
                }
                
                // Delay before retry
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                // Log the error
                Console.WriteLine($"Error in IdentifyAnswerableFieldsAsync (attempt {attempt + 1}): {ex.Message}");
                
                // If this is the last attempt, return empty list
                if (attempt == maxAttempts - 1)
                {
                    return new List<FormField>();
                }
                
                // Delay before retry
                await Task.Delay(200);
            }
        }

        return new List<FormField>();
    }
}
