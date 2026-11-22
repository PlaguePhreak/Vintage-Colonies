using CreateAColony.Colonies;
using CreateAColony.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The Market's For sale tab (economy step 1): what settlers can buy here, how many are on the stall, and the price
/// of each. Officers turn a kind on or off and change its price with - and +.
/// </summary>
public partial class GuiDialogHut
{
    const int ForSaleTab = 3500;

    void ComposeForSale(GuiComposer composer)
    {
        int id = BuildingId;
        double y = composer.Section(Lang.Get("createacolony:market-forsale-section"), 30, Width);
        if (!snapshot.EconomyStarted) y = composer.Text(Lang.Get("createacolony:market-needs-townhall", snapshot.EconomyLevel), GuiKit.Muted(), 0, y, Width, 6);
        y = composer.Text(ColonyMoney.Get(capi, "createacolony:market-forsale-hint", snapshot.SavingsKeep), GuiKit.Muted(), 0, y, Width, 10);

        foreach (var row in snapshot.MarketRows)
        {
            string kind = row.Kind;
            string name = Lang.Get("createacolony:market-kind-" + kind);
            string line = row.On
                ? ColonyMoney.Get(capi, "createacolony:market-row", name, row.Price, row.Stock)
                : Lang.Get("createacolony:market-row-off", name);
            composer.AddStaticText(line, GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width - 230, 25));
            if (Officer)
            {
                double x = Width - 220;
                int price = row.Price;
                composer.AddSmallButton("-", () => SendText(ColonyAction.SetMarketRule, id, kind + "|" + System.Math.Max(0, price - 1)), ElementBounds.Fixed(x, y, 36, 28));
                composer.AddSmallButton("+", () => SendText(ColonyAction.SetMarketRule, id, kind + "|" + System.Math.Min(99, price + 1)), ElementBounds.Fixed(x + 42, y, 36, 28));
                composer.AddSmallButton(Lang.Get(row.On ? "createacolony:market-turn-off" : "createacolony:market-turn-on"),
                    () => SendText(ColonyAction.SetMarketRule, id, kind + "|" + (row.On ? "off" : "on")), ElementBounds.Fixed(x + 90, y, 120, 28));
            }
            y += 32;
            y = composer.Text(Lang.Get("createacolony:market-row-hint-" + kind, row.Sold), GuiKit.Muted(), 10, y, Width - 10, 8);
        }
    }
}
