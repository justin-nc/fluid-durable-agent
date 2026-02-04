using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly string[] EventNames = ["message", "form_action", "token_update", "invalid_input"]; // Add more event names here as needed
    private const string FormsContainer = "forms";
    
    private readonly BlobStorageService _blobStorageService;
    private readonly Agent_FieldCompletion _fieldCompletionAgent;
    private readonly Agent_FieldValidation _fieldValidationAgent;
    private readonly Agent_Conversation _conversationAgent;
    private readonly Agent_MessageEvaluate _messageEvaluateAgent;
    private readonly Agent_ConversationRedirect _conversationRedirectAgent;
    private readonly ILogger<SessionOrchestrator> _logger;

    public SessionOrchestrator(BlobStorageService blobStorageService, Agent_FieldCompletion fieldCompletionAgent, Agent_FieldValidation fieldValidationAgent, Agent_Conversation conversationAgent, Agent_MessageEvaluate messageEvaluateAgent, Agent_ConversationRedirect conversationRedirectAgent, ILogger<SessionOrchestrator> logger)
    {
        _blobStorageService = blobStorageService;
        _fieldCompletionAgent = fieldCompletionAgent;
        _fieldValidationAgent = fieldValidationAgent;
        _conversationAgent = conversationAgent;
        _messageEvaluateAgent = messageEvaluateAgent;
        _conversationRedirectAgent = conversationRedirectAgent;
        _logger = logger;
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
                        // Add all messages to history
                        if (messageData.Messages != null && messageData.Messages.Count > 0)
                        {
                            foreach (var msg in messageData.Messages)
                            {
                                state.History.Add(msg);
                            }
                            state.NewMessage = messageData.Messages[messageData.Messages.Count - 1];
                        }
                        
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
                    
                    if (formActionData != null)
                    {
                        // Add messages to history if provided
                        if (formActionData.Messages != null && formActionData.Messages.Count > 0)
                        {
                            foreach (var msg in formActionData.Messages)
                            {
                                state.History.Add(msg);
                            }
                        }
                        
                        // Update field values if provided
                        if (formActionData.NewFieldValues != null && formActionData.NewFieldValues.Count > 0)
                        {
                            UpdateFieldValues(state, formActionData.NewFieldValues, "(Updated via form_action)");
                        }
                    }

                    logger.LogInformation("Updated {Count} field values via form_action", formActionData?.NewFieldValues?.Count ?? 0);
                    break;
                case "token_update":
                    // Deserialize the token update data
                    var tokenUpdateData = JsonSerializer.Deserialize<TokenUpdateEventData>(eventData, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (tokenUpdateData != null)
                    {
                        state.ClientAccessToken = tokenUpdateData.Token;
                        state.TokenExpiration = tokenUpdateData.Expiration;
                        logger.LogInformation("Updated client access token, expires at {Expiration}", tokenUpdateData.Expiration);
                    }
                    break;
                case "invalid_input":
                    messageData = JsonSerializer.Deserialize<MessageEventData>(eventData, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    state.NewMessage = messageData.Messages[messageData.Messages.Count - 1];                    
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
        try { 
            // Read the request body
            string body;
            using (var reader = new StreamReader(req.Body))
            {
                body = await reader.ReadToEndAsync();
            }
            
            // Check if body is empty
            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("SessionOrchestrator_Start: Request body is empty");
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync(JsonSerializer.Serialize(new { error = "Request body is empty. Expected JSON with formCode and version." }));
                return badRequest;
            }
            
            SessionStartRequest? startRequest = null;
            try
            {
                startRequest = JsonSerializer.Deserialize<SessionStartRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "SessionOrchestrator_Start: Failed to deserialize JSON. Body: {Body}", body);
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                var errorMsg = new { error = "Invalid JSON format", message = jsonEx.Message, receivedBody = body };
                await badRequest.WriteStringAsync(JsonSerializer.Serialize(errorMsg));
                return badRequest;
            }
            
            if (startRequest == null)
            {
                _logger.LogWarning("SessionOrchestrator_Start: Deserialized request is null. Body: {Body}", body);
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid request body. Could not deserialize JSON." }));
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
            var responseObj = new { instanceId = instanceId, formCode = startRequest.FormCode, version = startRequest.Version };
            await response.WriteStringAsync(JsonSerializer.Serialize(responseObj));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionOrchestrator_Start: Unhandled exception during session initialization");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            var errorObj = new { error = "Durable Task Client initialization error", message = ex.Message, type = ex.GetType().Name, innerMessage = ex.InnerException?.Message };
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(errorObj));
            return errorResponse;
        }

    }

    [Function("SessionOrchestrator_Send")]
    public async Task<HttpResponseData> Send(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "session/{instanceId}/message")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        try
        {
            using var reader = new StreamReader(req.Body);
            string body = await reader.ReadToEndAsync();
            
            // Get current state before processing
            SessionState? currentState = await GetCurrentStateAsync(client, instanceId);
            if (currentState.FormCode == null) {
                _logger.LogWarning("SessionOrchestrator_Send: No form specified for instance {InstanceId}", instanceId);
                var errorResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "No form specified for this request." }));
                return errorResponse;                
            }        


            var (form, error, fieldIds) = await LoadFormAsync(req, currentState);
            if (error != null || form == null)
            {
                _logger.LogWarning("SessionOrchestrator_Send: Form not found for instance {InstanceId}. FormCode: {FormCode}", instanceId, currentState.FormCode);
                var errorResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Form Instance not found or no status available" }));
                return errorResponse;
            }

            // Evaluate the message to understand user intent
            // Extract form context from CodeBlock with id="context" if available
            var formContext = string.Empty;
            var contextBlock = form.Body?.FirstOrDefault(b => b.Type == "CodeBlock" && b.Id == "context");
            if (contextBlock != null && !string.IsNullOrEmpty(contextBlock.CodeSnippet))
            {
                formContext = contextBlock.CodeSnippet;
            }
            else
            {
                // Fallback to serializing the entire form if no context block found
                formContext = JsonSerializer.Serialize(form, new JsonSerializerOptions { WriteIndented = false });
            }
            
            var priorMessages = currentState.History ?? new List<string>();
            priorMessages.Add(body);
            
            //Look at the message to evaluate its content
            var evaluationStopwatch = Stopwatch.StartNew();
            var messageEvaluation = await _messageEvaluateAgent.EvaluateMessageAsync(priorMessages, formContext);
            evaluationStopwatch.Stop();
            _logger.LogInformation("EvaluateMessageAsync completed in {Seconds:F2} seconds. Question: {Question}, Request: {Request}, Distraction: {Distraction}, Values: {Values}",
                evaluationStopwatch.Elapsed.TotalSeconds,
                messageEvaluation.ContainsQuestion,
                messageEvaluation.ContainsRequest,
                messageEvaluation.ContainsDistraction,
                messageEvaluation.ContainsValues);

            // Handle distraction - User appears to be focused on something other than the form
            if (messageEvaluation.ContainsDistraction)
            {
                _logger.LogWarning("Distraction detected in message for instance {InstanceId}", instanceId);
                
                // Log the invalid input event
                var invalidInputEvent = new { message = body, reason = "distraction" };
                await client.RaiseEventAsync(instanceId, "invalid_input", JsonSerializer.Serialize(invalidInputEvent));
                
                // Generate redirect response using Agent_ConversationRedirect
                var redirectStopwatch = Stopwatch.StartNew();
                var redirectResponse = await _conversationRedirectAgent.GenerateRedirectResponseAsync(
                    form,
                    currentState.CompletedFieldValues,
                    focusFieldId: null
                );
                redirectStopwatch.Stop();
                _logger.LogInformation("GenerateRedirectResponseAsync completed in {Seconds:F2} seconds", redirectStopwatch.Elapsed.TotalSeconds);
                
                // Return redirect response
                HttpResponseData distractionResponse = req.CreateResponse(HttpStatusCode.OK);
                distractionResponse.Headers.Add("Content-Type", "application/json");
                var distractionResponseDict = new Dictionary<string, object>
                {
                    ["status"] = "distraction_detected"
                };
                
                if (!string.IsNullOrEmpty(redirectResponse.FinalThoughts))
                {
                    distractionResponseDict["finalThoughts"] = redirectResponse.FinalThoughts;
                }
                if (!string.IsNullOrEmpty(redirectResponse.FieldFocus))
                {
                    distractionResponseDict["fieldFocus"] = redirectResponse.FieldFocus;
                }
                if (redirectResponse.ResponseOptions != null && redirectResponse.ResponseOptions.Count > 0)
                {
                    distractionResponseDict["responseOptions"] = redirectResponse.ResponseOptions;
                }
                
                await distractionResponse.WriteStringAsync(JsonSerializer.Serialize(distractionResponseDict));
                return distractionResponse;
            }

            // Process based on message evaluation
            var newFieldValues = new List<FormFieldValue>();
            var validationResult = new ValidationResult();

            // If message contains values, extract and validate field values
            if (messageEvaluation.ContainsValues && body.Trim().Length > 0)
            {
                var extractStopwatch = Stopwatch.StartNew();
                newFieldValues = await _fieldCompletionAgent.ExtractFieldValuesAsync(
                    body, 
                    form, 
                    currentState.CompletedFieldValues);
                extractStopwatch.Stop();
                _logger.LogInformation("ExtractFieldValuesAsync completed in {Seconds:F2} seconds with {Count} fields extracted",
                    extractStopwatch.Elapsed.TotalSeconds,
                    newFieldValues?.Count ?? 0);

                // Update current state with new values
                if (newFieldValues != null && newFieldValues.Count > 0)
                {
                    UpdateFieldValues(currentState, newFieldValues);
                }
                
                // Validate new field values
                if (newFieldValues != null && newFieldValues.Count > 0)
                {
                    var validationStopwatch = Stopwatch.StartNew();
                    validationResult = await _fieldValidationAgent.ValidateFieldValuesAsync(
                        body,
                        form,
                        currentState.CompletedFieldValues,
                        newFieldValues);
                    validationStopwatch.Stop();
                    _logger.LogInformation("ValidateFieldValuesAsync completed in {Seconds:F2} seconds", validationStopwatch.Elapsed.TotalSeconds);
                }
            }
            // If contains question or request, skip field completion (go straight to conversation)

            // Generate conversational response
            var conversationStopwatch = Stopwatch.StartNew();
            var conversationResponse = await _conversationAgent.GenerateResponseAsync(
                currentState.History ?? new List<string>(),
                form,
                currentState.CompletedFieldValues,
                newFieldValues,
                validationResult,
                focusFieldId: null // TODO: Add focusFieldId parameter to the Send function if needed
                
            );
            conversationStopwatch.Stop();
            _logger.LogInformation("GenerateResponseAsync completed in {Seconds:F2} seconds", conversationStopwatch.Elapsed.TotalSeconds);

            // If conversation agent drafted a field, add it to newFieldValues
            if (conversationResponse.DraftedField != null && !string.IsNullOrEmpty(conversationResponse.DraftedField.FieldName))
            {
                var draftedFieldValue = new FormFieldValue
                {
                    FieldName = conversationResponse.DraftedField.FieldName,
                    Value = JsonSerializer.SerializeToElement(conversationResponse.DraftedField.Value),
                    Drafted = true,
                    Note = "AI-drafted content"
                };
                //Add the drafted value to the new field values list
                newFieldValues.Add(draftedFieldValue);
                
                // Update the current state with the drafted field
                UpdateFieldValues(currentState, new List<FormFieldValue> { draftedFieldValue });
            }


            string fieldFocusInfo = conversationResponse.FieldFocus != null ? $" [Next focus field: {conversationResponse.FieldFocus}]" : "";
            var messageList = new List<string>
            {
                $"user: {body}",
                $"assistant: {conversationResponse.FinalThoughts}{fieldFocusInfo}"
            };

            // Add form_input message if any fields were updated
            if (newFieldValues != null && newFieldValues.Count > 0)
            {
                var fieldNames = newFieldValues.Select(f => f.FieldName).ToArray();
                messageList.Add($"form_input: [{string.Join(", ", fieldNames)}]");
            }

            // Create message event data that includes both message and field completions
            var messageEventData = new MessageEventData
            {
                Messages = messageList,
                FieldCompletions = currentState.CompletedFieldValues
            };
            
            // Raise the message event to the orchestrator with all data
            await client.RaiseEventAsync(instanceId, EventNames[0], JsonSerializer.Serialize(messageEventData));
            
            HttpResponseData response = req.CreateResponse(HttpStatusCode.Accepted);
            response.Headers.Add("Content-Type", "application/json");
            
            // Build response object with conditional validation fields
            var responseDict = new Dictionary<string, object>
            {
                ["status"] = newFieldValues.Count > 0 ? "fields_updated" : "ok"
            };
            
            responseDict["newFieldValues"] = newFieldValues;       
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
            
            // Add conversation response components
            if (!string.IsNullOrEmpty(conversationResponse.QuestionResponse))
            {
                responseDict["questionResponse"] = conversationResponse.QuestionResponse;
            }
            //Prevents acknowledgment if a draft was made
            if (!string.IsNullOrEmpty(conversationResponse.AcknowledgeInputs))
            {
                responseDict["acknowledgeInputs"] = conversationResponse.AcknowledgeInputs;
            }
            if (!string.IsNullOrEmpty(conversationResponse.ValidationConcerns))
            {
                responseDict["validationConcerns"] = conversationResponse.ValidationConcerns;
            }
            if (!string.IsNullOrEmpty(conversationResponse.FinalThoughts))
            {
                responseDict["finalThoughts"] = conversationResponse.FinalThoughts;
            }
            if (!string.IsNullOrEmpty(conversationResponse.FieldFocus))
            {
                responseDict["fieldFocus"] = conversationResponse.FieldFocus;
            }
            if (conversationResponse.ResponseOptions != null && conversationResponse.ResponseOptions.Count > 0)
            {
                responseDict["responseOptions"] = conversationResponse.ResponseOptions;
            }
            

            await response.WriteStringAsync(JsonSerializer.Serialize(responseDict));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionOrchestrator_Send: Unhandled exception for instance {InstanceId}", instanceId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            var errorObj = new { error = "Failed to process message", message = ex.Message, type = ex.GetType().Name, instanceId = instanceId, innerMessage = ex.InnerException?.Message };
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(errorObj));
            return errorResponse;
        }
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
        var (form, error, fieldIds) = await LoadFormAsync(req, currentState);
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
        
        // Create message list with form_input notification
        var fieldNames = fieldUpdateRequest.NewFieldValues.Select(f => f.FieldName).ToArray();
        var messageList = new List<string>
        {
            $"form_input: [{string.Join(", ", fieldNames)}]"
        };
        
        // Create form action event data with field updates and messages
        var formActionData = new FormActionEventData
        {
            NewFieldValues = fieldUpdateRequest.NewFieldValues,
            Messages = messageList
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

    [Function("SessionOrchestrator_GenerateClientToken")]
    public async Task<HttpResponseData> GenerateClientToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "session/{instanceId}/token")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        try
        {
            // Get current state to validate the instance exists
            SessionState? currentState = await GetCurrentStateAsync(client, instanceId);
            if (currentState == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                notFound.Headers.Add("Content-Type", "application/json");
                await notFound.WriteStringAsync("{\"error\": \"Instance not found\"}");
                return notFound;
            }

            // Generate a secure token
            var tokenBytes = new byte[32];
            RandomNumberGenerator.Fill(tokenBytes);
            var token = Convert.ToBase64String(tokenBytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
            
            // Set expiration to 24 hours from now
            var expiration = DateTime.UtcNow.AddHours(24);
            
            // Update the state with the token
            currentState.ClientAccessToken = token;
            currentState.TokenExpiration = expiration;
            
            // Raise an event to update the orchestrator state with the new token
            var updateTokenData = new TokenUpdateEventData
            {
                Token = token,
                Expiration = expiration
            };
            await client.RaiseEventAsync(instanceId, "token_update", JsonSerializer.Serialize(updateTokenData));
            
            // Create the encoded access string
            var accessString = $"{instanceId}:{token}";
            var accessBytes = Encoding.UTF8.GetBytes(accessString);
            var encodedAccess = Convert.ToBase64String(accessBytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
            
            HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            
            var responseObj = new
            {
                accessToken = encodedAccess,
                expiresAt = expiration.ToString("o"),
                updateUrl = $"/api/fieldUpdate/{encodedAccess}"
            };
            
            await response.WriteStringAsync(JsonSerializer.Serialize(responseObj));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionOrchestrator_GenerateClientToken: Error generating token for instance {InstanceId}", instanceId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to generate token", message = ex.Message }));
            return errorResponse;
        }
    }

    [Function("SessionOrchestrator_ClientFieldUpdate_Options")]
    public HttpResponseData ClientFieldUpdateOptions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "fieldUpdate/{encodedAccess}")] HttpRequestData req,
        string encodedAccess)
    {
        // Handle CORS preflight request
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
        response.Headers.Add("Access-Control-Max-Age", "86400"); // 24 hours
        return response;
    }

    [Function("SessionOrchestrator_ClientFieldUpdate")]
    public async Task<HttpResponseData> ClientFieldUpdate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "fieldUpdate/{encodedAccess}")] HttpRequestData req,
        string encodedAccess,
        [DurableClient] DurableTaskClient client)
    {
        try
        {
            // Decode the access string
            var normalizedAccess = encodedAccess.Replace("-", "+").Replace("_", "/");
            // Add padding if necessary
            var padding = (4 - (normalizedAccess.Length % 4)) % 4;
            normalizedAccess += new string('=', padding);
            
            string decodedAccess;
            try
            {
                var accessBytes = Convert.FromBase64String(normalizedAccess);
                decodedAccess = Encoding.UTF8.GetString(accessBytes);
            }
            catch (FormatException)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                badRequest.Headers.Add("Content-Type", "application/json");
                badRequest.Headers.Add("Access-Control-Allow-Origin", "*");
                await badRequest.WriteStringAsync("{\"error\": \"Invalid access token format\"}");
                return badRequest;
            }
            
            // Parse instanceId and token
            var parts = decodedAccess.Split(':');
            if (parts.Length != 2)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                badRequest.Headers.Add("Content-Type", "application/json");
                badRequest.Headers.Add("Access-Control-Allow-Origin", "*");
                await badRequest.WriteStringAsync("{\"error\": \"Invalid access token structure\"}");
                return badRequest;
            }
            
            var instanceId = parts[0];
            var providedToken = parts[1];
            
            // Get current state and validate token
            SessionState? currentState = await GetCurrentStateAsync(client, instanceId);
            if (currentState == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                notFound.Headers.Add("Content-Type", "application/json");
                notFound.Headers.Add("Access-Control-Allow-Origin", "*");
                await notFound.WriteStringAsync("{\"error\": \"Session not found\"}");
                return notFound;
            }
            
            // Validate token
            if (string.IsNullOrEmpty(currentState.ClientAccessToken) || 
                currentState.ClientAccessToken != providedToken)
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                unauthorized.Headers.Add("Content-Type", "application/json");
                unauthorized.Headers.Add("Access-Control-Allow-Origin", "*");
                await unauthorized.WriteStringAsync("{\"error\": \"Invalid or expired access token\"}");
                return unauthorized;
            }
            
            // Check token expiration
            if (currentState.TokenExpiration.HasValue && currentState.TokenExpiration.Value < DateTime.UtcNow)
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                unauthorized.Headers.Add("Content-Type", "application/json");
                unauthorized.Headers.Add("Access-Control-Allow-Origin", "*");
                await unauthorized.WriteStringAsync("{\"error\": \"Access token has expired\"}");
                return unauthorized;
            }
            
            // Read and parse the request body
            using var reader = new StreamReader(req.Body);
            string body = await reader.ReadToEndAsync();
            
            var fieldUpdateRequest = JsonSerializer.Deserialize<FieldUpdateRequest>(body, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (fieldUpdateRequest?.NewFieldValues == null || fieldUpdateRequest.NewFieldValues.Count == 0)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                badRequest.Headers.Add("Content-Type", "application/json");
                badRequest.Headers.Add("Access-Control-Allow-Origin", "*");
                await badRequest.WriteStringAsync("{\"error\": \"NewFieldValues array is required and cannot be empty\"}");
                return badRequest;
            }
            
            // Load the form to validate field IDs
            var (form, error, fieldIds) = await LoadFormAsync(req, currentState);
            if (error != null || form == null)
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.NotFound);
                errorResponse.Headers.Add("Content-Type", "application/json");
                errorResponse.Headers.Add("Access-Control-Allow-Origin", "*");
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
                badRequest.Headers.Add("Access-Control-Allow-Origin", "*");
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
                string.Empty,
                form,
                currentState.CompletedFieldValues,
                fieldUpdateRequest.NewFieldValues);
            
            // Create message list with form_input notification
            var fieldNames = fieldUpdateRequest.NewFieldValues.Select(f => f.FieldName).ToArray();
            var messageList = new List<string>
            {
                $"form_input: [{string.Join(", ", fieldNames)}] (via client)"
            };
            
            // Create form action event data with field updates and messages
            var formActionData = new FormActionEventData
            {
                NewFieldValues = fieldUpdateRequest.NewFieldValues,
                Messages = messageList
            };
            
            // Raise the form_action event to the orchestrator
            await client.RaiseEventAsync(instanceId, EventNames[1], JsonSerializer.Serialize(formActionData));
            
            HttpResponseData response = req.CreateResponse(HttpStatusCode.Accepted);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionOrchestrator_ClientFieldUpdate: Error processing field update");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Access-Control-Allow-Origin", "*");
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to process field update", message = ex.Message }));
            return errorResponse;
        }
    }


    private class MessageEventData
    {
        public List<string>? Messages { get; set; }
        public Dictionary<string, FieldValue>? FieldCompletions { get; set; }
    }

    private class FormActionEventData
    {
        public List<FormFieldValue>? NewFieldValues { get; set; }
        public List<string>? Messages { get; set; }
    }

    private class FieldUpdateRequest
    {
        public List<FormFieldValue>? NewFieldValues { get; set; }
    }

    private class TokenUpdateEventData
    {
        public string? Token { get; set; }
        public DateTime? Expiration { get; set; }
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

    private async Task<(Form? form, HttpResponseData? errorResponse, String fieldIds)> LoadFormAsync(HttpRequestData req, SessionState? currentState)
    {
        var form = new Form();
        String fieldIds="";
        
        if (currentState != null && !string.IsNullOrEmpty(currentState.FormCode))
        {
            // Load form from blob storage
            // Determine the filename based on version
            string fileName = string.IsNullOrEmpty(currentState.Version) ? "latest.json" : $"{currentState.Version}.json";
            
            try
            {
                string formJson = await _blobStorageService.ReadFileAsync(currentState.FormCode, fileName, FormsContainer);
                form = JsonSerializer.Deserialize<Form>(formJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                fieldIds = string.Join(",", form.Body.Select(f => f.Id));

            }
            catch (FileNotFoundException ex)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync($"Form not found: {currentState.FormCode}/{fileName} - {ex.Message}");
                return (null, notFound, null);
            }
            catch (Exception ex)
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error loading form: {ex.Message} | Stack: {ex.StackTrace} | Inner: {ex.InnerException?.Message}");
                return (null, errorResponse, null);
            }
        }        
        return (form, null, fieldIds);
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
