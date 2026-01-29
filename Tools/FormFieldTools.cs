using System.ComponentModel;
using fluid_durable_agent.Models;
using fluid_durable_agent.Functions;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Entities;

namespace fluid_durable_agent.Tools;

public sealed class FormFieldTools
{
    private static EntityInstanceId GetEntityId(AgentSessionId sessionId)
    {
        return new EntityInstanceId($"formfields-{sessionId.Name}", sessionId.Key);
    }

    private static AgentSessionId GetSessionId()
    {
        return (AgentSessionId)DurableAgentContext.Current.EntityContext.Id;
    }

    [Description("Gets the current form field context for this agent session.")]
    public async Task<FormFieldsState> GetFormFieldsAsync()
    {
        AgentSessionId sessionId = GetSessionId();
        EntityInstanceId entityId = GetEntityId(sessionId);
        EntityMetadata<FormFieldsState>? entity = await DurableAgentContext.Current.Client.Entities.GetEntityAsync<FormFieldsState>(
            entityId,
            includeState: true);
        return entity?.State ?? new FormFieldsState();
    }

    [Description("Upserts form field values for this agent session. The input is a dictionary keyed by field name.")]
    public async Task<FormFieldsState> UpsertFormFieldsAsync(Dictionary<string, FormFieldValue> fields)
    {
        AgentSessionId sessionId = GetSessionId();
        EntityInstanceId entityId = GetEntityId(sessionId);
        await DurableAgentContext.Current.Client.Entities.SignalEntityAsync(
            entityId,
            nameof(FormFieldsEntity.UpsertAndGet),
            fields);

        EntityMetadata<FormFieldsState>? entity = await DurableAgentContext.Current.Client.Entities.GetEntityAsync<FormFieldsState>(
            entityId,
            includeState: true);
        return entity?.State ?? new FormFieldsState();
    }

    [Description("Replaces the entire form field context for this agent session.")]
    public async Task<FormFieldsState> SetFormFieldsAsync(Dictionary<string, FormFieldValue> fields)
    {
        AgentSessionId sessionId = GetSessionId();
        EntityInstanceId entityId = GetEntityId(sessionId);
        await DurableAgentContext.Current.Client.Entities.SignalEntityAsync(
            entityId,
            nameof(FormFieldsEntity.SetAll),
            fields);

        EntityMetadata<FormFieldsState>? entity = await DurableAgentContext.Current.Client.Entities.GetEntityAsync<FormFieldsState>(
            entityId,
            includeState: true);
        return entity?.State ?? new FormFieldsState();
    }

    [Description("Clears all form field values for this agent session.")]
    public Task ClearFormFieldsAsync()
    {
        AgentSessionId sessionId = GetSessionId();
        EntityInstanceId entityId = GetEntityId(sessionId);
        return DurableAgentContext.Current.Client.Entities.SignalEntityAsync(
            entityId,
            nameof(FormFieldsEntity.Clear));
    }
}
