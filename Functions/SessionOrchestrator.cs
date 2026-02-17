using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
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
    private const string SessionMappingContainer = "session-mappings";
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    
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
        
        // Get entity IDs for this session
        var instanceId = context.InstanceId;
        var historyEntityId = new EntityInstanceId(nameof(SessionHistoryEntity), instanceId);
        var fieldsEntityId = new EntityInstanceId(nameof(FormFieldsEntity), instanceId);

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
                        // Add all messages to history entity via activity
                        if (messageData.Messages != null && messageData.Messages.Count > 0)
                        {
                            await context.CallActivityAsync(nameof(UpdateHistoryActivity), 
                                new { InstanceId = instanceId, Messages = messageData.Messages });
                            state.NewMessage = messageData.Messages[messageData.Messages.Count - 1];
                        }
                        
                        // Update field completions in entity if provided
                        if (messageData.FieldCompletions != null && messageData.FieldCompletions.Count > 0)
                        {
                            // Convert FieldValue to FormFieldValue for entity storage
                            var formFieldValues = messageData.FieldCompletions
                                .Select(kvp => new FormFieldValue
                                {
                                    FieldName = kvp.Key,
                                    Value = kvp.Value.Value,
                                    Note = kvp.Value.Note
                                })
                                .ToDictionary(f => f.FieldName!, f => f);
                            await context.CallActivityAsync(nameof(UpdateFieldsActivity), 
                                new { InstanceId = instanceId, Fields = formFieldValues });
                        }
                    }
                    break;
                case "form_action":
                    // Deserialize the form action data which contains field updates
                    var formActionData = JsonSerializer.Deserialize<FormActionEventData>(eventData, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (formActionData != null)
                    {
                        // Add messages to history entity if provided via activity
                        if (formActionData.Messages != null && formActionData.Messages.Count > 0)
                        {
                            await context.CallActivityAsync(nameof(UpdateHistoryActivity), 
                                new { InstanceId = instanceId, Messages = formActionData.Messages });
                        }
                        
                        // Update field values in entity if provided
                        if (formActionData.NewFieldValues != null && formActionData.NewFieldValues.Count > 0)
                        {
                            var fieldsDict = formActionData.NewFieldValues
                                .Where(f => !string.IsNullOrEmpty(f.FieldName))
                                .Select(f => {
                                    var inferredNote = f.Inferred == true ? " (Inferred)" : "";
                                    var draftedNote = f.Drafted == true ? " (Drafted)" : "";
                                    var note = $"{f.Note}(Updated via form_action){inferredNote}{draftedNote}";
                                    return new FormFieldValue
                                    {
                                        FieldName = f.FieldName,
                                        Value = f.Value,
                                        Note = note,
                                        Inferred = f.Inferred,
                                        Drafted = f.Drafted
                                    };
                                })
                                .ToDictionary(f => f.FieldName!, f => f);
                            await context.CallActivityAsync(nameof(UpdateFieldsActivity), 
                                new { InstanceId = instanceId, Fields = fieldsDict });
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
                    }
                    break;
                case "invalid_input":
                    messageData = JsonSerializer.Deserialize<MessageEventData>(eventData, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (messageData != null && messageData.Messages != null && messageData.Messages.Count > 0)
                    {
                        state.NewMessage = messageData.Messages[messageData.Messages.Count - 1];
                        logger.LogInformation("Recorded invalid input message for instance");
                    }
                    else
                    {
                        logger.LogWarning("Received invalid_input event with null or empty message data");
                    }
                    break;
                default:
                    logger.LogWarning("Received unknown event type: {EventType}", eventType);
                    break;
            }
            
            context.SetCustomStatus(state);
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

            // Create orchestration instance
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                OrchestratorName,
                new SessionState 
                { 
                    FormCode = startRequest.FormCode, 
                    Version = startRequest.Version
                });
            
            // Initialize entities if FormData is provided
            if (startRequest.FormData != null && startRequest.FormData.Count > 0)
            {
                var fieldsEntityId = new EntityInstanceId(nameof(FormFieldsEntity), instanceId);
                var formFieldValues = new Dictionary<string, FormFieldValue>();
                
                foreach (var kvp in startRequest.FormData)
                {
                    formFieldValues[kvp.Key] = new FormFieldValue
                    {
                        FieldName = kvp.Key,
                        Value = JsonSerializer.SerializeToElement(kvp.Value)
                    };
                }
                
                await client.Entities.SignalEntityAsync(fieldsEntityId, "SetAll", formFieldValues);
            }
            
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
            
            // Check for chat_command in the message body
            string? chat_command = null;
            var trimmedBody = body.Trim();
            if (!string.IsNullOrEmpty(trimmedBody) && trimmedBody.StartsWith("/"))
            {
                // Extract chat_command: everything between / and first space (or end of string)
                var spaceIndex = trimmedBody.IndexOf(' ');
                if (spaceIndex > 1)
                {
                    chat_command = trimmedBody.Substring(1, spaceIndex - 1);
                    // Remove the chat_command from the body
                    body = trimmedBody.Substring(spaceIndex + 1).TrimStart();
                }
                else if (trimmedBody.Length > 1)
                {
                    chat_command = trimmedBody.Substring(1);
                    // chat_command is the entire message, set body to empty
                    body = string.Empty;
                }
            }
            
            // Get and validate current state
            var (currentState, errorResponse) = await GetValidatedStateAsync(client, instanceId, req);
            if (errorResponse != null)
            {
                return errorResponse;
            }
            
            if (currentState?.FormCode == null) {
                _logger.LogWarning("SessionOrchestrator_Send: No form specified for instance {InstanceId}", instanceId);
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "No form specified for this request." }));
                return notFoundResponse;                
            }        


            var (form, error, fieldIds, sectionNames) = await LoadFormAsync(req, currentState);
            if (error != null || form == null)
            {
                _logger.LogWarning("SessionOrchestrator_Send: Form not found for instance {InstanceId}. FormCode: {FormCode}", instanceId, currentState.FormCode);
                var formErrorResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await formErrorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Form Instance not found or no status available" }));
                return formErrorResponse;
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
            
            // Get history from entity
            var priorMessages = await GetHistoryFromEntityAsync(client, instanceId);
            
            // Get field values from entity
            var completedFieldValues = await GetFieldValuesFromEntityAsync(client, instanceId);
            
            bool jumpToConversation = false; // This can be set based on certain conditions in the message evaluation if needed
            if (body.Trim().Length ==0 || chat_command == "next")
            {
                jumpToConversation = true;
                _logger.LogInformation("Message is empty or next chat_command received, skipping to conversation generation for instance {InstanceId}", instanceId);
            }
            else {
                if (chat_command == "init") {
                    priorMessages.Add($"system: The user message represents an initial dump of information from the user."); // Add the initial message to the prior messages for evaluation
                }   
                priorMessages.Add(body); // Add the new message to the prior messages for evaluation
            }
            
            //Look at the message to evaluate its content
            var evaluationStopwatch = Stopwatch.StartNew();
            var messageEvaluation =chat_command == "init" ? new MessageEvaluationResult { ContainsQuestion = false, ContainsRequest = false, ContainsDistraction = false, ContainsValues = true } : new MessageEvaluationResult { ContainsQuestion = false, ContainsRequest = false, ContainsDistraction = false, ContainsValues = false };
            if (!jumpToConversation && chat_command != "init") {
                messageEvaluation =await _messageEvaluateAgent.EvaluateMessageAsync(priorMessages, formContext, fieldIds, sectionNames);
            evaluationStopwatch.Stop();
            _logger.LogInformation("EvaluateMessageAsync completed in {Seconds:F2} seconds. Question: {Question}, Request: {Request}, Distraction: {Distraction}, Values: {Values}",
                evaluationStopwatch.Elapsed.TotalSeconds,
                messageEvaluation.ContainsQuestion,
                messageEvaluation.ContainsRequest,
                messageEvaluation.ContainsDistraction,
                messageEvaluation.ContainsValues);
            }

            // Handle distraction - User appears to be focused on something other than the form
            if (messageEvaluation.ContainsDistraction || jumpToConversation)
            {                              
                // Log the invalid input event
                if (chat_command != "next")
                {
                     _logger.LogWarning("Distraction detected in message for instance {InstanceId}", instanceId);
                    var invalidInputEvent = new { message = body, reason = "distraction" };
                    await client.RaiseEventAsync(instanceId, "invalid_input", JsonSerializer.Serialize(invalidInputEvent));
                }
                
                // Generate redirect response using Agent_ConversationRedirect
                var redirectStopwatch = Stopwatch.StartNew();
                var redirectResponse = await _conversationRedirectAgent.GenerateRedirectResponseAsync(
                    form,
                    completedFieldValues,
                    focusFieldId: null,
                    jumpToConversation? false : messageEvaluation.ContainsDistraction
                );
                redirectStopwatch.Stop();
                _logger.LogInformation("GenerateRedirectResponseAsync completed in {Seconds:F2} seconds", redirectStopwatch.Elapsed.TotalSeconds);
                
                // Log AI response to history
                string redirectFieldFocusInfo = redirectResponse.FieldFocus != null ? $" [Next focus field: {redirectResponse.FieldFocus}]" : "";
                var redirectMessageList = new List<string>();
                
                // Only add user message if it's not empty and not a "next" command
                /*if (!string.IsNullOrWhiteSpace(body) && chat_command != "next")
                {
                    redirectMessageList.Add($"user: {body}");
                }*/
                
                // Add assistant response to history
                redirectMessageList.Add($"assistant: {redirectResponse.FinalThoughts}{redirectFieldFocusInfo}");
                
                // Raise message event to log to history (no field changes in redirect)
                var redirectMessageEventData = new MessageEventData
                {
                    Messages = redirectMessageList,
                    FieldCompletions = null // No field updates in redirect response
                };
                await client.RaiseEventAsync(instanceId, EventNames[0], JsonSerializer.Serialize(redirectMessageEventData, JsonOptions));
                
                // Return redirect response
                HttpResponseData distractionResponse = req.CreateResponse(HttpStatusCode.OK);
                distractionResponse.Headers.Add("Content-Type", "application/json");
                var distractionResponseDict = new Dictionary<string, object>
                {
                    ["status"] = chat_command=="next" ? "next_action" :     "distraction_detected"
                };
                
                if (!string.IsNullOrEmpty(redirectResponse.FinalThoughts))
                {
                    distractionResponseDict["finalThoughts"] = redirectResponse.FinalThoughts;
                }
                if (!string.IsNullOrEmpty(redirectResponse.FieldFocus))
                {
                    distractionResponseDict["fieldFocus"] = redirectResponse.FieldFocus;
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
                    priorMessages.TakeLast(5).ToList(), // Pass the last 5 messages as prior dialog for context 
                    form, 
                    completedFieldValues,
                    chat_command=="init"? true : false );// Anticipate bulk completion on initial message
                extractStopwatch.Stop();
                _logger.LogInformation("ExtractFieldValuesAsync completed in {Seconds:F2} seconds with {Count} fields extracted",
                    extractStopwatch.Elapsed.TotalSeconds,
                    newFieldValues?.Count ?? 0);

                // Update local field values for use in subsequent operations
                if (newFieldValues != null && newFieldValues.Count > 0)
                {
                    foreach (var newValue in newFieldValues)
                    {
                        if (!string.IsNullOrEmpty(newValue.FieldName))
                        {
                            var inferredNote = newValue.Inferred == true ? " (Inferred)" : "";
                            var draftedNote = newValue.Drafted == true ? " (Drafted)" : "";
                            var note = $"{newValue.Note}{inferredNote}{draftedNote}";
                            
                            completedFieldValues[newValue.FieldName] = new FieldValue
                            {
                                Value = newValue.Value,
                                Note = string.IsNullOrEmpty(note) ? null : note
                            };
                        }
                    }
                }
                
                // Validate new field values
                if (newFieldValues != null && newFieldValues.Count > 0)
                {
                    var validationStopwatch = Stopwatch.StartNew();
                    validationResult = await _fieldValidationAgent.ValidateFieldValuesAsync(
                        body,
                        form,
                        completedFieldValues,
                        newFieldValues);
                    validationStopwatch.Stop();
                    _logger.LogInformation("ValidateFieldValuesAsync completed in {Seconds:F2} seconds", validationStopwatch.Elapsed.TotalSeconds);
                }
            }
            // If contains question or request, skip field completion (go straight to conversation)

            // Generate conversational response
            var conversationStopwatch = Stopwatch.StartNew();
            var conversationResponse = await _conversationAgent.GenerateResponseAsync(
                priorMessages,
                form,
                completedFieldValues,
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
                
                // Update local field values
                completedFieldValues[draftedFieldValue.FieldName] = new FieldValue
                {
                    Value = draftedFieldValue.Value,
                    Note = draftedFieldValue.Note
                };
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

            // Create message event data - only pass NEW field values (not all)
            // to reduce event payload size since old values are already in entity
            Dictionary<string, FieldValue>? newFieldCompletions = null;
            if (newFieldValues != null && newFieldValues.Count > 0)
            {
                newFieldCompletions = newFieldValues
                    .Where(f => !string.IsNullOrEmpty(f.FieldName))
                    .ToDictionary(
                        f => f.FieldName!,
                        f => new FieldValue { Value = f.Value, Note = f.Note }
                    );
            }
            
            var messageEventData = new MessageEventData
            {
                Messages = messageList,
                FieldCompletions = newFieldCompletions
            };
            
            // Raise the message event to the orchestrator with all data
            await client.RaiseEventAsync(instanceId, EventNames[0], JsonSerializer.Serialize(messageEventData, JsonOptions));
            
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
            

            await response.WriteStringAsync(JsonSerializer.Serialize(responseDict, JsonOptions));
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
        // Get and validate current state
        var (currentState, errorResponse) = await GetValidatedStateAsync(client, instanceId, req);
        if (errorResponse != null)
        {
            return errorResponse;
        }
        
        if (currentState == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            notFound.Headers.Add("Content-Type", "application/json");
            await notFound.WriteStringAsync("{\"error\": \"Instance not found\"}");
            return notFound;
        }
        
        // Get field values from entity
        var completedFieldValues = await GetFieldValuesFromEntityAsync(client, instanceId);
        
        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        
        // Convert Dictionary<string, FieldValue> to Dictionary<string, string> for output
        var completedFieldValuesOutput = new Dictionary<string, string>();
        foreach (var kvp in completedFieldValues)
        {
            completedFieldValuesOutput[kvp.Key] = kvp.Value.Value?.ToString() ?? "";
        }
        
        var responseObject = new
        {
            instanceId = instanceId,
            formCode = currentState.FormCode,
            version = currentState.Version,
            completedFieldValues = completedFieldValuesOutput
        };
        
        await response.WriteStringAsync(JsonSerializer.Serialize(responseObject, JsonOptions));
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
        
        // Get and validate current state
        var (currentState, errorResponse) = await GetValidatedStateAsync(client, instanceId, req);
        if (errorResponse != null)
        {
            return errorResponse;
        }
        
        // Load the form to validate field IDs
        var (form, error, fieldIds, sectionNames) = await LoadFormAsync(req, currentState);
        if (error != null || form == null)
        {
            var formErrorResponse = req.CreateResponse(HttpStatusCode.NotFound);
            formErrorResponse.Headers.Add("Content-Type", "application/json");
            await formErrorResponse.WriteStringAsync("{\"error\": \"Form not found or could not be loaded\"}");
            return formErrorResponse;
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
        
        // Get current field values from entity for validation
        var completedFieldValues = await GetFieldValuesFromEntityAsync(client, instanceId);
        
        // Validate the field values using the validation agent
        var validationResult = await _fieldValidationAgent.ValidateFieldValuesAsync(
            string.Empty, // No user message for direct field updates
            form,
            completedFieldValues,
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
        await client.RaiseEventAsync(instanceId, EventNames[1], JsonSerializer.Serialize(formActionData, JsonOptions));
        
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
        
        await response.WriteStringAsync(JsonSerializer.Serialize(responseDict, JsonOptions));
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
            // Get and validate current state
            var (currentState, errorResponse) = await GetValidatedStateAsync(client, instanceId, req);
            if (errorResponse != null)
            {
                return errorResponse;
            }

            if (currentState == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                notFound.Headers.Add("Content-Type", "application/json");
                await notFound.WriteStringAsync("{\"error\": \"Instance not found\"}");
                return notFound;
            }

            string token;
            DateTime expiration;
            
            // Check if a valid token already exists
            if (!string.IsNullOrEmpty(currentState.ClientAccessToken) && 
                currentState.TokenExpiration.HasValue && 
                currentState.TokenExpiration.Value > DateTime.UtcNow)
            {
                // Return existing valid token
                token = currentState.ClientAccessToken;
                expiration = currentState.TokenExpiration.Value;
                _logger.LogInformation("Returning existing valid token for instance {InstanceId}, expires at {Expiration}", instanceId, expiration);
            }
            else
            {
                // Generate a new secure token
                var tokenBytes = new byte[32];
                RandomNumberGenerator.Fill(tokenBytes);
                token = Convert.ToBase64String(tokenBytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
                
                // Set expiration to 24 hours from now
                expiration = DateTime.UtcNow.AddHours(24);
                
                // Raise an event to update the orchestrator state with the new token
                var updateTokenData = new TokenUpdateEventData
                {
                    Token = token,
                    Expiration = expiration
                };
                await client.RaiseEventAsync(instanceId, "token_update", JsonSerializer.Serialize(updateTokenData));
                
                _logger.LogInformation("Generated new token for instance {InstanceId}, expires at {Expiration}", instanceId, expiration);
            }
            
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
            
            // Get and validate current state
            var (currentState, errorResponse) = await GetValidatedStateAsync(client, instanceId, req, includeCorsHeaders: true);
            if (errorResponse != null)
            {
                return errorResponse;
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
            var (form, error, fieldIds, sectionNames) = await LoadFormAsync(req, currentState);
            if (error != null || form == null)
            {
                var formErrorResponse = req.CreateResponse(HttpStatusCode.NotFound);
                formErrorResponse.Headers.Add("Content-Type", "application/json");
                formErrorResponse.Headers.Add("Access-Control-Allow-Origin", "*");
                await formErrorResponse.WriteStringAsync("{\"error\": \"Form not found or could not be loaded\"}");
                return formErrorResponse;
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
            
            // Get current field values from entity for validation
            var completedFieldValues = await GetFieldValuesFromEntityAsync(client, instanceId);
            
            // Validate the field values using the validation agent
            var validationResult = await _fieldValidationAgent.ValidateFieldValuesAsync(
                string.Empty,
                form,
                completedFieldValues,
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
                { "status", "accepted" },
                { "message", "Field updates queued for processing" },
                { "updatedFields", fieldUpdateRequest.NewFieldValues.Count }
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


    [Function("SessionOrchestrator_Terminate")]
    public async Task<HttpResponseData> Terminate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "session/{instanceId}/terminate")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        try
        {
            await client.TerminateInstanceAsync(instanceId, "Terminated by user request");
            
            HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            { 
                status = "terminated",
                instanceId = instanceId,
                message = "Orchestration instance has been terminated"
            }));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to terminate instance {InstanceId}", instanceId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to terminate instance", message = ex.Message }));
            return errorResponse;
        }
    }

    [Function("SessionOrchestrator_Debug")]
    public async Task<HttpResponseData> Debug(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "session/{instanceId}/debug")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        try
        {
            var instance = await client.GetInstanceAsync(instanceId, true);
            
            if (instance == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync(JsonSerializer.Serialize(new { error = "Instance not found" }));
                return notFound;
            }
            
            // Get custom status
            SessionState? customStatus = null;
            try
            {
                customStatus = instance.ReadCustomStatusAs<SessionState>();
            }
            catch { }
            
            // Get input
            SessionState? inputState = null;
            try
            {
                inputState = instance.ReadInputAs<SessionState>();
            }
            catch { }
            
            // Get entity data
            var historyFromEntity = await GetHistoryFromEntityAsync(client, instanceId);
            var fieldsFromEntity = await GetFieldValuesFromEntityAsync(client, instanceId);
            
            HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            { 
                instanceId = instanceId,
                runtimeStatus = instance.RuntimeStatus.ToString(),
                customStatus = customStatus,
                customStatusRaw = instance.SerializedCustomStatus,
                orchestrationInput = inputState,
                historyEntity = new { count = historyFromEntity.Count, messages = historyFromEntity },
                fieldsEntity = new { count = fieldsFromEntity.Count, fields = fieldsFromEntity }
            }, new JsonSerializerOptions { WriteIndented = true }));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get debug info for instance {InstanceId}", instanceId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to get debug info", message = ex.Message }));
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

    private class SessionMapping
    {
        public string OldInstanceId { get; set; } = string.Empty;
        public string NewInstanceId { get; set; } = string.Empty;
        public DateTime ReplacedAt { get; set; }
    }

    private async Task<(Form? form, HttpResponseData? errorResponse, String fieldIds, String sectionNames)> LoadFormAsync(HttpRequestData req, SessionState? currentState)
    {
        var form = new Form();
        String fieldIds="";
        String sectionNames=""; 
        
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
                
                // Extract section names from Badge elements
                var badges = form.Body.Where(b => b.Type == "Badge" && !string.IsNullOrEmpty(b.Text));
                sectionNames = string.Join(", ", badges.Select(b => b.Text));

            }
            catch (FileNotFoundException ex)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync($"Form not found: {currentState.FormCode}/{fileName} - {ex.Message}");
                return (null, notFound, null, null);
            }
            catch (Exception ex)
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error loading form: {ex.Message} | Stack: {ex.StackTrace} | Inner: {ex.InnerException?.Message}");
                return (null, errorResponse, null, null);
            }
        }        
        return (form, null, fieldIds,  sectionNames);
    }

    private async Task<SessionState?> GetCurrentStateAsync(DurableTaskClient client, string instanceId)
    {
        var (state, _) = await GetCurrentStateWithStatusAsync(client, instanceId);
        return state;
    }

    private async Task<List<string>> GetHistoryFromEntityAsync(DurableTaskClient client, string instanceId)
    {
        try
        {
            var historyEntityId = new EntityInstanceId(nameof(SessionHistoryEntity), instanceId);
            var historyEntity = await client.Entities.GetEntityAsync(historyEntityId);
            
            if (historyEntity != null && historyEntity.State != null)
            {
                var historyJson = historyEntity.State.ToString();
                var history = JsonSerializer.Deserialize<List<string>>(historyJson);
                return history ?? new List<string>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve history from entity for instance {InstanceId}", instanceId);
        }
        
        return new List<string>();
    }

    private async Task<Dictionary<string, FieldValue>> GetFieldValuesFromEntityAsync(DurableTaskClient client, string instanceId)
    {
        try
        {
            var fieldsEntityId = new EntityInstanceId(nameof(FormFieldsEntity), instanceId);
            var fieldsEntity = await client.Entities.GetEntityAsync(fieldsEntityId);
            
            if (fieldsEntity != null && fieldsEntity.State != null)
            {
                var stateJson = fieldsEntity.State.ToString();
                var formFieldsState = JsonSerializer.Deserialize<FormFieldsState>(stateJson);
                
                if (formFieldsState?.Fields != null)
                {
                    // Convert FormFieldValue to FieldValue for backwards compatibility
                    return formFieldsState.Fields.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new FieldValue
                        {
                            Value = kvp.Value.Value,
                            Note = kvp.Value.Note
                        });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve field values from entity for instance {InstanceId}", instanceId);
        }
        
        return new Dictionary<string, FieldValue>();
    }

    private async Task<(SessionState? state, OrchestrationRuntimeStatus? status)> GetCurrentStateWithStatusAsync(DurableTaskClient client, string instanceId)
    {
        var instance = await client.GetInstanceAsync(instanceId, true);
        
        if (instance == null)
        {
            return (null, null);
        }
        
        SessionState? currentState = null;
        
        // Try to get custom status first, fall back to input if not available
        try
        {
            currentState = instance.ReadCustomStatusAs<SessionState>();
        }
        catch { }
        
        // If custom status is null, try reading from input (initial state)
        if (currentState == null && instance.ReadInputAs<SessionState>() is SessionState inputState)
        {
            currentState = inputState;
        }
        
        return (currentState, instance.RuntimeStatus);
    }

    private async Task<string?> GetReplacementSessionIdAsync(string oldInstanceId)
    {
        try
        {
            var mappingJson = await _blobStorageService.ReadFileAsync(
                "FailedSessionMapping",
                $"{oldInstanceId}.json",
                SessionMappingContainer);
            
            var mapping = JsonSerializer.Deserialize<SessionMapping>(mappingJson);
            return mapping?.NewInstanceId;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private async Task SaveSessionMappingAsync(string oldInstanceId, string newInstanceId)
    {
        var mapping = new SessionMapping
        {
            OldInstanceId = oldInstanceId,
            NewInstanceId = newInstanceId,
            ReplacedAt = DateTime.UtcNow
        };
        
        var mappingJson = JsonSerializer.Serialize(mapping, JsonOptions);
        await _blobStorageService.WriteFileAsync(
            "FailedSessionMapping",
            $"{oldInstanceId}.json",
            mappingJson,
            SessionMappingContainer,
            overwrite: true);
    }

    private async Task<(SessionState? state, HttpResponseData? errorResponse)> GetValidatedStateAsync(
        DurableTaskClient client, 
        string instanceId, 
        HttpRequestData req,
        bool includeCorsHeaders = false)
    {
        var (currentState, status) = await GetCurrentStateWithStatusAsync(client, instanceId);
        
        // Check if instance exists
        if (currentState == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            notFound.Headers.Add("Content-Type", "application/json");
            if (includeCorsHeaders)
            {
                notFound.Headers.Add("Access-Control-Allow-Origin", "*");
            }
            await notFound.WriteStringAsync("{\"error\": \"Instance not found\"}");
            return (null, notFound);
        }
        
        // Check if orchestration is in a valid state to receive events
        if (status == OrchestrationRuntimeStatus.Failed || 
            status == OrchestrationRuntimeStatus.Terminated || 
            status == OrchestrationRuntimeStatus.Completed)
        {
            _logger.LogWarning("Session {InstanceId} is in {Status} state. Checking for replacement session.", instanceId, status);
            
            // Check if a replacement session already exists
            string? existingReplacementId = await GetReplacementSessionIdAsync(instanceId);
            string newInstanceId;
            
            if (existingReplacementId != null)
            {
                newInstanceId = existingReplacementId;
                _logger.LogInformation("Found existing replacement session {NewInstanceId} for failed session {OldInstanceId}", newInstanceId, instanceId);
            }
            else
            {

                // Create a new orchestration instance with the current state
                newInstanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                    OrchestratorName,
                    currentState);
                
                // Save the mapping
                await SaveSessionMappingAsync(instanceId, newInstanceId);
                
                _logger.LogInformation("Created replacement session {NewInstanceId} for failed session {OldInstanceId}", newInstanceId, instanceId);
            }
            
            // Return a response indicating the session was replaced
            var replacementResponse = req.CreateResponse(HttpStatusCode.Gone);
            replacementResponse.Headers.Add("Content-Type", "application/json");
            if (includeCorsHeaders)
            {
                replacementResponse.Headers.Add("Access-Control-Allow-Origin", "*");
            }
            await replacementResponse.WriteStringAsync(JsonSerializer.Serialize(new 
            { 
                error = $"Original session was in {status} state and has been replaced.",
                oldInstanceId = instanceId,
                newInstanceId = newInstanceId,
                message = "Please retry your request with the new session ID."
            }));
            return (null, replacementResponse);
        }
        
        return (currentState, null);
    }

    // Activity functions for entity operations
    [Function(nameof(UpdateHistoryActivity))]
    public async Task UpdateHistoryActivity([ActivityTrigger] object input, [DurableClient] DurableTaskClient client)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(input.ToString() ?? "{}", 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (data != null && data.ContainsKey("InstanceId") && data.ContainsKey("Messages"))
        {
            var instanceId = data["InstanceId"].ToString();
            var messagesJson = data["Messages"].ToString();
            var messages = JsonSerializer.Deserialize<List<string>>(messagesJson ?? "[]");
            
            if (!string.IsNullOrEmpty(instanceId) && messages != null && messages.Count > 0)
            {
                var historyEntityId = new EntityInstanceId(nameof(SessionHistoryEntity), instanceId);
                await client.Entities.SignalEntityAsync(historyEntityId, "AddRange", messages);
            }
        }
    }

    [Function(nameof(UpdateFieldsActivity))]
    public async Task UpdateFieldsActivity([ActivityTrigger] object input, [DurableClient] DurableTaskClient client)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(input.ToString() ?? "{}", 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (data != null && data.ContainsKey("InstanceId") && data.ContainsKey("Fields"))
        {
            var instanceId = data["InstanceId"].ToString();
            var fieldsJson = data["Fields"].ToString();
            var fields = JsonSerializer.Deserialize<Dictionary<string, FormFieldValue>>(fieldsJson ?? "{}");
            
            if (!string.IsNullOrEmpty(instanceId) && fields != null && fields.Count > 0)
            {
                var fieldsEntityId = new EntityInstanceId(nameof(FormFieldsEntity), instanceId);
                await client.Entities.SignalEntityAsync(fieldsEntityId, "UpsertAndGet", fields);
            }
        }
    }

}
