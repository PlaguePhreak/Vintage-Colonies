using System;
using System.Linq;
using CreateAColony.Colonies;
using CreateAColony.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The Requests tab every hut has (0.142.0): what the hut and its worker asked the couriers for, how much the colony
/// has, and who is bringing it or getting it. And the Courier's Station's Couriers tab: who hauls, what they are
/// doing, and from where to where.
/// </summary>
public partial class GuiDialogHut
{
    const int RequestRowsPerPage = 5;
    const double RequestRowHeight = 66;
    const int CourierRowsPerPage = 4;
    const double CourierRowHeight = 84;

    void ComposeRequests(GuiComposer composer)
    {
        var rows = snapshot.Requests;
        // Give all: everything the viewer carries that this hut asked for (0.167.0).
        bool canGive = ViewerRole >= ColonyRole.Member;
        var givable = canGive ? rows.Where(r => r.PlayerHas > 0 && r.RequestId > 0).ToList() : new System.Collections.Generic.List<RequestView>();
        if (givable.Count > 0)
        {
            string all = string.Join(",", givable.Select(r => r.RequestId));
            composer.AddSmallButton(Lang.Get("createacolony:requests-give-all"), () => SendText(ColonyAction.FillRequests, BuildingId, all),
                ElementBounds.Fixed(Width - 150, 28, 150, 28));
            composer.AddHoverText(Lang.Get("createacolony:requests-give-all-hint"), GuiKit.Detail(), 260, ElementBounds.Fixed(Width - 150, 28, 150, 28));
        }
        double y = composer.Text(Lang.Get(canGive ? "createacolony:requests-hint-give" : "createacolony:requests-hint"), GuiKit.Muted(), 0, 30,
            givable.Count > 0 ? Width - 160 : Width, 8);
        if (givable.Count > 0) y = Math.Max(y, 66);
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:requests-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        int pages = (rows.Count + RequestRowsPerPage - 1) / RequestRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int onPage = Math.Min(RequestRowsPerPage, rows.Count - page * RequestRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, onPage * RequestRowHeight + 8), 3, 0.8f);
        double top = y + 4;
        foreach (var r in rows.Skip(page * RequestRowsPerPage).Take(RequestRowsPerPage))
        {
            double x = 10;
            bool give = canGive && r.PlayerHas > 0 && r.RequestId > 0;
            double textWidth = Width - x - (give ? 70 : 10);
            if (r.Stack.Length > 0)
            {
                var stack = new ItemStack(r.Stack);
                if (stack.ResolveBlockOrItem(capi.World))
                {
                    composer.AddRichtext(new RichTextComponentBase[] { new ItemstackTextComponent(capi, stack, 34, 6) }, ElementBounds.Fixed(6, top + 4, 44, 40));
                    x = 52;
                    textWidth -= 42;
                }
            }
            string title = Lang.Get("createacolony:requests-amount", r.Quantity, r.Name)
                + (r.Extra > 0 ? " " + Lang.Get("createacolony:requests-spare", r.Extra) : "")
                + (r.For.Length > 0 ? " " + Lang.Get("createacolony:requests-for", r.For) : "");
            composer.AddStaticText(title, GuiKit.Body(), ElementBounds.Fixed(x, top + 2, textWidth, 20));
            string counts = Lang.Get("createacolony:requests-counts", r.Delivered, r.Quantity, r.InColony)
                + (r.PlayerHas > 0 ? " " + Lang.Get("createacolony:requests-you-carry", r.PlayerHas) : "");
            composer.AddStaticText(counts, r.PlayerHas > 0 ? GuiKit.Detail().WithColor(GuiKit.GoodColor) : GuiKit.Detail(),
                ElementBounds.Fixed(x, top + 22, textWidth, 18));
            var color = r.Color == 0 ? GuiKit.GoodColor : r.Color == 1 ? GuiKit.WarnColor : MissingColor;
            composer.AddStaticText(r.Status, GuiKit.Detail().WithColor(color), ElementBounds.Fixed(x, top + 40, textWidth, 18));
            if (give)
            {
                string id = r.RequestId.ToString();
                int amount = Math.Min(Math.Max(1, r.Needed), r.PlayerHas);
                composer.AddSmallButton("^", () => SendText(ColonyAction.FillRequests, BuildingId, id), ElementBounds.Fixed(Width - 50, top + 16, 40, 28));
                composer.AddHoverText(Lang.Get("createacolony:requests-give", amount), GuiKit.Detail(), 220, ElementBounds.Fixed(Width - 50, top + 16, 40, 28));
            }
            top += RequestRowHeight;
        }
        AddPager(composer, pages, y + RequestRowsPerPage * RequestRowHeight + 16);
    }

    void ComposeCouriers(GuiComposer composer)
    {
        double y = composer.Text(snapshot.Info, GuiKit.Body(), 0, 30, Width, 4);
        y = composer.Text(Lang.Get("createacolony:couriers-hint"), GuiKit.Muted(), 0, y, Width, 8);
        var rows = snapshot.Couriers;
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:couriers-none"), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width);
            return;
        }
        int pages = (rows.Count + CourierRowsPerPage - 1) / CourierRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int onPage = Math.Min(CourierRowsPerPage, rows.Count - page * CourierRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, onPage * CourierRowHeight + 8), 3, 0.8f);
        double top = y + 4;
        foreach (var c in rows.Skip(page * CourierRowsPerPage).Take(CourierRowsPerPage))
        {
            string name = c.Helping ? Lang.Get("createacolony:couriers-helping", c.Name) : c.Name;
            composer.AddStaticText(name, GuiKit.Body(), ElementBounds.Fixed(10, top + 2, Width - 20, 20));
            composer.AddStaticText(c.Doing, GuiKit.Detail(), ElementBounds.Fixed(10, top + 22, Width - 20, 18));
            string route = c.From.Length > 0 && c.To.Length > 0 ? Lang.Get("createacolony:couriers-route", c.From, c.To)
                : c.To.Length > 0 ? Lang.Get("createacolony:couriers-to", c.To) : "";
            if (route.Length > 0) composer.AddStaticText(route, GuiKit.Detail().WithColor(GuiKit.GoodColor), ElementBounds.Fixed(10, top + 40, Width - 20, 18));
            composer.AddStaticText(c.Carrying.Length > 0 ? Lang.Get("createacolony:couriers-carrying", c.Carrying) : Lang.Get("createacolony:couriers-empty"),
                GuiKit.Muted(), ElementBounds.Fixed(10, top + 58, Width - 20, 18));
            top += CourierRowHeight;
        }
        AddPager(composer, pages, y + CourierRowsPerPage * CourierRowHeight + 16);
    }
}
