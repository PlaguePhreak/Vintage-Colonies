using CreateAColony.Network;

namespace CreateAColony.Colonies;

/// <summary>
/// Force upgrade (Aaron, 0.172.0): a hut whose asset pack has no blueprint for its next level can still go up a level
/// from the Upgrade tab. Nothing is built; the hut just gets the new level, with everything a level gives (more
/// workers, more land, higher skill caps). The building that stands keeps the last blueprint its pack has, so
/// repairs and upkeep use that one (BlueprintLibrary.Get).
/// </summary>
public partial class ColonyManager
{
    /// <summary>The level Force upgrade would raise the building to, or 0 when it can't be used on it now.</summary>
    public int ForceUpgradeLevel(Building building)
    {
        if (!Config.AllowForceUpgrade || building.IsStation || building.Removing || building.HasWorkOrder) return 0;
        if (building.Type == BuildingTypes.MineshaftEntrance) return 0;
        int next = building.Level + 1;
        if (next > BlueprintLibrary.MaxLevels) return 0;
        // A next level with a blueprint is built the normal way.
        if (next <= Blueprints.MaxLevel(building)) return 0;
        return next;
    }

    /// <summary>Raises a hut one level without building it. Returns a lang key on failure.</summary>
    public string? ForceUpgrade(Colony colony, Building building)
    {
        if (!Config.AllowForceUpgrade) return "createacolony:force-upgrade-off";
        if (building.HasWorkOrder) return "createacolony:build-already";
        if (building.Removing) return "createacolony:remove-already";
        int next = ForceUpgradeLevel(building);
        if (next == 0) return building.Level >= BlueprintLibrary.MaxLevels ? "createacolony:force-upgrade-top" : "createacolony:force-upgrade-has-blueprint";

        // The same as a finished build: the new level, the land it claims, the log line and the chat message.
        building.Upkeep = false;
        building.Repairing = false;
        building.TargetLevel = next;
        CompleteWorkOrder(colony, building);
        return null;
    }
}
