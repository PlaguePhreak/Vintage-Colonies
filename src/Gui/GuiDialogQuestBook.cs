using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Network;
using CreateAColony.Quests;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The quest book, laid out like FTB Quests: it fills most of the screen, with every group and chapter in a
/// list down the left (groups fold, a red "!" marks a reward waiting) and the chosen chapter's map filling the
/// rest. Clicking a quest pops its card up over the map (GuiDialogQuestCard). All the text and layout comes
/// from the quest files; the server only says where each quest stands.
/// </summary>
public class GuiDialogQuestBook : GuiDialog
{
    const double SidebarWidth = 230, Gap = 8, TopRow = 34, BottomRow = 24;

    readonly ColonyNetwork network;
    readonly QuestLibrary library;
    QuestBookSnapshot snapshot;
    Dictionary<string, QuestStateView> states = new();
    string lastSignature = "";

    int groupIndex;
    // The chapter chosen in each group, the quest chosen in each chapter, and where each map is scrolled to.
    readonly Dictionary<string, string> chapterOf = new();
    readonly Dictionary<string, string?> selectedOf = new();
    readonly Dictionary<string, QuestMapView> views = new();
    readonly QuestSidebarState sidebarState = new();
    // Item icons looked up once per quest or chapter; null when nothing matched.
    readonly Dictionary<string, ItemStack?> icons = new();
    readonly Dictionary<string, ItemStack?> chapterIcons = new();
    GuiElementQuestMap? mapElement;
    GuiDialogQuestCard? card;

    public override string? ToggleKeyCombinationCode => null;

    public GuiDialogQuestBook(ICoreClientAPI capi, ColonyNetwork network, QuestBookSnapshot snapshot) : base(capi)
    {
        this.network = network;
        this.snapshot = snapshot;
        library = network.ClientQuests;
        Apply(snapshot);
        // Open on the first chapter that has quests.
        var first = ShownGroups().FirstOrDefault();
        if (first != null) groupIndex = library.Groups.IndexOf(first);
        Compose();
    }

    public void Update(QuestBookSnapshot fresh)
    {
        string before = lastSignature;
        Apply(fresh);
        // Timed refreshes only redraw when something moved, so the map and the list stay put.
        if (fresh.Periodic && before == lastSignature) return;
        Compose();
        // The open card shows the same quest with its new progress.
        if (card != null && card.IsOpened() && library.Get(card.QuestCode) is { } q) card.Show(q);
    }

    void Apply(QuestBookSnapshot fresh)
    {
        snapshot = fresh;
        states = fresh.Quests.ToDictionary(s => s.Code, s => s);
        lastSignature = string.Join("|", fresh.Quests.Select(s => s.Code + s.Status + (s.Tracked ? "t" : "") + string.Join(",", s.Progress))) + fresh.ColonyId;
    }

    // ---- What is chosen -------------------------------------------------------------------

    QuestGroupDef? Group => library.Groups.Count == 0 ? null : library.Groups[Math.Clamp(groupIndex, 0, library.Groups.Count - 1)];

    QuestChapterDef? Chapter
    {
        get
        {
            var group = Group;
            if (group == null) return null;
            var chapters = ShownChapters(group);
            if (chapters.Count == 0) return null;
            return chapters.FirstOrDefault(c => c.Code == chapterOf.GetValueOrDefault(group.Code)) ?? chapters[0];
        }
    }

    /// <summary>Chapters with at least one quest; empty ones are left out of the book until they are written.</summary>
    static List<QuestChapterDef> ShownChapters(QuestGroupDef group) => group.Chapters.Where(c => c.Quests.Count > 0).ToList();

    List<QuestGroupDef> ShownGroups() => library.Groups.Where(g => ShownChapters(g).Count > 0).ToList();

    public QuestStatus StatusOf(QuestDef q) => states.TryGetValue(q.Code, out var s) ? (QuestStatus)s.Status : QuestStatus.Locked;

    public QuestStateView? StateOf(QuestDef q) => states.GetValueOrDefault(q.Code);

    (int Done, int Total) ChapterProgress(QuestChapterDef chapter)
    {
        int total = chapter.Quests.Count(q => !q.Secret || StatusOf(q) != QuestStatus.Locked);
        int done = chapter.Quests.Count(q => StatusOf(q) == QuestStatus.Claimed);
        return (done, total);
    }

    int Waiting(QuestChapterDef chapter) => chapter.Quests.Count(q => StatusOf(q) == QuestStatus.Complete);

    /// <summary>Why a locked quest is locked, in words: the quests it needs, or that it needs a colony. "" when there is nothing to say.</summary>
    public string WhyLocked(QuestDef quest)
    {
        if (quest.Soon) return Lang.Get("createacolony:questbook-soon");
        if (quest.PerColony && snapshot.ColonyId.Length == 0) return Lang.Get("createacolony:questbook-needs-colony");
        var needs = quest.Requires.Select(r => library.Get(r)).Where(r => r != null && !r.Soon && StatusOf(r!) != QuestStatus.Claimed).Select(r => Lang.Get(QuestLibrary.TitleKey(r!))).ToList();
        if (quest.RequiresAny && needs.Count < quest.Requires.Count) return "";
        return needs.Count > 0 ? Lang.Get(quest.RequiresAny ? "createacolony:questbook-needs-any" : "createacolony:questbook-needs", string.Join(", ", needs)) : "";
    }

    // ---- Layout ---------------------------------------------------------------------------

    /// <summary>The window's inner size in unscaled pixels: most of the screen, whatever the GUI scale.</summary>
    (double Width, double Height) WindowSize()
    {
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double screenWidth = capi.Render.FrameWidth / scale, screenHeight = capi.Render.FrameHeight / scale;
        return (Math.Clamp(screenWidth - 90, 720, 1700), Math.Clamp(screenHeight - 150, 400, 1000));
    }

    void Compose()
    {
        var (width, height) = WindowSize();
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        string title = Lang.Get("createacolony:questbook-title");
        if (snapshot.ColonyName.Length > 0) title += " · " + snapshot.ColonyName;

        mapElement = null;
        var composer = capi.Gui.CreateCompo("createacolony-questbook", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(title, () => TryClose())
            .BeginChildElements(bgBounds);

        double top = 30;
        var groups = ShownGroups();
        if (groups.Count == 0)
        {
            composer.Text(Lang.Get("createacolony:quests-none-loaded"), GuiKit.Muted(), 0, top, width);
            SingleComposer = composer.EndChildElements().Compose();
            return;
        }
        var group = Group;
        if (group == null || !groups.Contains(group))
        {
            group = groups[0];
            groupIndex = library.Groups.IndexOf(group);
        }
        var chapter = Chapter!;

        // The list of groups and chapters down the left.
        double bodyHeight = height - TopRow - BottomRow;
        var rows = new List<QuestSidebarRow>();
        foreach (var g in groups)
        {
            var chapters = ShownChapters(g);
            rows.Add(new QuestSidebarRow { IsGroup = true, GroupCode = g.Code, Name = Lang.Get("createacolony:questgroup-" + g.Code), Waiting = chapters.Sum(Waiting) });
            foreach (var c in chapters)
            {
                var (done, total) = ChapterProgress(c);
                rows.Add(new QuestSidebarRow
                {
                    GroupCode = g.Code, ChapterCode = c.Code, Name = Lang.Get("createacolony:chapter-" + c.Code),
                    Done = done, Total = total, Waiting = Waiting(c), Selected = c == chapter, Icon = ChapterIcon(c)
                });
            }
        }
        var sidebarBounds = ElementBounds.Fixed(0, top, SidebarWidth, height);
        composer.AddInset(sidebarBounds.FlatCopy(), 3, 0.8f);
        composer.AddInteractiveElement(new GuiElementQuestSidebar(capi, sidebarBounds, rows, sidebarState, row =>
        {
            if (row.IsGroup) return;
            int index = library.Groups.FindIndex(g => g.Code == row.GroupCode);
            if (index < 0) return;
            if (index == groupIndex && chapterOf.GetValueOrDefault(row.GroupCode) == row.ChapterCode && Chapter?.Code == row.ChapterCode) return;
            // Close the card first, while the old chapter is still the open one, so its quest is let go.
            card?.TryClose();
            groupIndex = index;
            chapterOf[row.GroupCode] = row.ChapterCode;
            Compose();
        }), "questsidebar");

        // The top row: the chapter, how far along it is, and Claim all.
        double x = SidebarWidth + Gap, mapWidth = width - x;
        var (chapterDone, chapterTotal) = ChapterProgress(chapter);
        string scope = Lang.Get(chapter.PerColony ? "createacolony:questbook-scope-colony" : "createacolony:questbook-scope-player");
        composer.AddStaticText(Lang.Get("createacolony:questbook-progress", chapterDone, chapterTotal) + "   ·   " + scope, GuiKit.Body(), ElementBounds.Fixed(x, top + 5, mapWidth - 200, 24));
        int waiting = library.Groups.SelectMany(g => g.Chapters).Sum(Waiting);
        if (waiting > 0)
        {
            composer.AddSmallButton(Lang.Get("createacolony:questbook-claim-all", waiting), ClaimAll, ElementBounds.Fixed(width - 180, top, 180, 28));
        }

        // The chapter's map.
        var mapBounds = ElementBounds.Fixed(x, top + TopRow, mapWidth, bodyHeight);
        composer.AddInset(mapBounds.FlatCopy(), 3, 0.8f);
        var quests = chapter.Quests;
        string chapterCode = chapter.Code;
        if (!views.TryGetValue(chapterCode, out var view)) views[chapterCode] = view = new QuestMapView();
        mapElement = new GuiElementQuestMap(capi, mapBounds, quests, states, IconsFor(quests), view, selectedOf.GetValueOrDefault(chapterCode), code =>
        {
            selectedOf[chapterCode] = code;
            ShowCard(code == null ? null : quests.FirstOrDefault(q => q.Code == code));
        }, Lang.Get("createacolony:chapter-" + chapterCode));
        mapElement.CentreOnQuests(chapterCode);
        composer.AddInteractiveElement(mapElement, "questmap");

        composer.AddStaticText(Lang.Get("createacolony:questbook-pick"), GuiKit.Muted(), ElementBounds.Fixed(x, top + TopRow + bodyHeight + 6, mapWidth, 20));

        SingleComposer = composer.EndChildElements().Compose();
    }

    /// <summary>Pops the quest's card up over the map, or closes the card when no quest is chosen.</summary>
    void ShowCard(QuestDef? quest)
    {
        if (quest == null)
        {
            card?.TryClose();
            return;
        }
        if (card == null)
        {
            card = new GuiDialogQuestCard(capi, network, this, quest);
            card.TryOpen();
        }
        else
        {
            card.Show(quest);
            if (!card.IsOpened()) card.TryOpen();
        }
    }

    /// <summary>Called by the card when it closes: the map lets go of the quest it showed.</summary>
    public void CardClosed(GuiDialogQuestCard closed)
    {
        if (closed != card) return;
        var chapter = Chapter;
        if (chapter == null) return;
        selectedOf[chapter.Code] = null;
        mapElement?.Select(null);
    }

    /// <summary>Claims every quest whose reward is waiting, in every chapter.</summary>
    bool ClaimAll()
    {
        foreach (var q in library.Groups.SelectMany(g => g.Chapters).SelectMany(c => c.Quests).Where(q => StatusOf(q) == QuestStatus.Complete).ToList())
        {
            network.SendQuestAction(q.Code, QuestAction.Claim);
        }
        return true;
    }

    // Clicks on the card belong to the card, even though the book fills the screen behind it.
    public override void OnMouseDown(MouseEvent args)
    {
        if (card != null && card.Covers(args.X, args.Y)) return;
        base.OnMouseDown(args);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (card != null && card.Covers(args.X, args.Y)) return;
        base.OnMouseUp(args);
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (card != null && card.Covers(capi.Input.MouseX, capi.Input.MouseY)) return;
        base.OnMouseWheel(args);
    }

    public static double[] StatusColor(QuestStatus status) => status switch
    {
        QuestStatus.Locked => GuiKit.MutedColor,
        QuestStatus.Active => new[] { 0.5, 0.72, 1.0, 1.0 },
        QuestStatus.Complete => new[] { 1.0, 0.82, 0.25, 1.0 },
        QuestStatus.Claimed => GuiKit.GoodColor,
        _ => new[] { 1.0, 1.0, 1.0, 1.0 }
    };

    // ---- Words and pictures -----------------------------------------------------------------

    /// <summary>One objective in words, from its type: "Have 8 × Flint", "Build a Warehouse", "Have 3 settlers"...</summary>
    public static bool IsManual(ICoreClientAPI capi, QuestObjectiveDef o) =>
        o.Type == ObjectiveTypes.Manual || (o.Type is ObjectiveTypes.Obtain or ObjectiveTypes.Deliver or ObjectiveTypes.Stock && QuestItems.Sample(capi.World, o.Code) == null);

    public static string ObjectiveText(ICoreClientAPI capi, QuestObjectiveDef o)
    {
        if (o.Text.Length > 0) return Lang.Get("createacolony:" + o.Text);
        if (IsManual(capi, o)) return Lang.Get("createacolony:objective-manual");
        int count = Math.Max(1, o.Count);
        switch (o.Type)
        {
            case ObjectiveTypes.Obtain:
            case ObjectiveTypes.Deliver:
            case ObjectiveTypes.Stock:
                return Lang.Get("createacolony:objective-" + o.Type, count, ItemName(capi, o.Code));
            case ObjectiveTypes.Building:
                return o.Level > 1
                    ? Lang.Get("createacolony:objective-building-level", Lang.Get("createacolony:building-" + o.Code), o.Level)
                    : Lang.Get("createacolony:objective-building", Lang.Get("createacolony:building-" + o.Code));
            case ObjectiveTypes.Buildings:
                return o.Code.Length > 0
                    ? Lang.Get("createacolony:objective-buildings-of", count, Lang.Get("createacolony:building-" + o.Code))
                    : Lang.Get("createacolony:objective-buildings", count);
            case ObjectiveTypes.Job:
                return Lang.Get("createacolony:objective-job", count, Lang.Get("createacolony:job-" + o.Code));
            case ObjectiveTypes.Stat:
                return Lang.Get("createacolony:objective-stat", count, GuiDialogHut.StatLabel(o.Code));
            case ObjectiveTypes.Event:
                return Lang.Get("createacolony:objective-event", count, Lang.Get("createacolony:" + o.Code));
            default:
                return Lang.Get("createacolony:objective-" + o.Type, count);
        }
    }

    /// <summary>A readable name for an item code or wildcard, from the first matching item.</summary>
    static string ItemName(ICoreClientAPI capi, string code)
    {
        var stack = QuestItems.Sample(capi.World, code);
        if (stack != null) return stack.GetName();
        // Fall back to the code's last part, with the wildcards taken out.
        string tail = code.Contains(':') ? code[(code.IndexOf(':') + 1)..] : code;
        return tail.Replace("*", "").Replace("-", " ").Trim();
    }

    Dictionary<string, ItemStack?> IconsFor(List<QuestDef> quests)
    {
        foreach (var q in quests)
        {
            if (icons.ContainsKey(q.Code)) continue;
            ItemStack? stack = q.Icon.Length > 0 ? QuestItems.Sample(capi.World, q.Icon) : null;
            if (stack == null)
            {
                var first = q.Objectives.FirstOrDefault(o => o.Type is ObjectiveTypes.Obtain or ObjectiveTypes.Deliver or ObjectiveTypes.Stock);
                if (first != null) stack = QuestItems.Sample(capi.World, first.Code);
            }
            stack ??= QuestItems.Sample(capi.World, "game:paper-parchment");
            icons[q.Code] = stack;
        }
        return icons;
    }

    /// <summary>A chapter's icon for the side list: its own, else its first quest's.</summary>
    ItemStack? ChapterIcon(QuestChapterDef chapter)
    {
        if (chapterIcons.TryGetValue(chapter.Code, out var known)) return known;
        ItemStack? stack = chapter.Icon.Length > 0 ? QuestItems.Sample(capi.World, chapter.Icon) : null;
        if (stack == null && chapter.Quests.Count > 0) stack = IconsFor(chapter.Quests.Take(1).ToList()).GetValueOrDefault(chapter.Quests[0].Code);
        return chapterIcons[chapter.Code] = stack;
    }

    public override void OnGuiClosed()
    {
        card?.TryClose();
        network.SendQuestAction("", QuestAction.CloseWindow);
        base.OnGuiClosed();
    }
}
