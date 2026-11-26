using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using CreateAColony.Network;
using CreateAColony.Quests;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CreateAColony.Gui;

/// <summary>
/// The Town Hall window, laid out like MineColonies' with tabs down the left: Actions (name, level,
/// upgrade, land), Town map, Information (event log and work orders), Citizens, Statistics, Permissions and
/// Settings. Every change goes to the server, which answers with a fresh snapshot; the window never
/// changes colony data on its own.
/// </summary>
public class GuiDialogTownHall : GuiDialog
{
    const int RowsPerPage = 8;
    const double RowHeight = 34;
    const double Width = 560;
    const int EventsPerPage = 10;
    const double EventRowHeight = 26;
    const int OrdersPerPage = 5;
    const double OrderRowHeight = 62;

    const int ActionsTab = 0;
    const int InfoTab = 1;
    const int CitizensTab = 2;
    const int StatsTab = 3;
    const int PermissionsTab = 4;
    const int SettingsTab = 5;
    const int MapTab = 6;
    const int BannersTab = 7;
    const int ProspectingTab = 8;
    const int OreRowsPerPage = 7;
    const double OreRowHeight = 44;
    const int QuestsTab = 9;
    const int MilitiaTab = 10;
    const int BailiffTab = 11;
    const int WinterTab = 12;
    const int MayorTab = 13;
    const int MayorRowsPerPage = 9;
    const int ReportRowsPerPage = 12;
    const double MapHeight = 380;
    const int MapMargin = 16;
    // Opened with the ? button on the Actions tab, with a Back button.
    const int HelpView = 20;
    /// <summary>The job panel for one settler (0.203.0), opened from the Citizens tab.</summary>
    const int JobPanelView = 21;
    /// <summary>The Citizens tab and the job panel are wider than the other tabs: the job needs its name and a button.</summary>
    const double CitizensWidth = 660;
    const int JobRowsPerPage = 10;
    const double JobRowHeight = 30;
    int jobPanelCitizen;

    static readonly (int Id, string Lang)[] Tabs =
    {
        (ActionsTab, "tab-actions"), (QuestsTab, "tab-quests"), (MapTab, "tab-townmap"), (BannersTab, "tab-banners"), (InfoTab, "tab-information"), (CitizensTab, "tab-citizens"),
        (BailiffTab, "tab-bailiff"), (MayorTab, "tab-mayor"), (MilitiaTab, "tab-militia"), (WinterTab, "tab-winter"), (StatsTab, "tab-statistics"), (PermissionsTab, "tab-permissions"), (ProspectingTab, "tab-prospecting"), (SettingsTab, "tab-settings")
    };

    readonly ColonyNetwork network;
    ColonySnapshot snapshot;
    int currentTab;
    int page;
    bool renaming;
    string pendingName;
    // Which page of the Information tab (0 events, 1 work orders) and of the Statistics tab (0 colony, 1 work done).
    int infoPage;
    int statsPage;
    int statPeriod = 1;
    // Which page of the Citizens tab: 0 the living (and wanderers), 1 the dead.
    int citizensPage;
    // Which page of the Bailiff tab: 0 the bailiff and their rules, 1 the ledger, 2 the estate report.
    int bailiffPage;
    // Which page of the Permissions tab (0 players, 1 ranks, 2 the log) and of Settings (0 general, 1 town).
    int permPage;
    int settingsPage;
    int chosenRank;
    string pendingRankName = "";
    string pendingRankRename = "";
    string pendingEnter;
    string pendingLeave;
    // Where the Town map is looking and how far zoomed in, kept while the window is open.
    readonly ColonyMapView mapView = new();
    GuiElementColonyMap? mapElement;

    public string ColonyId => snapshot.ColonyId;

    public override string? ToggleKeyCombinationCode => null;

    public GuiDialogTownHall(ICoreClientAPI capi, ColonyNetwork network, ColonySnapshot snapshot) : base(capi)
    {
        this.network = network;
        this.snapshot = snapshot;
        pendingName = snapshot.Name;
        pendingEnter = snapshot.EnterText;
        pendingLeave = snapshot.LeaveText;
        Compose();
    }

    public void Update(ColonySnapshot fresh)
    {
        // Keep a half-typed name unless the name really changed.
        if (fresh.Name != snapshot.Name && !renaming) pendingName = fresh.Name;
        if (fresh.EnterText != snapshot.EnterText) pendingEnter = fresh.EnterText;
        if (fresh.LeaveText != snapshot.LeaveText) pendingLeave = fresh.LeaveText;
        snapshot = fresh;
        // On the Town map, timed refreshes only move the dots, so a zoom or a drag in progress is kept.
        if (fresh.Periodic && currentTab == MapTab && mapElement != null && MapBox(fresh) is not null)
        {
            mapElement.SetSnapshot(fresh, capi.World.Player?.Entity?.Pos.XYZ);
            return;
        }
        // Timed progress refreshes would close an open dropdown or take the focus from the name box.
        if (fresh.Periodic && (renaming || currentTab is CitizensTab or JobPanelView or PermissionsTab or BailiffTab or MayorTab or SettingsTab)) return;
        Compose();
    }

    ColonyRole ViewerRole => (ColonyRole)snapshot.ViewerRole;
    bool Officer => ViewerRole >= ColonyRole.Officer;

    void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        int activeTab = currentTab == HelpView ? ActionsTab : currentTab == JobPanelView ? CitizensTab : currentTab;
        var tabs = Tabs.Select(t => new GuiTab { Name = Lang.Get("createacolony:" + t.Lang), DataInt = t.Id, Active = t.Id == activeTab }).ToArray();

        mapElement = null;
        var composer = capi.Gui.CreateCompo("createacolony-townhall", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(snapshot.Name, () => TryClose())
            .AddVerticalTabs(tabs, ElementBounds.Fixed(-160, 35, 160, tabs.Length * 30), (id, tab) => { currentTab = tab.DataInt; page = 0; renaming = false; Compose(); }, "tabs")
            .BeginChildElements(bgBounds);

        switch (currentTab)
        {
            case ActionsTab: ComposeActions(composer); break;
            case MapTab: ComposeMap(composer); break;
            case ProspectingTab: ComposeProspecting(composer); break;
            case BannersTab: ComposeBanners(composer); break;
            case QuestsTab: ComposeQuests(composer); break;
            case InfoTab: ComposeInformation(composer); break;
            case CitizensTab: ComposeCitizens(composer); break;
            case MilitiaTab: ComposeMilitia(composer); break;
            case WinterTab: ComposeWinter(composer); break;
            case BailiffTab: ComposeBailiff(composer); break;
            case MayorTab: ComposeMayor(composer); break;
            case StatsTab: ComposeStatistics(composer); break;
            case PermissionsTab: ComposePermissions(composer); break;
            case SettingsTab: ComposeSettings(composer); break;
            case HelpView: ComposeHelp(composer); break;
            case JobPanelView: ComposeJobPanel(composer); break;
        }

        composer.FitTabs(tabs.Length);
        SingleComposer = composer.EndChildElements().Compose();
        SingleComposer.GetTextInput("colonyname")?.SetValue(pendingName);
        SingleComposer.GetSwitch("permadeath")?.SetValue(snapshot.Permadeath);
        SingleComposer.GetSwitch("selfdefence")?.SetValue(snapshot.SelfDefence);
        SingleComposer.GetSwitch("supportallies")?.SetValue(snapshot.SupportAllies);
        SingleComposer.GetSwitch("banneronnew")?.SetValue(snapshot.BannerOnNew);
        SingleComposer.GetSwitch("bailiffjobs")?.SetValue(snapshot.BailiffGivesJobs);
        SingleComposer.GetSwitch("bailiffrent")?.SetValue(snapshot.BailiffCollectsRent);
        SingleComposer.GetSwitch("bailifflend")?.SetValue(snapshot.BailiffLendsWorkers);
        SingleComposer.GetSwitch("bailiffwages")?.SetValue(snapshot.BailiffPaysWages);
        SingleComposer.GetSwitch("autohiring")?.SetValue(snapshot.AutoHiring);
        SingleComposer.GetSwitch("winterprep")?.SetValue(snapshot.WinterPrep);
        SingleComposer.GetSwitch("autohousing")?.SetValue(snapshot.AutoHousing);
        SingleComposer.GetSwitch("entermessages")?.SetValue(snapshot.EnterMessages);
        SingleComposer.GetSwitch("telloutsiders")?.SetValue(snapshot.TellOutsiders);
        SingleComposer.GetSwitch("settlersunhurt")?.SetValue(snapshot.SettlersUnhurt);
        SingleComposer.GetSwitch("noenemies")?.SetValue(snapshot.NoEnemies);
        SingleComposer.GetSwitch("autoreport")?.SetValue(snapshot.AutoReport);
        SingleComposer.GetSwitch("mayorfree")?.SetValue(snapshot.MayorFreeHuts);
        for (int i = 0; i < snapshot.MayorOrder.Count; i++) SingleComposer.GetSwitch("mayororder" + i)?.SetValue(!snapshot.MayorSkip.Contains(snapshot.MayorOrder[i]));
        SingleComposer.GetTextInput("text-enter")?.SetValue(pendingEnter);
        SingleComposer.GetTextInput("text-leave")?.SetValue(pendingLeave);
        SingleComposer.GetTextInput("rankname")?.SetValue(pendingRankName);
        SingleComposer.GetTextInput("rankrename")?.SetValue(pendingRankRename);
        if (snapshot.Ranks.Find(r => r.Id == chosenRank) is { } shownRank)
            foreach (var perm in ColonyManager.AllPerms)
                SingleComposer.GetSwitch("perm-" + (int)perm)?.SetValue((shownRank.Perms & (int)perm) != 0);
    }

    /// <summary>A row of page buttons along the top of a tab, the chosen one marked. Returns the y below them.</summary>
    double PageButtons(GuiComposer composer, string[] langKeys, int chosen, Action<int> choose, double y, double buttonWidth)
    {
        double x = 0;
        for (int i = 0; i < langKeys.Length; i++)
        {
            int index = i;
            string label = (i == chosen ? "• " : "") + Lang.Get("createacolony:" + langKeys[i]);
            composer.AddSmallButton(label, () => { choose(index); page = 0; Compose(); return true; }, ElementBounds.Fixed(x, y, buttonWidth, 28),
                i == chosen ? EnumButtonStyle.Normal : EnumButtonStyle.Small);
            x += buttonWidth + 8;
        }
        return y + 40;
    }


    // ---- Prospecting tab ------------------------------------------------------------------

    /// <summary>
    /// The Prospecting tab: every ore the colony's prospectors found, strongest first, with the best
    /// reading of each and where it is from the Town Hall. The readings themselves are on the Colony
    /// prospecting layer of the world map.
    /// </summary>
    void ComposeProspecting(GuiComposer composer)
    {
        double y = composer.Text(Lang.Get("createacolony:townhall-prospecting-hint", snapshot.OreReadings, snapshot.ProspectorHuts), GuiKit.Muted(), 0, 30, Width, 10);
        if (snapshot.Ores.Count == 0)
        {
            composer.Text(Lang.Get(snapshot.ProspectorHuts == 0 ? "createacolony:townhall-prospecting-nohut" : "createacolony:ore-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        int pages = (snapshot.Ores.Count + OreRowsPerPage - 1) / OreRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(OreRowsPerPage, snapshot.Ores.Count - page * OreRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * OreRowHeight + 8), 3, 0.8f);
        y += 4;
        foreach (var ore in snapshot.Ores.Skip(page * OreRowsPerPage).Take(OreRowsPerPage))
        {
            string density = Lang.Get("propick-density-" + new[] { "verypoor", "poor", "decent", "high", "veryhigh", "ultrahigh" }[(int)GameMath.Clamp(ore.Best * 7.5, 0, 5)]);
            composer.AddStaticText(Lang.Get("ore-" + ore.Code), CairoFont.WhiteSmallText(), ElementBounds.Fixed(10, y + 4, Width - 20, 20));
            string detail = Lang.Get("createacolony:ore-row", density, ore.Where, ore.Sites)
                + (ore.MinedOut > 0 ? "  " + Lang.Get("createacolony:ore-row-minedout", ore.MinedOut) : "");
            composer.AddStaticText(detail, GuiKit.Detail().WithColor(ore.MinedOut >= ore.Sites ? GuiKit.MutedColor : GuiKit.GoodColor), ElementBounds.Fixed(10, y + 22, Width - 20, 20));
            y += OreRowHeight;
        }
        composer.Pager(page, pages, y + 14, Width, p => { page = p; Compose(); });
    }

    // ---- Actions tab ----------------------------------------------------------------------

    /// <summary>
    /// The Actions tab, like a hut's Main tab: the colony's name and Town Hall level with a pencil to
    /// rename, the colony's numbers, the Town Hall's build options, its land, and the ? guide.
    /// </summary>
    void ComposeActions(GuiComposer composer)
    {
        var hall = snapshot.Buildings.FirstOrDefault();
        double y = 30;

        // Header: name and level, and the pencil.
        double hallStatusHeight = hall?.TargetLevel > 0 ? Math.Max(22, GuiKit.TextHeight(capi, GuiKit.Detail(), GuiDialogHut.StatusText(hall), Width - 20) + 4) : 0;
        double headerHeight = 60 + (hallStatusHeight > 0 ? hallStatusHeight + 2 : 0);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, headerHeight), 3, 0.8f);
        if (renaming)
        {
            composer
                .AddTextInput(ElementBounds.Fixed(10, y + 8, Width - 230, 28), text => pendingName = text, GuiKit.Body(), "colonyname")
                .AddSmallButton(Lang.Get("createacolony:save"), OnRename, ElementBounds.Fixed(Width - 212, y + 8, 100, 28))
                .AddSmallButton(Lang.Get("createacolony:cancel"), () => { renaming = false; pendingName = snapshot.Name; Compose(); return true; }, ElementBounds.Fixed(Width - 106, y + 8, 96, 28));
        }
        else
        {
            composer.AddStaticText(snapshot.Name, GuiKit.Heading(), ElementBounds.Fixed(10, y + 8, Width - 60, 26));
            if (Officer)
            {
                composer.AddIconButton("paintbrush", _ => { renaming = true; pendingName = snapshot.Name; Compose(); }, ElementBounds.Fixed(Width - 42, y + 6, 30, 30), "rename");
                composer.AddHoverText(Lang.Get("createacolony:townhall-rename"), GuiKit.Detail(), 200, ElementBounds.Fixed(Width - 42, y + 6, 30, 30));
            }
        }
        if (hall != null)
        {
            composer
                .AddStaticText(Lang.Get("createacolony:building-townhall") + ": " + GuiDialogHut.LevelText(hall), GuiKit.Detail(), ElementBounds.Fixed(10, y + 36, Width / 2, 20))
                .AddStaticText(Lang.Get("createacolony:placement-style", Lang.Get("createacolony:style-" + BlueprintStyles.Normalize(hall.Style))),
                    GuiKit.Detail().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width / 2, y + 36, Width / 2 - 10, 20));
            if (hall.TargetLevel > 0)
            {
                composer.AddStaticText(GuiDialogHut.StatusText(hall), GuiKit.Detail().WithColor(GuiStyle.ActiveButtonTextColor), ElementBounds.Fixed(10, y + 58, Width - 20, hallStatusHeight));
            }
        }
        y += headerHeight + 8;

        // The colony's numbers in two columns on a sunken panel.
        int alive = snapshot.Citizens.Count(c => c.State != "dead" && !c.Visitor);
        int hungry = snapshot.Citizens.Count(c => c.State == "hungry");
        const double pad = 10;
        double column = (Width - 3 * pad) / 2;
        composer.AddInset(ElementBounds.Fixed(0, y, Width, 3 * 24 + 2 * pad), 3, 0.8f);
        double left = y + pad, right = y + pad;
        left = composer.Row(Lang.Get("createacolony:stat-level"), snapshot.Level.ToString(), pad, left, column);
        string settlers = snapshot.PopulationCap > 0 ? Lang.Get("createacolony:townhall-of", alive, snapshot.PopulationCap) : alive.ToString();
        left = composer.Row(Lang.Get("createacolony:stat-settlers"), settlers, pad, left, column, snapshot.PopulationCap > 0 && alive >= snapshot.PopulationCap ? GuiKit.WarnColor : null);
        composer.Row(Lang.Get("createacolony:stat-hungry"), hungry.ToString(), pad, left, column, hungry > 0 ? GuiKit.WarnColor : null);
        right = composer.Row(Lang.Get("createacolony:stat-beds"), snapshot.BedCount.ToString(), 2 * pad + column, right, column);
        right = composer.Row(Lang.Get("createacolony:stat-members"), snapshot.Members.Count.ToString(), 2 * pad + column, right, column);
        if (snapshot.AverageHappiness >= 0)
        {
            string mood = Lang.Get("createacolony:mood-value", snapshot.AverageHappiness, Lang.Get("createacolony:mood-" + MoodWord(snapshot.AverageHappiness)));
            composer.Row(Lang.Get("createacolony:stat-happiness"), mood, 2 * pad + column, right, column, snapshot.AverageHappiness < 30 ? GuiKit.WarnColor : null);
        }
        y += 3 * 24 + 2 * pad + 10;

        int visitors = snapshot.Citizens.Count(c => c.Visitor);
        if (visitors > 0) y = composer.Text(Lang.Get("createacolony:townhall-visitors", visitors), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 8);

        // Build options for the Town Hall itself; other buildings are ordered from their own huts.
        if (hall != null && Officer && (hall.TargetLevel > 0 || hall.Level < hall.MaxLevel || hall.Level == 0 || hall.MaxLevel > 0))
        {
            y = composer.Section(Lang.Get("createacolony:hut-build-options"), y, Width);
            y = AddBuildButtons(composer, hall, y);
        }
        y = composer.Text(Lang.Get("createacolony:townhall-builder-hint"), GuiKit.Muted(), 0, y, Width, 10);

        // The colony's land, as MineColonies shows its protected area.
        y = composer.Section(Lang.Get("createacolony:townhall-land"), y, Width);
        y = composer.Text(Lang.Get("createacolony:overview-claim", snapshot.ClaimColumns.ToString("N0"), snapshot.ClaimSquares, snapshot.TownHallClaimSize),
            GuiKit.Detail(), 0, y, Width, 10);

        if (!Officer) y = composer.Text(Lang.Get("createacolony:visitor-readonly"), GuiKit.Muted(), 0, y, Width, 10);

        // Footer: the guide.
        composer.AddSmallButton("?", () => Open(HelpView), ElementBounds.Fixed(0, y, 40, 28));
        composer.AddHoverText(Lang.Get("createacolony:townhall-help-hover"), GuiKit.Detail(), 220, ElementBounds.Fixed(0, y, 40, 28));
        y += 38;
    }

    /// <summary>Upgrade or Cancel, and Turn until the first level is built. Returns the y below them.</summary>
    double AddBuildButtons(GuiComposer composer, BuildingView b, double y)
    {
        int id = b.BuildingId;
        if (b.TargetLevel > 0)
        {
            composer.AddSmallButton(Lang.Get(b.Repairing ? "createacolony:repair-stop" : "createacolony:build-cancel"), () => Send(ColonyAction.CancelBuild, id), ElementBounds.Fixed(0, y, 160, 28));
            if (!b.Repairing && b.TargetLevel == b.Level) AddRepairButton(composer, b, 170, y);
            return y + 36;
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
            // Like a hut, the Town Hall can be turned until its first level is built.
            composer.AddSmallButton(Lang.Get("createacolony:build-rotate", b.Rotation), () => Send(ColonyAction.Rotate, id), ElementBounds.Fixed(x, y, 190, 28));
        }
        else if (b.MaxLevel > 0) AddRepairButton(composer, b, x, y);
        return y + 36;
    }

    /// <summary>Repair: a builder puts the Town Hall back as its blueprint has it (ColonyManager.Repair.cs).</summary>
    void AddRepairButton(GuiComposer composer, BuildingView b, double x, double y)
    {
        int id = b.BuildingId;
        composer.AddSmallButton(Lang.Get("createacolony:repair"), () => Send(ColonyAction.Repair, id), ElementBounds.Fixed(x, y, 160, 28));
        composer.AddHoverText(Lang.Get("createacolony:repair-hint"), GuiKit.Detail(), 300, ElementBounds.Fixed(x, y, 160, 28));
    }

    // ---- Quests tab -----------------------------------------------------------------------

    /// <summary>
    /// The colony's quests at a glance: how far each colony chapter is, the quests ready to claim, and
    /// a button that opens the quest book itself.
    /// </summary>
    void ComposeQuests(GuiComposer composer)
    {
        double y = 30;
        var library = network.ClientQuests;
        var states = snapshot.Quests.ToDictionary(s => s.Code, s => s);
        QuestStatus StatusOf(QuestDef q) => states.TryGetValue(q.Code, out var s) ? (QuestStatus)s.Status : QuestStatus.Locked;

        composer.AddSmallButton(Lang.Get("createacolony:questbook-open"), () => { network.RequestQuestBook(); return true; }, ElementBounds.Fixed(Width - 180, y - 2, 180, 28));
        y = composer.Text(Lang.Get("createacolony:townhall-quests-intro"), GuiKit.Detail(), 0, y + 4, Width - 190, 10);

        // Quests ready to claim, so officers see them without opening the book.
        var ready = library.All.Where(q => q.PerColony && StatusOf(q) == QuestStatus.Complete).Take(5).ToList();
        if (ready.Count > 0)
        {
            y = composer.Section(Lang.Get("createacolony:townhall-quests-ready"), y, Width);
            foreach (var q in ready)
            {
                y = composer.Text("• " + QuestLibrary.Title(q) + " — " + QuestLibrary.Title(q.Chapter), GuiKit.Body().WithColor(new[] { 1.0, 0.82, 0.25, 1.0 }), 10, y, Width - 10, 2);
            }
            y += 8;
        }

        // Each colony chapter as a row: name, done / total, and a bar.
        foreach (var group in library.Groups)
        {
            var chapters = group.Chapters.Where(c => c.PerColony).ToList();
            if (chapters.Count == 0) continue;
            y = composer.Section(QuestLibrary.Title(group), y, Width);
            foreach (var chapter in chapters)
            {
                int total = chapter.Quests.Count(q => !q.Secret || StatusOf(q) != QuestStatus.Locked);
                int done = chapter.Quests.Count(q => StatusOf(q) == QuestStatus.Claimed);
                var font = done > 0 && done == total ? GuiKit.Body().WithColor(GuiKit.GoodColor) : GuiKit.Body();
                composer
                    .AddStaticText(QuestLibrary.Title(chapter), font, ElementBounds.Fixed(10, y, Width * 0.5, 22))
                    .AddStaticText(total == 0 ? Lang.Get("createacolony:townhall-quests-later") : done + " / " + total,
                        GuiKit.Detail().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width * 0.5, y + 1, Width * 0.5 - 10, 22));
                y += 24;
            }
            y += 4;
        }
    }

    void ComposeHelp(GuiComposer composer)
    {
        composer
            .AddStaticText(Lang.Get("createacolony:help-title", Lang.Get("createacolony:building-townhall")), GuiKit.Heading(), ElementBounds.Fixed(0, 30, Width - 110, 26))
            .AddSmallButton(Lang.Get("createacolony:back"), () => Open(ActionsTab), ElementBounds.Fixed(Width - 100, 28, 100, 28));
        composer.Rich(Lang.Get("createacolony:guide-townhall"), GuiKit.Detail(), 0, 64, Width);
    }

    bool Open(int view)
    {
        currentTab = view;
        page = 0;
        renaming = false;
        Compose();
        return true;
    }

    // ---- Town map tab ---------------------------------------------------------------------

    /// <summary>The area the Town map shows: the colony's land and buildings plus a margin, or null if it has neither.</summary>
    static (int MinX, int MinZ, int MaxX, int MaxZ)? MapBox(ColonySnapshot snapshot)
    {
        var xs = new List<int>();
        var zs = new List<int>();
        for (int r = 0; r + 3 < snapshot.ClaimRects.Count; r += 4)
        {
            xs.Add(snapshot.ClaimRects[r]); xs.Add(snapshot.ClaimRects[r + 2]);
            zs.Add(snapshot.ClaimRects[r + 1]); zs.Add(snapshot.ClaimRects[r + 3]);
        }
        foreach (var b in snapshot.MapBuildings)
        {
            xs.Add(b.X1); xs.Add(b.X2);
            zs.Add(b.Z1); zs.Add(b.Z2);
        }
        if (xs.Count == 0) return null;
        return (xs.Min() - MapMargin, zs.Min() - MapMargin, xs.Max() + MapMargin, zs.Max() + MapMargin);
    }

    /// <summary>
    /// The Town map: the colony's land and buildings from above, with the settlers and the player.
    /// The mouse wheel or the + and − buttons zoom, dragging moves the map, Fit shows it all again, and
    /// pointing at a building names it. The ground is all land near the colony that a player or a settler has loaded.
    /// </summary>
    void ComposeMap(GuiComposer composer)
    {
        double y = 30;
        if (MapBox(snapshot) is not { } box)
        {
            composer.Text(Lang.Get("createacolony:townmap-empty"), GuiKit.Body(), 0, y, Width);
            return;
        }
        var mapBounds = ElementBounds.Fixed(0, y, Width, MapHeight);
        mapElement = new GuiElementColonyMap(capi, mapBounds, snapshot, box, capi.World.Player?.Entity?.Pos.XYZ, mapView);
        composer
            .AddInset(mapBounds.FlatCopy(), 3, 0.8f)
            .AddInteractiveElement(mapElement, "townmap");
        y += MapHeight + 8;

        // Zoom buttons on the left, Find me on the right.
        composer
            .AddSmallButton(Lang.Get("createacolony:townmap-zoomout"), () => { mapElement?.ZoomBy(1 / 1.5); return true; }, ElementBounds.Fixed(0, y, 34, 28))
            .AddSmallButton(Lang.Get("createacolony:townmap-zoomin"), () => { mapElement?.ZoomBy(1.5); return true; }, ElementBounds.Fixed(40, y, 34, 28))
            .AddSmallButton(Lang.Get("createacolony:townmap-fit"), () => { mapElement?.Fit(); return true; }, ElementBounds.Fixed(80, y, 50, 28))
            .AddSmallButton(Lang.Get("createacolony:townmap-findme"), () => { mapElement?.CenterOnPlayer(); return true; }, ElementBounds.Fixed(Width - 110, y, 110, 28));
        y += 36;
        y = composer.Text(Lang.Get("createacolony:townmap-legend"), GuiKit.Detail(), 0, y, Width, 6);
        composer.Text(Lang.Get("createacolony:townmap-hint"), GuiKit.Muted(), 0, y, Width);
    }

    // ---- Banners tab ----------------------------------------------------------------------

    /// <summary>
    /// The colony banner (a Heraldry: Banners banner): set it from the player's hand, copy its design
    /// onto the banners of the Town Hall, the Guard Towers or every building, and put it on new buildings.
    /// </summary>
    void ComposeBanners(GuiComposer composer)
    {
        double y = 30;
        if (!snapshot.HeraldryInstalled)
        {
            y = composer.Text(Lang.Get("createacolony:banner-needs-mod"), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 12);
        }

        y = composer.Section(Lang.Get("createacolony:banner-colony"), y, Width);
        ItemStack? banner = null;
        if (snapshot.BannerStack != null)
        {
            banner = new ItemStack(snapshot.BannerStack);
            if (!banner.ResolveBlockOrItem(capi.World)) banner = null;
        }
        composer.AddInset(ElementBounds.Fixed(0, y, Width, 76), 3, 0.8f);
        if (banner != null)
        {
            composer.AddRichtext(new RichTextComponentBase[]
            {
                new ItemstackTextComponent(capi, banner, 64, 10),
                new RichTextComponent(capi, banner.GetName(), GuiKit.Body()) { VerticalAlign = EnumVerticalAlign.Middle }
            }, ElementBounds.Fixed(6, y + 4, Width - 200, 70));
        }
        else
        {
            composer.AddStaticText(Lang.Get("createacolony:banner-none-yet"), GuiKit.Detail(), ElementBounds.Fixed(10, y + 28, Width - 200, 22));
        }
        if (Officer)
        {
            composer.AddSmallButton(Lang.Get("createacolony:banner-use-held"), () => SendText(ColonyAction.SetColonyBanner, ""), ElementBounds.Fixed(Width - 180, y + 24, 170, 28));
        }
        y += 84;
        y = composer.Text(Lang.Get("createacolony:banner-hint"), GuiKit.Muted(), 0, y, Width, 12);

        if (!Officer || banner == null) return;

        y = composer.Section(Lang.Get("createacolony:banner-replace"), y, Width);
        composer
            .AddSmallButton(Lang.Get("createacolony:banner-on-townhall"), () => SendText(ColonyAction.ReplaceBanners, "townhall"), ElementBounds.Fixed(0, y, 180, 28))
            .AddSmallButton(Lang.Get("createacolony:banner-on-towers"), () => SendText(ColonyAction.ReplaceBanners, "guardtower"), ElementBounds.Fixed(190, y, 180, 28))
            .AddSmallButton(Lang.Get("createacolony:banner-on-all"), () => SendText(ColonyAction.ReplaceBanners, "all"), ElementBounds.Fixed(380, y, 180, 28));
        y += 36;
        y = composer.Text(Lang.Get("createacolony:banner-replace-hint"), GuiKit.Muted(), 0, y, Width, 12);

        composer
            .AddSwitch(on => SendText(ColonyAction.SetBannerOnNew, on ? "1" : "0"), ElementBounds.Fixed(0, y, 30, 30), "banneronnew")
            .AddStaticText(Lang.Get("createacolony:banner-on-new"), GuiKit.Body(), ElementBounds.Fixed(40, y + 5, Width - 40, 25));
    }

    bool SendText(ColonyAction action, string text)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = action, Text = text });
        return true;
    }

    // ---- Information tab ------------------------------------------------------------------

    void ComposeInformation(GuiComposer composer)
    {
        double y = PageButtons(composer, new[] { "townhall-events", "tab-orders" }, infoPage, p => infoPage = p, 30, 150);
        if (infoPage == 0) ComposeEvents(composer, y);
        else ComposeWorkOrders(composer, y);
    }

    /// <summary>The event log, newest first: settlers joining, leaving, dying, and buildings placed and finished.</summary>
    void ComposeEvents(GuiComposer composer, double y)
    {
        var events = Enumerable.Reverse(snapshot.Events).ToList();
        if (events.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:townhall-no-events"), GuiKit.Body(), 0, y, Width);
            return;
        }
        int pages = (events.Count + EventsPerPage - 1) / EventsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(EventsPerPage, events.Count - page * EventsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * EventRowHeight + 8), 3, 0.8f);
        y += 4;
        foreach (var e in events.Skip(page * EventsPerPage).Take(EventsPerPage))
        {
            composer
                .AddStaticText(When(e.TotalDays), GuiKit.Muted(), ElementBounds.Fixed(10, y + 5, 140, 20))
                .AddStaticText(EventText(e), GuiKit.Detail(), ElementBounds.Fixed(155, y + 4, Width - 165, 20));
            y += EventRowHeight;
        }
        composer.Pager(page, pages, y + 12, Width, p => { page = p; Compose(); });
    }

    /// <summary>A moment in in-game days as the game's own calendar shows it: "1 May, 14:00", or "1 May, Year 2" in another year.</summary>
    string When(double totalDays)
    {
        var calendar = capi.World.Calendar;
        int daysPerMonth = Math.Max(1, calendar.DaysPerMonth);
        int daysPerYear = Math.Max(1, calendar.DaysPerYear);
        int year = (int)(totalDays / daysPerYear);
        int dayOfYear = (int)(totalDays - (double)year * daysPerYear);
        int month = Math.Clamp(dayOfYear / daysPerMonth, 0, 11);
        int day = dayOfYear % daysPerMonth + 1;
        string monthName = Lang.Get("month-" + (EnumMonth)(month + 1));
        if (year != (int)(calendar.TotalDays / daysPerYear)) return Lang.Get("createacolony:townhall-event-date-year", day, monthName, year);
        double hours = (totalDays - Math.Floor(totalDays)) * calendar.HoursPerDay;
        int hour = (int)hours;
        int minute = (int)((hours - hour) * 60);
        return Lang.Get("createacolony:townhall-event-time", day, monthName, hour.ToString("00"), minute.ToString("00"));
    }

    static string EventText(EventView e)
    {
        var args = e.Args.Select(a => a.StartsWith('@') ? Lang.Get("createacolony:" + a[1..]) : a).Cast<object>().ToArray();
        return ColonyMoney.Get("createacolony:" + e.Key, args);
    }

    /// <summary>Every building waiting to be built or upgraded, in the order builders take them, with Cancel for officers.</summary>
    void ComposeWorkOrders(GuiComposer composer, double y)
    {
        var orders = snapshot.WorkOrders;
        if (orders.Count == 0)
        {
            y = composer.Text(Lang.Get("createacolony:hut-no-orders"), GuiKit.Body(), 0, y, Width, 10);
            return;
        }
        int pages = (orders.Count + OrdersPerPage - 1) / OrdersPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(OrdersPerPage, orders.Count - page * OrdersPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * OrderRowHeight + 8), 3, 0.8f);
        y += 4;
        int number = page * OrdersPerPage;
        foreach (var o in orders.Skip(page * OrdersPerPage).Take(OrdersPerPage))
        {
            number++;
            string title = number + ". " + (o.Removing || o.Repairing ? GuiDialogHut.OrderTitle(o) : o.Name + " " + Lang.Get("createacolony:order-level", o.TargetLevel));
            composer
                .AddStaticText(title, GuiKit.Body(), ElementBounds.Fixed(10, y + 6, Width - 250, 25))
                .AddStaticText(GuiDialogHut.StatusText(o), GuiKit.Detail().WithColor(GuiStyle.ActiveButtonTextColor), ElementBounds.Fixed(10, y + 28, Width - 250, 32));
            if (Officer)
            {
                int id = o.TeardownId > 0 ? o.TeardownId : o.BuildingId;
                var action = o.TeardownId > 0 ? ColonyAction.CancelTeardown : ColonyAction.CancelBuild;
                string key = GuiDialogHut.OrderKey(o);
                composer
                    .AddSmallButton("^", () => Move(key, ColonyManager.MoveUp), ElementBounds.Fixed(Width - 236, y + 14, 30, 28))
                    .AddSmallButton("v", () => Move(key, ColonyManager.MoveDown), ElementBounds.Fixed(Width - 202, y + 14, 30, 28))
                    .AddSmallButton(Lang.Get("createacolony:priority-top"), () => Move(key, ColonyManager.MoveTop), ElementBounds.Fixed(Width - 168, y + 14, 62, 28))
                    .AddSmallButton(Lang.Get("createacolony:build-cancel"), () => Send(action, id), ElementBounds.Fixed(Width - 100, y + 14, 90, 28));
            }
            y += OrderRowHeight;
        }
        composer.Pager(page, pages, y + 12, Width, p => { page = p; Compose(); });
        y += pages > 1 ? 52 : 16;
        y = composer.Text(Lang.Get("createacolony:townhall-orders-hint"), GuiKit.Muted(), 0, y, Width, 6);
    }

    // ---- Citizens tab ---------------------------------------------------------------------

    /// <summary>The Citizens tab: Current Citizens (the living and wanderers) and Dead Citizens pages.</summary>
    void ComposeCitizens(GuiComposer composer)
    {
        int living = snapshot.Citizens.Count(c => c.State != "dead" && !c.Visitor), dead = snapshot.Citizens.Count(c => c.State == "dead");
        var labels = new[] { "citizens-current", "citizens-dead" };
        double top = 30;
        double x = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            string label = (i == citizensPage ? "• " : "") + Lang.Get("createacolony:" + labels[i], i == 0 ? living : dead);
            composer.AddSmallButton(label, () => { citizensPage = index; page = 0; Compose(); return true; }, ElementBounds.Fixed(x, top, 200, 28),
                i == citizensPage ? EnumButtonStyle.Normal : EnumButtonStyle.Small);
            x += 208;
        }
        top += 40;
        if (citizensPage == 1) ComposeDeadCitizens(composer, top);
        else ComposeCurrentCitizens(composer, top);
    }

    /// <summary>The dead: name, the job they had, when they died, and whether they have been buried.</summary>
    void ComposeDeadCitizens(GuiComposer composer, double top)
    {
        var dead = snapshot.Citizens.Where(c => c.State == "dead").OrderByDescending(c => c.DiedTotalDays).ThenBy(c => c.Name).ToList();
        double y;
        if (dead.Count == 0)
        {
            y = composer.Text(Lang.Get("createacolony:citizens-no-dead"), GuiKit.Body(), 0, top, Width);
        }
        else
        {
            composer
                .AddStaticText(Lang.Get("createacolony:col-name"), GuiKit.Muted(), ElementBounds.Fixed(10, top, 150, 20))
                .AddStaticText(Lang.Get("createacolony:col-job"), GuiKit.Muted(), ElementBounds.Fixed(140, top, 95, 20))
                .AddStaticText(Lang.Get("createacolony:col-died"), GuiKit.Muted(), ElementBounds.Fixed(240, top, 105, 20))
                .AddStaticText(Lang.Get("createacolony:col-cause"), GuiKit.Muted(), ElementBounds.Fixed(350, top, 115, 20))
                .AddStaticText(Lang.Get("createacolony:col-burial"), GuiKit.Muted(), ElementBounds.Fixed(470, top, 90, 20));

            int pages = (dead.Count + RowsPerPage - 1) / RowsPerPage;
            page = Math.Clamp(page, 0, pages - 1);
            int shown = Math.Min(RowsPerPage, dead.Count - page * RowsPerPage);
            composer.AddInset(ElementBounds.Fixed(0, top + 22, Width, shown * RowHeight + 6), 3, 0.8f);
            y = top + 25;
            foreach (var c in dead.Skip(page * RowsPerPage).Take(RowsPerPage))
            {
                string died = c.DiedTotalDays > 0 ? When(c.DiedTotalDays) : Lang.Get("createacolony:citizens-died-unknown");
                var burialFont = c.Buried ? GuiKit.Detail().WithColor(GuiKit.GoodColor) : GuiKit.Detail().WithColor(GuiKit.WarnColor);
                composer
                    .AddStaticText(c.Name, GuiKit.Body().WithColor(GuiKit.MutedColor), ElementBounds.Fixed(10, y + 6, 128, 25))
                    .AddStaticText(c.Child ? Lang.Get("createacolony:citizens-child") : JobName(c.Job), GuiKit.Detail(), ElementBounds.Fixed(140, y + 8, 95, 25))
                    .AddStaticText(died, GuiKit.Detail(), ElementBounds.Fixed(240, y + 8, 105, 25))
                    .AddStaticText(DeathCauseName(c.DeathCause), GuiKit.Detail(), ElementBounds.Fixed(350, y + 8, 115, 25))
                    .AddStaticText(Lang.Get(c.Buried ? "createacolony:citizens-buried" : "createacolony:citizens-not-buried"), burialFont, ElementBounds.Fixed(470, y + 8, 90, 25));
                y += RowHeight;
            }
            composer.Pager(page, pages, y + 14, Width, p => { page = p; Compose(); });
            y += pages > 1 ? 54 : 16;
        }
    }

    /// <summary>What killed a settler, in words: "Wolf (male)", a player's name, or "Starved".</summary>
    public static string DeathCauseName(string cause)
    {
        int colon = cause.IndexOf(':');
        if (colon < 0) return Lang.Get("createacolony:citizens-died-unknown");
        string kind = cause[..colon], what = cause[(colon + 1)..];
        switch (kind)
        {
            case "player":
            case "settler":
                return what.Length > 0 ? what : Lang.Get("createacolony:death-cause-" + kind);
            case "entity":
                var code = new AssetLocation(what);
                string key = code.Domain + ":item-creature-" + code.Path;
                if (Lang.HasTranslation(key)) return Lang.Get(key);
                // A creature with no name of its own: its first code part ("wolf" of wolf-male).
                string first = code.Domain + ":item-creature-" + code.Path.Split('-')[0];
                return Lang.HasTranslation(first) ? Lang.Get(first) : code.Path;
            default:
                string type = "createacolony:death-cause-" + what.ToLowerInvariant();
                return Lang.HasTranslation(type) ? Lang.Get(type) : what;
        }
    }

    /// <summary>The living, with their state, food and bed, and (for officers) their job and Recall.</summary>
    void ComposeCurrentCitizens(GuiComposer composer, double top)
    {
        // Wanderers asking to join come first, so they are not missed.
        var citizens = snapshot.Citizens.Where(c => c.State != "dead").OrderByDescending(c => c.Visitor).ThenBy(c => c.Name).ToList();
        if (citizens.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:no-citizens"), GuiKit.Body(), 0, top, Width);
            return;
        }

        composer
            .AddStaticText(Lang.Get("createacolony:col-name"), GuiKit.Muted(), ElementBounds.Fixed(10, top, 150, 20))
            .AddStaticText(Lang.Get("createacolony:col-status"), GuiKit.Muted(), ElementBounds.Fixed(165, top, 170, 20))
            .AddStaticText(Lang.Get("createacolony:col-job"), GuiKit.Muted(), ElementBounds.Fixed(340, top, 150, 20));

        int pages = (citizens.Count + RowsPerPage - 1) / RowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(RowsPerPage, citizens.Count - page * RowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, top + 22, CitizensWidth, shown * RowHeight + 6), 3, 0.8f);
        double y = top + 25;
        foreach (var c in citizens.Skip(page * RowsPerPage).Take(RowsPerPage))
        {
            if (c.Visitor)
            {
                ComposeVisitor(composer, c, y);
                y += RowHeight;
                continue;
            }
            string status = Lang.Get("createacolony:settler-status", Lang.Get("createacolony:state-" + c.State), c.FoodPercent,
                Lang.Get(c.HasBed ? "createacolony:has-bed" : "createacolony:no-bed"));
            composer
                .AddStaticText(c.Name, c.State == "dead" ? GuiKit.Body().WithColor(GuiKit.MutedColor) : GuiKit.Body(), ElementBounds.Fixed(10, y + 6, 150, 25))
                .AddStaticText(status, c.State == "hungry" ? GuiKit.Detail().WithColor(GuiKit.WarnColor) : GuiKit.Detail(), ElementBounds.Fixed(165, y + 8, 172, 25));
            if (c.TraitsText.Length > 0) composer.AddHoverText(Lang.Get("createacolony:traits-hover", c.TraitsText), GuiKit.Detail(), 260, ElementBounds.Fixed(10, y + 6, 150, 25));

            if (c.Child && c.State != "dead")
            {
                composer.AddStaticText(Lang.Get("createacolony:child-grows-up", c.GrowsUpHours), GuiKit.Detail(), ElementBounds.Fixed(340, y + 8, 145, 25));
                if (Officer && c.State != "downed")
                {
                    int childId = c.CitizenId;
                    composer.AddSmallButton(Lang.Get("createacolony:townhall-recall"), () => SendCitizen(ColonyAction.RecallCitizen, childId), ElementBounds.Fixed(CitizensWidth - 70, y + 2, 70, 28));
                }
            }
            else
            {
                // The real job (0.203.0): a settler the Bailiff lent out keeps its own job's name, with what it does now under it.
                bool lent = c.HomeJob.Length > 0;
                string real = lent ? c.HomeJob : c.Job;
                composer.AddStaticText(JobName(real), GuiKit.Body(), ElementBounds.Fixed(340, y + (lent ? 0 : 6), 135, 20));
                if (lent)
                {
                    composer.AddStaticText(Lang.Get(c.Job.Length == 0 ? "createacolony:citizens-lent-hauling" : "createacolony:citizens-lent-as", JobName(c.Job)),
                        GuiKit.Muted(), ElementBounds.Fixed(340, y + 17, 135, 16));
                }
                if (Officer && c.State != "dead")
                {
                    int citizenId = c.CitizenId;
                    composer.AddSmallButton(Lang.Get(real.Length == 0 ? "createacolony:jobpanel-assign-job" : "createacolony:jobpanel-assign-new"),
                        () => { jobPanelCitizen = citizenId; return Open(JobPanelView); }, ElementBounds.Fixed(478, y + 2, 108, 28));
                    if (c.State != "downed")
                    {
                        composer.AddSmallButton(Lang.Get("createacolony:townhall-recall"), () => SendCitizen(ColonyAction.RecallCitizen, citizenId), ElementBounds.Fixed(CitizensWidth - 70, y + 2, 70, 28));
                    }
                }
            }
            y += RowHeight;
        }

        composer.Pager(page, pages, y + 14, CitizensWidth, p => { page = p; Compose(); });
        y += pages > 1 ? 54 : 16;
        if (Officer) y = composer.Text(Lang.Get("createacolony:townhall-recall-hint"), GuiKit.Muted(), 0, y, Width, 6);
    }

    /// <summary>
    /// The job panel (Aaron, 0.203.0): every job A to Z with "No job" first, and for each one how good this settler is at
    /// it, whether it likes it, whether a trait rules it out, and how many places are free. One press gives the job.
    /// </summary>
    void ComposeJobPanel(GuiComposer composer)
    {
        const double W = CitizensWidth;
        var c = snapshot.Citizens.Find(x => x.CitizenId == jobPanelCitizen);
        if (c == null || c.State == "dead" || c.Child || c.Visitor || !Officer)
        {
            currentTab = CitizensTab;
            ComposeCitizens(composer);
            return;
        }
        bool lent = c.HomeJob.Length > 0;
        string real = lent ? c.HomeJob : c.Job;
        if (real == "bishop") real = Jobs.Priest;
        composer
            .AddStaticText(Lang.Get("createacolony:jobpanel-title", c.Name), GuiKit.Heading(), ElementBounds.Fixed(0, 30, W - 110, 26))
            .AddSmallButton(Lang.Get("createacolony:back"), () => Open(CitizensTab), ElementBounds.Fixed(W - 100, 28, 100, 28));
        string now = Lang.Get("createacolony:jobpanel-now", JobName(real));
        if (lent) now += " " + Lang.Get(c.Job.Length == 0 ? "createacolony:jobpanel-lent-hauling" : "createacolony:jobpanel-lent-as", JobName(c.Job));
        double y = composer.Text(now, GuiKit.Body(), 0, 64, W, 2);
        if (c.TraitsText.Length > 0) y = composer.Text(Lang.Get("createacolony:traits-hover", c.TraitsText), GuiKit.Muted(), 0, y, W, 2);
        y += 8;

        // "No job" first, then every job by its name.
        var jobs = new[] { "" }.Concat(Jobs.All.Where(j => j.Length > 0).OrderBy(JobName, StringComparer.CurrentCultureIgnoreCase)).ToList();
        composer
            .AddStaticText(Lang.Get("createacolony:col-job"), GuiKit.Muted(), ElementBounds.Fixed(10, y, 170, 20))
            .AddStaticText(Lang.Get("createacolony:jobpanel-col-skill"), GuiKit.Muted(), ElementBounds.Fixed(185, y, 125, 20))
            .AddStaticText(Lang.Get("createacolony:jobpanel-col-likes"), GuiKit.Muted(), ElementBounds.Fixed(315, y, 150, 20))
            .AddStaticText(Lang.Get("createacolony:jobpanel-col-places"), GuiKit.Muted(), ElementBounds.Fixed(470, y, 100, 20));
        int pages = (jobs.Count + JobRowsPerPage - 1) / JobRowsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(JobRowsPerPage, jobs.Count - page * JobRowsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y + 22, W, shown * JobRowHeight + 6), 3, 0.8f);
        y += 25;
        int citizenId = c.CitizenId;
        foreach (string job in jobs.Skip(page * JobRowsPerPage).Take(JobRowsPerPage))
        {
            bool has = job == real;
            composer.AddStaticText(JobName(job), has ? GuiKit.Body().WithColor(GuiKit.GoodColor) : GuiKit.Body(), ElementBounds.Fixed(10, y + 5, 172, 22));
            string blockedBy = "";
            if (job.Length > 0)
            {
                // The skill this job uses, and how the settler stands in it.
                int at = snapshot.JobSkillJobs.IndexOf(job);
                string skill = at >= 0 && at < snapshot.JobSkillCodes.Count ? snapshot.JobSkillCodes[at] : "";
                int s = skill.Length == 0 ? -1 : c.SkillCodes.IndexOf(skill);
                int level = s >= 0 && s < c.SkillLevels.Count ? c.SkillLevels[s] : 0;
                int passion = s >= 0 && s < c.SkillPassions.Count ? c.SkillPassions[s] : 0;
                blockedBy = s >= 0 && s < c.SkillBlocks.Count ? c.SkillBlocks[s] : "";
                bool trait = c.JobTraits.Contains(job);
                if (skill.Length > 0)
                {
                    composer.AddStaticText(Lang.Get("createacolony:jobpanel-skill", Lang.Get("createacolony:skill-" + skill), level),
                        level >= 3 ? GuiKit.Detail().WithColor(GuiKit.GoodColor) : GuiKit.Detail(), ElementBounds.Fixed(185, y + 7, 128, 20));
                }
                string likes = blockedBy.Length > 0 ? Lang.Get("createacolony:jobpanel-never", Lang.Get("createacolony:trait-" + blockedBy))
                    : string.Join(", ", new[] { passion > 0 ? Lang.Get("createacolony:jobpanel-passion-" + Math.Min(2, passion)) : "", trait ? Lang.Get("createacolony:jobpanel-trait") : "" }.Where(t => t.Length > 0));
                if (likes.Length > 0)
                {
                    composer.AddStaticText(likes, GuiKit.Detail().WithColor(blockedBy.Length > 0 ? GuiKit.WarnColor : GuiKit.GoodColor), ElementBounds.Fixed(315, y + 7, 153, 20));
                }
                var count = snapshot.Jobs.Find(j => j.Job == job);
                int places = count?.Places ?? 0, free = places - (count?.Workers ?? 0);
                string placesText = places < 0 ? Lang.Get("createacolony:jobpanel-places-any")
                    : places == 0 ? Lang.Get("createacolony:jobpanel-places-none")
                    : free > 0 ? Lang.Get("createacolony:jobpanel-places-free", free, places)
                    : Lang.Get("createacolony:jobpanel-places-full", places);
                composer.AddStaticText(placesText, places < 0 || free > 0 ? GuiKit.Detail() : GuiKit.Muted(), ElementBounds.Fixed(470, y + 7, 105, 20));
            }
            if (has)
            {
                composer.AddStaticText(Lang.Get("createacolony:jobpanel-has"), GuiKit.Muted(), ElementBounds.Fixed(W - 82, y + 7, 80, 20));
            }
            else if (blockedBy.Length == 0)
            {
                string code = job;
                composer.AddSmallButton(Lang.Get(job.Length == 0 ? "createacolony:jobpanel-remove" : "createacolony:manage-assign"), () =>
                {
                    OnSetJob(citizenId, code);
                    return Open(CitizensTab);
                }, ElementBounds.Fixed(W - 82, y + 1, 80, 26));
            }
            y += JobRowHeight;
        }
        composer.Pager(page, pages, y + 14, W, p => { page = p; Compose(); });
        y += pages > 1 ? 54 : 16;
        composer.Text(Lang.Get("createacolony:jobpanel-hint"), GuiKit.Muted(), 0, y, W, 6);
    }

    /// <summary>A wanderer's row: who, how long it still waits, and Welcome / Turn away buttons for officers.</summary>
    void ComposeVisitor(GuiComposer composer, CitizenView c, double y)
    {
        var visitorFont = CairoFont.WhiteSmallText().WithColor(new[] { 1.0, 0.85, 0.4, 1.0 });
        composer
            .AddStaticText(c.Name, visitorFont, ElementBounds.Fixed(10, y + 6, 150, 25))
            .AddStaticText(Lang.Get("createacolony:visitor-status", c.VisitorHoursLeft), GuiKit.Detail(), ElementBounds.Fixed(165, y + 8, 172, 25));
        if (c.TraitsText.Length > 0) composer.AddHoverText(Lang.Get("createacolony:traits-hover", c.TraitsText), GuiKit.Detail(), 260, ElementBounds.Fixed(10, y + 6, 150, 25));
        if (!Officer) return;
        int citizenId = c.CitizenId;
        composer
            .AddSmallButton(Lang.Get("createacolony:visitor-welcome"), () => SendCitizen(ColonyAction.WelcomeVisitor, citizenId), ElementBounds.Fixed(340, y + 2, 105, 28))
            .AddSmallButton(Lang.Get("createacolony:visitor-turn-away"), () => SendCitizen(ColonyAction.TurnAwayVisitor, citizenId), ElementBounds.Fixed(450, y + 2, 110, 28));
    }

    bool SendCitizen(ColonyAction action, int citizenId)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = action, CitizenId = citizenId });
        return true;
    }

    // ---- Statistics tab -------------------------------------------------------------------

    void ComposeStatistics(GuiComposer composer)
    {
        double y = PageButtons(composer, new[] { "townhall-stats-colony", "townhall-stats-work" }, statsPage, p => statsPage = p, 30, 150);
        if (statsPage == 0) ComposeColonyStats(composer, y);
        else ComposeWorkStats(composer, y);
    }

    /// <summary>Settlers against beds, the colony's age and buildings, and every job's workers against its places.</summary>
    void ComposeColonyStats(GuiComposer composer, double y)
    {
        int alive = snapshot.Citizens.Count(c => c.State != "dead" && !c.Visitor);
        int dead = snapshot.Citizens.Count(c => c.State == "dead");
        int days = Math.Max(0, (int)(capi.World.Calendar.TotalDays - snapshot.FoundedTotalDays));

        int statRows = snapshot.PopulationCap > 0 ? 5 : 4;
        composer.AddInset(ElementBounds.Fixed(0, y, Width, statRows * 24 + 12), 3, 0.8f);
        double row = y + 6;
        row = composer.Row(Lang.Get("createacolony:townhall-stat-settlers"), Lang.Get("createacolony:townhall-of", alive, snapshot.BedCount + snapshot.FreeBeds), 10, row, Width - 20,
            snapshot.FreeBeds == 0 ? GuiKit.WarnColor : null);
        if (snapshot.PopulationCap > 0) row = composer.Row(Lang.Get("createacolony:townhall-stat-population"), Lang.Get("createacolony:townhall-of", alive, snapshot.PopulationCap), 10, row, Width - 20,
            alive >= snapshot.PopulationCap ? GuiKit.WarnColor : null);
        row = composer.Row(Lang.Get("createacolony:townhall-stat-lost"), dead.ToString(), 10, row, Width - 20);
        row = composer.Row(Lang.Get("createacolony:townhall-stat-buildings"), snapshot.BuildingCount.ToString(), 10, row, Width - 20);
        composer.Row(Lang.Get("createacolony:townhall-stat-age"), Lang.Get("createacolony:townhall-days", days), 10, row, Width - 20);
        y += statRows * 24 + 12 + 6;
        y = composer.Text(Lang.Get("createacolony:townhall-beds-hint"), GuiKit.Muted(), 0, y, Width, 10);

        y = composer.Section(Lang.Get("createacolony:townhall-jobs"), y, Width);
        // A to Z, with "No job" first (0.203.0).
        var jobs = snapshot.Jobs.Where(j => j.Workers > 0 || j.Places > 0).OrderBy(j => j.Job.Length > 0).ThenBy(j => JobName(j.Job), StringComparer.CurrentCultureIgnoreCase).ToList();
        if (jobs.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:no-citizens"), GuiKit.Detail(), 0, y, Width);
            return;
        }
        composer.AddInset(ElementBounds.Fixed(0, y, Width, jobs.Count * 24 + 12), 3, 0.8f);
        y += 6;
        foreach (var j in jobs)
        {
            string count = j.Places < 0 ? j.Workers.ToString() : Lang.Get("createacolony:townhall-of", j.Workers, j.Places);
            bool open = j.Places > j.Workers;
            y = composer.Row(JobName(j.Job), count, 10, y, Width - 20, open ? GuiKit.WarnColor : null);
        }
        composer.Text(Lang.Get("createacolony:townhall-jobs-hint"), GuiKit.Muted(), 0, y + 12, Width);
    }

    /// <summary>Every hut's Statistics added together, for a chosen time.</summary>
    void ComposeWorkStats(GuiComposer composer, double y)
    {
        var periods = GuiDialogHut.StatPeriods;
        y = PageButtons(composer, periods.Select(p => "stats-" + p).ToArray(), statPeriod, p => statPeriod = p, y, 104);
        var rows = snapshot.Stats.Where(s => s.Counts.Length > statPeriod && s.Counts[statPeriod] > 0).ToList();
        if (rows.Count == 0)
        {
            y = composer.Text(Lang.Get("createacolony:stats-none"), GuiKit.Body(), 0, y, Width, 10);
        }
        else
        {
            composer.AddInset(ElementBounds.Fixed(0, y, Width, rows.Count * 24 + 12), 3, 0.8f);
            y += 6;
            foreach (var row in rows.OrderBy(r => r.Key.StartsWith("kill:") ? 0 : 1).ThenByDescending(r => r.Counts[statPeriod]))
            {
                y = composer.Row(GuiDialogHut.StatLabel(row.Key), row.Counts[statPeriod].ToString(), 10, y, Width - 20);
            }
            y += 12;
        }
    }

    // ---- Permissions tab ------------------------------------------------------------------

    void ComposePermissions(GuiComposer composer)
    {
        bool isOwner = ViewerRole == ColonyRole.Owner;
        double top = 30;
        if (isOwner) top = PageButtons(composer, new[] { "perm-players", "perm-ranks", "perm-log" }, permPage, p => permPage = p, 30, 150);
        else permPage = 0;
        switch (permPage)
        {
            case 1: ComposeRanks(composer, top); return;
            case 2: ComposePermissionLog(composer, top); return;
        }

        // Member, Officer, then the owner's own ranks (0.185.0).
        var codes = new List<string> { "role:" + (int)ColonyRole.Member, "role:" + (int)ColonyRole.Officer };
        var names = new List<string> { Lang.Get("createacolony:role-member"), Lang.Get("createacolony:role-officer") };
        foreach (var r in snapshot.Ranks)
        {
            codes.Add("rank:" + r.Id);
            names.Add(r.Name);
        }

        double y = top;
        composer
            .AddStaticText(Lang.Get("createacolony:col-player"), GuiKit.Muted(), ElementBounds.Fixed(10, y, 210, 20))
            .AddStaticText(Lang.Get("createacolony:col-role"), GuiKit.Muted(), ElementBounds.Fixed(230, y, 160, 20));
        y += 22;
        composer.AddInset(ElementBounds.Fixed(0, y, Width, snapshot.Members.Count * RowHeight + 6), 3, 0.8f);
        y += 3;
        foreach (var m in snapshot.Members)
        {
            var role = (ColonyRole)m.Role;
            composer.AddStaticText(m.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 6, 210, 25));

            if (isOwner && role != ColonyRole.Owner)
            {
                string uid = m.PlayerUid;
                string current = role == ColonyRole.Member && m.RankId != 0 ? "rank:" + m.RankId : "role:" + (int)role;
                int selected = Math.Max(0, codes.IndexOf(current));
                composer
                    .AddDropDown(codes.ToArray(), names.ToArray(), selected, (code, on) => OnPickRank(uid, code),
                        ElementBounds.Fixed(230, y + 2, 160, 28), "role-" + uid)
                    .AddSmallButton(Lang.Get("createacolony:remove"), () => OnRemove(uid), ElementBounds.Fixed(Width - 130, y + 2, 120, 28));
            }
            else
            {
                composer.AddStaticText(m.RankName.Length > 0 ? m.RankName : RoleName(role), GuiKit.Body(), ElementBounds.Fixed(230, y + 6, 160, 25));
            }
            y += RowHeight;
        }

        y = composer.Text(Lang.Get("createacolony:invite-hint"), GuiKit.Muted(), 0, y + 12, Width, 8);
        y = composer.Text(Lang.Get("createacolony:townhall-roles-hint"), GuiKit.Muted(), 0, y, Width, 8);
    }

    void OnPickRank(string uid, string code)
    {
        var parts = code.Split(':');
        if (parts[0] == "role") OnSetRole(uid, int.Parse(parts[1]));
        else SendText(ColonyAction.RankChange, "assign|" + uid + "|" + parts[1]);
    }

    /// <summary>The owner's Ranks page: make a rank, pick one, rename or delete it, and tick what it may do.</summary>
    void ComposeRanks(GuiComposer composer, double y)
    {
        y = composer.Text(Lang.Get("createacolony:ranks-hint"), GuiKit.Muted(), 0, y, Width, 10);
        if (snapshot.Ranks.Count < snapshot.MaxRanks)
        {
            composer
                .AddTextInput(ElementBounds.Fixed(0, y, 260, 28), text => pendingRankName = text, GuiKit.Body(), "rankname")
                .AddSmallButton(Lang.Get("createacolony:rank-add"), () =>
                {
                    if (pendingRankName.Trim().Length == 0) return true;
                    SendText(ColonyAction.RankChange, "add|" + pendingRankName);
                    pendingRankName = "";
                    return true;
                }, ElementBounds.Fixed(270, y, 130, 28));
            y += 40;
        }
        else
        {
            y = composer.Text(Lang.Get("createacolony:ranks-full-hint", snapshot.MaxRanks), GuiKit.Detail(), 0, y, Width, 10);
        }
        if (snapshot.Ranks.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:ranks-none"), GuiKit.Body(), 0, y, Width);
            return;
        }

        var rank = snapshot.Ranks.Find(r => r.Id == chosenRank) ?? snapshot.Ranks[0];
        chosenRank = rank.Id;
        composer
            .AddStaticText(Lang.Get("createacolony:rank"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 60, 25))
            .AddDropDown(snapshot.Ranks.Select(r => r.Id.ToString()).ToArray(), snapshot.Ranks.Select(r => r.Name).ToArray(),
                snapshot.Ranks.IndexOf(rank), (code, on) => { chosenRank = int.Parse(code); Compose(); }, ElementBounds.Fixed(60, y, 200, 28), "rankpick")
            .AddSmallButton(Lang.Get("createacolony:delete"), () => SendText(ColonyAction.RankChange, "delete|" + rank.Id), ElementBounds.Fixed(Width - 110, y, 110, 28));
        y += 38;
        composer
            .AddTextInput(ElementBounds.Fixed(0, y, 260, 28), text => pendingRankRename = text, GuiKit.Body(), "rankrename")
            .AddSmallButton(Lang.Get("createacolony:rank-rename"), () =>
            {
                if (pendingRankRename.Trim().Length == 0) return true;
                SendText(ColonyAction.RankChange, "rename|" + rank.Id + "|" + pendingRankRename);
                pendingRankRename = "";
                return true;
            }, ElementBounds.Fixed(270, y, 130, 28));
        y += 44;

        y = composer.Section(Lang.Get("createacolony:rank-may", rank.Name), y, Width);
        int i = 0;
        foreach (var perm in ColonyManager.AllPerms)
        {
            int bit = (int)perm;
            double x = i % 2 == 0 ? 0 : Width / 2;
            double rowY = y + i / 2 * 34;
            string label = Lang.Get("createacolony:perm-" + perm.ToString().ToLowerInvariant());
            composer
                .AddSwitch(on => SendText(ColonyAction.RankChange, "perm|" + rank.Id + "|" + bit), ElementBounds.Fixed(x, rowY, 30, 30), "perm-" + bit)
                .AddStaticText(label, GuiKit.Body(), ElementBounds.Fixed(x + 38, rowY + 5, Width / 2 - 45, 25));
            i++;
        }
        y += (ColonyManager.AllPerms.Length + 1) / 2 * 34 + 8;
        composer.Text(Lang.Get("createacolony:ranks-officer-hint"), GuiKit.Muted(), 0, y, Width, 8);
    }

    /// <summary>The owner's log of what members, officers and ranked players did, newest first.</summary>
    void ComposePermissionLog(GuiComposer composer, double y)
    {
        var log = snapshot.PermissionLog;
        if (log.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:permlog-none"), GuiKit.Body(), 0, y, Width);
            return;
        }
        int pages = (log.Count + EventsPerPage - 1) / EventsPerPage;
        page = Math.Clamp(page, 0, pages - 1);
        int shown = Math.Min(EventsPerPage, log.Count - page * EventsPerPage);
        composer.AddInset(ElementBounds.Fixed(0, y, Width, shown * EventRowHeight + 8), 3, 0.8f);
        y += 4;
        foreach (var e in log.Skip(page * EventsPerPage).Take(EventsPerPage))
        {
            composer
                .AddStaticText(When(e.TotalDays), GuiKit.Muted(), ElementBounds.Fixed(10, y + 5, 140, 20))
                .AddStaticText(EventText(e), GuiKit.Detail(), ElementBounds.Fixed(155, y + 4, Width - 165, 20));
            y += EventRowHeight;
        }
        composer.Pager(page, pages, y + 12, Width, p => { page = p; Compose(); });
    }

    // ---- Colony militia tab ---------------------------------------------------------------

    /// <summary>
    /// Every grown settler with their job, whether they are a guard, and the weapon they would fight with;
    /// then the Self Defence and Support allies switches (officers change them).
    /// </summary>
    void ComposeMilitia(GuiComposer composer)
    {
        double top = 30;
        var people = snapshot.Citizens.Where(c => c.State != "dead" && !c.Visitor && !c.Child)
            .OrderByDescending(c => c.Job == Jobs.Guard).ThenByDescending(c => c.Weapon.Length > 0).ThenBy(c => c.Name).ToList();
        int guards = people.Count(c => c.Job == Jobs.Guard), armed = people.Count(c => c.Job != Jobs.Guard && c.Weapon.Length > 0);
        top = composer.Text(Lang.Get("createacolony:militia-summary", guards, armed, people.Count - guards - armed), GuiKit.Detail(), 0, top, Width, 6);
        if (snapshot.RalliedBy.Count > 0) top = composer.Text(Lang.Get("createacolony:militia-rallied", string.Join(", ", snapshot.RalliedBy)), GuiKit.Detail().WithColor(GuiKit.GoodColor), 0, top, Width, 6);
        if (snapshot.RaidStatus.Length > 0) top = composer.Text(snapshot.RaidStatus, GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, top, Width, 6);
        double y;
        if (people.Count == 0)
        {
            y = composer.Text(Lang.Get("createacolony:militia-nobody"), GuiKit.Body(), 0, top, Width);
        }
        else
        {
            composer
                .AddStaticText(Lang.Get("createacolony:col-name"), GuiKit.Muted(), ElementBounds.Fixed(10, top, 150, 20))
                .AddStaticText(Lang.Get("createacolony:col-job"), GuiKit.Muted(), ElementBounds.Fixed(165, top, 120, 20))
                .AddStaticText(Lang.Get("createacolony:col-militia-role"), GuiKit.Muted(), ElementBounds.Fixed(290, top, 90, 20))
                .AddStaticText(Lang.Get("createacolony:col-weapon"), GuiKit.Muted(), ElementBounds.Fixed(385, top, 175, 20));

            int rows = RowsPerPage - 2;
            int pages = (people.Count + rows - 1) / rows;
            page = Math.Clamp(page, 0, pages - 1);
            int shown = Math.Min(rows, people.Count - page * rows);
            composer.AddInset(ElementBounds.Fixed(0, top + 22, Width, shown * RowHeight + 6), 3, 0.8f);
            y = top + 25;
            foreach (var c in people.Skip(page * rows).Take(rows))
            {
                bool guard = c.Job == Jobs.Guard;
                var roleFont = guard ? GuiKit.Detail().WithColor(GuiKit.GoodColor) : GuiKit.Detail();
                var weaponFont = c.Weapon.Length > 0 ? GuiKit.Detail() : GuiKit.Detail().WithColor(GuiKit.MutedColor);
                composer
                    .AddStaticText(c.Name, GuiKit.Body(), ElementBounds.Fixed(10, y + 6, 150, 25))
                    .AddStaticText(JobName(c.Job), GuiKit.Detail(), ElementBounds.Fixed(165, y + 8, 120, 25))
                    .AddStaticText(c.Weapon.Length > 0 ? c.Weapon : Lang.Get("createacolony:militia-no-weapon"), weaponFont, ElementBounds.Fixed(385, y + 8, 175, 25));
                // A guard fights up close or with a bow or sling (0.178.0); officers switch it here.
                string role = Lang.Get(!guard ? "createacolony:militia-civilian" : c.GuardRanged ? "createacolony:militia-guard-ranged" : "createacolony:militia-guard-melee");
                if (guard && Officer)
                {
                    int id = c.CitizenId;
                    bool next = !c.GuardRanged;
                    composer.AddSmallButton(role, () =>
                    {
                        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetGuardRanged, CitizenId = id, Text = next ? "1" : "0" });
                        return true;
                    }, ElementBounds.Fixed(288, y + 4, 92, 26), EnumButtonStyle.Small);
                }
                else composer.AddStaticText(role, roleFont, ElementBounds.Fixed(290, y + 8, 90, 25));
                y += RowHeight;
            }
            composer.Pager(page, pages, y + 14, Width, p => { page = p; Compose(); });
            y += pages > 1 ? 54 : 16;
        }

        y = composer.Section(Lang.Get("createacolony:militia-settings"), y, Width);
        if (people.Any(c => c.Job == Jobs.Guard)) y = composer.Text(Lang.Get("createacolony:militia-ranged-hint"), GuiKit.Muted(), 0, y, Width, 8);
        y = MilitiaSwitch(composer, "selfdefence", ColonyAction.SetSelfDefence, snapshot.SelfDefence, "militia-selfdefence", "militia-selfdefence-hint", y);
        MilitiaSwitch(composer, "supportallies", ColonyAction.SetSupportAllies, snapshot.SupportAllies, "militia-supportallies", "militia-supportallies-hint", y);
    }

    /// <summary>One militia switch with its name and a line on what it does; officers can flip it. Returns the y below it.</summary>
    double MilitiaSwitch(GuiComposer composer, string key, ColonyAction action, bool on, string label, string hint, double y)
    {
        string state = Lang.Get(on ? "createacolony:on" : "createacolony:off");
        if (Officer)
        {
            composer
                .AddSwitch(v => network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = action, Text = v ? "1" : "0" }),
                    ElementBounds.Fixed(0, y, 30, 30), key)
                .AddStaticText(Lang.Get("createacolony:" + label) + ": " + state, GuiKit.Body(), ElementBounds.Fixed(40, y + 5, Width - 40, 25));
        }
        else
        {
            composer.AddStaticText(Lang.Get("createacolony:" + label) + ": " + state, GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width, 25));
        }
        y += 34;
        return composer.Text(Lang.Get("createacolony:" + hint), GuiKit.Muted(), 0, y, Width, 10);
    }

    // ---- Bailiff tab ----------------------------------------------------------------------

    /// <summary>
    /// The Bailiff tab: who the bailiff is and their rules (what to do with wanderers, giving out jobs, rent),
    /// their ledger of the last days, and the estate report of every hut.
    /// </summary>
    void ComposeBailiff(GuiComposer composer)
    {
        double y = PageButtons(composer, new[] { "bailiff-page-rules", "bailiff-page-money", "bailiff-page-ledger", "bailiff-page-report" }, bailiffPage, p => bailiffPage = p, 30, 132);
        switch (bailiffPage)
        {
            case 1: ComposeMoney(composer, y); break;
            case 2: ComposeLedger(composer, y); break;
            case 3: ComposeEstateReport(composer, y); break;
            default: ComposeBailiffRules(composer, y); break;
        }
    }

    void ComposeBailiffRules(GuiComposer composer, double y)
    {
        if (snapshot.BailiffName.Length == 0)
        {
            y = composer.Text(Lang.Get("createacolony:bailiff-none"), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 6);
        }
        else
        {
            string state = Lang.Get("createacolony:bailiff-state-" + snapshot.BailiffState);
            var font = snapshot.BailiffState == BailiffState.OnDuty ? GuiKit.Body().WithColor(GuiKit.GoodColor) : GuiKit.Body().WithColor(GuiKit.WarnColor);
            y = composer.Text(Lang.Get("createacolony:bailiff-who", snapshot.BailiffName, state), font, 0, y, Width, 6);
        }
        y = composer.Text(Lang.Get("createacolony:bailiff-about"), GuiKit.Muted(), 0, y, Width, 10);

        y = composer.Section(Lang.Get("createacolony:bailiff-wanderers"), y, Width);
        string[] rules = Enumerable.Range(0, BailiffWanderers.Last + 1).Select(i => i.ToString()).ToArray();
        string[] ruleNames = rules.Select(r => Lang.Get("createacolony:bailiff-wanderers-" + r)).ToArray();
        int rule = Math.Clamp(snapshot.WandererRule, 0, BailiffWanderers.Last);
        if (Officer)
        {
            composer.AddDropDown(rules, ruleNames, rule, (code, on) => SendBailiffRule("wanderers|" + code), ElementBounds.Fixed(0, y, 300, 28), "bailiffwanderers");
            y += 36;
        }
        else
        {
            y = composer.Text(ruleNames[rule], GuiKit.Body(), 0, y, Width, 6);
        }
        y = composer.Text(Lang.Get("createacolony:bailiff-wanderers-hint"), GuiKit.Muted(), 0, y, Width, 10);

        y = composer.Section(Lang.Get("createacolony:bailiff-duties"), y, Width);
        y = BailiffSwitch(composer, "bailiffjobs", "jobs", snapshot.BailiffGivesJobs, "bailiff-jobs", "bailiff-jobs-hint", y);
        y = BailiffSwitch(composer, "bailifflend", "lend", snapshot.BailiffLendsWorkers, "bailiff-lend", "bailiff-lend-hint", y);
        // How long a worker who only waits for a delivery keeps the job (0.201.0).
        string[] waits = { "1", "2", "3", "5", "7", "14" };
        int waitNow = Math.Max(0, Array.IndexOf(waits, snapshot.BailiffWaitDays.ToString()));
        if (Officer)
        {
            composer.AddStaticText(Lang.Get("createacolony:bailiff-wait"), GuiKit.Body(), ElementBounds.Fixed(0, y + 4, 420, 24));
            composer.AddDropDown(waits, waits.Select(w => Lang.Get("createacolony:bailiff-wait-days", w)).ToArray(), waitNow,
                (code, on) => SendBailiffRule("wait|" + code), ElementBounds.Fixed(430, y, 130, 28), "bailiffwait");
            y += 36;
        }
        else
        {
            y = composer.Text(Lang.Get("createacolony:bailiff-wait") + " " + Lang.Get("createacolony:bailiff-wait-days", snapshot.BailiffWaitDays), GuiKit.Body(), 0, y, Width, 6);
        }
        y = composer.Text(Lang.Get("createacolony:bailiff-wait-hint"), GuiKit.Muted(), 0, y, Width, 10);
        composer.Text(Lang.Get("createacolony:bailiff-fields-hint"), GuiKit.Muted(), 0, y, Width, 6);
    }

    /// <summary>Taxes and wages: the treasury, the tax rate and the two switches (ColonyManager.Economy.cs).</summary>
    void ComposeMoney(GuiComposer composer, double y)
    {
        if (!snapshot.EconomyOpen) y = composer.Text(Lang.Get("createacolony:economy-locked", snapshot.EconomyLevel), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 8);
        else if (snapshot.Treasury == 0) y = composer.Text(ColonyMoney.Get(capi, "createacolony:bailiff-empty"), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 8);
        y = composer.Text(ColonyMoney.Get(capi, "createacolony:bailiff-treasury", snapshot.Treasury, snapshot.WageBill, DayName(snapshot.NextPayday)),
            snapshot.Treasury < snapshot.WageBill ? GuiKit.Body().WithColor(GuiKit.WarnColor) : GuiKit.Body(), 0, y, Width, 10);

        // The colony's money: rusty gears, or with the Coinage mod one of its coins (ColonyMoney).
        y = composer.Section(Lang.Get("createacolony:currency"), y, Width);
        if (!snapshot.OwnCoin)
        {
            y = composer.Text(Lang.Get("createacolony:currency-gears"), GuiKit.Body(), 0, y, Width, 10);
        }
        else
        {
            y = composer.Text(Lang.Get("createacolony:currency-own", snapshot.CurrencyName, snapshot.CoinMetalName), GuiKit.Body(), 0, y, Width, 6);
            if (Officer)
            {
                composer.AddSmallButton(Lang.Get("createacolony:currency-use-held"), () => { SendBailiffRule("currency|hand"); return true; }, ElementBounds.Fixed(0, y, 220, 28));
                composer.AddSmallButton(Lang.Get("createacolony:currency-use-any"), () => { SendBailiffRule("currency|any"); return true; }, ElementBounds.Fixed(230, y, 220, 28));
                y += 36;
            }
            y = composer.Text(Lang.Get("createacolony:currency-hint"), GuiKit.Muted(), 0, y, Width, 10);
        }

        y = composer.Section(Lang.Get("createacolony:bailiff-tax"), y, Width);
        string[] rates = Enumerable.Range(0, 11).Select(i => (i * 5).ToString()).ToArray();
        string[] rateNames = rates.Select(r => r + "%").ToArray();
        int rate = Math.Clamp(snapshot.TaxPercent / 5, 0, 10);
        if (Officer)
        {
            composer.AddDropDown(rates, rateNames, rate, (code, on) => SendBailiffRule("tax|" + code), ElementBounds.Fixed(0, y, 120, 28), "bailifftax");
            y += 36;
        }
        else
        {
            y = composer.Text(rateNames[rate], GuiKit.Body(), 0, y, Width, 6);
        }
        y = composer.Text(Lang.Get("createacolony:bailiff-tax-hint"), GuiKit.Muted(), 0, y, Width, 10);

        y = composer.Section(Lang.Get("createacolony:bailiff-duties"), y, Width);
        y = BailiffSwitch(composer, "bailiffrent", "rent", snapshot.BailiffCollectsRent, "bailiff-rent", "bailiff-rent-hint", y, snapshot.TaxPercent);
        BailiffSwitch(composer, "bailiffwages", "wages", snapshot.BailiffPaysWages, "bailiff-wages", "bailiff-wages-hint", y);
    }

    /// <summary>One of the bailiff's switches with its name and a line on what it does; officers can flip it. Returns the y below it.</summary>
    double BailiffSwitch(GuiComposer composer, string key, string rule, bool on, string label, string hint, double y, params object[] hintArgs)
    {
        string text = Lang.Get("createacolony:" + label) + ": " + Lang.Get(on ? "createacolony:on" : "createacolony:off");
        if (Officer)
        {
            composer
                .AddSwitch(v => SendBailiffRule(rule + "|" + (v ? "1" : "0")), ElementBounds.Fixed(0, y, 30, 30), key)
                .AddStaticText(text, GuiKit.Body(), ElementBounds.Fixed(40, y + 5, Width - 40, 25));
        }
        else
        {
            composer.AddStaticText(text, GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width, 25));
        }
        y += 34;
        return composer.Text(Lang.Get("createacolony:" + hint, hintArgs), GuiKit.Muted(), 0, y, Width, 8);
    }

    // ---- Mayor tab (0.199.0) ------------------------------------------------------------------

    /// <summary>The Mayor: who it is, the mode, what they want to do next (with Approve, Other spot, Skip this one), and the build order.</summary>
    void ComposeMayor(GuiComposer composer)
    {
        double y = 30;
        if (snapshot.MayorName.Length == 0)
        {
            y = composer.Text(Lang.Get("createacolony:mayor-none"), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 6);
        }
        else
        {
            string state = Lang.Get("createacolony:bailiff-state-" + snapshot.MayorState);
            var font = snapshot.MayorState == BailiffState.OnDuty ? GuiKit.Body().WithColor(GuiKit.GoodColor) : GuiKit.Body().WithColor(GuiKit.WarnColor);
            y = composer.Text(Lang.Get("createacolony:mayor-who", snapshot.MayorName, state), font, 0, y, Width, 6);
        }
        y = composer.Text(Lang.Get("createacolony:mayor-about"), GuiKit.Muted(), 0, y, Width, 8);

        string[] modes = { "0", "1", "2" };
        string[] modeNames = modes.Select(m => Lang.Get("createacolony:mayor-mode-" + m)).ToArray();
        int mode = Math.Clamp(snapshot.MayorMode, 0, 2);
        composer.AddStaticText(Lang.Get("createacolony:mayor-mode"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 120, 25));
        if (Officer)
        {
            composer.AddDropDown(modes, modeNames, mode, (code, on) => SendText(ColonyAction.MayorAction, "mode|" + code), ElementBounds.Fixed(120, y, 220, 28), "mayormode");
        }
        else
        {
            composer.AddStaticText(modeNames[mode], GuiKit.Body(), ElementBounds.Fixed(120, y + 5, 220, 25));
        }
        y += 36;

        y = composer.Section(Lang.Get("createacolony:mayor-next"), y, Width);
        if (snapshot.MayorPlan.Length > 0)
        {
            y = composer.Text(Lang.Get("createacolony:mayor-wants", snapshot.MayorPlan), GuiKit.Body(), 0, y, Width, 2);
            if (snapshot.MayorPlanReason.Length > 0) y = composer.Text(snapshot.MayorPlanReason, GuiKit.Detail(), 0, y, Width, 2);
            if (snapshot.MayorPlanNew) y = composer.Text(snapshot.MayorPlanWhere, GuiKit.Detail(), 0, y, Width, 6);
            if (snapshot.MayorPlanApproved)
            {
                y = composer.Text(snapshot.MayorIdle.Length > 0 ? snapshot.MayorIdle : Lang.Get("createacolony:mayor-approved"), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 6);
            }
            else if (Officer)
            {
                composer.AddSmallButton(Lang.Get("createacolony:mayor-approve"), () => SendText(ColonyAction.MayorAction, "approve"), ElementBounds.Fixed(0, y, 120, 28));
                if (snapshot.MayorPlanNew)
                {
                    composer.AddSmallButton(Lang.Get("createacolony:mayor-other"), () => SendText(ColonyAction.MayorAction, "other"), ElementBounds.Fixed(130, y, 120, 28));
                }
                composer.AddSmallButton(Lang.Get("createacolony:mayor-skip"), () => SendText(ColonyAction.MayorAction, "skip"), ElementBounds.Fixed(260, y, 140, 28));
                composer.AddSmallButton(Lang.Get("createacolony:mayor-show"), () => SendText(ColonyAction.MayorAction, "show"), ElementBounds.Fixed(410, y, 70, 28));
                composer.AddSmallButton(Lang.Get("createacolony:mayor-hide"), () => SendText(ColonyAction.MayorAction, "hide"), ElementBounds.Fixed(490, y, 70, 28));
                y += 36;
            }
        }
        else
        {
            y = composer.Text(snapshot.MayorIdle.Length > 0 ? snapshot.MayorIdle : Lang.Get("createacolony:mayor-nothing"), GuiKit.Body(), 0, y, Width, 6);
            if (Officer && snapshot.MayorName.Length > 0)
            {
                composer.AddSmallButton(Lang.Get("createacolony:mayor-look"), () => SendText(ColonyAction.MayorAction, "look"), ElementBounds.Fixed(0, y, 160, 28));
                y += 36;
            }
        }

        y = composer.Section(Lang.Get("createacolony:mayor-order"), y, Width);
        y = composer.Text(Lang.Get("createacolony:mayor-order-hint"), GuiKit.Muted(), 0, y, Width, 6);
        int count = snapshot.MayorOrder.Count;
        int pages = Math.Max(1, (count + MayorRowsPerPage - 1) / MayorRowsPerPage);
        page = Math.Clamp(page, 0, pages - 1);
        for (int i = page * MayorRowsPerPage; i < Math.Min(count, (page + 1) * MayorRowsPerPage); i++)
        {
            string type = snapshot.MayorOrder[i];
            int from = i < snapshot.MayorOrderFrom.Count ? snapshot.MayorOrderFrom[i] : 0;
            string label = (i + 1) + ". " + Lang.Get("createacolony:building-" + type)
                + (from > 0 ? "  " + Lang.Get("createacolony:mayor-from", from) : "");
            bool on = !snapshot.MayorSkip.Contains(type);
            if (Officer)
            {
                composer.AddSwitch(_ => SendText(ColonyAction.MayorAction, "toggle|" + type), ElementBounds.Fixed(0, y, 26, 26), "mayororder" + i);
                composer.AddSmallButton("^", () => SendText(ColonyAction.MayorAction, "up|" + type), ElementBounds.Fixed(Width - 70, y, 30, 26));
                composer.AddSmallButton("v", () => SendText(ColonyAction.MayorAction, "down|" + type), ElementBounds.Fixed(Width - 35, y, 30, 26));
            }
            composer.AddStaticText(label, on ? GuiKit.Body() : GuiKit.Muted(), ElementBounds.Fixed(36, y + 4, Width - 120, 24));
            y += 30;
        }
        composer.Pager(page, pages, y + 6, Width, p => { page = p; Compose(); });
        y += pages > 1 ? 46 : 8;
        if (Officer) composer.AddSmallButton(Lang.Get("createacolony:mayor-reset"), () => SendText(ColonyAction.MayorAction, "reset"), ElementBounds.Fixed(0, y, 200, 28));
    }

    void SendBailiffRule(string text) =>
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetBailiffRule, Text = text });

    /// <summary>The ledger: the last days, newest first, with taxes, wages, wanderers and jobs given out, and the totals.</summary>
    void ComposeLedger(GuiComposer composer, double y)
    {
        var days = snapshot.Ledger.OrderByDescending(d => d.Day).Take(10).ToList();
        if (days.Count == 0)
        {
            y = composer.Text(Lang.Get("createacolony:ledger-empty"), GuiKit.Body(), 0, y, Width, 10);
        }
        else
        {
            composer
                .AddStaticText(Lang.Get("createacolony:ledger-col-day"), GuiKit.Muted(), ElementBounds.Fixed(10, y, 70, 20))
                .AddStaticText(Lang.Get("createacolony:ledger-col-rent"), GuiKit.Muted(), ElementBounds.Fixed(85, y, 110, 20))
                .AddStaticText(Lang.Get("createacolony:ledger-col-wages"), GuiKit.Muted(), ElementBounds.Fixed(200, y, 110, 20))
                .AddStaticText(Lang.Get("createacolony:ledger-col-wanderers"), GuiKit.Muted(), ElementBounds.Fixed(315, y, 130, 20))
                .AddStaticText(Lang.Get("createacolony:ledger-col-jobs"), GuiKit.Muted(), ElementBounds.Fixed(450, y, 110, 20));
            composer.AddInset(ElementBounds.Fixed(0, y + 22, Width, days.Count * 26 + 6), 3, 0.8f);
            y += 25;
            foreach (var d in days)
            {
                composer
                    .AddStaticText(DayName(d.Day), GuiKit.Detail(), ElementBounds.Fixed(10, y + 4, 70, 20))
                    .AddStaticText(d.Payers > 0 ? ColonyMoney.Get(capi, "createacolony:ledger-rent", d.Rent, d.Payers) : "-", GuiKit.Detail(), ElementBounds.Fixed(85, y + 4, 110, 20))
                    .AddStaticText(d.Wages > 0 || d.Unpaid > 0 ? ColonyMoney.Get(capi, "createacolony:ledger-wages", d.Wages, d.Unpaid) : "-",
                        d.Unpaid > 0 ? GuiKit.Detail().WithColor(GuiKit.WarnColor) : GuiKit.Detail(), ElementBounds.Fixed(200, y + 4, 110, 20))
                    .AddStaticText(Lang.Get("createacolony:ledger-wanderers", d.Welcomed, d.TurnedAway), GuiKit.Detail(), ElementBounds.Fixed(315, y + 4, 130, 20))
                    .AddStaticText(Lang.Get("createacolony:ledger-jobs", d.JobsGiven, d.Lent), GuiKit.Detail(), ElementBounds.Fixed(450, y + 4, 110, 20));
                y += 26;
            }
            y += 14;
            y = composer.Text(ColonyMoney.Get(capi, "createacolony:ledger-total", snapshot.Ledger.Sum(d => d.Rent), snapshot.Ledger.Sum(d => d.Wages), snapshot.Ledger.Count),
                GuiKit.Body(), 0, y, Width, 6);
        }
        composer.Text(Lang.Get("createacolony:ledger-hint"), GuiKit.Muted(), 0, y, Width, 6);
    }

    /// <summary>The estate report: every hut, who works there and what they are doing or waiting on.</summary>
    void ComposeEstateReport(GuiComposer composer, double y)
    {
        var lines = snapshot.EstateReport;
        int pages = Math.Max(1, (lines.Count + ReportRowsPerPage - 1) / ReportRowsPerPage);
        page = Math.Clamp(page, 0, pages - 1);
        foreach (var line in lines.Skip(page * ReportRowsPerPage).Take(ReportRowsPerPage))
        {
            y = composer.Text(line, GuiKit.Detail(), 0, y, Width, 4);
        }
        composer.Pager(page, pages, y + 10, Width, p => { page = p; Compose(); });
        y += pages > 1 ? 50 : 10;
        composer.Text(Lang.Get("createacolony:estate-hint"), GuiKit.Muted(), 0, y, Width, 6);
    }

    /// <summary>An in-game day as the calendar shows it: "1 May".</summary>
    string DayName(int totalDays)
    {
        var calendar = capi.World.Calendar;
        int daysPerMonth = Math.Max(1, calendar.DaysPerMonth);
        int daysPerYear = Math.Max(1, calendar.DaysPerYear);
        int dayOfYear = totalDays % daysPerYear;
        int month = Math.Clamp(dayOfYear / daysPerMonth, 0, 11);
        return Lang.Get("createacolony:ledger-day", dayOfYear % daysPerMonth + 1, Lang.Get("month-" + (EnumMonth)(month + 1)));
    }

    // ---- Settings tab ---------------------------------------------------------------------

    /// <summary>Permadeath (the owner's switch), and how wanderers and the warehouse are set up.</summary>
    void ComposeSettings(GuiComposer composer)
    {
        double top = PageButtons(composer, new[] { "settings-general", "settings-town" }, settingsPage, p => settingsPage = p, 30, 150);
        if (settingsPage == 1)
        {
            ComposeTownSettings(composer, top);
            return;
        }
        double y = composer.Section(Lang.Get("createacolony:permadeath"), top, Width);
        if (ViewerRole == ColonyRole.Owner)
        {
            composer
                .AddSwitch(on => network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetPermadeath, Text = on ? "1" : "0" }),
                    ElementBounds.Fixed(0, y, 30, 30), "permadeath")
                .AddStaticText(Lang.Get(snapshot.Permadeath ? "createacolony:on" : "createacolony:off"), GuiKit.Body(), ElementBounds.Fixed(40, y + 5, 100, 25));
        }
        else
        {
            composer.AddStaticText(Lang.Get(snapshot.Permadeath ? "createacolony:on" : "createacolony:off"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, Width, 25));
        }
        y += 36;
        y = composer.Text(Lang.Get(snapshot.Permadeath ? "createacolony:permadeath-on-hint" : "createacolony:permadeath-off-hint"), GuiKit.Muted(), 0, y, Width, 12);

        y = composer.Section(Lang.Get("createacolony:townhall-wanderers"), y, Width);
        y = composer.Text(snapshot.WandererDays <= 0 ? Lang.Get("createacolony:overview-no-wanderers")
            : Lang.Get(snapshot.WanderersNeedFreeBed ? "createacolony:overview-wanderers-bed" : "createacolony:overview-wanderers",
                snapshot.WandererDays.ToString("0.#"), snapshot.FreeBeds), GuiKit.Detail(), 0, y, Width, 4);
        y = composer.Text(Lang.Get("createacolony:townhall-wanderers-hint"), GuiKit.Muted(), 0, y, Width, 12);

        y = composer.Section(Lang.Get("createacolony:building-warehouse"), y, Width);
        y = composer.Text(snapshot.HasWarehouse
            ? Lang.Get("createacolony:overview-warehouse", snapshot.WarehouseChests, snapshot.DropOffs, snapshot.OpenRequests)
            : Lang.Get("createacolony:overview-no-warehouse"), GuiKit.Detail(), 0, y, Width, 12);

        ComposeRaidSettings(composer, y);
    }

    /// <summary>
    /// The Town page of Settings (0.185.0): auto hiring, auto housing, and the messages players see when they walk into
    /// or out of the colony. Officers change them.
    /// </summary>
    void ComposeTownSettings(GuiComposer composer, double y)
    {
        y = TownSwitch(composer, "autohiring", "createacolony:autohiring", "createacolony:autohiring-hint", snapshot.AutoHiring,
            on => SendText(ColonyAction.SetAutoHiring, on ? "1" : "0"), y);
        y = TownSwitch(composer, "autohousing", "createacolony:autohousing", "createacolony:autohousing-hint", snapshot.AutoHousing,
            on => SendText(ColonyAction.SetAutoHousing, on ? "1" : "0"), y);
        y = TownSwitch(composer, "entermessages", "createacolony:entermessages", "createacolony:entermessages-hint", snapshot.EnterMessages,
            on => SendText(ColonyAction.SetEnterMessages, "on|" + (on ? "1" : "0")), y);
        y = TownSwitch(composer, "telloutsiders", "createacolony:telloutsiders", "createacolony:telloutsiders-hint", snapshot.TellOutsiders,
            on => SendText(ColonyAction.SetEnterMessages, "outsiders|" + (on ? "1" : "0")), y);
        y = TownSwitch(composer, "settlersunhurt", "createacolony:settlersunhurt", "createacolony:settlersunhurt-hint", snapshot.SettlersUnhurt,
            on => SendText(ColonyAction.SetTestSafety, "unhurt|" + (on ? "1" : "0")), y);
        y = TownSwitch(composer, "noenemies", "createacolony:noenemies", "createacolony:noenemies-hint", snapshot.NoEnemies,
            on => SendText(ColonyAction.SetTestSafety, "enemies|" + (on ? "1" : "0")), y);
        y = TownSwitch(composer, "autoreport", "createacolony:autoreport", "createacolony:autoreport-hint", snapshot.AutoReport,
            on => SendText(ColonyAction.SetTestSafety, "report|" + (on ? "1" : "0")), y);
        y = TownSwitch(composer, "mayorfree", "createacolony:mayorfree", "createacolony:mayorfree-hint", snapshot.MayorFreeHuts,
            on => SendText(ColonyAction.MayorAction, "free|" + (on ? "1" : "0")), y);

        y = composer.Section(Lang.Get("createacolony:enter-texts"), y, Width);
        if (!Officer)
        {
            y = composer.Text(Lang.Get("createacolony:enter-text") + " " + (snapshot.EnterText.Length > 0 ? snapshot.EnterText : Lang.Get("createacolony:enter-colony", snapshot.Name)), GuiKit.Body(), 0, y, Width, 4);
            composer.Text(Lang.Get("createacolony:leave-text") + " " + (snapshot.LeaveText.Length > 0 ? snapshot.LeaveText : Lang.Get("createacolony:leave-colony", snapshot.Name)), GuiKit.Body(), 0, y, Width, 4);
            return;
        }
        foreach (var (kind, label) in new[] { ("enter", "createacolony:enter-text"), ("leave", "createacolony:leave-text") })
        {
            string which = kind;
            composer
                .AddStaticText(Lang.Get(label), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 90, 25))
                .AddTextInput(ElementBounds.Fixed(90, y, Width - 200, 28), text => { if (which == "enter") pendingEnter = text; else pendingLeave = text; }, GuiKit.Body(), "text-" + kind)
                .AddSmallButton(Lang.Get("createacolony:save"), () => SendText(ColonyAction.SetEnterMessages, which + "|" + (which == "enter" ? pendingEnter : pendingLeave)),
                    ElementBounds.Fixed(Width - 100, y, 100, 28));
            y += 38;
        }
        composer.Text(Lang.Get("createacolony:enter-texts-hint", snapshot.Name), GuiKit.Muted(), 0, y, Width, 8);
    }

    double TownSwitch(GuiComposer composer, string key, string label, string hint, bool value, Action<bool> set, double y)
    {
        if (Officer) composer.AddSwitch(set, ElementBounds.Fixed(0, y, 30, 30), key);
        else composer.AddStaticText(Lang.Get(value ? "createacolony:on" : "createacolony:off"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 36, 25));
        composer.AddStaticText(Lang.Get(label), GuiKit.Body(), ElementBounds.Fixed(40, y + 5, Width - 40, 25));
        y += 32;
        return composer.Text(Lang.Get(hint), GuiKit.Muted(), 40, y, Width - 40, 10);
    }

    /// <summary>
    /// Winter (0.190.0): how far off the first frost is, and the colony's food, firewood and warm clothes against what
    /// the winter will take, each with a plain verdict. Officers switch whether settlers get ready by themselves.
    /// </summary>
    void ComposeWinter(GuiComposer composer)
    {
        double y = 30;
        string cold = snapshot.WinterColdest.ToString("0");
        string head = snapshot.WinterState switch
        {
            ColonyManager.WinterState.Off => Lang.Get("createacolony:winter-off"),
            ColonyManager.WinterState.None => Lang.Get("createacolony:winter-none"),
            ColonyManager.WinterState.Early => Lang.Get("createacolony:winter-early", snapshot.WinterDays),
            ColonyManager.WinterState.Now => Lang.Get("createacolony:winter-now", snapshot.WinterDays, cold),
            _ => Lang.Get("createacolony:winter-coming", snapshot.WinterDays, snapshot.WinterLength, cold)
        };
        y = composer.Text(head, GuiKit.Body(), 0, y, Width, 10);
        if (snapshot.WinterState is ColonyManager.WinterState.Coming or ColonyManager.WinterState.Now)
        {
            const double pad = 10;
            var lines = new List<(string Text, bool Short)>
            {
                (Lang.Get("createacolony:winter-line-food", snapshot.WinterFood, snapshot.WinterFoodNeeded)
                    + " " + Verdict(snapshot.WinterFood, snapshot.WinterFoodNeeded), snapshot.WinterFood < snapshot.WinterFoodNeeded),
                (Lang.Get("createacolony:winter-line-firewood", snapshot.WinterFirewood, snapshot.WinterFirewoodNeeded)
                    + " " + Verdict(snapshot.WinterFirewood, snapshot.WinterFirewoodNeeded), snapshot.WinterFirewood < snapshot.WinterFirewoodNeeded),
                (Lang.Get("createacolony:winter-line-clothes", snapshot.WinterDressed, snapshot.WinterPeople, cold)
                    + " " + (snapshot.WinterDressed >= snapshot.WinterPeople ? Lang.Get("createacolony:winter-enough")
                        : Lang.Get("createacolony:winter-need-warmer", snapshot.WinterPeople - snapshot.WinterDressed)), snapshot.WinterDressed < snapshot.WinterPeople)
            };
            double inner = Width - 2 * pad;
            double height = lines.Sum(l => GuiKit.TextHeight(capi, GuiKit.Body(), l.Text, inner) + 8) + 2 * pad - 8;
            composer.AddInset(ElementBounds.Fixed(0, y, Width, height), 3, 0.8f);
            double ly = y + pad;
            foreach (var (text, isShort) in lines)
            {
                ly = composer.Text(text, GuiKit.Body().WithColor(isShort ? GuiKit.WarnColor : GuiKit.GoodColor), pad, ly, inner, 8);
            }
            y += height + 10;
            y = composer.Text(Lang.Get("createacolony:winter-how"), GuiKit.Muted(), 0, y, Width, 10);
            if (snapshot.WinterPrepping) y = composer.Text(Lang.Get("createacolony:winter-prepping"), GuiKit.Detail().WithColor(GuiKit.GoodColor), 0, y, Width, 10);
        }
        if (snapshot.WinterState == ColonyManager.WinterState.Off) return;
        y = composer.Section(Lang.Get("createacolony:winter-settings"), y, Width);
        TownSwitch(composer, "winterprep", "createacolony:winterprep", "createacolony:winterprep-hint", snapshot.WinterPrep,
            on => SendText(ColonyAction.SetWinterPrep, on ? "1" : "0"), y);

        static string Verdict(int have, int need) => have >= need ? Lang.Get("createacolony:winter-enough") : Lang.Get("createacolony:winter-short-by", need - have);
    }

    /// <summary>Raids (0.183.0): on, off or only when sent; their size; send one now; when the next may come.</summary>
    void ComposeRaidSettings(GuiComposer composer, double y)
    {
        y = composer.Section(Lang.Get("createacolony:raids"), y, Width);
        if (!snapshot.RaidsAllowed)
        {
            composer.Text(Lang.Get("createacolony:raids-off-server"), GuiKit.Detail(), 0, y, Width, 12);
            return;
        }
        if (snapshot.RaidStatus.Length > 0) y = composer.Text(snapshot.RaidStatus, GuiKit.Body(), 0, y, Width, 6);
        string[] modes = { "raids-mode-on", "raids-mode-off", "raids-mode-manual" };
        string[] sizes = { "raids-size-small", "raids-size-normal", "raids-size-big" };
        if (Officer)
        {
            int nextMode = (snapshot.RaidMode + 1) % 3, nextSize = (snapshot.RaidSize + 1) % 3;
            composer
                .AddStaticText(Lang.Get("createacolony:raids-mode"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 90, 25))
                .AddSmallButton(Lang.Get("createacolony:" + modes[Math.Clamp(snapshot.RaidMode, 0, 2)]), () =>
                {
                    network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetRaidMode, Text = nextMode.ToString() });
                    return true;
                }, ElementBounds.Fixed(90, y, 170, 28))
                .AddStaticText(Lang.Get("createacolony:raids-size"), GuiKit.Body(), ElementBounds.Fixed(280, y + 5, 60, 25))
                .AddSmallButton(Lang.Get("createacolony:" + sizes[Math.Clamp(snapshot.RaidSize, 0, 2)]), () =>
                {
                    network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetRaidSize, Text = nextSize.ToString() });
                    return true;
                }, ElementBounds.Fixed(340, y, 110, 28));
            if (snapshot.RaidStatus.Length == 0)
            {
                composer.AddSmallButton(Lang.Get("createacolony:raids-start"), () =>
                {
                    network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.StartRaid });
                    return true;
                }, ElementBounds.Fixed(Width - 130, y, 130, 28));
            }
            y += 36;
        }
        else
        {
            y = composer.Text(Lang.Get("createacolony:raids-mode") + " " + Lang.Get("createacolony:" + modes[Math.Clamp(snapshot.RaidMode, 0, 2)])
                + ", " + Lang.Get("createacolony:raids-size") + " " + Lang.Get("createacolony:" + sizes[Math.Clamp(snapshot.RaidSize, 0, 2)]), GuiKit.Body(), 0, y, Width, 6);
        }
        y = ComposeRaidRaces(composer, y);
        string next = snapshot.RaidMode != 0 ? ""
            : snapshot.DaysToNextRaid < 0 ? Lang.Get("createacolony:raids-too-small", snapshot.RaidMinSettlers)
            : Lang.Get("createacolony:raids-next", Math.Max(1, (int)Math.Ceiling(snapshot.DaysToNextRaid)));
        string record = Lang.Get("createacolony:raids-record", snapshot.RaidsWon, snapshot.RaidsTotal);
        composer.Text(next.Length > 0 ? next + " " + record : record, GuiKit.Muted(), 0, y, Width, 12);
    }

    /// <summary>Monster races (0.187.0): one On/Off button each, on by default (Aaron); a plain list for members.</summary>
    double ComposeRaidRaces(GuiComposer composer, double y)
    {
        if (snapshot.RaidRaces.Count == 0) return y;
        if (!Officer)
        {
            var on = snapshot.RaidRaces.Where(r => !snapshot.RaidRacesOff.Contains(r)).Select(r => Lang.Get("createacolony:race-" + r)).ToList();
            return composer.Text(Lang.Get("createacolony:raids-races") + " " + (on.Count > 0 ? string.Join(", ", on) : Lang.Get("createacolony:raids-races-none")), GuiKit.Body(), 0, y, Width, 6);
        }
        double x = 0;
        composer.AddStaticText(Lang.Get("createacolony:raids-races"), GuiKit.Body(), ElementBounds.Fixed(0, y + 5, 130, 25));
        x = 130;
        foreach (string race in snapshot.RaidRaces)
        {
            bool off = snapshot.RaidRacesOff.Contains(race);
            string label = Lang.Get("createacolony:race-" + race) + ": " + Lang.Get(off ? "createacolony:raids-race-off" : "createacolony:raids-race-on");
            string send = race + (off ? ":1" : ":0");
            composer.AddSmallButton(label, () =>
            {
                network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetRaidRace, Text = send });
                return true;
            }, ElementBounds.Fixed(x, y, 200, 28));
            x += 210;
            if (x + 200 > Width)
            {
                x = 130;
                y += 34;
            }
        }
        return y + (x > 130 ? 36 : 2);
    }

    // ---- Sending --------------------------------------------------------------------------

    bool Move(string key, int move)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = ColonyAction.MovePriority, BuildingId = 0, Text = key, Role = move });
        return true;
    }

    bool Send(ColonyAction action, int buildingId)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = action, BuildingId = buildingId });
        return true;
    }

    static string JobName(string code) => Lang.Get("createacolony:job-" + (code.Length == 0 ? "none" : code));

    static string RoleName(ColonyRole role) => Lang.Get("createacolony:role-" + role.ToString().ToLowerInvariant());

    bool OnRename()
    {
        renaming = false;
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.Rename, Text = pendingName });
        Compose();
        return true;
    }

    void OnSetJob(int citizenId, string job)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetJob, CitizenId = citizenId, Text = job });
    }

    void OnSetRole(string uid, int role)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.SetRole, PlayerUid = uid, Role = role });
    }

    bool OnRemove(string uid)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.RemoveMember, PlayerUid = uid });
        return true;
    }

    public override void OnGuiClosed()
    {
        network.SendAction(new ColonyActionPacket { ColonyId = ColonyId, Action = ColonyAction.CloseWindow });
        base.OnGuiClosed();
    }

    /// <summary>The mood word for a happiness, the same bands as ColonyManager.MoodOf.</summary>
    static string MoodWord(int h) => h >= 85 ? "joyful" : h >= 70 ? "happy" : h >= 45 ? "content" : h >= 30 ? "unhappy" : h >= 15 ? "miserable" : "broken";
}
