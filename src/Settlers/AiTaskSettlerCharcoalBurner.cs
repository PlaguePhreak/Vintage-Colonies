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
/// A charcoal burner works from the Charcoal Burner's Hut (ColonyManager.Charcoal.cs). In order: it opens a pit that
/// has burnt out and takes the charcoal home; then, while the colony is short of charcoal, it fetches firewood and
/// builds and lights a new pit at a free spot. It digs with a shovel. Firewood comes from the hut storage, which
/// couriers fill from the warehouse (and the Forester's Hut, if it splits firewood).
/// </summary>
public class AiTaskSettlerCharcoalBurner : AiTaskSettlerWorker
{
    enum Work { None, Build, Open }

    Work work;
    CharcoalPit? pit;
    Vec3d? stand;

    public AiTaskSettlerCharcoalBurner(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.CharcoalBurner;
    protected override string HutType => BuildingTypes.CharcoalBurner;
    protected override EnumTool Tool => EnumTool.Shovel;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.CharcoalWorkSeconds;

    /// <summary>It keeps the firewood for its next pit.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsFirewood(stack);

    /// <summary>It stands beside the pit, never on it, since the pit gets dug out and covered.</summary>
    protected override Vec3d WalkGoal() => stand ?? base.WalkGoal();

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        work = Work.None;
        pit = null;
        workRange = 3f;
        var pits = m.CharcoalPitsOf(colony, hut);

        // A pit that has burnt out comes first, so the charcoal is not left lying in the ground.
        var done = pits.FirstOrDefault(p => m.CharcoalPitDone(p) && !IsUnreachable(p.Pos));
        if (done != null)
        {
            work = Work.Open;
            pit = done;
            SetStatus("charcoal-opening");
            return GoTo(done);
        }

        if (pits.Count == 0)
        {
            SetStatus("charcoal-no-spot");
            return false;
        }
        if (m.CharcoalStock(colony, hut, citizen) >= m.CharcoalWant(colony))
        {
            SetStatus("charcoal-stocked");
            return false;
        }
        var free = pits.FirstOrDefault(p => p.Stage == 0 && !IsUnreachable(p.Pos));
        if (free == null)
        {
            SetStatus("charcoal-burning");
            return false;
        }

        int need = m.FirewoodPerPit();
        int have = m.PackCount(citizen, ColonyManager.IsFirewood);
        if (have >= need)
        {
            work = Work.Build;
            pit = free;
            SetStatus("charcoal-building");
            return GoTo(free);
        }
        var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsFirewood(s.Itemstack)));
        if (chest != null && !m.PackFull(citizen))
        {
            SetStatus("charcoal-fetching-wood");
            return FetchSupplies(chest.Pos, ColonyManager.IsFirewood, need - have);
        }
        m.EnsureCraftRequest(colony, hut, "game:firewood", need - have);
        SetStatus("charcoal-needs-wood");
        return false;
    }

    /// <summary>Walks to the ground beside the pit, on the side with the most room.</summary>
    bool GoTo(CharcoalPit target)
    {
        var ba = entity.World.BlockAccessor;
        stand = null;
        foreach (var face in BlockFacing.HORIZONTALS)
        {
            var feet = target.Pos.AddCopy(face).Up();
            if (ba.GetBlock(feet).CollisionBoxes?.Length > 0 || ba.GetBlock(feet.UpCopy()).CollisionBoxes?.Length > 0) continue;
            stand = feet.ToVec3d().Add(0.5, 0, 0.5);
            break;
        }
        return GoWork(target.Pos);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        if (pit == null) return;
        if (Hand is { } hand && HoldsTool) m.WearTool(entity, hand);
        if (work == Work.Build)
        {
            if (!m.BuildCharcoalPit(colony, hut, citizen, pit)) MarkUnreachable(pit.Pos);
        }
        else if (work == Work.Open)
        {
            m.OpenCharcoalPit(colony, hut, citizen, pit);
        }
        work = Work.None;
    }

    protected override string DescribeWork() => work switch
    {
        Work.Build => Lang.Get("createacolony:activity-charcoalburner-build"),
        Work.Open => Lang.Get("createacolony:activity-charcoalburner-open"),
        _ => Lang.Get("createacolony:activity-charcoalburner-idle")
    };
}
