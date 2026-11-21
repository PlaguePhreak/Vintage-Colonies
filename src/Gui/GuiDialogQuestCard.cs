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
/// The card that pops up over the quest book's map when a quest is clicked, as in FTB Quests: the quest's name
/// in the title bar, Tasks on the left and Rewards on the right, the story under them, then the buttons.
/// It is a small window of its own, so the map keeps the whole screen behind it.
/// </summary>
public class GuiDialogQuestCard : GuiDialog
{
    const double Width = 440;

    readonly ColonyNetwork network;
    readonly GuiDialogQuestBook book;
    QuestDef quest;

    public override string? ToggleKeyCombinationCode => null;
    // Always drawn above the quest book, which fills the screen behind it.
    public override double DrawOrder => 0.2;
    public override bool PrefersUngrabbedMouse => true;

    public string QuestCode => quest.Code;

    public GuiDialogQuestCard(ICoreClientAPI capi, ColonyNetwork network, GuiDialogQuestBook book, QuestDef quest) : base(capi)
    {
        this.network = network;
        this.book = book;
        this.quest = quest;
        Compose();
    }

    /// <summary>Shows another quest, or the same one with fresh progress.</summary>
    public void Show(QuestDef fresh)
    {
        quest = fresh;
        Compose();
    }

    /// <summary>True when a screen point lies on the card, so the book under it leaves the click alone.</summary>
    public bool Covers(int x, int y) => IsOpened() && SingleComposer?.Bounds.PointInside(x, y) == true;

    void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightMiddle).WithFixedAlignmentOffset(-40, 0);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var status = book.StatusOf(quest);
        var state = book.StateOf(quest);
        var composer = capi.Gui.CreateCompo("createacolony-questcard", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get(QuestLibrary.TitleKey(quest)), () => TryClose())
            .BeginChildElements(bgBounds);

        double y = 30;
        var statusFont = GuiKit.Body().WithColor(GuiDialogQuestBook.StatusColor(status));
        composer.AddStaticText(Lang.Get("createacolony:queststatus-" + status.ToString().ToLowerInvariant()), statusFont, ElementBounds.Fixed(0, y, Width / 2, 24));
        string scope = Lang.Get(quest.PerColony ? "createacolony:questbook-scope-colony" : "createacolony:questbook-scope-player");
        composer.AddStaticText(scope, GuiKit.Muted().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width / 2, y + 2, Width / 2, 24));
        y += 28;

        // Tasks on the left, Rewards on the right, side by side in two boxes.
        const double gap = 8, pad = 8;
        double leftWidth = Math.Floor(Width * 0.6), rightX = leftWidth + gap, rightWidth = Width - rightX;
        double boxTop = y;
        double innerLeft = leftWidth - 2 * pad, innerRight = rightWidth - 2 * pad;

        // Work out both columns first, so the boxes can be drawn behind them.
        var lines = new List<(string Text, CairoFont Font)>();
        for (int i = 0; i < quest.Objectives.Count; i++)
        {
            var o = quest.Objectives[i];
            int progress = state != null && i < state.Progress.Count ? state.Progress[i] : 0;
            int needed = state != null && i < state.Needed.Count ? state.Needed[i] : Math.Max(1, o.Count);
            bool done = status == QuestStatus.Claimed || progress >= needed;
            string line = (done ? "✓ " : "• ") + GuiDialogQuestBook.ObjectiveText(capi, o);
            if (needed > 1 || o.Type is ObjectiveTypes.Obtain or ObjectiveTypes.Deliver or ObjectiveTypes.Stock) line += "  (" + Math.Min(progress, needed) + " / " + needed + ")";
            lines.Add((line, done ? GuiKit.Detail().WithColor(GuiKit.GoodColor) : GuiKit.Detail()));
        }
        if (lines.Count == 0) lines.Add((Lang.Get("createacolony:questbook-no-tasks"), GuiKit.Muted()));

        var parts = new List<RichTextComponentBase>();
        foreach (var reward in quest.Rewards)
        {
            var stack = QuestItems.Sample(capi.World, reward.Code);
            if (stack == null) continue;
            stack = stack.Clone();
            stack.StackSize = Math.Max(1, reward.Count);
            parts.Add(new ItemstackTextComponent(capi, stack, 40, 4, EnumFloat.Inline));
        }
        string noRewards = Lang.Get("createacolony:questbook-no-rewards");
        int perRow = Math.Max(1, (int)(innerRight / 44));
        double rewardsHeight = parts.Count > 0 ? Math.Ceiling(parts.Count / (double)perRow) * 46 : GuiKit.TextHeight(capi, GuiKit.Muted(), noRewards, innerRight) + 4;
        double tasksHeight = lines.Sum(l => GuiKit.TextHeight(capi, l.Font, l.Text, innerLeft) + 4);
        double boxHeight = 34 + Math.Max(tasksHeight, rewardsHeight) + pad;

        composer.AddInset(ElementBounds.Fixed(0, boxTop, leftWidth, boxHeight), 3, 0.8f);
        composer.AddInset(ElementBounds.Fixed(rightX, boxTop, rightWidth, boxHeight), 3, 0.8f);
        composer.AddStaticText(Lang.Get("createacolony:questbook-tasks"), GuiKit.Heading().WithColor(new[] { 0.55, 0.7, 1.0, 1.0 }), ElementBounds.Fixed(pad, y + 6, innerLeft, 24));
        composer.AddStaticText(Lang.Get("createacolony:questbook-rewards"), GuiKit.Heading().WithColor(new[] { 1.0, 0.75, 0.3, 1.0 }), ElementBounds.Fixed(rightX + pad, y + 6, innerRight, 24));
        double left = y + 34;
        foreach (var (text, font) in lines) left = composer.Text(text, font, pad, left, innerLeft, 4);
        if (parts.Count > 0) composer.AddRichtext(parts.ToArray(), ElementBounds.Fixed(rightX + pad, y + 34, innerRight, rewardsHeight));
        else composer.Text(noRewards, GuiKit.Muted(), rightX + pad, y + 34, innerRight, 4);
        y = boxTop + boxHeight + 10;

        // The story. Quest text may use the game's text tags, such as <font color="#ffd966">, for coloured words.
        string desc = Lang.Get(QuestLibrary.TitleKey(quest) + "-desc");
        if (desc.EndsWith("-desc")) desc = "";
        if (desc.Length > 0) y = composer.Rich(desc, GuiKit.Detail(), 0, y, Width, 10);

        if (status == QuestStatus.Locked)
        {
            string why = book.WhyLocked(quest);
            if (why.Length > 0) y = composer.Text(why, GuiKit.Muted(), 0, y, Width, 10);
        }
        else if (status == QuestStatus.Available && quest.Accept)
        {
            y = composer.Text(Lang.Get("createacolony:questbook-accept-hint"), GuiKit.Muted(), 0, y, Width, 10);
        }

        // Buttons along the bottom, from the right.
        double bx = Width;
        void Button(string key, Action click)
        {
            bx -= 100;
            composer.AddSmallButton(Lang.Get("createacolony:" + key), () => { click(); return true; }, ElementBounds.Fixed(bx, y, 96, 28));
            bx -= 6;
        }
        switch (status)
        {
            case QuestStatus.Available when quest.Accept:
                Button("quest-accept", () => network.SendQuestAction(quest.Code, QuestAction.Accept));
                break;
            case QuestStatus.Complete:
                Button("quest-claim", () => network.SendQuestAction(quest.Code, QuestAction.Claim));
                break;
            case QuestStatus.Active when quest.Accept:
                Button("quest-drop", () => network.SendQuestAction(quest.Code, QuestAction.Drop));
                break;
        }
        if (status == QuestStatus.Active && Enumerable.Range(0, quest.Objectives.Count).Any(i => GuiDialogQuestBook.IsManual(capi, quest.Objectives[i]) && (state == null || i >= state.Progress.Count || state.Progress[i] < Math.Max(1, quest.Objectives[i].Count))))
        {
            bx -= 40;
            composer.AddSmallButton(Lang.Get("createacolony:quest-check"), () => { network.SendQuestAction(quest.Code, QuestAction.Check); return true; }, ElementBounds.Fixed(bx - 100, y, 136, 28));
            bx -= 106;
        }
        if (status is QuestStatus.Available or QuestStatus.Active or QuestStatus.Complete)
        {
            bool tracked = state?.Tracked == true;
            Button(tracked ? "quest-untrack" : "quest-track", () => network.SendQuestAction(quest.Code, QuestAction.Track));
        }
        composer.AddSmallButton(Lang.Get("createacolony:quest-card-close"), () => TryClose(), ElementBounds.Fixed(0, y, 96, 28));

        SingleComposer = composer.EndChildElements().Compose();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        book.CardClosed(this);
    }
}
