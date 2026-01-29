using System.IO;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using fluid_durable_agent.Models;
using fluid_durable_agent.Services;
using fluid_durable_agent.Agents;

namespace fluid_durable_agent.Functions;

public class SessionOrchestrator
{
    private const string OrchestratorName = "SessionOrchestrator";
    private static readonly string[] EventNames = ["message", "form_action"]; // Add more event names here as needed
    private const string FormsContainer = "forms";
    
    private readonly BlobStorageService _blobStorageService;
    private readonly Agent_FieldCompletion _fieldCompletionAgent;
    private readonly Agent_FieldValidation _fieldValidationAgent;

    public SessionOrchestrator(BlobStorageService blobStorageService, Agent_FieldCompletion fieldCompletionAgent, Agent_FieldValidation fieldValidationAgent)
    {
        _blobStorageService = blobStorageService;
        _fieldCompletionAgent = fieldCompletionAgent;
        _fieldValidationAgent = fieldValidationAgent;
    }

    [Function(OrchestratorName)]
    public async Task<SessionState> RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(SessionOrchestrator));
        var state = context.GetInput<SessionState>() ?? new SessionState();
        state.History ??= new List<string>();

        while (true)
        {            
            // Create tasks for all event names
            var eventTasks = EventNames.Select(eventName => 
                context.WaitForExternalEvent<string>(eventName)
            ).ToArray();
            
            // Wait for any event to complete
            var completedTaskIndex = Array.IndexOf(eventTasks, await Task.WhenAny(eventTasks));
            
            // Get the event type and data
            string eventType = EventNames[completedTaskIndex];
            string eventData = await eventTasks[completedTaskIndex];
            
            // Handle different event types
            switch (eventType)
            {
                case "message":
                    // Deserialize the message data which now includes field completions
                    var messageData = JsonSerializer.Deserialize<MessageEventData>(eventData, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (messageData != null)
                    {
                        state.NewMessage = messageData.Message ?? string.Empty;
                        state.History.Add(messageData.Message ?? string.Empty);
                        
                        // Update field completions if provided
                        if (messageData.FieldCompletions != null && messageData.FieldCompletions.Count > 0)
                        {
                            state.CompletedFieldValues = messageData.FieldCompletions;                          
                        }
                    }
                    break;
                case "form_action":
                    // Deserialize the form action data which contains field updates
                    var formActionData = JsonSerializer.Deserialize<FormActionEventData>(eventData, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (formActionData?.NewFieldValues != null && formActionData.NewFieldValues.Count > 0)
                    {
                        UpdateFieldValues(state, formActionData.NewFieldValues, "(Updated via form_action)");
                    }
                    logger.LogInformation("Updated {Count} field values via form_action", formActionData?.NewFieldValues?.Count ?? 0);
                    break;
                default:
                    logger.LogWarning("Received unknown event type: {EventType}", eventType);
                    break;
            }
            
            context.SetCustomStatus(state);
            
            // Only use ContinueAsNew to trim history when it gets too large
            if (state.History.Count >= 100)
            {
                // Keep only the last 50 messages
                state.History = state.History.Skip(state.History.Count - 50).ToList();
                context.ContinueAsNew(state);
                return state; // Return the state when restarting
            }
        }
    }

    [Function("SessionOrchestrator_Start")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "session")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        using var reader = new StreamReader(req.Body);
        string body = await reader.ReadToEndAsync();
        SessionStartRequest? startRequest = JsonSerializer.Deserialize<SessionStartRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (startRequest == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body");
            return badRequest;
        }

        

        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            OrchestratorName,
            new SessionState 
            { 
                FormCode = startRequest.FormCode, 
                Version = startRequest.Version, 
                History = new List<string>(),
                CompletedFieldValues = new Dictionary<string, FieldValue>()
            });
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"instanceId\":\"" + instanceId + "\",\"formCode\":\"" + startRequest.FormCode + "\",\"version\":\"" + startRequest.Version + "\"}");
        return response;
    }

    [Function("SessionOrchestrator_Send")]
    public async Task<HttpResponseData> Send(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "session/{instanceId}/message")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        using var reader = new StreamReader(req.Body);
        string body = await reader.ReadToEndAsync();
        
        // Get current state before processing
        SessionState? currentState = await GetCurrentStateAsync(client, instanceId);
        if (currentState.FormCode == null) {
            var errorResponse = req.CreateResponse(HttpStatusCode.NotFound  );
            await errorResponse.WriteStringAsync("{\"error\": \"No form specified for this request.\"}");
            return errorResponse;
        }        
        var (form, error) = await LoadFormAsync(req, currentState);
        if (error != null || form == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.NotFound  );
            await errorResponse.WriteStringAsync("{\"error\": \"Form Instance not found or no status available\"}");
            return errorResponse;
        }
        // Extract field values from the message using AI
        var newFieldValues = new List<FormFieldValue>();
        newFieldValues = await _fieldCompletionAgent.ExtractFieldValuesAsync(
                body, 
                form, 
                currentState.CompletedFieldValues);
        
        var validationResult = new ValidationResult();

        if (newFieldValues != null && newFieldValues.Count > 0)
        {
            UpdateFieldValues(currentState, newFieldValues);
            
            validationResult = await _fieldValidationAgent.ValidateFieldValuesAsync(
            body,
            form,
            currentState.CompletedFieldValues,
            newFieldValues);
        }
                       // Validate the extracted field values
       
            

        // Create message event data that includes both message and field completions
        var messageEventData = new MessageEventData
        {
            Message = body,
            FieldCompletions = currentState.CompletedFieldValues
        };
        
        // Raise the message event to the orchestrator with all data
        await client.RaiseEventAsync(instanceId, EventNames[0], JsonSerializer.Serialize(messageEventData));
        
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        
        var responseObject = new
        {
            newFieldValues = newFieldValues.Count > 0 ? newFieldValues : null,
            validation = validationResult,
            status = newFieldValues.Count > 0 ? "fields_updated" : "ok",
            message = newFieldValues.Count > 0 ? null : "No new fields acquired."
        };
        
        await response.WriteStringAsync(JsonSerializer.Serialize(responseObject));
        return response;
    }

    [Function("SessionOrchestrator_GetFields")]
    public async Task<HttpResponseData> GetFields(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "session/{instanceId}/fields")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        // Get current state
        SessionState? currentState = await GetCurrentStateAsync(client, instanceId);
        
        if (currentState == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            notFound.Headers.Add("Content-Type", "application/json");
            await notFound.WriteStringAsync("{\"error\": \"Instance not found\"}");
            return notFound;
        }
        
        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        
        var responseObject = new
        {
            instanceId = instanceId,
            formCode = currentState.FormCode,
            version = currentState.Version,
            completedFieldValues = currentState.CompletedFieldValues ?? new Dictionary<string, FieldValue>()
        };
        
        await response.WriteStringAsync(JsonSerializer.Serialize(responseObject));
        return response;
    }

    [Function("SessionOrchestrator_UpdateFields")]
    public async Task<HttpResponseData> UpdateFields(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "session/{instanceId}/fields")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        using var reader = new StreamReader(req.Body);
        string body = await reader.ReadToEndAsync();
        
        // Parse the field update request
        var fieldUpdateRequest = JsonSerializer.Deserialize<FieldUpdateRequest>(body, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (fieldUpdateRequest?.NewFieldValues == null || fieldUpdateRequest.NewFieldValues.Count == 0)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            badRequest.Headers.Add("Content-Type", "application/json");
            await badRequest.WriteStringAsync("{\"error\": \"NewFieldValues array is required and cannot be empty\"}");
            return badRequest;
        }
        
        // Get current state to validate the instance exists
        SessionState? currentState = await GetCurrentStateAsync(client, instanceId);
        if (currentState == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            notFound.Headers.Add("Content-Type", "application/json");
            await notFound.WriteStringAsync("{\"error\": \"Instance not found\"}");
            return notFound;
        }
        
        // Load the form to validate field IDs
        var (form, error) = await LoadFormAsync(req, currentState);
        if (error != null || form == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.NotFound);
            errorResponse.Headers.Add("Content-Type", "application/json");
            await errorResponse.WriteStringAsync("{\"error\": \"Form not found or could not be loaded\"}");
            return errorResponse;
        }
        
        // Validate that all fields exist in the form
        var validFieldIds = form.Body?.Select(f => f.Id).ToHashSet() ?? new HashSet<string>();
        var invalidFields = fieldUpdateRequest.NewFieldValues
            .Where(fv => !string.IsNullOrEmpty(fv.FieldName) && !validFieldIds.Contains(fv.FieldName))
            .Select(fv => fv.FieldName)
            .ToList();
        
        if (invalidFields.Count > 0)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            badRequest.Headers.Add("Content-Type", "application/json");
            var errorMsg = new
            {
                error = "Invalid field IDs provided",
                invalidFields = invalidFields
            };
            await badRequest.WriteStringAsync(JsonSerializer.Serialize(errorMsg));
            return badRequest;
        }
        
        // Validate the field values using the validation agent
        var validationResult = await _fieldValidationAgent.ValidateFieldValuesAsync(
            string.Empty, // No user message for direct field updates
            form,
            currentState.CompletedFieldValues,
            fieldUpdateRequest.NewFieldValues);
        
        // Create form action event data
        var formActionData = new FormActionEventData
        {
            NewFieldValues = fieldUpdateRequest.NewFieldValues
        };
        
        // Raise the form_action event to the orchestrator
        await client.RaiseEventAsync(instanceId, EventNames[1], JsonSerializer.Serialize(formActionData));
        
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        
        // Build response object with conditional validation fields
        var responseDict = new Dictionary<string, object>
        {
            ["status"] = "accepted",
            ["message"] = "Field updates queued for processing",
            ["updatedFields"] = fieldUpdateRequest.NewFieldValues.Count
        };
        
        // Add errors at root level if present
        if (validationResult.Errors != null && validationResult.Errors.Count > 0)
        {
            responseDict["errors"] = validationResult.Errors;
        }
        
        // Add warnings at root level if present
        if (validationResult.Warnings != null && validationResult.Warnings.Count > 0)
        {
            responseDict["warnings"] = validationResult.Warnings;
        }
        
        await response.WriteStringAsync(JsonSerializer.Serialize(responseDict));
        return response;
    }


    private class MessageEventData
    {
        public string? Message { get; set; }
        public Dictionary<string, FieldValue>? FieldCompletions { get; set; }
    }

    private class FormActionEventData
    {
        public List<FormFieldValue>? NewFieldValues { get; set; }
    }

    private class FieldUpdateRequest
    {
        public List<FormFieldValue>? NewFieldValues { get; set; }
    }

    private void UpdateFieldValues(SessionState state, List<FormFieldValue> newFieldValues, string explicitNote = "")
    {
        foreach (var newValue in newFieldValues)
        {
            if (!string.IsNullOrEmpty(newValue.FieldName))
            {
                var inferredNote = newValue.Inferred == true ? " (Inferred)" : "";
                var draftedNote = newValue.Drafted == true ? " (Drafted)" : "";
                var note = $"{newValue.Note}{explicitNote}{inferredNote}{draftedNote}";
                
                if (note.Length == 0)
                {
                    state.CompletedFieldValues[newValue.FieldName] = new FieldValue
                    {
                        Value = newValue.Value
                    };
                }
                else
                {
                    state.CompletedFieldValues[newValue.FieldName] = new FieldValue
                    {
                        Value = newValue.Value,
                        Note = note
                    };
                }
            }
        }
    }

    private async Task<(Form? form, HttpResponseData? errorResponse)> LoadFormAsync(HttpRequestData req, SessionState? currentState)
    {
        var form = new Form();
        
        if (currentState != null && !string.IsNullOrEmpty(currentState.FormCode))
        {
            // Load form from blob storage
            // Determine the filename based on version
            string fileName = string.IsNullOrEmpty(currentState.Version) ? "latest.json" : $"{currentState.Version}.json";
            
            try
            {
                string formJson = await _blobStorageService.ReadFileAsync(currentState.FormCode, fileName, FormsContainer);
                form = JsonSerializer.Deserialize<Form>(formJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (FileNotFoundException ex)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync($"Form not found: {currentState.FormCode}/{fileName} - {ex.Message}");
                return (null, notFound);
            }
            catch (Exception ex)
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error loading form: {ex.Message} | Stack: {ex.StackTrace} | Inner: {ex.InnerException?.Message}");
                return (null, errorResponse);
            }
        }
        
        return (form, null);
    }

    private async Task<SessionState?> GetCurrentStateAsync(DurableTaskClient client, string instanceId)
    {
        var instance = await client.GetInstanceAsync(instanceId, true);
        SessionState? currentState = null;
        
        // Try to get custom status first, fall back to input if not available
        try
        {
            currentState = instance?.ReadCustomStatusAs<SessionState>();
        }
        catch { }
        
        // If custom status is null, try reading from input (initial state)
        if (currentState == null && instance?.ReadInputAs<SessionState>() is SessionState inputState)
        {
            currentState = inputState;
        }
        
        return currentState;
    }

}
