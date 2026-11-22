using System.Linq;
using CreateAColony.Colonies;

namespace CreateAColony.Network;

/// <summary>The For sale tab of the Market's window (ColonyManager.Market.cs).</summary>
public partial class ColonyNetwork
{
    void AddMarket(HutSnapshot snapshot, Colony colony, Building market)
    {
        var m = Manager;
        snapshot.IsMarket = true;
        snapshot.SavingsKeep = m.Config.SavingsKeep;
        snapshot.EconomyStarted = m.EconomyOpen(colony);
        snapshot.EconomyLevel = m.Config.EconomyTownHallLevel;
        var totals = market.StatTotals;
        foreach (var kind in ColonyManager.MarketKinds)
        {
            snapshot.MarketRows.Add(new MarketRowView
            {
                Kind = kind,
                Price = m.MarketPrice(colony, kind),
                Stock = m.MarketStock(colony, market, kind),
                On = m.MarketSells(colony, kind),
                Sold = totals.TryGetValue("sold-" + kind, out int sold) ? sold : 0
            });
        }
    }
}
