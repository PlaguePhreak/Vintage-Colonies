using CreateAColony.Colonies;
using CreateAColony.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The placement window shown beside a building's outline: turn it, nudge it forward, back, left,
/// right, up or down, order it built here, or take the hut back. Esc closes the window but leaves the
/// outline up, so the player can walk around it; right-clicking with the Builder's Wand brings the
/// window back.
/// </summary>
public class GuiDialogPlacement : GuiDialog
{
    const double Width = 330;

    readonly ColonyNetwork network;
    PlacementSnapshot snapshot;

    public int BuildingId => snapshot.BuildingId;

    /// <summary>Set when another building's window takes this one's place.</summary>
    public bool Replaced;

    public override string? ToggleKeyCombinationCode => null;

    public GuiDialogPlacement(ICoreClientAPI capi, ColonyNetwork network, PlacementSnapshot snapshot) : base(capi)
    {
        this.network = network;
        this.snapshot = snapshot;
        Compose();
    }

    public void Update(PlacementSnapshot fresh)
    {
        snapshot = fresh;
        Compose();
    }

    void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.RightMiddle).WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        string name = Lang.Get("createacolony:building-" + snapshot.Type);
        var composer = capi.Gui.CreateCompo("createacolony-placement", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("createacolony:placement-title", name), () => TryClose())
            .BeginChildElements(bgBounds);

        double y = 30;
        // What is shown, on a sunken panel: style, outline and the building's turn and offsets.
        string shown = snapshot.ShownLevel == 0
            ? Lang.Get("createacolony:placement-no-blueprint")
            : Lang.Get("createacolony:placement-shown", snapshot.ShownLevel, snapshot.Blocked, snapshot.Blocks, snapshot.MaxLevel);
        string state = snapshot.Planned
            ? Lang.Get("createacolony:placement-state-planned", snapshot.Rotation)
            : Lang.Get("createacolony:placement-state", snapshot.Rotation, Signed(snapshot.YOffset), Signed(snapshot.XOffset), Signed(snapshot.ZOffset));
        var shownFont = snapshot.ShownLevel == 0 ? GuiKit.Detail().WithColor(GuiKit.WarnColor) : GuiKit.Detail();
        double panel = 8 + 24 + GuiKit.TextHeight(capi, shownFont, shown, Width - 20) + 4 + GuiKit.TextHeight(capi, GuiKit.Body(), state, Width - 20) + 8;
        composer.AddInset(ElementBounds.Fixed(0, y, Width, panel), 3, 0.8f);
        composer.AddStaticText(Lang.Get("createacolony:placement-style", Lang.Get("createacolony:style-" + BlueprintStyles.Normalize(snapshot.Style))),
            GuiKit.Body(), ElementBounds.Fixed(10, y + 8, Width - 20, 22));
        double inner = composer.Text(shown, shownFont, 10, y + 32, Width - 20, 4);
        composer.Text(state, GuiKit.Body(), 10, inner, Width - 20, 0);
        y += panel + 10;

        // The buildings nearby, outlined so this one can go right beside them, and any it would overlap.
        if (snapshot.Overlaps.Count > 0)
        {
            y = composer.Text(Lang.Get("createacolony:placement-overlap", string.Join(", ", snapshot.Overlaps)), GuiKit.Body().WithColor(GuiKit.WarnColor), 0, y, Width, 4);
        }
        if (snapshot.OverlapsLater.Count > 0)
        {
            y = composer.Text(Lang.Get("createacolony:placement-overlap-later", string.Join(", ", snapshot.OverlapsLater)), GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, y, Width, 4);
        }
        if (snapshot.Nearby > 0)
        {
            y = composer.Text(Lang.Get("createacolony:placement-nearby", snapshot.Nearby), GuiKit.Muted(), 0, y, Width, 4);
        }
        if (snapshot.Overlaps.Count + snapshot.OverlapsLater.Count + snapshot.Nearby > 0) y += 6;

        // A type with several buildings (a small or a medium house): pick which one, while unbuilt.
        if (snapshot.Variants.Count > 1)
        {
            int at = System.Math.Max(0, snapshot.Variants.IndexOf(snapshot.Variant));
            y = composer.Text(Lang.Get("createacolony:placement-variant", GuiKit.VariantName(snapshot.Variant), at + 1, snapshot.Variants.Count), GuiKit.Body(), 0, y, Width, 4);
            if (snapshot.CanAdjust)
            {
                AddPair(composer, "variant-prev", ColonyAction.PlacementVariant, "-1", "variant-next", ColonyAction.PlacementVariant, "1", y);
                y += 34;
            }
            y += 4;
        }

        // Flip through the building's levels to see each one where it would stand. A building with types keeps
        // the buttons even when this type has one level, so they do not seem to be swapped for the type buttons.
        if (snapshot.MaxLevel > 1 || snapshot.Variants.Count > 1)
        {
            AddPair(composer, "level-down", ColonyAction.PlacementLevel, "-1", "level-up", ColonyAction.PlacementLevel, "1", y);
            y += 34;
        }

        if (snapshot.CanAdjust)
        {
            AddPair(composer, "turn-left", ColonyAction.PlacementTurn, "-1", "turn-right", ColonyAction.PlacementTurn, "1", y);
            y += 34;
            AddPair(composer, "forward", ColonyAction.PlacementNudge, "forward", "back", ColonyAction.PlacementNudge, "back", y);
            y += 34;
            AddPair(composer, "left", ColonyAction.PlacementNudge, "left", "right", ColonyAction.PlacementNudge, "right", y);
            y += 34;
            AddPair(composer, "lower", ColonyAction.PlacementHeight, "-1", "raise", ColonyAction.PlacementHeight, "1", y);
            y += 44;
        }
        else
        {
            y = composer.Text(Lang.Get("createacolony:placement-fixed"), GuiKit.Muted(), 0, y, Width, 10);
        }

        if (snapshot.Planned)
        {
            // In creative no block is needed, and the window says so (0.201.0).
            bool creative = capi.World.Player?.WorldData?.CurrentGameMode == Vintagestory.API.Common.EnumGameMode.Creative;
            y = composer.Text(snapshot.Have > 0 ? Lang.Get("createacolony:wand-have", snapshot.Have, name)
                : creative ? Lang.Get("createacolony:wand-free-creative", name) : Lang.Get("createacolony:wand-need-block", name),
                GuiKit.Detail().WithColor(snapshot.Have > 0 || creative ? GuiKit.GoodColor : GuiKit.WarnColor), 0, y, Width, 8);
            composer
                .AddSmallButton(Lang.Get("createacolony:placement-accept"), () => Finish(ColonyAction.PlacementConfirm), ElementBounds.Fixed(0, y, 160, 28))
                .AddSmallButton(Lang.Get("createacolony:placement-discard"), () => Finish(ColonyAction.PlacementCancel), ElementBounds.Fixed(170, y, 160, 28));
            y += 34;
            composer.AddSmallButton(Lang.Get("createacolony:placement-change"), () => Finish(ColonyAction.WandChange), ElementBounds.Fixed(0, y, Width, 28));
            y += 40;
        }
        else
        {
            if (snapshot.CanOrder)
            {
                composer.AddSmallButton(Lang.Get("createacolony:placement-confirm"), () => Finish(ColonyAction.PlacementConfirm), ElementBounds.Fixed(0, y, 160, 28));
            }
            if (snapshot.CanAdjust && snapshot.Type is not ("townhall" or "warehouse"))
            {
                composer.AddSmallButton(Lang.Get("createacolony:placement-cancel"), () => Finish(ColonyAction.PlacementCancel), ElementBounds.Fixed(170, y, 160, 28));
            }
            y += 34;
            composer.AddSmallButton(Lang.Get("createacolony:placement-hide"), () => Finish(ColonyAction.PlacementClose), ElementBounds.Fixed(0, y, Width, 28));
            y += 40;
        }
        composer.Text(Lang.Get(snapshot.Planned ? "createacolony:placement-hint-planned" : "createacolony:placement-hint"), GuiKit.Muted(), 0, y, Width);

        SingleComposer = composer.EndChildElements().Compose();
    }

    static string Signed(int n) => n > 0 ? "+" + n : n.ToString();

    void AddPair(GuiComposer composer, string leftKey, ColonyAction leftAction, string leftText, string rightKey, ColonyAction rightAction, string rightText, double y)
    {
        composer
            .AddSmallButton(Lang.Get("createacolony:placement-" + leftKey), () => Send(leftAction, leftText), ElementBounds.Fixed(0, y, 160, 28))
            .AddSmallButton(Lang.Get("createacolony:placement-" + rightKey), () => Send(rightAction, rightText), ElementBounds.Fixed(170, y, 160, 28));
    }

    bool Send(ColonyAction action, string text)
    {
        network.SendAction(new ColonyActionPacket { ColonyId = snapshot.ColonyId, Action = action, BuildingId = snapshot.BuildingId, Text = text });
        return true;
    }

    bool Finish(ColonyAction action)
    {
        Send(action, "");
        if (action != ColonyAction.WandChange) network.OutlineCleared();
        TryClose();
        return true;
    }
}
