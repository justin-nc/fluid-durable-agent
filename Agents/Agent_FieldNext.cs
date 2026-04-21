using System.Text.Json;
using Microsoft.Extensions.AI;
using fluid_durable_agent.Models;

namespace fluid_durable_agent.Agents;

public class Agent_FieldNext
{
    private readonly IChatClient _chatClient;

    public Agent_FieldNext(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// Determines the next required field to be completed in the form, respecting $when conditions
    /// and skipping optional fields.
    /// </summary>
    /// <param name="form">The form containing fields</param>
    /// <param name="completedFields">Field values that have already been completed</param>
    /// <returns>FieldNextResult identifying the next field to complete, or null if the form is complete</returns>
    public async Task<FieldNextResult?> DetermineNextFieldAsync(
        Form form,
        Dictionary<string, FieldValue>? completedFields = null,
        List<string>? recentMessages = null)
    {
        if (form?.Body == null || form.Body.Count == 0)
            return null;

        // Build the full field list for the prompt, including $when expressions
        // Only include fields that have not yet been completed
        var fieldsInfoObject = form.Body
            .Select((field, index) => new { field, index })
            .Where(x => x.field.IsRequired == true
                && (completedFields == null || !completedFields.ContainsKey(x.field.Id ?? ""))
                && IsWhenConditionSatisfied(x.field.When, completedFields))
            .Select(x => new
            {
                order = x.index,
                id = x.field.Id,
                label = x.field.Label,
                type = x.field.Type,
                isRequired = x.field.IsRequired,
                subLabel = x.field.SubLabel,
                when = x.field.When   // null when there is no condition
            }).ToList();

        Console.WriteLine($"[Agent_FieldNext] Fields available for next field determination: {fieldsInfoObject.Count}");

        var fieldsInfo = JsonSerializer.Serialize(fieldsInfoObject, new JsonSerializerOptions { WriteIndented = true });
        
        //writeline($"Fields info for prompt: {fieldsInfo}");     
        // Build completed fields map
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

        // Build recent conversation context
        var recentMessagesSection = "";
        if (recentMessages != null && recentMessages.Count > 0)
        {
            var messagesText = string.Join("\n", recentMessages);
            recentMessagesSection = $"""

## RECENT CONVERSATION
{messagesText}

""";
        }

        var prompt = $@"You are a form-completion assistant. Your sole job is to identify the single next required field that the user should complete in this form.

# RULES — follow these exactly

1. **Skip non-required fields** — if `isRequired` is `false` or `null`, NEVER select that field.
2. **Evaluate `$when` conditions before selecting a field.**
   - A field's `when` property contains an Adaptive Card expression, e.g. `${{fieldId == 'Yes'}}`.
   - Parse the expression: extract the referenced field ID and expected value.
   - Check whether `completedFields` contains that field ID with the matching value.
   - If the condition is NOT satisfied (the dependency field hasn't been completed yet, or its value doesn't match), treat the field as invisible — do NOT select it.
3. **Skip already-completed fields** — any field whose ID appears in `completedFields` must not be selected.
4. **Preserve form order** — scan fields using the order to determine the first eligible field.
5. **If no eligible field remains**, set `fieldId` to null to signal the form is complete.
6. **Skip requested by user** - if the user is requesting to skip a field, find the next eligible field after the currently focused field. If there are no more eligible fields after the currently focused field, return null. DO NOT RETURN THE SAME FIELD AS THE ONE LAST MENTIONED.

# OUTPUT

Return ONLY a valid JSON object with no markdown fencing:
{{{{
  ""priorFieldId"": ""<id of the field most recently completed or focused, if available>"",
  ""fieldId"": ""<id of the next field, or null if complete>"",
  ""reasoning"": ""<one short sentence explaining why this field was chosen>""
}}}}

---
#INPUTS
{recentMessagesSection}


## FORM FIELDS (in order)
{fieldsInfo}

## COMPLETED FIELDS
{completedFieldsInfo}

priorFieldId should not be the same as fieldId!

Return ONLY valid JSON matching the structure above.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, prompt)
        };

        var content = "";
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: default);
                content = response?.Text ?? "{}";

                var result = JsonSerializer.Deserialize<FieldNextResult>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (result != null)
                    return result;
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
                        var result = JsonSerializer.Deserialize<FieldNextResult>(jsonContent,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (result != null)
                            return result;
                    }
                    catch (JsonException)
                    {
                        // fall through to retry
                    }
                }
            }

            if (attempt < maxAttempts)
                continue;
        }

        return null;
    }

    /// <summary>
    /// Returns true if the field's $when condition is satisfied (or absent).
    /// Parses Adaptive Card expressions of the form ${fieldId == 'value'} or ${fieldId != 'value'}.
    /// If the referenced dependency field has no value yet, the condition is not satisfied.
    /// </summary>
    private static bool IsWhenConditionSatisfied(string? when, Dictionary<string, FieldValue>? completedFields)
    {
        if (string.IsNullOrEmpty(when))
            return true;

        // Match ${fieldId == 'value'} or ${fieldId != 'value'}
        var match = System.Text.RegularExpressions.Regex.Match(
            when,
            @"\$\{(?<field>[A-Za-z0-9_]+)\s*(?<op>==|!=)\s*'(?<value>[^']*)'\}");

        if (!match.Success)
            return true; // Unknown expression format — don't exclude

        var depFieldId = match.Groups["field"].Value;
        var op = match.Groups["op"].Value;
        var expectedValue = match.Groups["value"].Value;

        if (completedFields == null || !completedFields.TryGetValue(depFieldId, out var depFieldValue))
            return false; // Dependency field has no value yet

        var actualValue = depFieldValue.Value?.ToString() ?? "";

        return op == "==" ? actualValue == expectedValue : actualValue != expectedValue;
    }
}

public class FieldNextResult
{
    public string? FieldId { get; set; }
    public string? Reasoning { get; set; }

    public string? PriorFieldId { get; set; } // The field that was most recently completed or focused, which influenced this suggestion
}
