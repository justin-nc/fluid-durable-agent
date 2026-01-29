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
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;

namespace fluid_durable_agent.Functions;


public static class FormFieldNext
{

    [Function("FormFieldNext")]
    public static async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "FormFieldNext")] HttpRequestData req,
        [DurableClient] DurableTaskClient durableClient)
    {

        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required");
        var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini";
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY") ?? throw new InvalidOperationException("AZURE_OPENAI_KEY is required");
        var dtsBackend = Environment.GetEnvironmentVariable("DurableTaskBackend") ?? "http://localhost:8082";
        var taskHubName = Environment.GetEnvironmentVariable("TASKHUB_NAME") ?? "default";
        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        AzureOpenAIClient client = !string.IsNullOrEmpty(key)
                                    ? new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key))
                                    : new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential());
        var instanceId = await durableClient.ScheduleNewOrchestrationInstanceAsync("AgentWorkflow", "Hello World");
        durableClient.CreateCheckStatusResponse(req, instanceId);
        await response.WriteStringAsync($"FormFieldNext-ok {instanceId}");
       // await response.WriteAsJsonAsync(new { instanceId = instanceId.ToString() });
        return response;
    }
}
