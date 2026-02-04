using System.ComponentModel;
using fluid_durable_agent.Models;
using fluid_durable_agent.Agents;

namespace fluid_durable_agent.Tools;

public sealed class CommodityCodeTools
{
    private readonly Agent_CommodityCodeLookup _commodityCodeLookup;

    public CommodityCodeTools(Agent_CommodityCodeLookup commodityCodeLookup)
    {
        _commodityCodeLookup = commodityCodeLookup;
    }

    [Description("Looks up the most appropriate commodity code for a given product or service description. Returns a JSON object with the commodity code and its description.")]
    public async Task<CommodityCodeResult> LookupCommodityCodeAsync(
        [Description("A description of the product, service, or item to classify")] string productDescription)
    {
        return await _commodityCodeLookup.LookupCommodityCodeAsync(productDescription);
    }
}
