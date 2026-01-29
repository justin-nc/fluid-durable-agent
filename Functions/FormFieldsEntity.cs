using System;
using fluid_durable_agent.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Entities;

namespace fluid_durable_agent.Functions;

public sealed class FormFieldsEntity
{
    public FormFieldsState State { get; set; } = new();

    public FormFieldsState Get() => State;

    public FormFieldsState UpsertAndGet(Dictionary<string, FormFieldValue> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        foreach (KeyValuePair<string, FormFieldValue> update in updates)
        {
            State.Fields[update.Key] = update.Value;
        }

        return State;
    }

    public FormFieldsState SetAll(Dictionary<string, FormFieldValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        State.Fields = new Dictionary<string, FormFieldValue>(fields, StringComparer.OrdinalIgnoreCase);
        return State;
    }

    public void Remove(string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            State.Fields.Remove(fieldName);
        }
    }

    public void Clear() => State.Fields.Clear();

    [Function(nameof(FormFieldsEntity))]
    public static Task Run([EntityTrigger] TaskEntityDispatcher dispatcher) => dispatcher.DispatchAsync<FormFieldsEntity>();
}
