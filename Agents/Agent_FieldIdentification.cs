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

        // Determine if this is an "init" message — run a second pass if so
        var lastUserMessage = safeDialog.LastOrDefault() ?? string.Empty;
        bool isInit = lastUserMessage.Contains("init", StringComparison.OrdinalIgnoreCase);
        int passes = priorDialog.Count < 3  || isInit ? 2 : 1;

        var fieldsInfoObject = fields.Where(field => !string.IsNullOrEmpty(field.Id) && (field.Type?.Contains("Input") == true)).Select(field => new { id = field.Id, label = field.Label }).ToList();

        var prompt = $@"You are a planning agent which is part of a larger team of agents which are helping a user complete a very long form.
INSTRUCTIONS:

Your job is to take the message stream and provide the id for fields that the user text would be capable of answering. Provide the list as a json array.
When the last message from the user was ""init"" this is an indication that this is an initialization phase, which means you should look to all message stream content for potential field completions.
An example of this would be if the assistant's last message said ""[Next focus field: divisionOfficeName]"" and the user then said ""Office of Awesomeness"", you would return the id for the divisionOfficeName field, because the user's message appears to be providing an answer to that field.

Return ONLY a JSON array of field IDs (strings) that can be answered by the user's input.
If no fields can be answered, return an empty array: []
Example response: [""{fieldsInfoObject[0].id}"", ""{fieldsInfoObject[1].id}""]

-----INPUTS-----

MESSAGE STREAM:
{string.Join("\n", safeDialog)}

FORM FIELDS:
";

        // Accumulate identified field IDs across passes
        var accumulatedFieldIds = new List<string>();

        for (int pass = 0; pass < passes; pass++)
        {
            var fieldsInfo=BuildFieldsInfo(fields, accumulatedFieldIds);
            int maxAttempts = 5;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var promptWithFields = prompt + fieldsInfo;
                    var messages = new List<ChatMessage>
                    {
                        new ChatMessage(ChatRole.System, promptWithFields)
                    };

                    var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
                    var responseText = response?.Text ?? "{}";
                    // Clean up markdown code blocks if present
                    if (responseText.StartsWith("```json"))
                        responseText = responseText.Substring(7);
                    if (responseText.StartsWith("```"))
                        responseText = responseText.Substring(3);
                    if (responseText.EndsWith("```"))
                        responseText = responseText.Substring(0, responseText.Length - 3);
                    responseText = responseText.Trim();

                    var fieldIds = JsonSerializer.Deserialize<List<string>>(responseText);

                    if (pass==1 )
                    {
                        // If this is the second pass and we still get no fields, it's likely that the model is struggling to identify fields based on the dialog. In this case, we can consider returning all remaining fields as a fallback, or we can return an empty list to indicate that no fields could be identified. For now, let's log this scenario and return an empty list.
                        Console.WriteLine($"**Second pass completed fields identified: {fieldIds.Count}.");
                    }
                    if (fieldIds != null)
                    {
                        foreach (var id in fieldIds)
                            accumulatedFieldIds.Add(id);

                        // Accept result (even empty) and move to next pass
                        break;
                    }

                    if (attempt < maxAttempts - 1)
                        await Task.Delay(200);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"Error parsing response in IdentifyAnswerableFieldsAsync (pass {pass + 1}, attempt {attempt + 1}): {ex.Message}");
                    if (attempt < maxAttempts - 1)
                        await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in IdentifyAnswerableFieldsAsync (pass {pass + 1}, attempt {attempt + 1}): {ex.Message}");
                    if (attempt < maxAttempts - 1)
                        await Task.Delay(200);
                }
            }
        }

        return fields.Where(f => !string.IsNullOrEmpty(f.Id) && accumulatedFieldIds.Contains(f.Id)).ToList();
    }

    private static string BuildFieldsInfo(List<FormField> fields, List<string> excludeFields)
    {
        var fieldsInfoObject = fields
            .Where(field => !string.IsNullOrEmpty(field.Id)
                && (field.Type?.Contains("Input") == true)
                && !excludeFields.Contains(field.Id!))
            .Select(field => new { id = field.Id, label = field.Label })
            .ToList();

        return JsonSerializer.Serialize(fieldsInfoObject, new JsonSerializerOptions { WriteIndented = false });
    }
}
