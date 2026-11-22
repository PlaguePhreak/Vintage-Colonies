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
/// The Archive's research as a full-screen tree, drawn with the quest book's map: one column per tier, one row
/// per branch, lines from the research each one needs. Grey with a padlock is locked, white can be started,
/// blue is running or paused, green is done. Clicking a research shows what it does, what it costs and how far
/// along it is in the panel below, with Start, Pause and Resume for officers. Opened from the Research tab.
/// </summary>
public class GuiDialogResearchTree : GuiDialog
{
    public const double TierGap = 4, NodeGap = 1.2, BranchGap = 1.5;
    static readonly string[] BranchOrder = { "settlement", "production", "population", "defence", "exploration" };
    static readonly Dictionary<string, string> BranchIcons = new()
    {
        ["settlement"] = "createacolony:townhall", ["production"] = "game:hammer-copper", ["population"] = "createacolony:hut-residence",
        ["defence"] = "game:spear-generic-copper", ["exploration"] = "game:paper-parchment"
    };

    readonly Func<HutSnapshot> snapshotOf;
    readonly Func<bool> officer;
    readonly Action<ColonyAction, string> send;
    readonly QuestMapView view = new();
    readonly Dictionary<string, ItemStack?> icons = new();
    readonly QuestChapterDef chapter = new() { Code = "researchtree", Group = new QuestGroupDef { Code = "researchtree" } };
    string? selected;

    public override string? ToggleKeyCombinationCode => null;
    public override float ZSize => 500;

    public GuiDialogResearchTree(ICoreClientAPI capi, Func<HutSnapshot> snapshotOf, Func<bool> officer, Action<ColonyAction, string> send) : base(capi)
    {
        this.snapshotOf = snapshotOf;
        this.officer = officer;
        this.send = send;
        Compose();
    }

    /// <summary>The Archive sent fresh numbers: draw them, keeping the map where it is.</summary>
    public void Refresh()
    {
        if (IsOpened()) Compose();
    }

    /// <summary>Where each research sits: tiers left to right, branches top to bottom. The quest book's Research chapter uses the same places.</summary>
    public static Dictionary<string, (double X, double Y)> Layout(IEnumerable<(string Code, string Branch, int Tier)> nodes)
    {
        var list = nodes.ToList();
        var branches = BranchOrder.Concat(list.Select(n => n.Branch).Distinct().Where(b => !BranchOrder.Contains(b)).OrderBy(b => b)).ToList();
        var places = new Dictionary<string, (double, double)>();
        foreach (var cell in list.GroupBy(n => (n.Branch, n.Tier)))
        {
            int k = 0;
            foreach (var n in cell) places[n.Code] = ((cell.Key.Tier - 1) * TierGap + k++ * NodeGap, branches.IndexOf(cell.Key.Branch) * BranchGap);
        }
        return places;
    }

    void Compose()
    {
        var snapshot = snapshotOf();
        double scale = Math.Max(0.5, RuntimeEnv.GUIScale);
        double width = Math.Clamp(capi.Render.FrameWidth / scale - 90, 720, 1700), height = Math.Clamp(capi.Render.FrameHeight / scale - 150, 400, 1000);
        const double panel = 150;

        var places = Layout(snapshot.Research.Select(r => (r.Code, r.Branch, r.Tier)));
        var quests = new List<QuestDef>();
        var states = new Dictionary<string, QuestStateView>();
        foreach (var r in snapshot.Research)
        {
            var (x, y) = places[r.Code];
            quests.Add(new QuestDef
            {
                Code = r.Code, X = x, Y = y, Chapter = chapter, Requires = r.Needs.ToList(),
                Title = Lang.Get("createacolony:research-" + r.Code),
                Shape = r.Tier >= 4 ? QuestShapes.Star : r.Tier == 3 ? QuestShapes.Square : QuestShapes.Round
            });
            var status = r.State switch { 4 => QuestStatus.Claimed, 3 => QuestStatus.Active, 2 => QuestStatus.Active, 1 => QuestStatus.Available, _ => QuestStatus.Locked };
            states[r.Code] = new QuestStateView { Code = r.Code, Status = (int)status };
            if (!icons.ContainsKey(r.Code))
            {
                icons[r.Code] = QuestItems.Sample(capi.World, BranchIcons.GetValueOrDefault(r.Branch, "game:paper-parchment")) ?? QuestItems.Sample(capi.World, "game:paper-parchment");
            }
        }

        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        var composer = capi.Gui.CreateCompo("createacolony-researchtree", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("createacolony:researchtree-title"), () => TryClose())
            .BeginChildElements(bgBounds);

        double top = 30;
        string stats = Lang.Get("createacolony:researchtree-stats", snapshot.ArchiveTier, snapshot.ResearchRunning, snapshot.ResearchSlots, snapshot.KnowledgeTokens, snapshot.StoredResearchPoints, snapshot.ResearchSpeed);
        composer.AddStaticText(stats, GuiKit.Body(), ElementBounds.Fixed(0, top + 4, width - 210, 24));
        composer.AddSmallButton(Lang.Get("createacolony:research-handin"), () => { send(ColonyAction.HandInResearch, ""); return true; }, ElementBounds.Fixed(width - 200, top, 200, 28));
        top += 36;

        var mapBounds = ElementBounds.Fixed(0, top, width, height - panel - 40);
        composer.AddInset(mapBounds.FlatCopy(), 3, 0.8f);
        var map = new GuiElementQuestMap(capi, mapBounds, quests, states, icons, view, selected, code =>
        {
            if (selected == code) return;
            selected = code;
            // Redrawn after the click is over, so the map is not replaced while it still handles the mouse.
            capi.Event.EnqueueMainThreadTask(() => { if (IsOpened()) Compose(); }, "createacolony-researchtree");
        }, Lang.Get("createacolony:researchtree-legend"));
        map.CentreOnQuests("researchtree");
        composer.AddInteractiveElement(map, "researchmap");

        // The picked research.
        double py = top + mapBounds.fixedHeight + 8;
        var picked = snapshot.Research.Find(r => r.Code == selected);
        if (picked == null)
        {
            composer.Text(Lang.Get("createacolony:researchtree-pick"), GuiKit.Muted(), 0, py, width);
        }
        else
        {
            string name = Lang.Get("createacolony:research-" + picked.Code) + "  ·  " + Lang.Get("createacolony:research-branch-" + picked.Branch) + "  ·  " + Lang.Get("createacolony:research-tier-short", picked.Tier);
            var font = picked.State == 4 ? GuiKit.Body().WithColor(GuiKit.GoodColor) : picked.State == 0 ? GuiKit.Body().WithColor(GuiKit.MutedColor) : GuiKit.Body();
            composer.AddStaticText(name, font, ElementBounds.Fixed(0, py + 4, width - 130, 24));
            string code = picked.Code;
            if (picked.State == 4)
            {
                composer.AddStaticText(Lang.Get("createacolony:research-done"), GuiKit.Body().WithColor(GuiKit.GoodColor).WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(width - 120, py + 4, 120, 24));
            }
            else if (officer() && !picked.Elsewhere && picked.State is 1 or 2 or 3)
            {
                bool pause = picked.State == 3;
                string label = Lang.Get(pause ? "createacolony:research-pause" : picked.State == 2 ? "createacolony:research-resume" : "createacolony:research-start");
                composer.AddSmallButton(label, () => { send(pause ? ColonyAction.PauseResearch : ColonyAction.StartResearch, code); return true; }, ElementBounds.Fixed(width - 120, py, 120, 28));
            }
            py += 32;
            py = composer.Text(Lang.Get("createacolony:research-" + picked.Code + "-desc"), GuiKit.Detail(), 0, py, width, 2);
            if (picked.State != 4) py = composer.Text(Lang.Get("createacolony:research-cost", picked.PointsNeeded, picked.Cost), GuiKit.Muted(), 0, py, width, 2);
            if (picked.State is 2 or 3)
            {
                int percent = picked.PointsNeeded <= 0 ? 0 : (int)Math.Round(100.0 * picked.Points / picked.PointsNeeded);
                string line = Lang.Get("createacolony:research-progress", picked.Points, picked.PointsNeeded, percent);
                if (picked.State == 2) line += " - " + Lang.Get("createacolony:research-paused");
                else if (picked.Elsewhere) line += " - " + Lang.Get("createacolony:research-elsewhere");
                py = composer.Text(line, GuiKit.Detail(), 0, py, width, 2);
            }
            if (picked.Note.Length > 0)
            {
                string note = picked.State == 0 ? Lang.Get("createacolony:research-locked-note", picked.Note) : picked.Note;
                composer.Text(note, GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, py, width, 2);
            }
        }
        // Keeps the window the same height whatever is picked.
        composer.AddStaticText("", GuiKit.Detail(), ElementBounds.Fixed(0, top + mapBounds.fixedHeight + panel, 10, 4));

        SingleComposer = composer.EndChildElements().Compose();
    }
}
