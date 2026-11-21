using System.Collections.Generic;
using System.Linq;
using CreateAColony.Network;
using CreateAColony.Quests;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The tracked quests, in the top right corner of the screen while playing: each one's name and its
/// objectives with progress, so the player can see what is left without opening the book.
/// </summary>
public class GuiDialogQuestTracker : HudElement
{
    const double Width = 250;

    readonly ColonyNetwork network;
    List<(QuestDef Quest, QuestStateView State)> tracked = new();

    public GuiDialogQuestTracker(ICoreClientAPI capi, ColonyNetwork network) : base(capi)
    {
        this.network = network;
    }

    public override double DrawOrder => 0.05;
    public override bool Focusable => false;

    /// <summary>Takes the tracked quests out of a fresh snapshot; opens or closes the panel as needed.</summary>
    public void Update(QuestBookSnapshot snapshot)
    {
        var library = network.ClientQuests;
        var fresh = snapshot.Quests
            .Where(s => s.Tracked && (QuestStatus)s.Status is QuestStatus.Available or QuestStatus.Active or QuestStatus.Complete)
            .Select(s => (Quest: library.Get(s.Code), State: s))
            .Where(p => p.Quest != null)
            .Select(p => (p.Quest!, p.State))
            .ToList();
        string before = Signature(tracked), after = Signature(fresh);
        tracked = fresh;
        if (tracked.Count == 0)
        {
            if (IsOpened()) TryClose();
            return;
        }
        if (before == after && IsOpened()) return;
        Compose();
        if (!IsOpened()) TryOpen();
    }

    static string Signature(List<(QuestDef Quest, QuestStateView State)> list) =>
        string.Join("|", list.Select(p => p.Quest.Code + p.State.Status + string.Join(",", p.State.Progress)));

    void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.LeftTop).WithFixedAlignmentOffset(8, 8);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(8);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        var composer = capi.Gui.CreateCompo("createacolony-questtracker", dialogBounds)
            .AddShadedDialogBG(bgBounds, false)
            .BeginChildElements(bgBounds);

        double y = 0;
        composer.AddStaticText(Lang.Get("createacolony:questbook-tracked"), GuiKit.Body(), ElementBounds.Fixed(0, y, Width, 22));
        y += 24;
        foreach (var (quest, state) in tracked)
        {
            var status = (QuestStatus)state.Status;
            var titleFont = status == QuestStatus.Complete ? GuiKit.Detail().WithColor(new[] { 1.0, 0.82, 0.25, 1.0 }) : GuiKit.Detail();
            string title = QuestLibrary.Title(quest);
            if (status == QuestStatus.Complete) title += " ✓";
            composer.AddStaticText(title, titleFont, ElementBounds.Fixed(0, y, Width, 20));
            y += 20;
            for (int i = 0; i < quest.Objectives.Count; i++)
            {
                var o = quest.Objectives[i];
                int progress = i < state.Progress.Count ? state.Progress[i] : 0;
                int needed = i < state.Needed.Count ? state.Needed[i] : 1;
                string line = "  " + GuiDialogQuestBook.ObjectiveText(capi, o);
                if (needed > 1 || o.Type is ObjectiveTypes.Obtain or ObjectiveTypes.Deliver or ObjectiveTypes.Stock) line += " " + progress + "/" + needed;
                var font = progress >= needed ? GuiKit.Muted().WithColor(GuiKit.GoodColor) : GuiKit.Muted();
                composer.AddStaticText(line, font, ElementBounds.Fixed(0, y, Width, 18));
                y += 18;
            }
            y += 4;
        }
        SingleComposer = composer.EndChildElements().Compose();
    }
}
