using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using CreateAColony.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CreateAColony.Gui;

/// <summary>
/// The window of a hut block. Every hut shows its own level with build and upgrade buttons; the
/// Builder's Hut also lists the colony's work orders and the items its builder still needs, each with
/// a picture and have/need counts. Like the Town Hall window it only asks the server for changes.
/// </summary>
public partial class GuiDialogHut : GuiDialog
{
    const double Width = 560;
    const int OrdersPerPage = 5;
    const double OrderRowHeight = 62;
    const int WorkAreaTab = 3;
    const int PotteryTab = 4;
    const int RecruitsTab = 5;
    const int KitchenTab = 6;
    const double RecruitRowHeight = 62;
    // A Guard Tower's tabs.
    const int ToolsTab = 7;
    const int HostilesTab = 8;
    const int SettingsTab = 9;
    const int MinStockTab = 10;
    const int StatsTab = 11;
    const int ResidentsTab = 12;
    const int OreTab = 13;
    const int QuarryTab = 14;
    /// <summary>A crafter hut's "keep in stock" tab (Smelting, Smithing).</summary>
    const int CraftTab = 15;
    /// <summary>A Miner's Hut's Levels and Guards tabs (its Settings tab shares SettingsTab with the Guard Tower's).</summary>
    const int LevelsTab = 16;
    const int MineGuardsTab = 17;
    /// <summary>An Explorer's or Gatherer's Hut's Markers tab.</summary>
    const int MarkersTab = 18;
    /// <summary>A gathering hut's Allowed to collect tab.</summary>
    const int CollectTab = 19;
    /// <summary>The Crafting recipes tab of huts that are not crafter huts (Forester, Farmer and others).</summary>
    const int RecipesTab = 24;
    /// <summary>Priorities: what a crafting hut keeps, in the order its worker makes it, with buttons to change that.</summary>
    const int PrioritiesTab = 25;
    /// <summary>A Church's Church tab: Faith, services, the tithe and what the church spends it on.</summary>
    const int ChurchTab = 26;
    const int OreRowsPerPage = 6;
    const double OreRowHeight = 44;
    /// <summary>Households shown per page of a Residence's Residents tab.</summary>
    const int HouseholdsPerPage = 3;
    // Pages opened from the Main tab, with a Back button.
    const int ManageView = 20;
    const int ChestView = 21;
    const int HelpView = 22;
    const int ChooseResidentsView = 23;
    /// <summary>Teach recipe, opened from the Crafting recipes tab (GuiDialogHut.Teach.cs).</summary>
    const int TeachView = 27;
    /// <summary>Requests: what the hut asked couriers for (GuiDialogHut.Requests.cs), and a Courier's Station's Couriers tab.</summary>
    const int RequestsTab = 28;
    const int CouriersTab = 29;
    const int ListRowsPerPage = 8;
    const double ListRowHeight = 36;

    readonly ColonyNetwork network;
    HutSnapshot snapshot;
    int currentTab;
    int page;
    bool renaming;
    /// <summary>Remove hut was pressed once; the second press removes it.</summary>
    bool confirmRemove;
    string pendingName = "";
    string hostileSearch = "";
    string collectSearch = "";
    bool settingSearch;
    int statPeriod = 4;

    public int BuildingId => snapshot.Building.BuildingId;

    /// <summary>Set when another hut's window takes this one's place, so closing it doesn't tell the server.</summary>
    public bool Replaced;

    public override string? ToggleKeyCombinationCode => null;

    public GuiDialogHut(ICoreClientAPI capi, ColonyNetwork network, HutSnapshot snapshot) : base(capi)
    {
        this.network = network;
        this.snapshot = snapshot;
        Compose();
    }

    public void Update(HutSnapshot fresh)
    {
        TeachUpdated(fresh);
        snapshot = fresh;
        Compose();
    }

    ColonyRole ViewerRole => (ColonyRole)snapshot.ViewerRole;
    bool Officer => ViewerRole >= ColonyRole.Officer;

    /// <summary>
    /// The tabs down the left side, in MineColonies' order: Main, Crafting recipes, then the hut's own tabs (Work orders,
    /// Levels, Ore...), then the shared ones where the hut has a use for them: Settings, Guards, Required
    /// resources, Minimum stock and Statistics.
    /// </summary>
    List<(int Id, string Lang)> Tabs()
    {
        var tabs = new List<(int, string)> { (0, "tab-main") };
        if (snapshot.IsCourierStation) tabs.Add((CouriersTab, "tab-couriers"));
        if (snapshot.HasRecipes) tabs.Add((RecipesTab, "tab-recipes"));
        if (snapshot.IsBuilderHut) tabs.Add((1, "tab-priorities"));
        if (snapshot.HasPriorities) tabs.Add((PrioritiesTab, "tab-priorities"));
        if (snapshot.MineLevels.Count > 0) tabs.Add((LevelsTab, "tab-levels"));
        if (snapshot.IsGatherHut) tabs.Add((WorkAreaTab, "tab-workarea"));
        if (snapshot.IsPotterHut) tabs.Add((PotteryTab, "tab-pottery"));
        if (snapshot.IsCookHut) tabs.Add((KitchenTab, "tab-kitchen"));
        if (snapshot.CraftTab.Length > 0) tabs.Add((CraftTab, "tab-" + snapshot.CraftTab));
        if (snapshot.IsTavern) tabs.Add((RecruitsTab, "tab-recruits"));
        if (snapshot.IsResidence) tabs.Add((ResidentsTab, "tab-residents"));
        if (snapshot.IsChurch) tabs.Add((ChurchTab, "tab-church"));
        if (snapshot.IsArchive) tabs.Add((ResearchTab, "tab-research"));
        if (snapshot.IsOreHut) tabs.Add((OreTab, "tab-ore"));
        if (snapshot.IsQuarry) tabs.Add((QuarryTab, "tab-quarry"));
        if (snapshot.MarkerGroups.Count > 0) tabs.Add((MarkersTab, "tab-markers"));
        if (snapshot.CollectKeys.Count > 0) tabs.Add((CollectTab, "tab-collect"));
        if (snapshot.IsGuardTower) tabs.AddRange(new[] { (ToolsTab, "tab-tools"), (HostilesTab, "tab-hostiles") });
        // The shared tabs.
        if (snapshot.IsGuardTower || snapshot.IsMiner) tabs.Add((SettingsTab, "tab-settings"));
        if (snapshot.IsMiner && !snapshot.IsMineEntrance) tabs.Add((MineGuardsTab, "tab-guards"));
        if (snapshot.IsBuilderHut || snapshot.IsMiner && !snapshot.IsMineEntrance) tabs.Add((2, "tab-resources"));
        if (snapshot.HasRequestsTab) tabs.Add((RequestsTab, "tab-requests"));
        if (snapshot.HasStorage) tabs.Add((MinStockTab, "tab-minstock"));
        if (snapshot.Job.Length > 0) tabs.Add((StatsTab, "tab-stats"));
        return tabs;
    }

    void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var tabs = Tabs();
        bool subView = currentTab is ManageView or ChestView or HelpView or ChooseResidentsView or TeachView;
        if (!subView && !tabs.Any(t => t.Id == currentTab)) currentTab = 0;
        // Choose residents is opened from the Residents tab, so that tab stays lit; the others belong to Main.
        int activeTab = currentTab == ChooseResidentsView ? ResidentsTab : currentTab == TeachView ? RecipesTab : subView ? 0 : currentTab;

        var composer = capi.Gui.CreateCompo("createacolony-hut", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(snapshot.DisplayName.Length > 0 ? snapshot.DisplayName : Lang.Get("createacolony:building-" + snapshot.Building.Type), () => TryClose());

        if (tabs.Count > 1)
        {
            var guiTabs = tabs.Select(t => new GuiTab { Name = Lang.Get("createacolony:" + t.Lang), DataInt = t.Id, Active = t.Id == activeTab }).ToArray();
            composer.AddVerticalTabs(guiTabs, ElementBounds.Fixed(-160, 35, 160, guiTabs.Length * 30), (id, tab) => { currentTab = tab.DataInt; page = 0; renaming = false; Compose(); }, "tabs");
        }

        composer.BeginChildElements(bgBounds);
        switch (currentTab)
        {
            case 0: ComposeMain(composer); break;
            case 1: ComposeOrders(composer); break;
            case 2: ComposeItems(composer); break;
            case LevelsTab: ComposeLevels(composer); break;
            case MineGuardsTab: ComposeMineGuards(composer); break;
            case WorkAreaTab: ComposeWorkArea(composer); break;
            case PotteryTab: ComposeKeepList(composer, snapshot.Pottery, "pottery", ColonyAction.SetPotteryKeep); break;
            case KitchenTab: ComposeKitchen(composer); break;
            case CraftTab: ComposeKeepList(composer, snapshot.Craft, snapshot.CraftTab, ColonyAction.SetCraftKeep); break;
            case RecipesTab: ComposeRecipes(composer); break;
            case TeachView: ComposeTeach(composer); break;
            case PrioritiesTab: ComposePriorities(composer); break;
            case RecruitsTab: ComposeRecruits(composer); break;
            case ResidentsTab: ComposeResidents(composer); break;
            case ChurchTab: ComposeChurch(composer); break;
            case ResearchTab: ComposeResearch(composer); break;
            case OreTab: ComposeOre(composer); break;
            case QuarryTab: ComposeQuarry(composer); break;
            case MarkersTab: ComposeMarkers(composer); break;
            case CollectTab: ComposeCollect(composer); break;
            case ToolsTab: ComposeTools(composer); break;
            case HostilesTab: ComposeHostiles(composer); break;
            case SettingsTab: if (snapshot.IsMiner) ComposeMineSettings(composer); else ComposeSettings(composer); break;
            case RequestsTab: ComposeRequests(composer); break;
            case CouriersTab: ComposeCouriers(composer); break;
            case MinStockTab: ComposeMinStock(composer); break;
            case StatsTab: ComposeStats(composer); break;
            case ManageView: ComposeManage(composer); break;
            case ChestView: ComposeChest(composer); break;
            case HelpView: ComposeHelp(composer); break;
            case ChooseResidentsView: ComposeChooseResidents(composer); break;
        }
        composer.FitTabs(tabs.Count);
        SingleComposer = composer.EndChildElements().Compose();
        SingleComposer.GetStatbar("supplied")?.SetValues(suppliedPercent, 0, 100);
        SingleComposer.GetStatbar("used")?.SetValues(usedPercent, 0, 100);
        SingleComposer.GetSwitch("anytype")?.SetValue(snapshot.AnyWoodOrStone);
        SingleComposer.GetSwitch("escort")?.SetValue(snapshot.Escort);
        SingleComposer.GetSwitch("mineprospected")?.SetValue(snapshot.MineProspected);
        SingleComposer.GetSwitch("berries")?.SetValue(snapshot.CollectBerries);
        SingleComposer.GetSwitch("retreat")?.SetValue(snapshot.Retreat);
        SingleComposer.GetSwitch("teachany")?.SetValue(teachAnyType);
        for (int i = 0; SingleComposer.GetSwitch("hostile-" + i) is { } toggle; i++) toggle.SetValue(hostileSwitchStates[i]);
        for (int i = 0; SingleComposer.GetSwitch("collect-" + i) is { } toggle; i++) toggle.SetValue(collectSwitchStates[i]);
        for (int i = 0; SingleComposer.GetSwitch("markers-" + i) is { } toggle; i++) toggle.SetValue(!snapshot.MarkersOff.Contains(snapshot.MarkerGroups[i]));
        if (SingleComposer.GetTextInput("buildingname") is { } nameInput) nameInput.SetValue(pendingName);
        if (SingleComposer.GetTextInput("hostilesearch") is { } search)
        {
            settingSearch = true;
            search.SetValue(hostileSearch);
            settingSearch = false;
            if (hostileSearch.Length > 0) SingleComposer.FocusElement(search.TabIndex);
        }
        if (SingleComposer.GetTextInput("collectsearch") is { } collectInput)
        {
            settingSearch = true;
            collectInput.SetValue(collectSearch);
            settingSearch = false;
            if (collectSearch.Length > 0) SingleComposer.FocusElement(collectInput.TabIndex);
        }
    }

    // ---- Main tab -------------------------------------------------------------------------

    /// <summary>
    /// The Main tab every hut has, as in MineColonies: the name and level (with a pencil to rename),
    /// the workers, Manage workers / Recall workers, the build options, how soon couriers come for the
    /// hut's things, and the ? guide, Inventory and storage list buttons.
    /// </summary>
    void ComposeMain(GuiComposer composer)
    {
        var b = snapshot.Building;
        double y = 30;

        // Header: name and level with the pencil, then the level line, the asset pack and any build in progress,
        // each on its own line so a long one wraps instead of running into the next.
        string levelText = snapshot.IsStation
            ? Lang.Get(snapshot.HostName.Length > 0 ? "createacolony:station-level" : "createacolony:station-level-nohost", b.Level, snapshot.HostName)
            : LevelText(b);
        string packText = b.Variant == ""
            ? Lang.Get("createacolony:placement-style", Lang.Get("createacolony:style-" + BlueprintStyles.Normalize(b.Style)))
            : Lang.Get("createacolony:placement-style-variant", Lang.Get("createacolony:style-" + BlueprintStyles.Normalize(b.Style)), GuiKit.VariantName(b.Variant));
        double levelHeight = GuiKit.TextHeight(capi, GuiKit.Detail(), levelText, Width - 20);
        double headerHeight = 40 + levelHeight + 22 + (b.TargetLevel > 0 || b.Removing ? 22 : 0) + 6;
        composer.AddInset(ElementBounds.Fixed(0, y, Width, headerHeight), 3, 0.8f);
        if (renaming)
        {
            composer
                .AddTextInput(ElementBounds.Fixed(10, y + 8, Width - 230, 28), text => pendingName = text, GuiKit.Body(), "buildingname")
                .AddSmallButton(Lang.Get("createacolony:save"), SaveName, ElementBounds.Fixed(Width - 212, y + 8, 100, 28))
                .AddSmallButton(Lang.Get("createacolony:cancel"), () => { renaming = false; Compose(); return true; }, ElementBounds.Fixed(Width - 106, y + 8, 96, 28));
        }
        else
        {
            composer.AddStaticText(Lang.Get("createacolony:hut-name-level", snapshot.DisplayName, b.Level), GuiKit.Heading(), ElementBounds.Fixed(10, y + 8, Width - 60, 26));
            if (Officer)
            {
                composer.AddIconButton("paintbrush", _ => { renaming = true; pendingName = snapshot.DisplayName; Compose(); }, ElementBounds.Fixed(Width - 42, y + 6, 30, 30), "rename");
                composer.AddHoverText(Lang.Get("createacolony:hut-rename"), GuiKit.Detail(), 200, ElementBounds.Fixed(Width - 42, y + 6, 30, 30));
            }
        }
        double line = composer.Text(levelText, GuiKit.Detail(), 10, y + 38, Width - 20, 2);
        composer.AddStaticText(packText, GuiKit.Muted(), ElementBounds.Fixed(10, line, Width - 20, 20));
        if (b.TargetLevel > 0 || b.Removing)
        {
            composer.AddStaticText(StatusText(b), GuiKit.Detail().WithColor(GuiStyle.ActiveButtonTextColor), ElementBounds.Fixed(10, line + 22, Width - 20, 22));
        }
        y += headerHeight + 12;

        if (snapshot.IsMineEntrance) y = ComposeEntranceHuts(composer, y);
        if (snapshot.MineEntranceName.Length > 0)
        {
            y = composer.Text(Lang.Get("createacolony:miner-at-entrance", snapshot.MineEntranceName), GuiKit.Detail(), 0, y, Width, 8);
        }

        // Assigned workers, with Manage workers and Recall workers.
        if (snapshot.Job.Length > 0)
        {
            string job = Lang.Get("createacolony:job-" + snapshot.Job);
            string names = snapshot.Workers.Count == 0 ? Lang.Get("createacolony:hut-workers-none", job)
                : string.Join(", ", snapshot.Workers.Select(w => w.Name));
            y = composer.Text(Lang.Get("createacolony:hut-workers", snapshot.Workers.Count, snapshot.WorkerSlots, names),
                snapshot.Workers.Count == 0 ? GuiKit.Body().WithColor(GuiKit.WarnColor) : GuiKit.Body(), 0, y, Width, 6);
            if (Officer)
            {
                composer
                    .AddSmallButton(Lang.Get("createacolony:hut-manage-workers"), () => Open(ManageView), ElementBounds.Fixed(0, y, 180, 28))
                    .AddSmallButton(Lang.Get("createacolony:hut-recall-workers"), () => Send(ColonyAction.RecallWorkers, b.BuildingId), ElementBounds.Fixed(190, y, 180, 28));
                y += 36;
            }
        }

        // Build options, and Remove hut.
        bool removable = b.Type is not (BuildingTypes.TownHall or BuildingTypes.Warehouse);
        if (Officer && (b.TargetLevel > 0 || b.Removing || b.Level < b.MaxLevel || b.Level > 0 && b.MaxLevel > 0 || removable))
        {
            y = composer.Section(Lang.Get("createacolony:hut-build-options"), y, Width);
            y = AddOrderButtons(composer, b, y);
            if (removable && !b.Removing)
            {
                string label = Lang.Get(confirmRemove ? "createacolony:remove-confirm" : "createacolony:remove-hut");
                composer.AddSmallButton(label, ConfirmRemove, ElementBounds.Fixed(0, y, 220, 28));
                if (confirmRemove) composer.AddSmallButton(Lang.Get("createacolony:cancel"), () => { confirmRemove = false; Compose(); return true; }, ElementBounds.Fixed(230, y, 100, 28));
                y += 34;
                y = composer.Text(Lang.Get(confirmRemove ? "createacolony:remove-hint-sure" : "createacolony:remove-hint"), GuiKit.Muted(), 0, y, Width, 4);
            }
            y += 4;
        }

        // Request system: how soon couriers come for this hut's things.
        if (snapshot.HasStorage)
        {
            y = composer.Section(Lang.Get("createacolony:hut-request-system"), y, Width);
            string priority = snapshot.PickupPriority == 0 ? Lang.Get("createacolony:pickup-never") : Lang.Get("createacolony:pickup-priority-n", snapshot.PickupPriority);
            composer.AddStaticText(Lang.Get("createacolony:pickup-priority"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 150, 25));
            if (Officer)
            {
                int at = snapshot.PickupPriority;
                int id = b.BuildingId;
                composer
                    .AddSmallButton("<", () => SendText(ColonyAction.SetPickupPriority, id, (at - 1).ToString()), ElementBounds.Fixed(150, y, 36, 28))
                    .AddStaticText(priority, GuiKit.Body().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(190, y + 5, 110, 25))
                    .AddSmallButton(">", () => SendText(ColonyAction.SetPickupPriority, id, (at + 1).ToString()), ElementBounds.Fixed(304, y, 36, 28))
                    .AddSmallButton(Lang.Get(snapshot.PickupNow ? "createacolony:pickup-asked" : "createacolony:pickup-now"),
                        () => Send(ColonyAction.PickupNow, id), ElementBounds.Fixed(Width - 190, y, 190, 28));
            }
            else
            {
                composer.AddStaticText(priority, GuiKit.Body(), ElementBounds.Fixed(150, y + 5, 200, 25));
            }
            y += 34;
            y = composer.Text(Lang.Get("createacolony:pickup-hint"), GuiKit.Muted(), 0, y, Width, 8);
        }

        // Footer: guide, inventory, storage list.
        composer.AddSmallButton("?", () => Open(HelpView), ElementBounds.Fixed(0, y, 40, 28));
        if (snapshot.HasStorage && ViewerRole >= ColonyRole.Member)
        {
            composer
                .AddSmallButton(Lang.Get("createacolony:hut-open-storage"), OpenStorage, ElementBounds.Fixed(50, y, 140, 28))
                .AddSmallButton(Lang.Get("createacolony:hut-chest-list", snapshot.Items.Sum(i => i.Count)), () => Open(ChestView), ElementBounds.Fixed(200, y, 180, 28));
        }
        y += 38;

        if (snapshot.Info.Length > 0)
        {
            // A worker's hut shows who works there, what they are doing and how the job works.
            y = composer.Section(Lang.Get("createacolony:hut-status-section"), y, Width);
            y = composer.Rich(snapshot.Info, GuiKit.Detail(), 0, y, Width, 10);
        }

        if (snapshot.IsBuilderHut)
        {
            y = composer.Section(Lang.Get("createacolony:hut-builder-section"), y, Width);
            y = AddAnyTypeSwitch(composer, y);
            composer.Text(Lang.Get("createacolony:build-hint"), GuiKit.Muted(), 0, y, Width);
        }
    }

    /// <summary>The first press asks again; the second sends Remove hut. A hut with nothing built goes at once, so the window closes.</summary>
    bool ConfirmRemove()
    {
        if (!confirmRemove)
        {
            confirmRemove = true;
            Compose();
            return true;
        }
        confirmRemove = false;
        var b = snapshot.Building;
        Send(ColonyAction.RemoveHut, b.BuildingId);
        if (b.Level == 0) TryClose();
        return true;
    }

    /// <summary>A Mineshaft Entrance: its mine, then the Miner's Huts that work it (with Unlink) and the others (with Link).</summary>
    double ComposeEntranceHuts(GuiComposer composer, double y)
    {
        int id = BuildingId;
        if (snapshot.MineInfo.Length > 0) y = composer.Text(snapshot.MineInfo, GuiKit.Detail(), 0, y, Width, 8);
        var linked = snapshot.MineHuts.Where(h => h.Linked).ToList();
        y = composer.Section(Lang.Get("createacolony:entrance-huts", linked.Count), y, Width);
        if (linked.Count == 0) y = composer.Text(Lang.Get("createacolony:entrance-huts-none"), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 8);
        foreach (var h in linked) y = EntranceHutRow(composer, h, id, y);
        var others = snapshot.MineHuts.Where(h => !h.Linked).ToList();
        if (others.Count > 0)
        {
            y = composer.Section(Lang.Get("createacolony:entrance-others"), y + 6, Width);
            foreach (var h in others.Take(ListRowsPerPage)) y = EntranceHutRow(composer, h, id, y);
            y = composer.Text(Lang.Get("createacolony:entrance-link-hint"), GuiKit.Muted(), 0, y + 4, Width, 8);
        }
        return y + 6;
    }

    double EntranceHutRow(GuiComposer composer, MineHutView h, int entranceId, double y)
    {
        string worker = h.Worker.Length > 0 ? Lang.Get("createacolony:entrance-hut-worker", h.Worker) : Lang.Get("createacolony:entrance-hut-noworker");
        if (h.Elsewhere.Length > 0) worker += " " + Lang.Get("createacolony:entrance-hut-elsewhere", h.Elsewhere);
        composer
            .AddStaticText(Lang.Get("createacolony:hut-name-level", h.Name, h.Level), GuiKit.Body(), ElementBounds.Fixed(10, y + 2, Width - 150, 22))
            .AddStaticText(worker, GuiKit.Detail().WithColor(h.Worker.Length > 0 ? GuiKit.MutedColor : GuiKit.WarnColor), ElementBounds.Fixed(10, y + 24, Width - 150, 20));
        if (Officer)
        {
            string hut = h.BuildingId.ToString();
            composer.AddSmallButton(Lang.Get(h.Linked ? "createacolony:entrance-unlink" : "createacolony:entrance-link"),
                () => SendText(h.Linked ? ColonyAction.UnlinkMineHut : ColonyAction.LinkMineHut, entranceId, hut), ElementBounds.Fixed(Width - 130, y + 8, 120, 28));
        }
        return y + 48;
    }

    bool SaveName()
    {
        renaming = false;
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = ColonyAction.RenameBuilding, BuildingId = BuildingId, Text = pendingName });
        return true;
    }

    bool Open(int view)
    {
        currentTab = view;
        page = 0;
        Compose();
        return true;
    }

    /// <summary>A Back button to the Main tab, top right. Returns the y below the page heading.</summary>
    double BackHeading(GuiComposer composer, string title)
    {
        composer
            .AddStaticText(title, GuiKit.Heading(), ElementBounds.Fixed(0, 30, Width - 110, 26))
            .AddSmallButton(Lang.Get("createacolony:back"), () => Open(0), ElementBounds.Fixed(Width - 100, 28, 100, 28));
        return 64;
    }

    /// <summary>Manage workers: who works here (with Remove), and everyone else (with Assign).</summary>
    void ComposeManage(GuiComposer composer)
    {
        double y = BackHeading(composer, Lang.Get("createacolony:hut-manage-workers"));
        string job = Lang.Get("createacolony:job-" + snapshot.Job);
        y = composer.Text(Lang.Get("createacolony:manage-hint", job), GuiKit.Muted(), 0, y, Width, 8);

        y = composer.Section(Lang.Get("createacolony:manage-here", snapshot.Workers.Count, snapshot.WorkerSlots), y, Width);
        if (snapshot.Workers.Count == 0) y = composer.Text(Lang.Get("createacolony:hut-workers-none", job), GuiKit.Detail(), 0, y, Width, 8);
        bool church = snapshot.Building.Type == BuildingTypes.Church;
        bool roles = snapshot.Building.Type == BuildingTypes.Prospector || church;
        string rolePrefix = church ? "createacolony:church-role-" : "createacolony:prospector-role-";
        foreach (var w in snapshot.Workers)
        {
            int id = w.CitizenId;
            if (roles)
            {
                // A prospector finds ore or mines it; a priest can be made the bishop. The button shows the role now and switches it.
                int other = church ? (w.WorkRole == ChurchRoles.Bishop ? ChurchRoles.Priest : ChurchRoles.Bishop)
                    : w.WorkRole == ProspectorRoles.Mine ? ProspectorRoles.Find : ProspectorRoles.Mine;
                composer
                    .AddStaticText(w.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 5, 160, 25))
                    .AddStaticText(SkillText(w), GuiKit.Muted(), ElementBounds.Fixed(175, y + 8, 120, 22))
                    .AddSmallButton(Lang.Get(rolePrefix + w.WorkRole), () => SendCitizenText(ColonyAction.SetWorkRole, id, other.ToString()), ElementBounds.Fixed(Width - 260, y, 120, 28))
                    .AddHoverText(Lang.Get(rolePrefix + "hint"), CairoFont.WhiteDetailText(), 300, ElementBounds.Fixed(Width - 260, y, 120, 28))
                    .AddSmallButton(Lang.Get("createacolony:manage-remove"), () => SendCitizen(ColonyAction.UnassignWorker, id), ElementBounds.Fixed(Width - 130, y, 120, 28));
                y += ListRowHeight;
                continue;
            }
            composer
                .AddStaticText(w.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 5, 220, 25))
                .AddStaticText(SkillText(w), GuiKit.Muted(), ElementBounds.Fixed(240, y + 8, 160, 22))
                .AddSmallButton(Lang.Get("createacolony:manage-remove"), () => SendCitizen(ColonyAction.UnassignWorker, id), ElementBounds.Fixed(Width - 130, y, 120, 28));
            y += ListRowHeight;
        }

        y = composer.Section(Lang.Get("createacolony:manage-others"), y + 6, Width);
        var others = snapshot.Candidates;
        if (others.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:manage-nobody"), GuiKit.Detail(), 0, y, Width);
            return;
        }
        int pages = (others.Count + ListRowsPerPage - 1) / ListRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        bool full = snapshot.Workers.Count >= snapshot.WorkerSlots;
        int shown = Math.Min(ListRowsPerPage, others.Count - page * ListRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * ListRowHeight + 8), 3, 0.8f);
        y += 4;
        foreach (var c in others.Skip(page * ListRowsPerPage).Take(ListRowsPerPage))
        {
            int id = c.CitizenId;
            // Their job now, and how good they already are at this hut's job (green when they know it well).
            string now = Lang.Get("createacolony:job-" + (c.Job.Length == 0 ? "none" : c.Job));
            string skill = SkillText(c);
            composer
                .AddStaticText(c.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 6, 190, 25))
                .AddStaticText(now, GuiKit.Muted(), ElementBounds.Fixed(200, y + 8, 125, 22));
            if (c.Incapable.Length > 0)
            {
                // A trait rules this job out: say which, and no Assign button.
                composer.AddStaticText(Lang.Get("createacolony:skill-incapable"), GuiKit.Muted(), ElementBounds.Fixed(325, y + 8, 100, 22))
                    .AddHoverText(Lang.Get("createacolony:job-incapable-by", Lang.Get("createacolony:trait-" + c.Incapable)), CairoFont.WhiteDetailText(), 260, ElementBounds.Fixed(325, y + 6, 100, 24));
                y += ListRowHeight;
                continue;
            }
            if (skill.Length > 0) composer.AddStaticText(skill, c.SkillLevel >= 3 || c.JobTrait || c.Passion > 0 ? GuiKit.Detail().WithColor(GuiKit.GoodColor) : GuiKit.Muted(), ElementBounds.Fixed(325, y + 8, 100, 22));
            if (!full) composer.AddSmallButton(Lang.Get("createacolony:manage-assign"), () => SendCitizen(ColonyAction.AssignWorker, id), ElementBounds.Fixed(Width - 130, y + 3, 120, 28));
            y += ListRowHeight;
        }
        AddPager(composer, pages, y + 14);
        if (full) composer.Text(Lang.Get("createacolony:manage-full"), GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, y + (pages > 1 ? 50 : 14), Width);
    }

    /// <summary>The chest list: everything in the hut's storage, with a ? that lights up the containers each item is in.</summary>
    void ComposeChest(GuiComposer composer)
    {
        double y = BackHeading(composer, Lang.Get("createacolony:chest-title"));
        var rows = snapshot.Items;
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:chest-empty"), GuiKit.Body(), 0, y, Width);
            return;
        }
        int pages = (rows.Count + ListRowsPerPage - 1) / ListRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(ListRowsPerPage, rows.Count - page * ListRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * 44 + 8), 3, 0.8f);
        y += 4;
        foreach (var row in rows.Skip(page * ListRowsPerPage).Take(ListRowsPerPage))
        {
            var stack = new ItemStack(row.Stack);
            if (!stack.ResolveBlockOrItem(capi.World)) continue;
            composer.AddRichtext(new RichTextComponentBase[]
            {
                new ItemstackTextComponent(capi, stack, 36, 10),
                new RichTextComponent(capi, stack.GetName(), CairoFont.WhiteSmallText()) { VerticalAlign = EnumVerticalAlign.Middle }
            }, ElementBounds.Fixed(6, y, 380, 44));
            composer.AddStaticText(row.Count.ToString(), GuiKit.Body().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - 170, y + 12, 100, 25));
            var where = row.Where;
            composer.AddSmallButton("?", () => Highlight(where), ElementBounds.Fixed(Width - 50, y + 8, 40, 28));
            y += 44;
        }
        AddPager(composer, pages, y + 14);
    }

    const int HighlightSlot = 2387;

    /// <summary>Lights up the containers for a few seconds.</summary>
    bool Highlight(List<BlockPos> where)
    {
        var player = capi.World.Player;
        capi.World.HighlightBlocks(player, HighlightSlot, where.Select(p => p.Copy()).ToList(), where.Select(_ => ColorUtil.ToRgba(160, 255, 200, 60)).ToList());
        capi.Event.RegisterCallback(_ => capi.World.HighlightBlocks(player, HighlightSlot, new List<BlockPos>()), 6000);
        return true;
    }

    /// <summary>The ? guide: how this building works.</summary>
    void ComposeHelp(GuiComposer composer)
    {
        double y = BackHeading(composer, Lang.Get("createacolony:help-title", snapshot.DisplayName));
        string key = "createacolony:guide-" + snapshot.Building.Type;
        string text = Lang.HasTranslation(key) ? Lang.Get(key) : Lang.HasTranslation("createacolony:hint-" + snapshot.Building.Type)
            ? Lang.Get("createacolony:hint-" + snapshot.Building.Type, snapshot.WorkRadius) : Lang.Get("createacolony:guide-none");
        composer.Rich(text, GuiKit.Detail(), 0, y, Width);
    }

    // ---- Miner's Hut tabs ------------------------------------------------------------------

    const int LevelsPerPage = 6;
    const double LevelRowHeight = 50;

    /// <summary>
    /// Levels: each loop of the mineshaft's stairs, with its sections and what is left to dig or build. A
    /// red number marks the level the miner is on. Mine here sends new sections to that level first;
    /// Repair has the miner put the level back as it was before anything else.
    /// </summary>
    void ComposeLevels(GuiComposer composer)
    {
        int id = BuildingId;
        double y = composer.Text(Lang.Get("createacolony:levels-hint"), GuiKit.Muted(), 0, 30, Width, 8);
        string target = snapshot.MineLevel == 0 ? Lang.Get("createacolony:levels-target-any") : Lang.Get("createacolony:levels-target", snapshot.MineLevel);
        composer.AddStaticText(target, GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width - 200, 25));
        if (Officer && snapshot.MineLevel != 0)
        {
            composer.AddSmallButton(Lang.Get("createacolony:levels-any"), () => SendText(ColonyAction.SetMineLevel, id, "0"), ElementBounds.Fixed(Width - 190, y, 190, 28));
        }
        y += 38;

        var rows = snapshot.MineLevels;
        int pages = (rows.Count + LevelsPerPage - 1) / LevelsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(LevelsPerPage, rows.Count - page * LevelsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * LevelRowHeight + 8), 3, 0.8f);
        y += 4;
        int hutY = snapshot.HutPos.Y;
        foreach (var level in rows.Skip(page * LevelsPerPage).Take(LevelsPerPage))
        {
            int number = level.Level;
            var numberFont = GuiKit.Heading().WithColor(level.Working ? MissingColor : GuiStyle.DialogDefaultTextColor);
            composer.AddStaticText(number.ToString(), numberFont.WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(6, y + 10, 34, 28));
            if (level.Working) composer.AddHoverText(Lang.Get("createacolony:levels-working"), GuiKit.Detail(), 200, ElementBounds.Fixed(6, y + 8, 34, 30));
            string title = Lang.Get("createacolony:levels-row", number, Math.Max(0, hutY - level.Y));
            string detail = Lang.Get("createacolony:levels-detail", level.Sections, level.Left);
            composer
                .AddStaticText(title, GuiKit.Body(), ElementBounds.Fixed(48, y + 4, Width - 300, 22))
                .AddStaticText(detail, GuiKit.Detail().WithColor(level.Left == 0 ? GuiKit.GoodColor : GuiKit.MutedColor), ElementBounds.Fixed(48, y + 26, Width - 300, 20));
            // The chosen and repairing levels show a label where their button would be, so the title never wraps.
            var tag = GuiKit.Body().WithOrientation(EnumTextOrientation.Center);
            if (snapshot.MineLevel == number)
                composer.AddStaticText(Lang.Get("createacolony:levels-chosen"), tag.Clone().WithColor(GuiKit.GoodColor), ElementBounds.Fixed(Width - 250, y + 14, 120, 22));
            else if (Officer)
                composer.AddSmallButton(Lang.Get("createacolony:levels-mine-here"), () => SendText(ColonyAction.SetMineLevel, id, number.ToString()), ElementBounds.Fixed(Width - 250, y + 10, 120, 28));
            if (snapshot.RepairLevel == number)
                composer.AddStaticText(Lang.Get("createacolony:levels-repairing"), tag.Clone().WithColor(GuiKit.WarnColor), ElementBounds.Fixed(Width - 120, y + 14, 110, 22));
            else if (Officer)
                composer.AddSmallButton(Lang.Get("createacolony:levels-repair"), () => SendText(ColonyAction.RepairMineLevel, id, number.ToString()), ElementBounds.Fixed(Width - 120, y + 10, 110, 28));
            y += LevelRowHeight;
        }
        AddPager(composer, pages, y + 14);
    }

    /// <summary>Settings of a Miner's Hut: the fill block, how deep the mine goes, and what is not in the mod yet.</summary>
    void ComposeMineSettings(GuiComposer composer)
    {
        int id = BuildingId;
        double y = 30;

        y = composer.Section(Lang.Get("createacolony:minesettings-fill"), y, Width);
        y = AddStringChoice(composer, y, snapshot.FillBlock, ColonyAction.SetFillBlock, MineFillBlocks.All.Select(f => (f, "createacolony:fill-" + f)).ToArray());
        y = composer.Text(Lang.Get("createacolony:minesettings-fill-hint"), GuiKit.Muted(), 0, y, Width, 12);

        y = composer.Section(Lang.Get("createacolony:minesettings-depth"), y, Width);
        int allowed = Math.Max(1, snapshot.MineLevelsAllowed);
        int max = snapshot.MaxMineLevels <= 0 ? allowed : Math.Min(snapshot.MaxMineLevels, allowed);
        string depth = snapshot.MaxMineLevels <= 0 ? Lang.Get("createacolony:minesettings-depth-all", allowed) : Lang.Get("createacolony:minesettings-depth-n", max, allowed);
        if (Officer)
        {
            composer
                .AddSmallButton("<", () => SendText(ColonyAction.SetMaxMineLevels, id, Math.Max(1, max - 1).ToString()), ElementBounds.Fixed(0, y, 36, 28))
                .AddStaticText(depth, GuiKit.Body().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(40, y + 4, 300, 25))
                .AddSmallButton(">", () => SendText(ColonyAction.SetMaxMineLevels, id, (max + 1 >= allowed ? 0 : max + 1).ToString()), ElementBounds.Fixed(344, y, 36, 28));
            y += 36;
        }
        else
        {
            y = composer.Text(depth, GuiKit.Body(), 0, y + 4, Width, 6);
        }
        y = composer.Text(Lang.Get("createacolony:minesettings-depth-hint"), GuiKit.Muted(), 0, y, Width, 12);

        y = composer.Section(Lang.Get("createacolony:settings-recipe-mode"), y, Width);
        composer.Text(Lang.Get("createacolony:settings-recipe-mode-later"), GuiKit.Muted(), 0, y, Width);
    }

    /// <summary>A row of buttons for a setting kept as text, the chosen one marked; plain text for non-officers. Returns the y below it.</summary>
    double AddStringChoice(GuiComposer composer, double y, string current, ColonyAction action, (string Value, string Lang)[] choices)
    {
        if (!Officer)
        {
            string chosen = choices.FirstOrDefault(c => c.Value == current).Lang ?? choices[0].Lang;
            return composer.Text(Lang.Get(chosen), GuiKit.Body(), 0, y + 4, Width, 6);
        }
        double x = 0;
        double width = Math.Min(170, (Width - 10 * (choices.Length - 1)) / choices.Length);
        int id = BuildingId;
        foreach (var (value, key) in choices)
        {
            string label = (value == current ? "\u2022 " : "") + Lang.Get(key);
            string v = value;
            composer.AddSmallButton(label, () => SendText(action, id, v), ElementBounds.Fixed(x, y, width, 28), value == current ? EnumButtonStyle.Normal : EnumButtonStyle.Small);
            x += width + 10;
        }
        return y + 36;
    }

    /// <summary>
    /// Guards: guards who go with this mine's miner and fight what comes for it. Only guards of Guard Towers
    /// set to Patrol mine (on the tower's Settings tab) can be taken on; one per hut level.
    /// </summary>
    void ComposeMineGuards(GuiComposer composer)
    {
        double y = composer.Text(Lang.Get("createacolony:mineguards-hint"), GuiKit.Muted(), 0, 30, Width, 8);
        y = composer.Section(Lang.Get("createacolony:mineguards-here", snapshot.MineGuards.Count, snapshot.MineGuardSlots), y, Width);
        if (snapshot.MineGuards.Count == 0) y = composer.Text(Lang.Get("createacolony:mineguards-none"), GuiKit.Detail(), 0, y, Width, 8);
        foreach (var g in snapshot.MineGuards)
        {
            int cid = g.CitizenId;
            composer.AddStaticText(g.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 5, Width - 150, 25));
            if (Officer) composer.AddSmallButton(Lang.Get("createacolony:manage-remove"), () => SendCitizen(ColonyAction.UnassignMineGuard, cid), ElementBounds.Fixed(Width - 130, y, 120, 28));
            y += ListRowHeight;
        }

        y = composer.Section(Lang.Get("createacolony:mineguards-others"), y + 6, Width);
        var others = snapshot.MineGuardChoices;
        if (others.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:mineguards-nobody"), GuiKit.Detail(), 0, y, Width);
            return;
        }
        int pages = (others.Count + ListRowsPerPage - 1) / ListRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        bool full = snapshot.MineGuards.Count >= snapshot.MineGuardSlots;
        foreach (var g in others.Skip(page * ListRowsPerPage).Take(ListRowsPerPage))
        {
            int cid = g.CitizenId;
            composer.AddStaticText(g.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 5, 200, 25));
            if (g.Job.Length > 0) composer.AddStaticText(Lang.Get("createacolony:mineguards-at", g.Job), GuiKit.Muted(), ElementBounds.Fixed(210, y + 8, Width - 350, 22));
            if (Officer && !full) composer.AddSmallButton(Lang.Get("createacolony:manage-assign"), () => SendCitizen(ColonyAction.AssignMineGuard, cid), ElementBounds.Fixed(Width - 130, y, 120, 28));
            y += ListRowHeight;
        }
        AddPager(composer, pages, y + 14);
        if (Officer && full) composer.Text(Lang.Get("createacolony:mineguards-full"), GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, y + (pages > 1 ? 50 : 14), Width);
    }

    // ---- Guard Tower tabs ------------------------------------------------------------------

    /// <summary>Selection tools: get a Guard Scepter, and see the guard spot or patrol points set with it.</summary>
    void ComposeTools(GuiComposer composer)
    {
        int id = BuildingId;
        double y = composer.Text(Lang.Get("createacolony:tools-hint"), GuiKit.Detail(), 0, 30, Width, 10);
        if (Officer)
        {
            composer.AddSmallButton(Lang.Get("createacolony:tools-obtain"), () => Send(ColonyAction.GetGuardScepter, id), ElementBounds.Fixed(0, y, 200, 28));
            y += 38;
        }
        string mode = snapshot.GuardTask switch
        {
            GuardTasks.Patrol => Lang.Get("createacolony:tools-mode-patrol"),
            GuardTasks.Follow => Lang.Get("createacolony:tools-mode-follow"),
            _ => Lang.Get("createacolony:tools-mode-guard")
        };
        y = composer.Text(mode, GuiKit.Body().WithColor(GuiStyle.ActiveButtonTextColor), 0, y, Width, 10);

        y = composer.Section(Lang.Get("createacolony:tools-guard-spot"), y, Width);
        y = composer.Text(snapshot.GuardSpot == null ? Lang.Get("createacolony:tools-at-tower") : Lang.Get("createacolony:tools-spot", Where(snapshot.GuardSpot)),
            GuiKit.Detail(), 0, y, Width, 8);

        y = composer.Section(Lang.Get("createacolony:tools-patrol-points", snapshot.PatrolPoints.Count), y, Width);
        if (snapshot.PatrolPoints.Count == 0)
        {
            y = composer.Text(Lang.Get("createacolony:tools-no-points"), GuiKit.Detail(), 0, y, Width, 8);
        }
        else
        {
            string points = string.Join("<br>", snapshot.PatrolPoints.Select((p, i) => Lang.Get("createacolony:tools-point", i + 1, Where(p))));
            y = composer.Rich(points, GuiKit.Detail(), 0, y, Width, 8);
        }
        if (Officer && (snapshot.PatrolPoints.Count > 0 || snapshot.GuardSpot != null))
        {
            composer.AddSmallButton(Lang.Get("createacolony:tools-clear"), () => Send(ColonyAction.ClearGuardPoints, id), ElementBounds.Fixed(0, y, 200, 28));
        }
    }

    /// <summary>A position as "5 east, 3 north of the tower".</summary>
    string Where(BlockPos p)
    {
        int dx = p.X - snapshot.HutPos.X, dz = p.Z - snapshot.HutPos.Z, dy = p.Y - snapshot.HutPos.Y;
        var parts = new List<string>();
        if (dx != 0) parts.Add(Lang.Get(dx > 0 ? "createacolony:dir-east" : "createacolony:dir-west", Math.Abs(dx)));
        if (dz != 0) parts.Add(Lang.Get(dz > 0 ? "createacolony:dir-south" : "createacolony:dir-north", Math.Abs(dz)));
        if (dy != 0) parts.Add(Lang.Get(dy > 0 ? "createacolony:dir-up" : "createacolony:dir-down", Math.Abs(dy)));
        return parts.Count == 0 ? Lang.Get("createacolony:dir-here") : Lang.Get("createacolony:dir-of-tower", string.Join(", ", parts));
    }

    readonly List<bool> hostileSwitchStates = new();

    /// <summary>Hostiles: every creature guards fight, each with a switch; a search box and Reset to default.</summary>
    void ComposeHostiles(GuiComposer composer)
    {
        int id = BuildingId;
        double y = composer.Text(Lang.Get("createacolony:hostiles-hint"), GuiKit.Muted(), 0, 30, Width, 8);
        composer.AddTextInput(ElementBounds.Fixed(0, y, Width - 200, 28), text =>
        {
            if (settingSearch || text == hostileSearch) return;
            hostileSearch = text;
            page = 0;
            Compose();
        }, GuiKit.Body(), "hostilesearch");
        if (Officer) composer.AddSmallButton(Lang.Get("createacolony:hostiles-reset"), () => SendText(ColonyAction.SetHostile, id, "*"), ElementBounds.Fixed(Width - 190, y, 190, 28));
        y += 38;

        var rows = snapshot.Hostiles
            .Select(code => (Code: code, Name: CreatureName(code)))
            .Where(r => hostileSearch.Length == 0 || r.Name.IndexOf(hostileSearch, StringComparison.OrdinalIgnoreCase) >= 0
                || r.Code.IndexOf(hostileSearch, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(r => r.Name).ToList();
        hostileSwitchStates.Clear();
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:hostiles-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        int pages = (rows.Count + ListRowsPerPage - 1) / ListRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(ListRowsPerPage, rows.Count - page * ListRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * ListRowHeight + 8), 3, 0.8f);
        y += 4;
        int i = 0;
        foreach (var row in rows.Skip(page * ListRowsPerPage).Take(ListRowsPerPage))
        {
            bool on = !snapshot.HostilesOff.Contains(row.Code);
            string code = row.Code;
            if (Officer)
            {
                composer.AddSwitch(value => SendText(ColonyAction.SetHostile, id, code + "|" + (value ? "1" : "0")), ElementBounds.Fixed(10, y + 3, 30, 30), "hostile-" + i);
                hostileSwitchStates.Add(on);
                i++;
            }
            composer
                .AddStaticText(row.Name, GuiKit.Body(), ElementBounds.Fixed(50, y + 7, Width - 200, 25))
                .AddStaticText(Lang.Get(on ? "createacolony:hostile-attack" : "createacolony:hostile-ignore"),
                    GuiKit.Detail().WithColor(on ? GuiKit.GoodColor : GuiKit.MutedColor).WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - 160, y + 9, 150, 22));
            y += ListRowHeight;
        }
        AddPager(composer, pages, y + 14);
    }

    /// <summary>
    /// The Markers tab of an Explorer's or Gatherer's Hut: which kinds of thing its worker puts on the "Colony
    /// markers" map layer, and a button to take its markers away again.
    /// </summary>
    void ComposeMarkers(GuiComposer composer)
    {
        int id = BuildingId;
        double y = 30;
        if (snapshot.GatherMode.Length > 0)
        {
            y = composer.Section(Lang.Get("createacolony:gathermode"), y, Width);
            y = AddStringChoice(composer, y, snapshot.GatherMode, ColonyAction.SetGatherMode, GatherModes.All.Select(m => (m, "createacolony:gathermode-" + m)).ToArray());
            y = composer.Text(Lang.Get("createacolony:gathermode-hint-" + snapshot.GatherMode), GuiKit.Muted(), 0, y, Width, 12);
            y = composer.Section(Lang.Get("createacolony:markers-title"), y, Width);
        }
        y = composer.Text(Lang.Get("createacolony:markers-hint", snapshot.MarkerCount), GuiKit.Muted(), 0, y, Width, 8);
        if (Officer)
        {
            composer.AddSmallButton(Lang.Get("createacolony:markers-remove"), () => SendText(ColonyAction.RemoveMarkers, id, ""), ElementBounds.Fixed(0, y, 220, 28));
            y += 38;
            if (snapshot.RemovedMarkerCount > 0)
            {
                y = composer.Text(Lang.Get("createacolony:markers-removed-hint", snapshot.RemovedMarkerCount), GuiKit.Muted(), 0, y, Width, 4);
                composer.AddSmallButton(Lang.Get("createacolony:markers-removed-again"), () => SendText(ColonyAction.MarkRemovedAgain, id, ""), ElementBounds.Fixed(0, y, 220, 28));
                y += 38;
            }
        }
        y = composer.Section(Lang.Get("createacolony:markers-groups"), y, Width);
        for (int i = 0; i < snapshot.MarkerGroups.Count; i++)
        {
            string group = snapshot.MarkerGroups[i];
            bool on = !snapshot.MarkersOff.Contains(group);
            string name = Lang.Get("createacolony:markergroup-" + group);
            if (Officer)
            {
                composer
                    .AddSwitch(value => SendText(ColonyAction.SetMarkerGroup, id, group + "|" + (value ? "1" : "0")), ElementBounds.Fixed(0, y, 30, 30), "markers-" + i)
                    .AddStaticText(name, GuiKit.Body(), ElementBounds.Fixed(40, y + 5, Width - 40, 25));
            }
            else
            {
                composer.AddStaticText(name + ": " + Lang.Get(on ? "createacolony:on" : "createacolony:off"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width, 25));
            }
            y += 34;
        }
    }

    readonly List<bool> collectSwitchStates = new();

    /// <summary>
    /// The Allowed to collect tab of a gathering hut: every tree, fruit, mushroom, ore, rock or kind of ground
    /// its worker can take, each with a switch. All are on until an officer turns one off.
    /// </summary>
    void ComposeCollect(GuiComposer composer)
    {
        int id = BuildingId;
        double y = composer.Text(Lang.Get("createacolony:collect-hint-" + snapshot.Building.Type), GuiKit.Muted(), 0, 30, Width, 8);
        composer.AddTextInput(ElementBounds.Fixed(0, y, Width - 250, 28), text =>
        {
            if (settingSearch || text == collectSearch) return;
            collectSearch = text;
            page = 0;
            Compose();
        }, GuiKit.Body(), "collectsearch");
        if (Officer)
        {
            composer
                .AddSmallButton(Lang.Get("createacolony:collect-all-on"), () => SendText(ColonyAction.SetCollect, id, "*"), ElementBounds.Fixed(Width - 240, y, 115, 28))
                .AddSmallButton(Lang.Get("createacolony:collect-all-off"), () => SendText(ColonyAction.SetCollect, id, "-"), ElementBounds.Fixed(Width - 115, y, 115, 28));
        }
        y += 38;

        var rows = new List<(string Key, ItemStack Stack, string Name)>();
        for (int k = 0; k < snapshot.CollectKeys.Count && k < snapshot.CollectStacks.Count; k++)
        {
            var stack = new ItemStack(snapshot.CollectStacks[k]);
            if (!stack.ResolveBlockOrItem(capi.World)) continue;
            string key = snapshot.CollectKeys[k];
            string name = CollectName(key, stack);
            if (collectSearch.Length > 0 && name.IndexOf(collectSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add((key, stack, name));
        }
        collectSwitchStates.Clear();
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:collect-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        int pages = (rows.Count + ListRowsPerPage - 1) / ListRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(ListRowsPerPage, rows.Count - page * ListRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * ListRowHeight + 8), 3, 0.8f);
        y += 4;
        int i = 0;
        foreach (var row in rows.Skip(page * ListRowsPerPage).Take(ListRowsPerPage))
        {
            bool on = !snapshot.CollectOff.Contains(row.Key);
            string key = row.Key;
            if (Officer)
            {
                composer.AddSwitch(value => SendText(ColonyAction.SetCollect, id, key + "|" + (value ? "1" : "0")), ElementBounds.Fixed(10, y + 3, 30, 30), "collect-" + i);
                collectSwitchStates.Add(on);
                i++;
            }
            composer
                .AddRichtext(new RichTextComponentBase[]
                {
                    new ItemstackTextComponent(capi, row.Stack, 30, 6),
                    new RichTextComponent(capi, row.Name, on ? GuiKit.Body() : GuiKit.Body().WithColor(GuiKit.MutedColor)) { VerticalAlign = EnumVerticalAlign.Middle }
                }, ElementBounds.Fixed(50, y + 1, Width - 210, ListRowHeight - 2))
                .AddStaticText(Lang.Get(on ? "createacolony:collect-yes" : "createacolony:collect-no"),
                    GuiKit.Detail().WithColor(on ? GuiKit.GoodColor : GuiKit.MutedColor).WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - 160, y + 9, 150, 22));
            y += ListRowHeight;
        }
        AddPager(composer, pages, y + 14);
    }

    /// <summary>A row's name: the ore or kind of ground in words, a tree by its wood, otherwise the name of its picture (a fruit, a mushroom, a rock).</summary>
    static string CollectName(string key, ItemStack stack)
    {
        if (key.StartsWith("ore-") && Lang.HasTranslation(key)) return Lang.Get(key);
        if (key.StartsWith("animal-")) return CreatureName(key.Substring("animal-".Length));
        if (key.StartsWith("wood-")) return TreeName(key.Substring("wood-".Length), stack);
        // A fruit tree's picture is its fruit: "Cherry tree".
        if (key.StartsWith("fruittree")) return Lang.Get("createacolony:collect-fruittree", stack.GetName());
        if (Lang.HasTranslation("createacolony:collect-" + key)) return Lang.Get("createacolony:collect-" + key);
        return stack.GetName();
    }

    /// <summary>A tree's name from its wood, such as "Oak" or "Baldcypress", rather than its log's name.</summary>
    static string TreeName(string wood, ItemStack log)
    {
        foreach (string key in new[] { log.Collectible.Code.Domain + ":material-" + wood, "material-" + wood, "createacolony:tree-" + wood })
        {
            if (Lang.HasTranslation(key)) return Lang.Get(key);
        }
        // A wood no language file names: its code in words.
        string words = wood.Replace('_', ' ').Replace('-', ' ');
        return words.Length == 0 ? log.GetName() : char.ToUpperInvariant(words[0]) + words[1..];
    }

    static string CreatureName(string code)
    {
        string key = "item-creature-" + code;
        return Lang.HasTranslation(key) ? Lang.Get(key) : code;
    }

    /// <summary>Settings: the task (Patrol, Guard, Follow), retreat, hire trainee, patrol mode and follow mode.</summary>
    void ComposeSettings(GuiComposer composer)
    {
        int id = BuildingId;
        double y = 30;
        y = composer.Section(Lang.Get("createacolony:settings-task"), y, Width);
        var tasks = new List<(int, string)> { (GuardTasks.Patrol, "createacolony:task-patrol"), (GuardTasks.Guard, "createacolony:task-guard"), (GuardTasks.Follow, "createacolony:task-follow") };
        if (snapshot.Building.Type == BuildingTypes.GuardTower) tasks.Add((GuardTasks.PatrolMine, "createacolony:task-patrolmine"));
        y = AddChoice(composer, y, snapshot.GuardTask, ColonyAction.SetGuardTask, tasks.ToArray());
        string taskHint = snapshot.GuardTask switch
        {
            GuardTasks.PatrolMine => Lang.Get("createacolony:task-patrolmine-hint"),
            GuardTasks.Patrol => Lang.Get("createacolony:task-patrol-hint"),
            GuardTasks.Follow => Lang.Get("createacolony:task-follow-hint", snapshot.FollowName.Length > 0 ? snapshot.FollowName : "?"),
            _ => Lang.Get("createacolony:task-guard-hint")
        };
        y = composer.Text(taskHint, GuiKit.Muted(), 0, y, Width, 6);
        if (Officer && snapshot.GuardTask == GuardTasks.Guard)
        {
            composer.AddSmallButton(Lang.Get("createacolony:settings-set-target"), () => Open(ToolsTab), ElementBounds.Fixed(0, y, 180, 28));
            y += 36;
        }

        if (snapshot.GuardTask == GuardTasks.Patrol)
        {
            y = composer.Section(Lang.Get("createacolony:settings-patrol-mode"), y + 4, Width);
            y = AddChoice(composer, y, snapshot.PatrolManual ? 1 : 0, ColonyAction.SetPatrolManual, new[] { (0, "createacolony:patrol-auto"), (1, "createacolony:patrol-manual") });
            y = composer.Text(Lang.Get(snapshot.PatrolManual ? "createacolony:patrol-manual-hint" : "createacolony:patrol-auto-hint"), GuiKit.Muted(), 0, y, Width, 6);
            if (Officer && snapshot.PatrolManual)
            {
                composer.AddSmallButton(Lang.Get("createacolony:settings-set-positions"), () => Open(ToolsTab), ElementBounds.Fixed(0, y, 180, 28));
                y += 36;
            }
        }

        if (snapshot.GuardTask == GuardTasks.Follow)
        {
            y = composer.Section(Lang.Get("createacolony:settings-follow-mode"), y + 4, Width);
            y = AddChoice(composer, y, snapshot.FollowTight ? 1 : 0, ColonyAction.SetFollowTight, new[] { (0, "createacolony:follow-loose"), (1, "createacolony:follow-tight") });
        }

        y = composer.Section(Lang.Get("createacolony:settings-retreat"), y + 4, Width);
        y = AddSetting(composer, "retreat", ColonyAction.SetRetreat, snapshot.Retreat, y);
        y = composer.Text(Lang.Get("createacolony:settings-retreat-hint"), GuiKit.Muted(), 0, y, Width, 10);

        y = composer.Section(Lang.Get("createacolony:settings-trainee"), y, Width);
        composer.Text(Lang.Get("createacolony:settings-trainee-later"), GuiKit.Muted(), 0, y, Width);
    }

    /// <summary>A row of buttons, one per choice, the chosen one marked; plain text for non-officers. Returns the y below it.</summary>
    double AddChoice(GuiComposer composer, double y, int current, ColonyAction action, (int Value, string Lang)[] choices)
    {
        if (!Officer)
        {
            string chosen = choices.FirstOrDefault(c => c.Value == current).Lang ?? choices[0].Lang;
            return composer.Text(Lang.Get(chosen), GuiKit.Body(), 0, y + 4, Width, 6);
        }
        double x = 0;
        double width = Math.Min(170, (Width - 10 * (choices.Length - 1)) / choices.Length);
        int id = BuildingId;
        foreach (var (value, key) in choices)
        {
            string label = (value == current ? "\u2022 " : "") + Lang.Get(key);
            int v = value;
            composer.AddSmallButton(label, () => SendText(action, id, v.ToString()), ElementBounds.Fixed(x, y, width, 28), value == current ? EnumButtonStyle.Normal : EnumButtonStyle.Small);
            x += width + 10;
        }
        return y + 36;
    }

    /// <summary>Minimum stock: items couriers keep at least this many of in the tower, and Add held item.</summary>
    void ComposeMinStock(GuiComposer composer)
    {
        int id = BuildingId;
        double y = composer.Text(Lang.Get("createacolony:minstock-hint"), GuiKit.Muted(), 0, 30, Width, 8);
        var rows = snapshot.MinStock;
        if (rows.Count == 0)
        {
            y = composer.Text(Lang.Get("createacolony:minstock-none"), GuiKit.Body(), 0, y, Width, 10);
        }
        else
        {
            int pages = (rows.Count + ListRowsPerPage - 1) / ListRowsPerPage;
            page = Math.Clamp(page, 0, pages - 1);
            int shown = Math.Min(ListRowsPerPage, rows.Count - page * ListRowsPerPage);
            composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * 44 + 8), 3, 0.8f);
            y += 4;
            foreach (var row in rows.Skip(page * ListRowsPerPage).Take(ListRowsPerPage))
            {
                var stack = new ItemStack(row.Stack);
                if (!stack.ResolveBlockOrItem(capi.World)) continue;
                composer.AddRichtext(new RichTextComponentBase[]
                {
                    new ItemstackTextComponent(capi, stack, 36, 10),
                    new RichTextComponent(capi, stack.GetName(), CairoFont.WhiteSmallText()) { VerticalAlign = EnumVerticalAlign.Middle }
                }, ElementBounds.Fixed(6, y, 260, 44));
                string code = row.Code;
                int count = row.Count;
                string keep = Lang.Get("createacolony:minstock-keep", count);
                if (Officer)
                {
                    composer
                        .AddSmallButton("<", () => SendText(ColonyAction.SetMinStock, id, code + "|" + Math.Max(1, count / 2)), ElementBounds.Fixed(Width - 290, y + 8, 36, 28))
                        .AddStaticText(keep, GuiKit.Body().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(Width - 250, y + 12, 100, 25))
                        .AddSmallButton(">", () => SendText(ColonyAction.SetMinStock, id, code + "|" + Math.Min(999, count * 2)), ElementBounds.Fixed(Width - 146, y + 8, 36, 28))
                        .AddSmallButton(Lang.Get("createacolony:minstock-remove"), () => SendText(ColonyAction.SetMinStock, id, code + "|0"), ElementBounds.Fixed(Width - 100, y + 8, 90, 28));
                }
                else
                {
                    composer.AddStaticText(keep, GuiKit.Body().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - 210, y + 12, 200, 25));
                }
                y += 44;
            }
            AddPager(composer, pages, y + 10);
            y += pages > 1 ? 50 : 14;
        }
        if (Officer) composer.AddSmallButton(Lang.Get("createacolony:minstock-add"), () => Send(ColonyAction.AddMinStock, id), ElementBounds.Fixed(0, y, 220, 28));
    }

    internal static readonly string[] StatPeriods = { "today", "yesterday", "week", "hundred", "alltime" };

    /// <summary>Statistics: counts such as monsters killed, for today, yesterday, the last week, the last 100 days or all time.</summary>
    void ComposeStats(GuiComposer composer)
    {
        double y = 30;
        double x = 0;
        for (int i = 0; i < StatPeriods.Length; i++)
        {
            int period = i;
            string label = (i == statPeriod ? "\u2022 " : "") + Lang.Get("createacolony:stats-" + StatPeriods[i]);
            composer.AddSmallButton(label, () => { statPeriod = period; Compose(); return true; }, ElementBounds.Fixed(x, y, 104, 28),
                i == statPeriod ? EnumButtonStyle.Normal : EnumButtonStyle.Small);
            x += 112;
        }
        y += 40;
        var rows = snapshot.Stats.Where(s => s.Counts.Length > statPeriod && s.Counts[statPeriod] > 0).ToList();
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:stats-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        composer.AddInset(ElementBounds.Fixed(0, y, Width, rows.Count * 24 + 12), 3, 0.8f);
        y += 6;
        foreach (var row in rows.OrderBy(r => r.Key.StartsWith("kill:") ? 0 : 1).ThenByDescending(r => r.Counts[statPeriod]))
        {
            y = composer.Row(StatLabel(row.Key), row.Counts[statPeriod].ToString(), 10, y, Width - 20);
        }
    }

    internal static string StatLabel(string key) => key.StartsWith("kill:")
        ? Lang.Get("createacolony:stat-kill", CreatureName(key[5..]))
        : Lang.Get("createacolony:stat-" + key);

    bool SendText(ColonyAction action, int buildingId, string text)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = action, BuildingId = buildingId, Text = text });
        return true;
    }

    bool SendCitizenText(ColonyAction action, int citizenId, string text)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = action, BuildingId = BuildingId, CitizenId = citizenId, Text = text });
        return true;
    }

    bool SendCitizen(ColonyAction action, int citizenId)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = action, BuildingId = BuildingId, CitizenId = citizenId });
        return true;
    }

    /// <summary>
    /// The Work area tab of a Forester's or Farmer's Hut: how far out its worker goes, whether a guard
    /// goes along, and for a farmer whether it collects berries and fruit. Officers change them.
    /// </summary>
    void ComposeWorkArea(GuiComposer composer)
    {
        bool officer = ViewerRole >= ColonyRole.Officer;
        bool farmer = snapshot.Building.Type == BuildingTypes.Farmer;
        string job = Lang.Get("createacolony:job-" + snapshot.Job);
        double y = 30;

        // The Quarry digs right beside itself, so it has no range to set.
        if (snapshot.Building.Type != BuildingTypes.Quarry) y = ComposeWorkRange(composer, job, officer, y);

        y = composer.Section(Lang.Get("createacolony:workarea-escort"), y, Width);
        y = AddSetting(composer, "escort", ColonyAction.SetEscort, snapshot.Escort, y);
        string guard = !snapshot.Escort ? Lang.Get("createacolony:workarea-escort-hint")
            : snapshot.GuardName.Length > 0 ? Lang.Get("createacolony:workarea-escort-by", snapshot.GuardName)
            : Lang.Get("createacolony:workarea-escort-none", snapshot.GuardCount);
        y = composer.Text(guard, snapshot.Escort && snapshot.GuardName.Length == 0 ? GuiKit.Detail().WithColor(GuiKit.WarnColor) : GuiKit.Muted(), 0, y, Width, 14);

        if (farmer)
        {
            y = composer.Section(Lang.Get("createacolony:workarea-berries"), y, Width);
            y = AddSetting(composer, "berries", ColonyAction.SetCollectBerries, snapshot.CollectBerries, y);
            composer.Text(Lang.Get("createacolony:workarea-berries-hint"), GuiKit.Muted(), 0, y, Width);
        }
    }

    /// <summary>The Work area tab's range: how far out the worker goes, with buttons for officers.</summary>
    double ComposeWorkRange(GuiComposer composer, string job, bool officer, double y)
    {
        y = composer.Section(Lang.Get("createacolony:workarea-range", job), y, Width);
        string range = Lang.Get("createacolony:workarea-blocks", snapshot.WorkRadius);
        if (officer)
        {
            var choices = snapshot.RadiusChoices;
            int at = Math.Max(0, choices.IndexOf(snapshot.WorkRadius));
            composer
                .AddSmallButton("<", () => SetRadius(choices[Math.Max(0, at - 1)]), ElementBounds.Fixed(0, y, 40, 28))
                .AddStaticText(range, GuiKit.Body().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(45, y + 4, 130, 25))
                .AddSmallButton(">", () => SetRadius(choices[Math.Min(choices.Count - 1, at + 1)]), ElementBounds.Fixed(180, y, 40, 28));
        }
        else
        {
            composer.AddStaticText(range, GuiKit.Body(), ElementBounds.Fixed(0, y + 4, Width, 25));
        }
        y += 36;
        if (snapshot.WorkRadius > 128) y = composer.Text(Lang.Get("createacolony:workarea-far"), GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, y, Width, 8);
        y = composer.Text(Lang.Get("createacolony:workarea-range-hint-" + snapshot.Building.Type), GuiKit.Muted(), 0, y, Width, 14);
        return y;
    }

    /// <summary>
    /// The Ore tab of a Prospector's or Miner's Hut: every ore the colony's prospectors found, strongest
    /// first, with how strong the best reading was and where. At a Miner's Hut officers choose which ore
    /// the miner digs for, or leave it to dig the best one found.
    /// </summary>
    void ComposeOre(GuiComposer composer)
    {
        bool miner = snapshot.Building.Type == BuildingTypes.Miner;
        // Both huts choose the ore they dig: the miner, and a Prospector's Hut's prospectors on Mine ore.
        bool chooser = miner || snapshot.Building.Type == BuildingTypes.Prospector;
        bool officer = Officer && chooser;
        double y = composer.Text(Lang.Get(miner ? "createacolony:ore-hint-miner" : "createacolony:ore-hint-prospector", snapshot.OreReadings), GuiKit.Muted(), 0, 30, Width, 10);
        if (chooser)
        {
            y = composer.Section(Lang.Get(miner ? "createacolony:ore-trips" : "createacolony:ore-prospector-mining"), y, Width);
            if (miner) y = AddSetting(composer, "mineprospected", ColonyAction.SetMineProspected, snapshot.MineProspected, y);
            y = composer.Text(Lang.Get(miner ? "createacolony:ore-trips-hint" : "createacolony:ore-prospector-mining-hint"), GuiKit.Muted(), 0, y, Width, 10);
            string choice = snapshot.MineOre.Length == 0 ? Lang.Get("createacolony:mine-any-ore") : Lang.Get("ore-" + snapshot.MineOre);
            composer.AddStaticText(Lang.Get("createacolony:ore-digging-for", choice), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width - 300, 25));
            if (officer)
            {
                int id = BuildingId;
                composer
                    .AddSmallButton(Lang.Get("createacolony:ore-any"), () => SendText(ColonyAction.SetMineOre, id, ""), ElementBounds.Fixed(Width - 290, y, 140, 28))
                    .AddSmallButton(Lang.Get("createacolony:ore-new-mine"), () => Send(ColonyAction.NewMine, id), ElementBounds.Fixed(Width - 140, y, 140, 28));
            }
            y += 38;
        }
        if (snapshot.Ores.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:ore-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        // The ore choice is above the list, so fewer rows fit.
        int perPage = chooser ? OreRowsPerPage - 2 : OreRowsPerPage;
        int pages = (snapshot.Ores.Count + perPage - 1) / perPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(perPage, snapshot.Ores.Count - page * perPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * OreRowHeight + 8), 3, 0.8f);
        y += 4;
        foreach (var ore in snapshot.Ores.Skip(page * perPage).Take(perPage))
        {
            string density = Lang.Get("propick-density-" + new[] { "verypoor", "poor", "decent", "high", "veryhigh", "ultrahigh" }[(int)GameMath.Clamp(ore.Best * 7.5, 0, 5)]);
            bool chosen = chooser && snapshot.MineOre == ore.Code;
            var font = chosen ? CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor) : CairoFont.WhiteSmallText();
            composer.AddStaticText(Lang.Get("ore-" + ore.Code), font, ElementBounds.Fixed(10, y + 4, 300, 20));
            string detail = Lang.Get("createacolony:ore-row", density, ore.Where, ore.Sites)
                + (ore.MinedOut > 0 ? "  " + Lang.Get("createacolony:ore-row-minedout", ore.MinedOut) : "");
            composer.AddStaticText(detail, GuiKit.Detail().WithColor(ore.MinedOut >= ore.Sites ? GuiKit.MutedColor : GuiKit.GoodColor), ElementBounds.Fixed(10, y + 22, Width - 160, 20));
            if (officer && !chosen)
            {
                string code = ore.Code;
                int id = BuildingId;
                composer.AddSmallButton(Lang.Get("createacolony:ore-mine-this"), () => SendText(ColonyAction.SetMineOre, id, code), ElementBounds.Fixed(Width - 140, y + 8, 130, 28));
            }
            y += OreRowHeight;
        }
        AddPager(composer, pages, y + 14);
    }

    /// <summary>The Quarry tab: where the pit is and how it is dug.</summary>
    void ComposeQuarry(GuiComposer composer)
    {
        double y = 30;
        y = composer.Rich(snapshot.PitInfo, GuiKit.Body(), 0, y, Width, 10);
        composer.Text(Lang.Get("createacolony:quarry-hint"), GuiKit.Muted(), 0, y, Width, 10);
    }

    /// <summary>
    /// A "keep in stock" list: the Pottery tab of a Potter's Hut (every kind of pottery the game's clay
    /// forming recipes make) or the Kitchen tab of a Cook's Hut (every food the game cooks in a fire pit),
    /// with how many the colony has and how many to keep. Kinds kept in stock come first. Lang keys start
    /// with the prefix: "-hint", "-none", "-have" and "-keep".
    /// </summary>
    void ComposeKeepList(GuiComposer composer, List<PotteryView> list, string prefix, ColonyAction action, double? top = null, double pageHeight = KeepListHeight)
    {
        bool officer = ViewerRole >= ColonyRole.Officer;
        double y = top ?? composer.Text(Lang.Get("createacolony:" + prefix + "-hint"), GuiKit.Muted(), 0, 30, Width, 10);
        // Meals the cook cannot make yet sink to the bottom.
        var rows = list.OrderBy(p => p.Locked).ThenByDescending(p => p.Keep > 0).ThenBy(p => p.Key).ToList();
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:" + prefix + "-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        // Each row is as tall as its wrapped text, and a page holds as many rows as fit, so long names never run into the next row.
        var layouts = rows.Select(row => KeepRowLayout(row, prefix)).ToList();
        var pageStarts = new List<int> { 0 };
        double filled = 0;
        for (int i = 0; i < layouts.Count; i++)
        {
            if (filled > 0 && filled + layouts[i].Height > pageHeight)
            {
                pageStarts.Add(i);
                filled = 0;
            }
            filled += layouts[i].Height;
        }
        int pages = pageStarts.Count;
        page = Math.Clamp(page, 0, pages - 1);
        int from = pageStarts[page], to = page + 1 < pages ? pageStarts[page + 1] : rows.Count;
        double listHeight = layouts.Skip(from).Take(to - from).Sum(l => l.Height);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, listHeight + 8), 3, 0.8f);
        y += 4;
        for (int i = from; i < to; i++)
        {
            var row = rows[i];
            var (height, name, nameHeight, detail, detailHeight, have) = layouts[i];
            var stack = new ItemStack(row.Stack);
            if (!stack.ResolveBlockOrItem(capi.World)) continue;
            // A thin line between rows.
            if (i > from) composer.AddInset(ElementBounds.Fixed(8, y, Width - 16, 1), 1, 0.4f);

            var countFont = CairoFont.WhiteDetailText().WithColor(row.Locked ? GuiKit.WarnColor : row.Keep == 0 ? GuiKit.MutedColor
                : row.Have >= row.Keep ? GuiKit.GoodColor : GuiKit.WarnColor);
            composer.AddRichtext(new RichTextComponentBase[] { new ItemstackTextComponent(capi, stack, 36, 6) }, ElementBounds.Fixed(6, y + (height - 40) / 2, 44, 40));
            double ty = y + KeepRowPad;
            composer.AddStaticText(name, row.Locked ? GuiKit.Body().WithColor(GuiKit.MutedColor) : GuiKit.Body(), ElementBounds.Fixed(KeepTextX, ty, KeepTextWidth, nameHeight));
            ty += nameHeight;
            if (detailHeight > 0)
            {
                composer.AddStaticText(detail, GuiKit.Muted(), ElementBounds.Fixed(KeepTextX, ty, KeepTextWidth, detailHeight));
                ty += detailHeight;
            }
            composer.AddStaticText(have, countFont, ElementBounds.Fixed(KeepTextX, ty, KeepTextWidth, KeepHaveHeight));

            string keep = Lang.Get("createacolony:" + prefix + "-keep", row.Keep);
            double by = y + (height - 28) / 2;
            if (officer)
            {
                var choices = snapshot.KeepChoices.Union(new[] { row.Keep }).OrderBy(c => c).ToList();
                int at = choices.IndexOf(row.Keep);
                string key = row.Key;
                composer
                    .AddSmallButton("<", () => SetKeep(action, key, choices[Math.Max(0, at - 1)]), ElementBounds.Fixed(Width - 200, by, 36, 28))
                    .AddStaticText(keep, GuiKit.Body().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(Width - 162, by + 4, 106, 25))
                    .AddSmallButton(">", () => SetKeep(action, key, choices[Math.Min(choices.Count - 1, at + 1)]), ElementBounds.Fixed(Width - 52, by, 36, 28));
            }
            else
            {
                composer.AddStaticText(keep, GuiKit.Body().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - 200, by + 4, 184, 25));
            }
            y += height;
        }
        AddPager(composer, pages, y + 14);
    }

    /// <summary>
    /// The Kitchen tab of a Cook's Hut: the food the cook has on hand, then the dishes it can make from it, each with
    /// how many to keep (ColonyManager.KitchenMenu.cs). Until an officer picks amounts, the cook chooses for itself.
    /// </summary>
    void ComposeKitchen(GuiComposer composer)
    {
        double y = composer.Text(Lang.Get("createacolony:kitchen-onhand"), GuiKit.Body(), 0, 30, Width, 4);
        var food = snapshot.KitchenOnHand;
        if (food.Count == 0) y = composer.Text(Lang.Get("createacolony:kitchen-onhand-none"), GuiKit.Muted(), 0, y, Width, 8);
        else
        {
            int perRow = (int)(Width / OnHandCell);
            int rows = Math.Min(2, (food.Count + perRow - 1) / perRow);
            composer.AddInset(ElementBounds.Fixed(0, y, Width, rows * OnHandCellHeight + 6), 2, 0.6f);
            for (int i = 0; i < Math.Min(food.Count, rows * perRow); i++)
            {
                var stack = new ItemStack(food[i].Stack);
                if (!stack.ResolveBlockOrItem(capi.World)) continue;
                double cx = 4 + i % perRow * OnHandCell, cy = y + 3 + i / perRow * OnHandCellHeight;
                composer.AddRichtext(new RichTextComponentBase[] { new ItemstackTextComponent(capi, stack, 30, 4) }, ElementBounds.Fixed(cx + 4, cy, 38, 36));
                composer.AddStaticText(food[i].Count.ToString(), CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center), ElementBounds.Fixed(cx, cy + 34, OnHandCell - 4, 16));
            }
            y += rows * OnHandCellHeight + 12;
        }

        string mode = Lang.Get(snapshot.KitchenChosen ? "createacolony:kitchen-chosen" : "createacolony:kitchen-auto");
        if (snapshot.KitchenChosen && Officer)
        {
            composer.AddSmallButton(Lang.Get("createacolony:kitchen-reset"), () =>
            {
                network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = ColonyAction.SetKitchenKeep, BuildingId = snapshot.Building.BuildingId, Text = "" });
                return true;
            }, ElementBounds.Fixed(Width - 170, y, 170, 26));
            y = composer.Text(mode, GuiKit.Muted(), 0, y + 2, Width - 180, 10);
        }
        else y = composer.Text(mode, GuiKit.Muted(), 0, y, Width, 10);
        y = Math.Max(y, 30 + 24);
        ComposeKeepList(composer, snapshot.Kitchen, "kitchen", ColonyAction.SetKitchenKeep, y, Math.Max(120, KeepListHeight + 60 - y));
    }

    const double OnHandCell = 46;
    const double OnHandCellHeight = 52;

    /// <summary>The Priorities tab's rows: one line of name each.</summary>
    const int PotteryRowsPerPage = 7;
    const double PotteryRowHeight = 44;
    const double KeepTextX = 56;
    const double KeepTextWidth = Width - 200 - KeepTextX - 12;
    const double KeepRowPad = 6;
    const double KeepHaveHeight = 18;
    /// <summary>How tall the rows on one page of a keep list may be together.</summary>
    const double KeepListHeight = 330;

    /// <summary>
    /// The text of one keep-list row and how tall it is: its name, what it is made from (a recipe row) and how
    /// many the colony has, each measured at the width it wraps to.
    /// </summary>
    (double Height, string Name, double NameHeight, string Detail, double DetailHeight, string Have) KeepRowLayout(PotteryView row, string prefix)
    {
        var stack = new ItemStack(row.Stack);
        string name = row.Name ?? (stack.ResolveBlockOrItem(capi.World) ? stack.GetName() : row.Key);
        string detail = row.Detail ?? "";
        // A locked meal shows greyed, with the Cooking level it takes ("Needs Cooking 6").
        string have = Lang.Get("createacolony:" + prefix + "-have", row.Have, row.Clay);
        if (row.Locked) have = Lang.Get("createacolony:kitchen-needs-level", row.NeedsLevel, row.CookLevel) + "  " + have;
        else if (row.NeedsLevel > 0) have = Lang.Get("createacolony:kitchen-level", row.NeedsLevel) + "  " + have;
        double nameHeight = GuiKit.TextHeight(capi, GuiKit.Body(), name, KeepTextWidth);
        double detailHeight = GuiKit.TextHeight(capi, GuiKit.Muted(), detail, KeepTextWidth);
        double height = Math.Max(48, KeepRowPad + nameHeight + detailHeight + KeepHaveHeight + KeepRowPad);
        return (height, name, nameHeight, detail, detailHeight, have);
    }

    /// <summary>
    /// The Recruits tab of a Tavern: today's people looking for work, what each asks in rusty gears,
    /// and a Hire button. The viewer pays, from what they carry and then from the Tavern's storage.
    /// </summary>
    /// <summary>A Residence's Residents tab: its beds, then each household living there with how they are related.</summary>
    void ComposeResidents(GuiComposer composer)
    {
        composer.AddSmallButton(Lang.Get("createacolony:residence-choose"), () => Open(ChooseResidentsView), ElementBounds.Fixed(Width - 170, 28, 170, 28));
        double y = composer.Text(Lang.Get("createacolony:residence-beds", snapshot.ResidenceBeds, snapshot.ResidenceFreeBeds), GuiKit.Body(), 0, 34, Width - 180);
        y = composer.Text(Lang.Get("createacolony:residence-hint"), GuiKit.Muted(), 0, y, Width, 10);
        if (snapshot.Households.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:residence-empty"), GuiKit.Body(), 0, y, Width);
            return;
        }

        int pages = (snapshot.Households.Count + HouseholdsPerPage - 1) / HouseholdsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        foreach (var house in snapshot.Households.Skip(page * HouseholdsPerPage).Take(HouseholdsPerPage))
        {
            string title = house.Members.Count > 1 || house.Surname.Length == 0
                ? Lang.Get("createacolony:residence-household", house.Surname.Length > 0 ? house.Surname : house.Members[0].Name)
                : Lang.Get("createacolony:residence-single", house.Members[0].Name);
            // The panel goes down first so the text draws on top of it.
            double height = 6 + GuiKit.TextHeight(capi, GuiKit.Heading(), title, Width - 20) + 4 + 24 * house.Members.Count
                + (house.Note.Length > 0 ? 2 + GuiKit.TextHeight(capi, GuiKit.Muted(), house.Note, Width - 40) + 4 : 0);
            composer.AddInset(ElementBounds.Fixed(0, y, Width, height + 6), 2, 0.6f);
            y = composer.Text(title, GuiKit.Heading(), 10, y + 6, Width - 20, 4);
            foreach (var m in house.Members)
            {
                string what = m.Child ? Lang.Get("createacolony:child-grows-up", m.GrowsUpHours) : JobName(m.Job);
                string relation = m.Relation.Length > 0 ? m.Relation : "";
                y = composer.Row(m.Name + (relation.Length > 0 ? "  (" + relation + ")" : ""), what, 20, y, Width - 40);
            }
            if (house.Note.Length > 0) y = composer.Text(house.Note, GuiKit.Muted(), 20, y + 2, Width - 40, 4);
            y += 14;
        }
        composer.Pager(page, pages, y + 4, Width, p => { page = p; Compose(); });
    }

    /// <summary>
    /// A Church's Church tab: the colony's Faith, the next service, the tithe (officers pick 0, 5 or 10 percent),
    /// the church's coins and the people it looks after, then what it took in and spent over the last 30 days.
    /// </summary>
    void ComposeChurch(GuiComposer composer)
    {
        double y = 30;
        int id = BuildingId;
        y = composer.Section(Lang.Get("createacolony:church-faith-section"), y, Width);
        y = composer.Row(Lang.Get("createacolony:church-faith-average"), snapshot.AverageFaith < 0 ? "-" : snapshot.AverageFaith.ToString(), 0, y, Width);
        string[] words = { "devout", "faithful", "doubting", "faithless" };
        for (int i = 0; i < words.Length && i < snapshot.FaithCounts.Count; i++)
        {
            y = composer.Row(Lang.Get("createacolony:faith-" + words[i]), snapshot.FaithCounts[i].ToString(), 10, y, Width - 10);
        }
        string service = snapshot.ServiceNow ? Lang.Get("createacolony:church-tab-service-now")
            : snapshot.ServiceInDays == 0 ? Lang.Get("createacolony:church-tab-service-today")
            : Lang.Get("createacolony:church-tab-service-in", snapshot.ServiceInDays);
        y = composer.Text(service, GuiKit.Body(), 0, y + 4, Width, 10);

        y = composer.Section(Lang.Get("createacolony:church-tithe-section"), y, Width);
        composer.AddStaticText(Lang.Get("createacolony:church-tithe", snapshot.TithePercent), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 200, 25));
        if (Officer)
        {
            double x = 200;
            foreach (int percent in new[] { 0, 5, 10 })
            {
                string label = (percent == snapshot.TithePercent ? "> " : "") + percent + "%";
                composer.AddSmallButton(label, () => SendText(ColonyAction.SetTithe, id, percent.ToString()), ElementBounds.Fixed(x, y, 60, 28));
                x += 66;
            }
        }
        y += 34;
        y = composer.Text(Lang.Get("createacolony:church-tithe-hint"), GuiKit.Muted(), 0, y, Width, 6);
        y = composer.Row(Lang.Get("createacolony:church-tab-coins"), snapshot.ChurchCoins.ToString(), 0, y, Width);

        y = composer.Section(Lang.Get("createacolony:church-care-section"), y + 6, Width);
        y = composer.Row(Lang.Get("createacolony:church-orphans"), snapshot.Orphans.ToString(), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-sheltered"), snapshot.Sheltered.ToString(), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-funerals-waiting"), snapshot.AwaitingFuneral.ToString(), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-chronicle"), Lang.Get("createacolony:church-chronicle-progress", Math.Min(snapshot.ChronicleEvents, snapshot.ChronicleNeeds), snapshot.ChronicleNeeds), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-stations"), snapshot.Stations.Count == 0 ? Lang.Get("createacolony:church-stations-none") : string.Join(", ", snapshot.Stations), 0, y, Width);

        y = composer.Section(Lang.Get("createacolony:church-ledger-section"), y + 6, Width);
        var days = snapshot.ChurchLedger;
        if (days.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:church-ledger-empty"), GuiKit.Muted(), 0, y, Width);
            return;
        }
        y = composer.Row(Lang.Get("createacolony:church-ledger-tithes"), days.Sum(d => d.Tithes).ToString(), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-ledger-food"), Lang.Get("createacolony:church-ledger-coins-for", days.Sum(d => d.FoodCoins), days.Sum(d => d.Meals)), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-ledger-bandages"), Lang.Get("createacolony:church-ledger-coins-for", days.Sum(d => d.BandageCoins), days.Sum(d => d.Bandages)), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-ledger-care"), days.Sum(d => d.CareCoins).ToString(), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-ledger-attended"), days.Sum(d => d.Attended).ToString(), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:church-ledger-funerals"), days.Sum(d => d.Funerals).ToString(), 0, y, Width);
        composer.Row(Lang.Get("createacolony:church-ledger-books"), days.Sum(d => d.Books).ToString(), 0, y, Width);
    }

    /// <summary>
    /// Choose residents: who lives here (with Move out) and every other grown settler with where they
    /// live now (with Move in). A settler's partner and children always move with them.
    /// </summary>
    void ComposeChooseResidents(GuiComposer composer)
    {
        composer
            .AddStaticText(Lang.Get("createacolony:residence-choose"), GuiKit.Heading(), ElementBounds.Fixed(0, 30, Width - 110, 26))
            .AddSmallButton(Lang.Get("createacolony:back"), () => Open(ResidentsTab), ElementBounds.Fixed(Width - 100, 28, 100, 28));
        double y = composer.Text(Lang.Get("createacolony:residence-choose-hint"), GuiKit.Muted(), 0, 64, Width, 8);

        var here = snapshot.Households.SelectMany(h => h.Members).ToList();
        y = composer.Section(Lang.Get("createacolony:residence-living-here", here.Count, snapshot.ResidenceBeds), y, Width);
        if (here.Count == 0) y = composer.Text(Lang.Get("createacolony:residence-nobody"), GuiKit.Detail(), 0, y, Width, 8);
        foreach (var m in here)
        {
            int id = m.CitizenId;
            composer
                .AddStaticText(m.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 5, 220, 25))
                .AddStaticText(m.Child ? Lang.Get("createacolony:job-child") : JobName(m.Job), GuiKit.Muted(), ElementBounds.Fixed(240, y + 8, 160, 22));
            // Children move with their parents, so only the grown-ups get a button.
            if (!m.Child) composer.AddSmallButton(Lang.Get("createacolony:residence-move-out"), () => SendCitizen(ColonyAction.MoveOutResidence, id), ElementBounds.Fixed(Width - 130, y, 120, 28));
            y += ListRowHeight;
        }

        y = composer.Section(Lang.Get("createacolony:residence-others", snapshot.ResidenceFreeBeds), y + 6, Width);
        var others = snapshot.ResidentChoices;
        if (others.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:manage-nobody"), GuiKit.Detail(), 0, y, Width);
            return;
        }
        int pages = (others.Count + ListRowsPerPage - 1) / ListRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(ListRowsPerPage, others.Count - page * ListRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * ListRowHeight + 8), 3, 0.8f);
        y += 4;
        foreach (var c in others.Skip(page * ListRowsPerPage).Take(ListRowsPerPage))
        {
            int id = c.CitizenId;
            string home = c.Relation.Length > 0 ? c.Relation : Lang.Get("createacolony:citizen-no-home");
            composer
                .AddStaticText(c.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 6, 190, 25))
                .AddStaticText(home, GuiKit.Muted(), ElementBounds.Fixed(200, y + 8, Width - 340, 22));
            if (snapshot.ResidenceFreeBeds > 0) composer.AddSmallButton(Lang.Get("createacolony:residence-move-in"), () => SendCitizen(ColonyAction.MoveInResidence, id), ElementBounds.Fixed(Width - 130, y + 3, 120, 28));
            y += ListRowHeight;
        }
        AddPager(composer, pages, y + 14);
        if (snapshot.ResidenceFreeBeds == 0) composer.Text(Lang.Get("createacolony:residence-full"), GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, y + (pages > 1 ? 50 : 14), Width);
    }

    static string JobName(string job) => Lang.Get("createacolony:job-" + (job.Length == 0 ? "none" : job));

    /// <summary>"Farming 3" for a worker list row, or nothing when skills are off.</summary>
    static string SkillText(CitizenView c) =>
        (c.Skill.Length == 0 ? "" : Lang.Get("createacolony:skill-level", Lang.Get("createacolony:skill-" + c.Skill), c.SkillLevel)
            + (c.Passion > 0 ? " " + Lang.Get("createacolony:passion-mark-" + Math.Min(2, c.Passion)) : ""))
        + (c.JobTrait ? " " + Lang.Get("createacolony:job-trait-mark") : "");

    void ComposeRecruits(GuiComposer composer)
    {
        if (snapshot.Building.Level < 1)
        {
            composer.Text(Lang.Get("createacolony:tavern-closed"), GuiKit.Body(), 0, 30, Width);
            return;
        }

        ItemStack? currency = null;
        if (snapshot.Currency != null)
        {
            currency = new ItemStack(snapshot.Currency);
            if (!currency.ResolveBlockOrItem(capi.World)) currency = null;
        }
        string have = snapshot.Creative ? Lang.Get("createacolony:recruits-creative") : Lang.Get("createacolony:recruits-have", snapshot.Gears);
        var header = new List<RichTextComponentBase>();
        if (currency != null) header.Add(new ItemstackTextComponent(capi, currency, 32, 5));
        header.Add(new RichTextComponent(capi, have, GuiKit.Body()) { VerticalAlign = EnumVerticalAlign.Middle });
        composer.AddRichtext(header.ToArray(), ElementBounds.Fixed(0, 30, Width, 36));
        double y = composer.Text(Lang.Get("createacolony:recruits-hint"), GuiKit.Muted(), 0, 70, Width, 10);

        if (snapshot.Recruits.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:recruits-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        if (snapshot.PopulationCap > 0 && snapshot.Population >= snapshot.PopulationCap)
        {
            y = composer.Text(Lang.Get("createacolony:recruits-full", snapshot.Population, snapshot.PopulationCap), GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, y, Width, 8);
        }

        bool canHire = ViewerRole >= ColonyRole.Member && !(snapshot.PopulationCap > 0 && snapshot.Population >= snapshot.PopulationCap);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, snapshot.Recruits.Count * RecruitRowHeight + 8), 3, 0.8f);
        y += 4;
        for (int i = 0; i < snapshot.Recruits.Count; i++)
        {
            var r = snapshot.Recruits[i];
            bool afford = snapshot.Creative || snapshot.Gears >= r.Cost;
            var costFont = CairoFont.WhiteDetailText().WithColor(afford ? GuiKit.GoodColor : GuiKit.WarnColor);
            // What they know comes after the price: "Farming 3, Strength 2".
            string skills = i < snapshot.RecruitSkills.Count && snapshot.RecruitSkills[i].Length > 0 ? "   " + snapshot.RecruitSkills[i] : "";
            // Their traits on a third line: what they were born with sets the price.
            string traits = i < snapshot.RecruitTraits.Count && snapshot.RecruitTraits[i].Length > 0 ? "\n" + snapshot.RecruitTraits[i] : "";
            composer.AddRichtext(new RichTextComponentBase[]
            {
                new RichTextComponent(capi, r.Name + "\n", GuiKit.Body()),
                new RichTextComponent(capi, Lang.Get("createacolony:recruit-line", Lang.Get("createacolony:sex-" + r.Sex), r.Cost), costFont),
                new RichTextComponent(capi, skills, GuiKit.Muted()),
                new RichTextComponent(capi, traits, GuiKit.Detail())
            }, ElementBounds.Fixed(10, y + 2, Width - 150, RecruitRowHeight));
            if (canHire)
            {
                string name = r.Name;
                composer.AddSmallButton(Lang.Get("createacolony:recruit-hire"), () => Hire(name), ElementBounds.Fixed(Width - 130, y + 7, 120, 28));
            }
            y += RecruitRowHeight;
        }
    }

    bool Hire(string name)
    {
        network.SendAction(new ColonyActionPacket
            { ColonyId = snapshot.ColonyId, Action = ColonyAction.HireRecruit, BuildingId = snapshot.Building.BuildingId, Text = name });
        return true;
    }

    bool SetKeep(ColonyAction action, string key, int keep)
    {
        network.SendAction(new ColonyActionPacket
            { ColonyId = snapshot.ColonyId, Action = action, BuildingId = snapshot.Building.BuildingId, Text = key + "|" + keep });
        return true;
    }

    /// <summary>An on/off setting under its section heading: a switch for officers, On or Off for everyone else. Returns the y below it.</summary>
    double AddSetting(GuiComposer composer, string key, ColonyAction action, bool on, double y)
    {
        string state = Lang.Get(on ? "createacolony:on" : "createacolony:off");
        if (ViewerRole >= ColonyRole.Officer)
        {
            int id = snapshot.Building.BuildingId;
            composer
                .AddSwitch(value => network.SendAction(new ColonyActionPacket
                    { ColonyId = snapshot.ColonyId, Action = action, BuildingId = id, Text = value ? "1" : "0" }),
                    ElementBounds.Fixed(0, y, 30, 30), key)
                .AddStaticText(state, GuiKit.Body(), ElementBounds.Fixed(40, y + 5, Width - 40, 25));
        }
        else
        {
            composer.AddStaticText(state, GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width, 25));
        }
        return y + 36;
    }

    bool SetRadius(int radius)
    {
        if (radius == snapshot.WorkRadius) return true;
        network.SendAction(new ColonyActionPacket
            { ColonyId = snapshot.ColonyId, Action = ColonyAction.SetWorkRadius, BuildingId = snapshot.Building.BuildingId, Text = radius.ToString() });
        return true;
    }

    /// <summary>The "any wood or stone" setting: a switch for officers, a line of text for everyone else. Returns the y below it.</summary>
    double AddAnyTypeSwitch(GuiComposer composer, double y)
    {
        if (ViewerRole >= ColonyRole.Officer)
        {
            composer
                .AddSwitch(on => network.SendAction(new ColonyActionPacket
                    { ColonyId = snapshot.ColonyId, Action = ColonyAction.SetAnyWoodOrStone, Text = on ? "1" : "0" }),
                    ElementBounds.Fixed(0, y, 30, 30), "anytype")
                .AddStaticText(Lang.Get("createacolony:any-type"), GuiKit.Body(), ElementBounds.Fixed(40, y + 5, Width - 40, 25));
            y += 34;
        }
        else
        {
            y = composer.Text(Lang.Get(snapshot.AnyWoodOrStone ? "createacolony:any-type-on" : "createacolony:any-type-off"), GuiKit.Body(), 0, y + 5, Width, 4);
        }
        return composer.Text(Lang.Get("createacolony:any-type-hint"), GuiKit.Muted(), ViewerRole >= ColonyRole.Officer ? 40 : 0, y, Width - 40, 12);
    }

    /// <summary>Build/upgrade, cancel and turn buttons for one building. Returns the y below them.</summary>
    double AddOrderButtons(GuiComposer composer, BuildingView b, double y)
    {
        if (ViewerRole < ColonyRole.Officer) return y;
        int id = b.BuildingId;
        if (b.Removing)
        {
            composer.AddSmallButton(Lang.Get("createacolony:remove-stop"), () => Send(ColonyAction.CancelBuild, id), ElementBounds.Fixed(0, y, 200, 28));
            return y + 34;
        }
        if (b.TargetLevel > 0)
        {
            composer.AddSmallButton(Lang.Get(b.Repairing ? "createacolony:repair-stop" : "createacolony:build-cancel"), () => Send(ColonyAction.CancelBuild, id), ElementBounds.Fixed(0, y, 160, 28));
            // An upkeep order (digging out ground) can be turned into a full repair.
            if (!b.Repairing && b.TargetLevel == b.Level) AddRepairButton(composer, b, 170, y);
            return y + 34;
        }
        double x = 0;
        if (b.Level < b.MaxLevel)
        {
            string label = Lang.Get(b.Level == 0 ? "createacolony:build-start" : "createacolony:build-upgrade", b.Level + 1);
            composer.AddSmallButton(label, () => Send(ColonyAction.Build, id), ElementBounds.Fixed(0, y, 160, 28));
            x = 170;
        }
        if (b.Level == 0)
        {
            if (b.MaxLevel == 0) return y;
            composer.AddSmallButton(Lang.Get("createacolony:build-rotate", b.Rotation), () => Send(ColonyAction.Rotate, id), ElementBounds.Fixed(170, y, 200, 28));
        }
        else if (b.MaxLevel > 0 && b.Type != BuildingTypes.MineshaftEntrance) AddRepairButton(composer, b, x, y);
        else if (x == 0) return y;
        return y + 34;
    }

    /// <summary>Repair: a builder puts the building back as its blueprint has it (ColonyManager.Repair.cs).</summary>
    internal void AddRepairButton(GuiComposer composer, BuildingView b, double x, double y)
    {
        int id = b.BuildingId;
        composer.AddSmallButton(Lang.Get("createacolony:repair"), () => Send(ColonyAction.Repair, id), ElementBounds.Fixed(x, y, 160, 28));
        composer.AddHoverText(Lang.Get("createacolony:repair-hint"), GuiKit.Detail(), 300, ElementBounds.Fixed(x, y, 160, 28));
    }

    void ComposeOrders(GuiComposer composer)
    {
        var orders = snapshot.Orders;
        if (orders.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:hut-no-orders"), GuiKit.Body(), 0, 30, Width);
            return;
        }

        int pages = (orders.Count + OrdersPerPage - 1) / OrdersPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(OrdersPerPage, orders.Count - page * OrdersPerPage);
        composer.AddInset(ElementBounds.Fixed(0, 30, Width, shown * OrderRowHeight + 8), 3, 0.8f);
        double y = 34;
        int number = page * OrdersPerPage;
        foreach (var o in orders.Skip(page * OrdersPerPage).Take(OrdersPerPage))
        {
            number++;
            string title = number + ". " + OrderTitle(o);
            composer
                .AddStaticText(title, GuiKit.Body(), ElementBounds.Fixed(10, y + 6, Width - 250, 25))
                .AddStaticText(StatusText(o), GuiKit.Detail().WithColor(GuiStyle.ActiveButtonTextColor), ElementBounds.Fixed(10, y + 30, Width - 250, 22));
            if (ViewerRole >= ColonyRole.Officer)
            {
                int id = o.TeardownId > 0 ? o.TeardownId : o.BuildingId;
                var action = o.TeardownId > 0 ? ColonyAction.CancelTeardown : ColonyAction.CancelBuild;
                AddMoveButtons(composer, OrderKey(o), Width - 236, y + 14, 0);
                composer.AddSmallButton(Lang.Get("createacolony:build-cancel"), () => Send(action, id), ElementBounds.Fixed(Width - 100, y + 14, 90, 28));
            }
            y += OrderRowHeight;
        }
        AddPager(composer, pages, y + 12);
    }

    /// <summary>The key a work order or trader house has in the builders' queue (ColonyManager.OrderKey).</summary>
    internal static string OrderKey(BuildingView o) => o.TeardownId > 0 ? "t:" + o.TeardownId : "b:" + o.BuildingId;

    /// <summary>▲, ▼ and "Do next" for one row of a Priorities list. Hut 0: the builders' queue.</summary>
    void AddMoveButtons(GuiComposer composer, string key, double x, double y, int hutId)
    {
        composer
            .AddSmallButton("^", () => Move(key, ColonyManager.MoveUp, hutId), ElementBounds.Fixed(x, y, 30, 28))
            .AddSmallButton("v", () => Move(key, ColonyManager.MoveDown, hutId), ElementBounds.Fixed(x + 34, y, 30, 28))
            .AddSmallButton(Lang.Get("createacolony:priority-top"), () => Move(key, ColonyManager.MoveTop, hutId), ElementBounds.Fixed(x + 68, y, 62, 28));
    }

    bool Move(string key, int move, int hutId)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = ColonyAction.MovePriority, BuildingId = hutId, Text = key, Role = move });
        return true;
    }

    /// <summary>
    /// The Priorities tab of a crafting hut: what it keeps, in the order its worker makes what is short, with
    /// how many there are. Officers move rows up and down; the order is the hut's from then on.
    /// </summary>
    void ComposePriorities(GuiComposer composer)
    {
        double y = 30;
        if (snapshot.GatherOrder.Count > 0)
        {
            y = ComposeGatherOrder(composer, y);
            y = composer.Section(Lang.Get("createacolony:priorities-crafting"), y, Width);
        }
        y = composer.Text(Lang.Get("createacolony:priorities-hint"), GuiKit.Muted(), 0, y, Width, 10);
        var rows = snapshot.Craft.Concat(snapshot.Recipes).Concat(snapshot.Kitchen).Concat(snapshot.Pottery).Concat(snapshot.WorkRows)
            .Where(r => r.Keep > 0).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var keys = snapshot.Priorities.Where(rows.ContainsKey).ToList();
        if (keys.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:priorities-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        // Under a Gatherer's collecting order there is room for fewer rows.
        int perPage = snapshot.GatherOrder.Count > 0 ? 3 : PotteryRowsPerPage;
        int pages = (keys.Count + perPage - 1) / perPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(perPage, keys.Count - page * perPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * PotteryRowHeight + 8), 3, 0.8f);
        y += 4;
        int number = page * perPage;
        foreach (var key in keys.Skip(page * perPage).Take(perPage))
        {
            number++;
            var row = rows[key];
            var stack = new ItemStack(row.Stack);
            if (!stack.ResolveBlockOrItem(capi.World)) continue;
            var countFont = CairoFont.WhiteDetailText().WithColor(row.Have >= row.Keep ? GuiKit.GoodColor : GuiKit.WarnColor);
            composer.AddRichtext(new RichTextComponentBase[]
            {
                new RichTextComponent(capi, number + ". ", CairoFont.WhiteSmallText()) { VerticalAlign = EnumVerticalAlign.Top },
                new ItemstackTextComponent(capi, stack, 38, 10),
                new RichTextComponent(capi, (row.Name ?? stack.GetName()) + "\n", CairoFont.WhiteSmallText()) { VerticalAlign = EnumVerticalAlign.Top },
                new RichTextComponent(capi, row.PriorityText ?? Lang.Get("createacolony:priorities-have", row.Have, row.Keep), countFont)
            }, ElementBounds.Fixed(6, y, Width - 150, PotteryRowHeight));
            if (ViewerRole >= ColonyRole.Officer) AddMoveButtons(composer, key, Width - 136, y + 6, snapshot.Building.BuildingId);
            y += PotteryRowHeight;
        }
        AddPager(composer, pages, y + 14);
    }

    const double GatherRowHeight = 32;

    /// <summary>
    /// A Gatherer's collecting order: the kinds of thing its gatherer goes for first, in two columns, with ^ and v
    /// for officers. Returns the height below it.
    /// </summary>
    double ComposeGatherOrder(GuiComposer composer, double y)
    {
        y = composer.Section(Lang.Get("createacolony:gatherorder"), y, Width);
        y = composer.Text(Lang.Get("createacolony:gatherorder-hint"), GuiKit.Muted(), 0, y, Width, 6);
        var order = snapshot.GatherOrder;
        int perColumn = (order.Count + 1) / 2;
        double column = Width / 2;
        composer.AddInset(ElementBounds.Fixed(0, y, Width, perColumn * GatherRowHeight + 8), 3, 0.8f);
        for (int i = 0; i < order.Count; i++)
        {
            double x = (i / perColumn) * column + 8, rowY = y + 4 + (i % perColumn) * GatherRowHeight;
            string key = ColonyManager.GatherKeyPrefix + order[i];
            composer.AddStaticText((i + 1) + ". " + Lang.Get("createacolony:gatherorder-" + order[i]), GuiKit.Body(),
                ElementBounds.Fixed(x, rowY + 5, column - 90, 24));
            if (ViewerRole >= ColonyRole.Officer)
            {
                int hutId = snapshot.Building.BuildingId;
                composer
                    .AddSmallButton("^", () => Move(key, ColonyManager.MoveUp, hutId), ElementBounds.Fixed(x + column - 84, rowY + 2, 30, 26))
                    .AddSmallButton("v", () => Move(key, ColonyManager.MoveDown, hutId), ElementBounds.Fixed(x + column - 50, rowY + 2, 30, 26));
            }
        }
        return y + perColumn * GatherRowHeight + 20;
    }

    static readonly double[] MissingColor = { 1.0, 0.4, 0.35, 1.0 };

    const int ResourceRowsPerPage = 6;
    const double ResourceRowHeight = 44;
    int suppliedPercent, usedPercent;

    /// <summary>
    /// Required resources, as in MineColonies: the work the hut's worker is on (a Builder's work order, a
    /// Miner's mineshaft piece), bars for how much of it the hut has (Supplied) and how much is done (Used),
    /// and one row per item still needed. Red: neither the hut nor you have it. Green: you carry some, and
    /// the ^ button puts them in the hut's storage. White: the hut has enough. Fulfil all does every row.
    /// </summary>
    void ComposeItems(GuiComposer composer)
    {
        string job = Lang.Get("createacolony:job-" + snapshot.Job);
        if (snapshot.BuilderName.Length == 0)
        {
            composer.Text(Lang.Get("createacolony:resources-no-worker", job), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, 30, Width);
            return;
        }
        if (snapshot.ResourcesFor.Length == 0)
        {
            composer.Text(Lang.Get("createacolony:items-nothing", snapshot.BuilderName), GuiKit.Body(), 0, 30, Width);
            return;
        }

        // Fulfil all: as many of every item as the hut still needs, from what the viewer carries.
        var givable = snapshot.Resources.Where(r => !r.IsTool && r.Code.Length > 0 && r.PlayerHas > 0 && r.Have < r.Needed).ToList();
        bool canGive = ViewerRole >= ColonyRole.Member;
        composer.AddStaticText(Lang.Get("createacolony:resources-project", snapshot.ResourcesFor), GuiKit.Heading(),
            ElementBounds.Fixed(0, 30, canGive && givable.Count > 0 ? Width - 160 : Width, 26));
        if (canGive && givable.Count > 0)
        {
            string all = string.Join("\n", givable.Select(r => r.Code + "|" + (r.Needed - r.Have)));
            composer.AddSmallButton(Lang.Get("createacolony:resources-fulfil-all"), () => SendText(ColonyAction.GiveAllResources, BuildingId, all),
                ElementBounds.Fixed(Width - 150, 28, 150, 28));
            composer.AddHoverText(Lang.Get("createacolony:resources-fulfil-all-hint"), GuiKit.Detail(), 260, ElementBounds.Fixed(Width - 150, 28, 150, 28));
        }

        // Supplied: share of the items in the hut. Used: share of the work done.
        var materials = snapshot.Resources.Where(r => !r.IsTool).ToList();
        int needed = materials.Sum(r => r.Needed);
        suppliedPercent = needed == 0 ? 100 : materials.Sum(r => Math.Min(r.Have, r.Needed)) * 100 / needed;
        usedPercent = Math.Clamp(snapshot.UsedPercent, 0, 100);
        double half = (Width - 20) / 2;
        composer
            .AddStaticText(Lang.Get("createacolony:resources-supplied-bar", suppliedPercent), GuiKit.Detail(), ElementBounds.Fixed(0, 64, half, 20))
            .AddStaticText(Lang.Get("createacolony:resources-used-bar", usedPercent), GuiKit.Detail(), ElementBounds.Fixed(half + 20, 64, half, 20))
            .AddStatbar(ElementBounds.Fixed(0, 86, half, 10), GuiStyle.FoodBarColor, "supplied")
            .AddStatbar(ElementBounds.Fixed(half + 20, 86, half, 10), GuiStyle.XPBarColor, "used");
        composer.AddStaticText(Lang.Get("createacolony:resources-step", snapshot.ResourcesStep), GuiKit.Muted(), ElementBounds.Fixed(0, 104, Width, 20));

        // Missing items first, so the list leads with what the worker is waiting on.
        var rows = snapshot.Resources.OrderBy(r => r.Have >= r.Needed).ThenByDescending(r => r.IsTool).ThenByDescending(r => r.Needed - r.Have).ToList();
        const double top = 130;
        if (rows.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:items-none"), GuiKit.Body(), 0, top, Width);
            return;
        }
        int pages = (rows.Count + ResourceRowsPerPage - 1) / ResourceRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int onPage = Math.Min(ResourceRowsPerPage, rows.Count - page * ResourceRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, top, Width, onPage * ResourceRowHeight + 8), 3, 0.8f);
        double y = top + 4;
        foreach (var r in rows.Skip(page * ResourceRowsPerPage).Take(ResourceRowsPerPage))
        {
            var stack = new ItemStack(r.Stack);
            if (!stack.ResolveBlockOrItem(capi.World)) continue;
            bool give = canGive && r.PlayerHas > 0 && r.Have < r.Needed && r.Code.Length > 0;
            composer.AddRichtext(new RichTextComponentBase[] { new ItemstackTextComponent(capi, stack, 34, 6) }, ElementBounds.Fixed(6, y + 2, 44, 40));
            composer.AddStaticText(ResourceName(stack, r), GuiKit.Body(), ElementBounds.Fixed(52, y + 4, Width - 240, 20));
            composer.AddStaticText(ResourceCounts(r), CountFont(r), ElementBounds.Fixed(52, y + 24, Width - 240, 18));
            composer.AddStaticText(Lang.Get("createacolony:items-count", Math.Min(r.Have, r.Needed), r.Needed),
                CountFont(r).Clone().WithFontSize(16).WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - 190, y + 12, 130, 22));
            if (give)
            {
                string code = r.Code;
                int amount = r.Needed - r.Have;
                composer.AddSmallButton("^", () => SendText(ColonyAction.GiveResource, BuildingId, code + "|" + amount), ElementBounds.Fixed(Width - 50, y + 8, 40, 28));
                composer.AddHoverText(Lang.Get("createacolony:resources-give", Math.Min(amount, r.PlayerHas)), GuiKit.Detail(), 220, ElementBounds.Fixed(Width - 50, y + 8, 40, 28));
            }
            y += ResourceRowHeight;
        }
        y = top + ResourceRowsPerPage * ResourceRowHeight + 16;
        composer.Text(Lang.Get("createacolony:resources-legend"), GuiKit.Muted(), 0, y, Width, 6);
        AddPager(composer, pages, y + 24);
    }

    /// <summary>White when the hut has enough, green when you carry some, red when nobody has it.</summary>
    static CairoFont CountFont(ResourceView r) => r.Have >= r.Needed ? GuiKit.Detail()
        : GuiKit.Detail().WithColor(r.PlayerHas > 0 ? GuiKit.GoodColor : r.InColony > 0 || r.MadeBy.Length > 0 ? GuiKit.WarnColor : MissingColor);

    string ResourceName(ItemStack stack, ResourceView r) =>
        r.IsTool ? Lang.Get("createacolony:items-tool", Lang.Get("createacolony:tool-" + (stack.Collectible.Tool?.ToString().ToLowerInvariant() ?? "")))
        : r.AnyOf.Length > 0 ? Lang.Get("createacolony:items-any-" + r.AnyOf, stack.GetName()) : WithWood(stack);

    /// <summary>"In the hut: 0. You have 100." under the item's name.</summary>
    static string ResourceCounts(ResourceView r)
    {
        if (r.Have >= r.Needed) return Lang.Get("createacolony:resources-ready");
        string line = Lang.Get("createacolony:resources-in-hut", r.Have);
        if (r.PlayerHas > 0) return line + "   " + Lang.Get("createacolony:resources-you-have", r.PlayerHas);
        if (r.IsTool) return line;
        // On its way: in the warehouse for a courier, or another hut makes it.
        if (r.InColony > 0) return line + "   " + Lang.Get("createacolony:resources-in-warehouse", r.InColony);
        if (r.MadeBy.Length > 0) return line + "   " + Lang.Get("createacolony:resources-made-by", r.MadeBy);
        return line + "   " + Lang.Get("createacolony:resources-nobody");
    }

    /// <summary>
    /// The item's name with its wood when the wood is an item attribute (W4RD0's Furniture, banner poles) and
    /// the name does not say it already, so "Rustic table (Oak)" and "Rustic table (Pine)" are told apart.
    /// </summary>
    static string WithWood(ItemStack stack)
    {
        string name = stack.GetName();
        string? wood = stack.Attributes?.GetTreeAttribute("types")?.GetString("wood");
        if (string.IsNullOrEmpty(wood) || !Lang.HasTranslation("material-" + wood)) return name;
        string material = Lang.Get("material-" + wood);
        return name.Contains(material, StringComparison.OrdinalIgnoreCase) ? name : name + " (" + material + ")";
    }

    void AddPager(GuiComposer composer, int pages, double y) => composer.Pager(page, pages, y, Width, p => { page = p; Compose(); });

    internal static string LevelText(BuildingView b) => b.MaxLevel == 0
        ? Lang.Get("createacolony:building-no-blueprints", b.Level)
        : Lang.Get("createacolony:building-level", b.Level, b.MaxLevel);

    internal static string StatusText(BuildingView b) => b.Removing
        ? Lang.Get("createacolony:remove-status", b.ProgressPercent, Lang.Get("createacolony:buildstatus-" + b.Status))
        : b.Repairing ? Lang.Get("createacolony:repair-status", b.ProgressPercent, Lang.Get("createacolony:buildstatus-" + b.Status))
        : Lang.Get("createacolony:build-status", b.TargetLevel, b.ProgressPercent, Lang.Get("createacolony:buildstatus-" + b.Status));

    /// <summary>A work order's title in the lists: the building and the level it goes to, or that it comes down.</summary>
    internal static string OrderTitle(BuildingView o) => o.TeardownId > 0 ? Lang.Get("createacolony:teardown-name", o.Name) : o.Removing
        ? Lang.Get("createacolony:order-remove", o.Name)
        : o.Repairing ? Lang.Get("createacolony:order-repair", o.Name)
        : Lang.Get("createacolony:building-" + o.Type) + " " + Lang.Get("createacolony:order-level", o.TargetLevel);

    /// <summary>The server opens the storage window; this one closes so they do not overlap.</summary>
    bool OpenStorage()
    {
        int id = snapshot.Building.BuildingId;
        TryClose();
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = ColonyAction.OpenStorage, BuildingId = id });
        return true;
    }

    bool Send(ColonyAction action, int buildingId)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = action, BuildingId = buildingId });
        return true;
    }

    public override void OnGuiClosed()
    {
        if (!Replaced) network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = ColonyAction.CloseWindow });
        base.OnGuiClosed();
    }
}
