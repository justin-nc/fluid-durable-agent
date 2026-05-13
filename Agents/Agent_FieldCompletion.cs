using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using static fluid_durable_agent.Tools.ConversationPromptTemplates;

namespace fluid_durable_agent.Agents;

public class Agent_FieldCompletion
{
    private readonly Microsoft.Extensions.AI.IChatClient _chatClient;
    private readonly Agent_FieldIdentification _fieldIdentificationAgent;

    public Agent_FieldCompletion(Microsoft.Extensions.AI.IChatClient chatClient, Agent_FieldIdentification fieldIdentificationAgent)
    {
        _chatClient = chatClient;
        _fieldIdentificationAgent = fieldIdentificationAgent;
    }

    /// <summary>
    /// Analyzes a user message against form fields to extract field values
    /// </summary>
    /// <param name="priorDialog">The user's chat message</param>
    /// <param name="form">The form containing fields to check against</param>
    /// <param name="completedFields">List of fields that have already been completed</param>
    /// <returns>List of extracted form field values</returns>
    public async Task<List<FormFieldValue>> ExtractFieldValuesAsync(List<String> priorDialog, Form form, Dictionary<string, FieldValue>? completedFields = null, string? chatCommand = null, MessageEvaluationResult? messageEvaluation = null, bool secondPass = false)
    {
        bool anticipateBulkCompletion = chatCommand == "init";
        string toolResponseText = chatCommand == "tool_response"? "The last response from the user was actually a tool response, so you should not expect the user to be providing values for multiple fields at once. Instead, look for information that would help complete the specific field that the tool response was related to." : string.Empty;

        string requestContext =@"Your job is to process user input and extract field values from the user's latest response.
You only provide the field completions for values that need to be changed or new values.";
        if (messageEvaluation.ContainsRequest == true)
        {
            if (messageEvaluation.ContainsValues == false) { //if there aren't any explicit values in the message, it's still likely a request for information that can be inferred, so we want to make sure the agent is primed to look for those inferences.
                requestContext ="**The user is making a request** — fulfill their request.  They are likely asking for one or more field values to be generated or derived based on other information that already exists in the form or conversation or information that you can deduce based on the form context. Whenever possible, generate appropriate values for those fields using the available context. If the user is asking for a suggestion, provide a suggestion based on the form context.";            }
            else {
                requestContext += "**The user is making a request** — fulfill their request.  They are likely asking for one or more field values to be generated or derived based on other information that already exists in the form or conversation or information that you can deduce based on the form context. Whenever possible, generate appropriate values for those fields using the available context. ";
            }            
        }
        requestContext+=@"If a user doesn't explicitly answer a question, but the answer can be inferred, complete the value but be sure to set 'inferred' to 'true'.
When the text of a user message provides a field value this would not be considered an inference, but if the user provides a date and you infer that another date is a certain amount of time after that date, that would be an inference.  If you cannot be certain about an inference, do not include it.
When a user provides a simply one-word answer to a question, it's often the case that this is meant to be a field value, even if the user doesn't explicitly say ""the value for [field] is [value]"".  Use the context of the conversation and the form to determine when this is the case and extract accordingly.";
        if (secondPass)
        {
            requestContext="The assistant has determined that there are additional fields that should be completed. Extract the values from the assistant's repoonse.";
        }

        if (form?.Body == null || form.Body.Count == 0)
        {
            return new List<FormFieldValue>();
        }

        // Call Agent_FieldIdentification to filter fields
        var identifiedFields = await _fieldIdentificationAgent.IdentifyAnswerableFieldsAsync(form.Body, priorDialog, chatCommand ?? string.Empty);

        // If the last message carries an [inputFocus: <fieldId>] prefix, ensure that field
        // is always included in the identified list regardless of what the identification agent returned.
        var lastMessage = priorDialog.LastOrDefault() ?? string.Empty;
        var inputFocusMatch = System.Text.RegularExpressions.Regex.Match(
            lastMessage, @"\[inputFocus:\s*([^\]]+)\]");
        if (inputFocusMatch.Success)
        {
            var focusFieldId = inputFocusMatch.Groups[1].Value.Trim();
            if (!identifiedFields.Any(f => f.Id == focusFieldId))
            {
                var focusField = form.Body.FirstOrDefault(f => f.Id == focusFieldId);
                if (focusField != null)
                    identifiedFields.Add(focusField);
            }
        }


        // If no fields were identified, return empty list
        if (identifiedFields.Count == 0)
        {
            return new List<FormFieldValue>();
        }

        // Build field information as JSON using only the identified fields
        var fieldsInfoObject = identifiedFields.Select(field => new
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
        var priorDialogString=string.Join("\n", priorDialog);  
       var prompt = $@"You are the extractor for an intelligent form completion assistant which is helping a user who is interacting with form fields as well as directly chatting.  
{requestContext}

# DEFINITIONS

## FIELD_VALUE specific attributes of a single form field completion 
**Properties Include**
-'fieldId': The id of the field.
-'value': The value that has been set for the field.
-'note': (Optional) Any helpful notes about the field completion. Notes should be short. There is no need to restate the field label or id or description.  The note is already associated with the field.
-'inferred': (Optional, boolean) should be set to true whenever the value wasn't explicitly provided by the user but inferred from their inputs.

#Inputs to anticipate: 

## PRIOR_DIALOG  - The conversation that should be inspected for values.  YOU ARE PROVIDED A CONVERSATION HISTORY BUT YOU MUST USE EXTREME DISCRETION WHEN DECIDING WHICH FIELDS TO COMPLETE. YOU ARE THE FIRST TO SEE THE LAST MESSAGE.  OTHER USER MESSAGES HAVE ALREADY BEEN SEEN BY YOU IN THE PAST AND LIKELY DON'T REPRESENT NEW VALUES.

## FIELDS: A JSON list of fields and their properties that needs to be captured.
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
 IMPORTANT: The fields listed below have already been identified as answerable from the user's input. You are expected to extract values for these fields.

## EXISTING_VALUES - a JSON list of FIELD_VALUES that have been completed thus far.

INSTRUCTIONS:
Return ONLY a JSON object mapping field IDs to extracted values.
Format: 
Only include fields where you found relevant information in the user message.
For choice fields, ensure the value matches one of the valid choices.
Dates should be in YYYY-MM-DD format.  If the user provides a date in a different format, convert it to YYYY-MM-DD.  If you cannot determine a valid date, do not provide a value for the field.
In order to complete a yes/no question, the user must explicitly state something that would directly indicate yes or no.  Do not infer a ""no"" based on absence of information.
Return all values (even number fields) as strings.  Do not attempt to return numeric values as numbers.
If no fields can be populated, return an empty object: {{}}

#IMPORTANT:
{{toolResponseText}}
All dates must be in YYYY-MM-DD format.  If the user provides a date in a different format, convert it to YYYY-MM-DD.  If you cannot determine a valid date, do not provide a value for the field.
Inferences must be well grounded.  
**ASSUMPTIONS ARE NOT INFERENCES** If a user sets a date, it would not be appropriate to assume another date value unless the user said that the other date was a certain amount of time after the first date, or that the two dates were the same, etc.  If you cannot be certain about an inference, do not include it.



#Expected Output (JSON):
An array of FIELD_VALUES that are completed based on the latest user input. SHOULD ONLY REPRESENT A UPDATES TO EXISTING VALUES (values that haven't changed should be ignored. **new_field_values MUST BE FOR ONE OF THE DEFINED FIELDS**  If information is provided for a field that does not exist, it should be ignored. Field Completions should only be provided for fields whose value exists.  Never provide null or N/A for a field completion where the user never provided relative input.
{{ ""fieldId"": ""<id of field>"", ""value"": ""<field value>"", ""note"": ""<field note>"", ""inferred"": true/false}}


**USE CARE.  SOME OF THE EXAMPLES BELOW MAY MENTION FIELDS THAT ARE NOT DEFINED IN THE FIELDS LIST.  DO NOT VIEW THESE AS VALID FIELDS TO PROVIDE.**
# Output (JSON) Examples
{outputExamples}

**** INPUTS  ****

#FIELDS
{fieldsInfo}

#PRIOR_DIALOG
{priorDialogString}

#important context
{TodayDateContext}


#EXISTING_VALUES  (prior to this call):";

  

        // Try up to 5 times to extract field values with 200ms delay between attempts
        List<FormFieldValue> extractedValues = new List<FormFieldValue>();
        int maxAttempts = chatCommand == "init" ? 3 : 2; // Allow more attempts for init since it's more likely to be a bulk completion and we want to give the model more chances to extract all values
        int identifiedFieldsCount = identifiedFields.Count;
        Console.WriteLine($"[Agent_FieldCompletion] Identified fields ({identifiedFieldsCount}): [{string.Join(", ", identifiedFields.Select(f => f.Id))}]");
        
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var content = "";
            try
            {
                // Build completed fields information as JSON if present
                Console.WriteLine($"[Agent_FieldCompletion] Attempt {attempt + 1} to extract field values.");
                var completedFieldsInfo = BuildCompletedFieldsJson(completedFields);
                var executePrompt=prompt +completedFieldsInfo;
                var messages = new List<Microsoft.Extensions.AI.ChatMessage>
                {
                    new(Microsoft.Extensions.AI.ChatRole.User, executePrompt)
                };
                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
                content = response?.Text ?? "{}";       
                var responseWrapper = JsonSerializer.Deserialize<List<FormFieldValue>>(content, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                // If we got results, aggregate them
                if (responseWrapper != null && responseWrapper.Count > 0)                
                {
                    // Always aggregate new values into extractedValues
                    foreach (var newValue in responseWrapper)
                    {
                        // Only add if not already present (based on fieldId)
                        if (!extractedValues.Any(ev => ev.FieldId == newValue.FieldId))
                        {
                            // Skip if completedFields already has the same value for this field
                            if (completedFields != null && !string.IsNullOrEmpty(newValue.FieldId) &&
                                completedFields.TryGetValue(newValue.FieldId, out var existing) &&
                                string.Equals(existing.Value?.ToString() ?? string.Empty, newValue.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            extractedValues.Add(newValue);
                            
                            // Add to completedFields as well
                            if (completedFields != null && !string.IsNullOrEmpty(newValue.FieldId))
                            {
                                completedFields[newValue.FieldId] = new FieldValue
                                {
                                    Value = newValue.Value,
                                    Note = newValue.Note
                                };
                            }
                        }
                    }
                    
                    // Check if we've extracted all identified fields
                    if (extractedValues.Count < identifiedFieldsCount && attempt < maxAttempts - 1)
                    {
                        // Still missing some fields, retry
                        await Task.Delay(200);
                        // Continue to next attempt
                    }
                    else
                    {
                        // All fields extracted or max attempts reached, return aggregated values
                        attempt = maxAttempts; // Exit loop
                        return extractedValues;
                    }
                }
                
                // If no results and this is the last attempt, return what we have
                if (attempt == maxAttempts - 1)
                {                   
                    if (extractedValues.Count == 0) {
                        Console.WriteLine("*****FIELD EXTRACTION FAILED *****");
                        Console.WriteLine($"Prompt: {executePrompt}");
                    }
                    return extractedValues;
                }
            }
            catch (JsonException)
            {
                // If parsing fails, try to extract JSON from the response
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    try
                    {
                        var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                        var responseWrapper = JsonSerializer.Deserialize<FieldCompletionResponse>(jsonContent,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        
                        if (responseWrapper?.NewFieldValues != null && responseWrapper.NewFieldValues.Count > 0)
                        {
                            // Aggregate these values as well
                            foreach (var newValue in responseWrapper.NewFieldValues)
                            {
                                if (!extractedValues.Any(ev => ev.FieldId == newValue.FieldId))
                                {
                                    // Skip if completedFields already has the same value for this field
                                    if (completedFields != null && !string.IsNullOrEmpty(newValue.FieldId) &&
                                        completedFields.TryGetValue(newValue.FieldId, out var existing) &&
                                        string.Equals(existing.Value?.ToString() ?? string.Empty, newValue.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    extractedValues.Add(newValue);
                                    
                                    if (completedFields != null && !string.IsNullOrEmpty(newValue.FieldId))
                                    {
                                        completedFields[newValue.FieldId] = new FieldValue
                                        {
                                            Value = newValue.Value,
                                            Note = newValue.Note
                                        };
                                    }
                                }
                            }
                            
                            // Check if we've extracted all identified fields
                            if (extractedValues.Count < identifiedFieldsCount && attempt < maxAttempts - 1)
                            {
                                await Task.Delay(200);
                                // Continue to next attempt
                            }
                            else
                            {
                                return extractedValues;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Continue to retry if not last attempt
                    }
                }
                
                // If this is the last attempt, return what we have so far
                if (attempt == maxAttempts - 1)
                {
                    return extractedValues;
                }
            }

            // Delay before retry (except on last iteration)
            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(200);
            }
        }

        return new List<FormFieldValue>();
    }

    private class FieldCompletionResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("new_field_values")]
        public List<FormFieldValue>? NewFieldValues { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("inputError")]
        public string? InputError { get; set; }
    }

    
    /// <summary>
    /// Analyzes a user message and returns validated field values
    /// </summary>
    /// <param name="userMessage">The user's chat message</param>
    /// <param name="form">The form to analyze against</param>
    /// <param name="completedFields">List of fields that have already been completed</param>
    /// <returns>Dictionary of validated field IDs and values</returns>
    public async Task<List<FormFieldValue>> AnalyzeMessageAsync(List<string> priorDialog, Form form, Dictionary<string, FieldValue>? completedFields = null)
    {
        var extractedValues = await ExtractFieldValuesAsync(priorDialog, form, completedFields);
        return extractedValues;
    }

    public static string outputExamples=@"
[
  {
    ""fieldId"": ""it_stateNetworkInfrastructure"",
    ""value"": ""Yes"",
    ""note"": ""User confirmed that the product connects to the state network or infrastructure.""
  }
]


[
  {
    ""fieldId"": ""titleOfITNeed"",
    ""value"": ""Automation Platform"",
    ""note"": ""User input indicates this effort is related to automation."",
    ""inferred"": true
  }
]


[
  {
    ""fieldId"": ""projectDeadline"",
    ""value"": ""2032-10-05"",
    ""note"": ""User indicated that the project needed to be completed by the 5th of October, 7 years from now."",
    ""inferred"": true
  },
 {
    ""fieldId"": ""requester_name"",
    ""value"": ""Joe Smith"",
    ""note"": ""User data provides this information."",
    ""inferred"": true
  }
]


[
  {
    ""fieldId"": ""requester_name"",
    ""value"": ""Joe Smith"",
    ""note"": ""User data provides this information."",
    ""inferred"": true
  }
]


[]
";

    /// <summary>
    /// Builds a JSON string representation of completed fields
    /// </summary>
    /// <param name="completedFields">Dictionary of completed field values</param>
    /// <returns>JSON string of completed fields</returns>
    private static string BuildCompletedFieldsJson(Dictionary<string, FieldValue>? completedFields)
    {
        if (completedFields == null || completedFields.Count == 0)
        {
            return "[]";
        }

        var completedFieldsObject = completedFields.Select(kvp => new
        {
            fieldId = kvp.Key,
            value = kvp.Value.Value?.ToString() ?? "",
            note = kvp.Value.Note
        }).ToList();
        
        return JsonSerializer.Serialize(completedFieldsObject, new JsonSerializerOptions { WriteIndented = true });
    }
}
