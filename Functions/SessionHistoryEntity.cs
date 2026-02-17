using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Entities;

namespace fluid_durable_agent.Functions;

public sealed class SessionHistoryEntity
{
    public List<string> History { get; set; } = new();

    public List<string> Get() => History;

    public void Add(string message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            History.Add(message);
        }
    }

    public void AddRange(List<string> messages)
    {
        if (messages != null && messages.Count > 0)
        {
            History.AddRange(messages);
        }
    }

    public void TrimToSize(int maxSize)
    {
        if (History.Count > maxSize)
        {
            // Keep only the last maxSize messages
            History = History.Skip(History.Count - maxSize).ToList();
        }
    }

    public void Clear() => History.Clear();

    [Function(nameof(SessionHistoryEntity))]
    public static Task Run([EntityTrigger] TaskEntityDispatcher dispatcher) => dispatcher.DispatchAsync<SessionHistoryEntity>();
}
