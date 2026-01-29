using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace fluid_durable_agent.Agents;

public class Agent_FieldCompletion
{
    private readonly Microsoft.Extensions.AI.IChatClient _chatClient;

    public Agent_FieldCompletion(Microsoft.Extensions.AI.IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Analyzes a user message against form fields to extract field values
    /// </summary>
    /// <param name="userMessage">The user's chat message</param>
    /// <param name="form">The form containing fields to check against</param>
    /// <param name="completedFields">List of fields that have already been completed</param>
    /// <returns>List of extracted form field values</returns>
    public async Task<List<FormFieldValue>> ExtractFieldValuesAsync(string userMessage, Form form, Dictionary<string, FieldValue>? completedFields = null)
    {
        if (form?.Body == null || form.Body.Count == 0)
        {
            return new List<FormFieldValue>();
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
        var completedFieldsInfo ="[]";
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

       var prompt = $@"You are the transcriber for an intelligent form completion assistant which is helping a user who is interacting with form fields as well as directly chatting.  
Your job is to process user input and determine what fields can be completed as a result of their latest response.  
You validate that the information that the user has provided is valid before completing a field.  If the input is invalid, you provide a friendly statement as such in the inputError field.
You only provide the field completions for values that need to be changed or new values.
If a user doesn't explicitly answer a question, but the answer can be inferred, complete the value but be sure to set 'inferred' to 'true'.
Occasionally, especially when the field is Multiline, the user will ask for your help drafting a response.  In these cases, use your general understanding of the topic as well as field_completions and prior_dialog to help create text.

# DEFINITIONS
## FIELD_VALUE specific attributes of a single form field completion
**Properties Include**
-'fieldName': The name of the field.
-'value': The value that has been set for the field.
-'note': (Optional) Any helpful notes about the field completion. Notes should be short. There is no need to restate the field label or id or description.  The note is already associated with the field.
-'inferred': (Optional, boolean) should be set to true whenever the value wasn't explicitly provided by the user but inferred from their inputs.
-'drafted': (Optional, boolean) should be set to true whenever the value represents text that was created on behalf of the user.
#Inputs to anticipate: 

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
- 'note': Contains important information about the field, including validation requirements.

## user_data - JSON Information about the user completing the form, also known as the requester. That should be used to automatically fill fields like requester_name, requester_email, etc.

## values - a JSON list of FIELD_VALUES that have been completed thus far.

#FIELDS
{fieldsInfo}

INSTRUCTIONS:
Return ONLY a JSON object mapping field IDs to extracted values.
Format: 
Only include fields where you found relevant information in the user message.
For choice fields, ensure the value matches one of the valid choices.
If no fields can be populated, return an empty object: {{}}

#Expected Output (JSON):
An array of FIELD_VALUES that are completed based on the latest user input.  THIS SHOULD NOT INCLUDE PRIOR VALUES; ONLY NEW OR CHANGED VALUES. **new_field_values MUST BE FOR ONE OF THE DEFINED FIELDS**  If information is provided for a field that does not exist, it should be ignored. Field Completions should only be provided for fields whose value exists.  Never provide null or N/A for a field completion where the user never provided relative input.
{{ ""fieldName"": ""<id of field>"", ""value"": ""<field value>"", ""note"": ""<field note>"", ""inferred"": true/false, ""drafted"": true/false }}


**USE CARE.  SOME OF THE EXAMPLES BELOW MAY MENTION FIELDS THAT ARE NOT DEFINED IN THE FIELDS LIST.  DO NOT VIEW THESE AS VALID FIELDS TO PROVIDE.**
# Output (JSON) Examples
{outputExamples}

**** INPUTS  ****

#prior_dialog
{userMessage}

#field_completions (prior to this call):
{completedFieldsInfo}

";

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, prompt)
        };
        var content="";
        // Parse the JSON response
        try
        {
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
            content = response?.Text ?? "{}";       
            var responseWrapper = JsonSerializer.Deserialize<List<FormFieldValue>>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return responseWrapper ?? new List<FormFieldValue>();
        }
        catch (JsonException)
        {
            // If parsing fails, try to extract JSON from the response
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var responseWrapper = JsonSerializer.Deserialize<FieldCompletionResponse>(jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return responseWrapper?.NewFieldValues ?? new List<FormFieldValue>();
            }
            return new List<FormFieldValue>();
        }
    }

    private class FieldCompletionResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("new_field_values")]
        public List<FormFieldValue>? NewFieldValues { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("inputError")]
        public string? InputError { get; set; }
    }

    /// <summary>
    /// Validates extracted field values against form field rules
    /// </summary>
    /// <param name="extractedValues">The extracted field values</param>
    /// <param name="form">The form containing validation rules</param>
    /// <returns>Dictionary of validated field values</returns>
    public Dictionary<string, string> ValidateFieldValues(Dictionary<string, string> extractedValues, Form form)
    {
        var validatedFields = new Dictionary<string, string>();

        foreach (var kvp in extractedValues)
        {
            var field = form.Body.FirstOrDefault(f => f.Id == kvp.Key);
            if (field == null)
                continue;

            var value = kvp.Value;

            // Validate choice fields
            if (field.Choices != null && field.Choices.Count > 0)
            {
                var matchingChoice = field.Choices.FirstOrDefault(c => 
                    c.Value.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                    c.Title.Equals(value, StringComparison.OrdinalIgnoreCase));
                
                if (matchingChoice != null)
                {
                    validatedFields[kvp.Key] = matchingChoice.Value;
                }
                // Skip invalid choice values
                continue;
            }

            // For text fields, just pass through
            if (field.Type == "Input.Text")
            {
                validatedFields[kvp.Key] = value;
            }
            // For date fields, validate format (basic check)
            else if (field.Type == "Input.Date")
            {
                if (DateTime.TryParse(value, out _))
                {
                    validatedFields[kvp.Key] = value;
                }
            }
            // For number fields, validate numeric
            else if (field.Type == "Input.Number")
            {
                if (decimal.TryParse(value, out _))
                {
                    validatedFields[kvp.Key] = value;
                }
            }
            else
            {
                // Default: include the value
                validatedFields[kvp.Key] = value;
            }
        }

        return validatedFields;
    }

    /// <summary>
    /// Analyzes a user message and returns validated field values
    /// </summary>
    /// <param name="userMessage">The user's chat message</param>
    /// <param name="form">The form to analyze against</param>
    /// <param name="completedFields">List of fields that have already been completed</param>
    /// <returns>Dictionary of validated field IDs and values</returns>
    public async Task<List<FormFieldValue>> AnalyzeMessageAsync(string userMessage, Form form, Dictionary<string, FieldValue>? completedFields = null)
    {
        var extractedValues = await ExtractFieldValuesAsync(userMessage, form, completedFields);
       // var validatedValues = ValidateFieldValues(extractedValues, form);
        return extractedValues;
    }

    public static string outputExamples=@"
[
  {
    ""fieldName"": ""it_stateNetworkInfrastructure"",
    ""value"": ""Yes"",
    ""note"": ""User confirmed that the product connects to the state network or infrastructure.""
  }
]


[
  {
    ""fieldName"": ""titleOfITNeed"",
    ""value"": ""Automation Platform"",
    ""note"": ""User input indicates this effort is related to automation."",
    ""drafted"": true
  }
]


[
  {
    ""fieldName"": ""projectDeadline"",
    ""value"": ""We need a virtualization platform to streamline infrastructure management, reduce hardware dependency, and improve scalability across our systems. This will also support modernization efforts by enabling more efficient resource utilization and easier deployment of applications in a secure, software-defined environment."",
    ""note"": ""Drafted description for the project deadline field based on user's request for help."",
    ""drafted"": true
  }
]


[
  {
    ""fieldName"": ""projectDeadline"",
    ""value"": ""10/5/2032"",
    ""note"": ""User indicated that the project needed to be completed by the 5th of October, 7 years from now."",
    ""inferred"": true
  },
 {
    ""fieldName"": ""requester_name"",
    ""value"": ""Joe Smith"",
    ""note"": ""User data provides this information."",
    ""inferred"": true
  }
]


[
  {
    ""fieldName"": ""requester_name"",
    ""value"": ""Joe Smith"",
    ""note"": ""User data provides this information."",
    ""inferred"": true
  }
]


[]";

}
