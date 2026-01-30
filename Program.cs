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
var fieldCompletionDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_FIELDCOMPLETION") ?? "gpt-oss-120b";
var fieldValidationDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_FIELDVALIDATION") ?? "gpt-oss-120b";
var conversationDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_CONVERSATION") ?? deploymentName; //use default if not set
var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY") ?? throw new InvalidOperationException("AZURE_OPENAI_KEY is required");
var dtsBackend = Environment.GetEnvironmentVariable("DurableTaskBackend") ?? "http://localhost:8082";
var taskHubName = Environment.GetEnvironmentVariable("TASKHUB_NAME") ?? "default";

// Create an AI agent following the standard Microsoft Agent Framework pattern
FormFieldTools formFieldTools = new();
DeterministicPromptTools deterministicPromptTools = new();

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
            AIFunctionFactory.Create(deterministicPromptTools.BuildPromptFromJson)
        ]);

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Add configuration
builder.Services.AddOptions();

// Register BlobStorageService
builder.Services.AddSingleton<BlobStorageService>();

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
    return new fluid_durable_agent.Agents.Agent_Conversation(chatClient);
});



// Configure Durable Task Client to use DTS backend
builder.Services.AddDurableTaskClient(clientBuilder =>
{
    clientBuilder.UseGrpc(dtsBackend);
});

builder.ConfigureDurableAgents(options => options.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1)));

using IHost app = builder.Build();
app.Run();