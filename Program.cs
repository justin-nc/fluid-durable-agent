using System;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AzureFunctions;
using Microsoft.Agents.AI.OpenAI;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using fluid_durable_agent.Tools;
using fluid_durable_agent.Services;

var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required");
var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-5-mini";
var lightweightDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_LIGHTWEIGHT") ?? "gpt-4.1-mini";
var fieldCompletionDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_FIELDCOMPLETION") ?? "gpt-oss-120b";
var fieldValidationDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_FIELDVALIDATION") ?? "gpt-oss-120b";
var conversationDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_CONVERSATION") ?? deploymentName; //use default if not set
var messageEvaluateDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_MESSAGEEVALUATE") ?? lightweightDeployment; //use default if not set
var conversationRedirectDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_CONVERSATIONREDIRECT") ?? lightweightDeployment; //use default if not set
var commodityCodeLookupDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_COMMODITYCODELOOKUP") ?? lightweightDeployment; //use default if not set
var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY") ?? throw new InvalidOperationException("AZURE_OPENAI_KEY is required");
var dtsBackend = Environment.GetEnvironmentVariable("DurableTaskBackend") ?? "http://localhost:8082";
var taskHubName = Environment.GetEnvironmentVariable("TASKHUB_NAME") ?? "default";


var builder = FunctionsApplication.CreateBuilder(args);
// Register BlobStorageService first so it can be used by other services
builder.Services.AddSingleton<BlobStorageService>();

// Create an AI agent following the standard Microsoft Agent Framework pattern
FormFieldTools formFieldTools = new();
DeterministicPromptTools deterministicPromptTools = new();

// Create commodity code lookup agent first
var commodityCodeLookupChatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
    .GetChatClient(lightweightDeployment)
    .AsIChatClient();
var blobStorageServiceForTools = new BlobStorageService(builder.Configuration);
var commodityCodeLookupAgent = new fluid_durable_agent.Agents.Agent_CommodityCodeLookup(
    commodityCodeLookupChatClient, 
    blobStorageServiceForTools, 
    null!); // Logger will be null for tools instance
var commodityCodeTools = new fluid_durable_agent.Tools.CommodityCodeTools(commodityCodeLookupAgent);

AIAgent agent = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
    .GetChatClient(deploymentName)
    .AsIChatClient()
    .AsAIAgent(
        instructions: "You are a helpful that gather field values from conversation.",
        name: "MyDurableAgent",
        tools:
        [
            AIFunctionFactory.Create(formFieldTools.GetFormFieldsAsync),
            AIFunctionFactory.Create(formFieldTools.UpsertFormFieldsAsync),
            AIFunctionFactory.Create(formFieldTools.SetFormFieldsAsync),
            AIFunctionFactory.Create(formFieldTools.ClearFormFieldsAsync),
            AIFunctionFactory.Create(deterministicPromptTools.BuildPromptFromJson),
            AIFunctionFactory.Create(commodityCodeTools.LookupCommodityCodeAsync)
        ]);



builder.ConfigureFunctionsWebApplication();

// Add configuration
builder.Services.AddOptions();

// Register IChatClient for Agent_FieldCompletion
builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp => 
    new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(fieldCompletionDeployment)
        .AsIChatClient());

// Register Agent_FieldCompletion
builder.Services.AddSingleton<fluid_durable_agent.Agents.Agent_FieldCompletion>(sp =>
{
    var chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(fieldCompletionDeployment)
        .AsIChatClient();
    return new fluid_durable_agent.Agents.Agent_FieldCompletion(chatClient);
});

// Register Agent_FieldValidation with its own client
builder.Services.AddSingleton<fluid_durable_agent.Agents.Agent_FieldValidation>(sp =>
{
    var chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(fieldValidationDeployment)
        .AsIChatClient();
    return new fluid_durable_agent.Agents.Agent_FieldValidation(chatClient);
});

// Register Agent_Conversation with its own client
builder.Services.AddSingleton<fluid_durable_agent.Agents.Agent_Conversation>(sp =>
{
    var chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(conversationDeployment)
        .AsIChatClient();
    var commodityCodeLookup = sp.GetRequiredService<fluid_durable_agent.Agents.Agent_CommodityCodeLookup>();
    return new fluid_durable_agent.Agents.Agent_Conversation(chatClient, commodityCodeLookup);
});

// Register Agent_MessageEvaluate with its own client
builder.Services.AddSingleton<fluid_durable_agent.Agents.Agent_MessageEvaluate>(sp =>
{
    var chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(messageEvaluateDeployment)
        .AsIChatClient();
    return new fluid_durable_agent.Agents.Agent_MessageEvaluate(chatClient);
});

// Register Agent_ConversationRedirect with its own client
builder.Services.AddSingleton<fluid_durable_agent.Agents.Agent_ConversationRedirect>(sp =>
{
    var chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(conversationRedirectDeployment)
        .AsIChatClient();
    return new fluid_durable_agent.Agents.Agent_ConversationRedirect(chatClient);
});

// Register Agent_CommodityCodeLookup with its own client
builder.Services.AddSingleton<fluid_durable_agent.Agents.Agent_CommodityCodeLookup>(sp =>
{
    var chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
        .GetChatClient(commodityCodeLookupDeployment)
        .AsIChatClient();
    var blobStorageService = sp.GetRequiredService<BlobStorageService>();
    var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<fluid_durable_agent.Agents.Agent_CommodityCodeLookup>>();
    return new fluid_durable_agent.Agents.Agent_CommodityCodeLookup(chatClient, blobStorageService, logger);
});


// Configure Durable Task Client to use DTS backend
builder.Services.AddDurableTaskClient(clientBuilder =>
{
    clientBuilder.UseGrpc(dtsBackend);
});

builder.ConfigureDurableAgents(options => options.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1)));

using IHost app = builder.Build();
app.Run();