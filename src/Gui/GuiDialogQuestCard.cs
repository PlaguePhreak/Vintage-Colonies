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

    /// <summary>A link in the quest text. Handbook links open behind the full-screen book, so the book and card close first.</summary>
    void OnLink(LinkTextComponent link)
    {
        string href = link.Href ?? "";
        if (href.StartsWith("handbook", StringComparison.OrdinalIgnoreCase))
        {
            TryClose();
            book.TryClose();
        }
        link.HandleLink();
    }

    void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightMiddle).WithFixedAlignmentOffset(-40, 0);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var status = book.StatusOf(quest);
        var state = book.StateOf(quest);
        var composer = capi.Gui.CreateCompo("createacolony-questcard", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(QuestLibrary.Title(quest), () => TryClose())
            .BeginChildElements(bgBounds);

        double y = 30;
        var statusFont = GuiKit.Body().WithColor(GuiDialogQuestBook.StatusColor(status));
        composer.AddStaticText(Lang.Get("createacolony:queststatus-" + status.ToString().ToLowerInvariant()), statusFont, ElementBounds.Fixed(0, y, Width / 2, 24));
        string scope = Lang.Get(quest.PerColony ? "createacolony:questbook-scope-colony" : "createacolony:questbook-scope-player");
        composer.AddStaticText(scope, GuiKit.Muted().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width / 2, y + 2, Width / 2, 24));
        y += 28;

        // The story first. Quest text may use the game's text tags: <font color="#ffd966"> for coloured words,
        // and <a href="handbook://craftinginfo-starterguide"> for a link into the handbook.
        composer.AddInset(ElementBounds.Fixed(0, y, Width, 2), 1, 0.5f);
        y += 10;
        string desc = QuestLibrary.Desc(quest);
        if (desc.Length > 0)
        {
            double descHeight = GuiKit.RichHeight(capi, GuiKit.Detail(), desc, Width);
            composer.AddRichtext(desc, GuiKit.Detail(), ElementBounds.Fixed(0, y, Width, descHeight), OnLink, "desc");
            y += descHeight + 8;
        }
        if (status == QuestStatus.Locked)
        {
            string why = book.WhyLocked(quest);
            if (why.Length > 0) y = composer.Text(why, GuiKit.Muted(), 0, y, Width, 8);
        }
        else if (status == QuestStatus.Available && quest.Accept)
        {
            y = composer.Text(Lang.Get("createacolony:questbook-accept-hint"), GuiKit.Muted(), 0, y, Width, 8);
        }

        // One row: a small item picture (or a dot), the text, and a number at the right.
        const double iconSize = 26, textX = 38;
        void Row(ItemStack? stack, string text, string right, CairoFont font)
        {
            double rightWidth = right.Length > 0 ? 70 : 0;
            double textWidth = Width - textX - rightWidth;
            double height = Math.Max(iconSize + 4, GuiKit.TextHeight(capi, font, text, textWidth) + 6);
            if (stack != null)
                composer.AddRichtext(new RichTextComponentBase[] { new ItemstackTextComponent(capi, stack, iconSize, 0, EnumFloat.Inline) }, ElementBounds.Fixed(2, y, iconSize + 6, iconSize + 4));
            else
                composer.AddStaticText("•", font, ElementBounds.Fixed(12, y + 4, 20, 24));
            composer.AddStaticText(text, font, ElementBounds.Fixed(textX, y + 4, textWidth, height));
            if (right.Length > 0)
                composer.AddStaticText(right, GuiKit.Muted().WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - rightWidth, y + 5, rightWidth, 22));
            y += height;
        }
        void Heading(string key)
        {
            composer.AddInset(ElementBounds.Fixed(0, y + 2, Width, 2), 1, 0.5f);
            composer.AddStaticText(Lang.Get("createacolony:" + key).ToUpperInvariant(), GuiKit.Muted(), ElementBounds.Fixed(0, y + 12, Width, 22));
            y += 36;
        }

        Heading("questbook-tasks");
        if (quest.Objectives.Count == 0) y = composer.Text(Lang.Get("createacolony:questbook-no-tasks"), GuiKit.Muted(), 0, y, Width, 4);
        for (int i = 0; i < quest.Objectives.Count; i++)
        {
            var o = quest.Objectives[i];
            int progress = state != null && i < state.Progress.Count ? state.Progress[i] : 0;
            int needed = state != null && i < state.Needed.Count ? state.Needed[i] : Math.Max(1, o.Count);
            bool done = status == QuestStatus.Claimed || progress >= needed;
            string count = done ? "✓" : needed > 1 || o.Type is ObjectiveTypes.Obtain or ObjectiveTypes.Deliver or ObjectiveTypes.Stock ? Math.Min(progress, needed) + " / " + needed : "";
            ItemStack? picture = o.Type is ObjectiveTypes.Obtain or ObjectiveTypes.Deliver or ObjectiveTypes.Stock or ObjectiveTypes.Place or ObjectiveTypes.BlockNear or ObjectiveTypes.Holds && o.Code.Length > 0
                ? QuestItems.Sample(capi.World, o.Code) : null;
            Row(picture, GuiDialogQuestBook.ObjectiveText(capi, o), count, done ? GuiKit.Detail().WithColor(GuiKit.GoodColor) : GuiKit.Detail());
        }
        y += 6;

        Heading("questbook-rewards");
        bool anyReward = false;
        foreach (var reward in quest.Rewards)
        {
            var stack = QuestItems.Sample(capi.World, reward.Code);
            if (stack == null) continue;
            anyReward = true;
            Row(stack, Math.Max(1, reward.Count) + "× " + stack.GetName(), "", GuiKit.Detail());
        }
        if (!anyReward) y = composer.Text(Lang.Get("createacolony:questbook-no-rewards"), GuiKit.Muted(), 0, y, Width, 4);
        y += 12;

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
