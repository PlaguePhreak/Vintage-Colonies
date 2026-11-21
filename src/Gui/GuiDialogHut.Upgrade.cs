using CreateAColony.Colonies;
using CreateAColony.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The Upgrade tab every hut with levels has (Aaron, 0.172.0): its level, which levels its pack has blueprints for,
/// the normal build buttons, and Force upgrade when the next level has no blueprint (ColonyManager.ForceUpgrade.cs).
/// </summary>
public partial class GuiDialogHut
{
    const int UpgradeTab = 2600;
    /// <summary>Force upgrade was pressed once; the second press sends it.</summary>
    bool confirmForce;

    bool HasUpgradeTab => !snapshot.IsStation && !snapshot.IsMineEntrance && snapshot.TopLevel > 0;

    void ComposeUpgrade(GuiComposer composer)
    {
        var b = snapshot.Building;
        double y = composer.Section(Lang.Get("createacolony:upgrade-level-section"), 30, Width);
        y = composer.Text(Lang.Get("createacolony:upgrade-level", b.Level, snapshot.TopLevel), GuiKit.Body(), 0, y, Width, 4);
        string blueprints = b.MaxLevel == 0 ? Lang.Get("createacolony:upgrade-no-blueprints")
            : b.MaxLevel == 1 ? Lang.Get("createacolony:upgrade-blueprints-one")
            : Lang.Get("createacolony:upgrade-blueprints", b.MaxLevel);
        y = composer.Text(blueprints, GuiKit.Muted(), 0, y, Width, 10);

        // Building, removing, or a next level with a blueprint: the normal buttons, as on the Main tab.
        if (b.TargetLevel > 0 || b.Removing || b.Level < b.MaxLevel)
        {
            y = composer.Section(Lang.Get("createacolony:upgrade-build-section"), y, Width);
            if (b.TargetLevel > b.Level) y = composer.Text(Lang.Get("createacolony:upgrade-building", b.TargetLevel), GuiKit.Body(), 0, y, Width, 6);
            y = AddOrderButtons(composer, b, y);
            return;
        }

        if (b.Level >= snapshot.TopLevel)
        {
            composer.Text(Lang.Get("createacolony:upgrade-top"), GuiKit.Body(), 0, y, Width);
            return;
        }

        y = composer.Section(Lang.Get("createacolony:upgrade-force-section"), y, Width);
        int next = b.Level + 1;
        if (snapshot.ForceUpgradeTo == 0)
        {
            // Turned off in the server config.
            composer.Text(Lang.Get("createacolony:upgrade-force-off", next), GuiKit.Muted(), 0, y, Width);
            return;
        }
        y = composer.Text(Lang.Get("createacolony:upgrade-force-hint", next), GuiKit.Body(), 0, y, Width, 8);
        if (!Officer)
        {
            composer.Text(Lang.Get("createacolony:upgrade-force-officers"), GuiKit.Muted(), 0, y, Width);
            return;
        }
        int id = b.BuildingId;
        string label = Lang.Get(confirmForce ? "createacolony:upgrade-force-sure" : "createacolony:upgrade-force", next);
        composer.AddSmallButton(label, () =>
        {
            if (!confirmForce)
            {
                confirmForce = true;
                Compose();
                return true;
            }
            confirmForce = false;
            return Send(ColonyAction.ForceUpgrade, id);
        }, ElementBounds.Fixed(0, y, 220, 28));
        if (confirmForce) composer.AddSmallButton(Lang.Get("createacolony:cancel"), () => { confirmForce = false; Compose(); return true; }, ElementBounds.Fixed(230, y, 100, 28));
    }
}
