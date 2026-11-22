using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// An explorer works from an Explorer's Hut: it walks out over the land around the hut, one square at a time,
/// nearest first, looks around at each stop, and puts what it sees on the colony's map (ore lying on the
/// ground, clay, traders, animals, danger and landmarks). It needs no tool.
/// Field trips (0.180.0, ColonyManager.FieldTrips.cs) come first: with a parchment it goes to a landmark the
/// Archive offers, studies it, writes Field notes and carries them to the Archive.
/// </summary>
public class AiTaskSettlerExplorer : AiTaskSettlerWorker
{
    public AiTaskSettlerExplorer(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Explorer;
    protected override string HutType => BuildingTypes.Explorer;
    protected override EnumTool Tool => EnumTool.Knife;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.ExplorerSecondsPerLook;
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;

    enum Trip { None, Study, Deliver }
    Trip trip;
    int tripId;
    int archiveId;

    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsFieldNotes(stack) || ColonyManager.IsParchment(stack);

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        trip = Trip.None;
        var m = manager!;
        // Field notes go to the Archive first.
        if (m.PackCount(citizen, ColonyManager.IsFieldNotes) > 0 && m.NearestArchive(colony, hut.HutPos) is { } archive)
        {
            trip = Trip.Deliver;
            archiveId = archive.BuildingId;
            SetStatus("trip-delivering");
            workRange = 3f;
            return GoWork(archive.HutPos);
        }
        // Then a field trip, with a parchment to write on.
        if (m.FieldTripFor(colony, citizen) is { } next)
        {
            if (m.PackCount(citizen, ColonyManager.IsParchment) > 0)
            {
                trip = Trip.Study;
                tripId = next.TripId;
                SetStatus("trip-going");
                workRange = 4f;
                return GoWork(ColonyManager.TripPos(next));
            }
            foreach (var chest in storage)
            {
                foreach (var slot in chest.Inventory)
                {
                    if (!slot.Empty && ColonyManager.IsParchment(slot.Itemstack)) return FetchSupplies(chest.Pos, ColonyManager.IsParchment, 2);
                }
            }
            if (m.CodeInWarehouse(colony, ColonyManager.IsParchment) is { } code) m.EnsureCraftRequest(colony, hut, code, 2);
            SetStatus("trip-parchment");
            // Explores meanwhile.
        }
        var spot = manager!.NextExploreSpot(colony, hut, entity.Pos.XYZ, IsUnreachable);
        if (spot == null)
        {
            SetStatus("explored-all");
            return false;
        }
        SetStatus("exploring");
        workRange = 3f;
        return GoWork(spot);
    }

    /// <summary>The spot to stand on; a spot that was out of sight gets its real height once its land loads.</summary>
    protected override Vec3d WalkGoal()
    {
        if (trip == Trip.Deliver) return target.ToVec3d().Add(0.5, 0, 0.5);
        var ba = entity.World.BlockAccessor;
        if (ba.GetChunkAtBlockPos(target) != null) target.Y = ba.GetTerrainMapheightAt(target) + 1;
        return target.ToVec3d().Add(0.5, 0, 0.5);
    }

    /// <summary>Looking around, not working with the hands.</summary>
    protected override string WorkAnimation(ItemSlot? hand) => trip == Trip.Study ? "interactstatic" : "idle";

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (trip)
        {
            case Trip.Deliver:
                if (colony.Buildings.Find(b => b.BuildingId == archiveId) is { } archive) m.DeliverFieldNotes(colony, citizen, archive);
                return;
            case Trip.Study:
                if (colony.FieldTrips.Find(t => t.TripId == tripId) is { } onTrip) m.StudyFieldTrip(colony, citizen, onTrip);
                return;
        }
        WalkGoal(); // the height of a far spot is only known once there
        m.Explore(colony, hut, target);
    }

    protected override string DescribeWork()
    {
        if (trip == Trip.Deliver) return Lang.Get("createacolony:activity-explorer-deliver");
        if (trip == Trip.Study && Colony?.FieldTrips.Find(t => t.TripId == tripId) is { } onTrip)
            return Lang.Get("createacolony:activity-explorer-trip", ColonyManager.TripTitle(onTrip));
        return Lang.Get("createacolony:activity-explorer-look");
    }
}
