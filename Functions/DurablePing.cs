using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using System.Net;

namespace fluid_durable_agent.Functions;

public static class DurablePing
{
    [Function("DurablePing")]
    public static async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "durable/ping")] HttpRequestData req,
        [DurableClient] DurableTaskClient durableClient)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("durable-client-ok");
        return response;
    }
}
