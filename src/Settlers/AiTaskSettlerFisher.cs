using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A fisher works from the Fisher's Hut (ColonyManager.Fishing.cs). It takes the game's fishing pole and bait from the
/// hut storage (couriers bring them), walks to the bank of water near the hut and casts, again and again. A spot with
/// no bites for a while is left for another. Fish go into its inventory and home to the hut storage when its hands are
/// full; couriers take them to the warehouse and the cooks.
/// </summary>
public class AiTaskSettlerFisher : AiTaskSettlerWorker
{
    ColonyManager.FishingSpot? spot;
    int misses;

    public AiTaskSettlerFisher(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Fisher;
    protected override string HutType => BuildingTypes.Fisher;
    protected override EnumTool Tool => EnumTool.Knife; // the pole has no tool type: see IsToolStack
    protected override ItemMatch Stock => ColonyManager.IsFishBait;
    protected override float WorkSeconds => manager!.Config.FisherSecondsPerCast;

    protected override bool IsToolStack(ItemStack? stack) => ColonyManager.IsFishingPole(stack);

    protected override string NeedsToolStatus => "fisher-needs-pole";

    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsFishBait(stack);

    protected override void RequestTool(Colony colony, Building hut)
    {
        var m = manager!;
        if (m.CodeInWarehouse(colony, s => ColonyManager.IsFishingPole(s)) is { } code) m.EnsureCraftRequest(colony, hut, code, 1);
    }

    protected override Vec3d WalkGoal() => spot != null ? spot.Stand.ToVec3d().Add(0.5, 0, 0.5) : base.WalkGoal();

    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        workRange = 5f;
        // Bait from the hut storage, while it has none.
        if (m.PackCount(citizen, ColonyManager.IsFishBait) == 0 && !m.PackFull(citizen)
            && storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsFishBait(s.Itemstack))) is { } chest)
        {
            SetStatus("fisher-fetching-bait");
            return FetchSupplies(chest.Pos, ColonyManager.IsFishBait, m.Config.FisherBaitCarried);
        }
        if (m.PackFull(citizen))
        {
            SetStatus("fisher-pack-full");
            return false;
        }
        var spots = m.FishingSpots(hut).Where(s => !IsUnreachable(s.Water)).ToList();
        if (spots.Count == 0)
        {
            spot = null;
            SetStatus("fisher-no-water");
            return false;
        }
        // Too many casts without a bite: try the next spot.
        if (spot == null || !spots.Contains(spot) || misses >= m.Config.FisherMissesBeforeMoving)
        {
            if (spot != null && misses >= m.Config.FisherMissesBeforeMoving) MarkUnreachable(spot.Water);
            misses = 0;
            spot = spots.FirstOrDefault(s => !IsUnreachable(s.Water)) ?? spots[0];
        }
        SetStatus("fisher-fishing");
        return GoWork(spot.Water);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        if (spot == null) return;
        entity.World.PlaySoundAt(new AssetLocation("game:sounds/environment/smallsplash"), spot.Water.X + 0.5, spot.Water.Y + 0.5, spot.Water.Z + 0.5, null, true, 16);
        var caught = m.CastLine(colony, hut, citizen, spot);
        if (caught == null)
        {
            misses++;
            return;
        }
        misses = 0;
        if (Hand is { } hand && HoldsTool) m.WearTool(entity, hand);
    }

    protected override string DescribeWork() => spot != null
        ? Lang.Get("createacolony:activity-fisher-fish")
        : Lang.Get("createacolony:activity-fisher-idle");
}
