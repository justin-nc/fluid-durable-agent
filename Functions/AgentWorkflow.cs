using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

namespace fluid_durable_agent.Functions;

public static class AgentWorkflow
{
    private static readonly HttpClient HttpClient = new();

    [Function("AgentWorkflow")]
    public static async Task<string> RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        string input = context.GetInput<string>() ?? string.Empty;
        string threadId = context.InstanceId;

        string step1 = await context.CallActivityAsync<string>(
            "AgentWorkflow_RunAgentStep",
            new AgentWorkflowStep($"Collect and normalize fields from this input: {input}", threadId));

        string step2 = await context.CallActivityAsync<string>(
            "AgentWorkflow_RunAgentStep",
            new AgentWorkflowStep($"Summarize the collected fields: {step1}", threadId));

        return step2;
    }

    [Function("AgentWorkflow_Start")]
    public static async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "workflow/agent")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        string body;
        using (StreamReader reader = new(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync("AgentWorkflow", body);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    [Function("AgentWorkflow_RunAgentStep")]
    public static async Task<string> RunAgentStep([ActivityTrigger] AgentWorkflowStep step)
    {
        string baseUrl = Environment.GetEnvironmentVariable("FUNCTIONS_BASE_URL") ?? "http://localhost:7071";
        string agentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "MyDurableAgent";

        string requestUri = $"{baseUrl.TrimEnd('/')}/api/agents/{agentName}/run?thread_id={Uri.EscapeDataString(step.ThreadId)}";
        using HttpContent content = new StringContent(step.Prompt ?? string.Empty, Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await HttpClient.PostAsync(requestUri, content);
        string responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Agent call failed with status {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    public sealed record AgentWorkflowStep(string Prompt, string ThreadId);
}
