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
/// An undertaker works from the Graveyard (ColonyManager.Graveyard.cs). In order: it puts the belongings of a body it
/// carries into the Graveyard's storage, digs a grave at a free grave plot and buries the body there, picks up a
/// body someone left in the Graveyard's storage, and walks out (up to its Work area range) to the bodies of the
/// colony's dead to wrap them and carry them home.
/// </summary>
public class AiTaskSettlerUndertaker : AiTaskSettlerWorker
{
    enum Work { None, Lift, Unpack, Dig, Bury }

    Work work;
    long corpseId;

    public AiTaskSettlerUndertaker(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Undertaker;
    protected override string HutType => BuildingTypes.Graveyard;
    protected override EnumTool Tool => EnumTool.Shovel;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.UndertakerSecondsPerTask;
    protected override bool WorkNeedsTool => work == Work.Dig;

    /// <summary>Keeps the bodies it carries until they are buried.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsShroudedBody(stack);

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        work = Work.None;
        workRange = 2.5f;

        if (m.CarriesBody(citizen))
        {
            // Home with a body: its belongings go into the Graveyard's storage first.
            if (m.CarriesBelongings(citizen))
            {
                work = Work.Unpack;
                SetStatus("undertaker-unpacking");
                return GoWork(storage[0].Pos);
            }
            var plot = m.PlotFor(colony, hut, citizen, m.CarriedDead(citizen));
            if (plot == null)
            {
                SetStatus("undertaker-no-plot");
                return false;
            }
            if (IsUnreachable(plot))
            {
                SetStatus("undertaker-plot-unreachable");
                return false;
            }
            work = m.IsOpenGrave(hut, plot) || !m.CanDig(plot) ? Work.Bury : Work.Dig;
            SetStatus(work == Work.Dig ? "undertaker-digging" : "undertaker-burying");
            return GoWork(plot);
        }

        // A body someone brought to the Graveyard and left in its storage.
        if (!m.PackFull(citizen) && storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsShroudedBody(s.Itemstack))) is { } chest)
        {
            SetStatus("undertaker-fetching");
            return FetchSupplies(chest.Pos, ColonyManager.IsShroudedBody, 1);
        }

        // The dead: only fetched while there is a free plot to bury them in.
        var bodies = m.BodiesToFetch(colony, hut, citizen);
        if (bodies.Count == 0)
        {
            SetStatus("undertaker-waiting");
            return false;
        }
        if (m.FreePlotCount(hut, citizen) == 0)
        {
            SetStatus("undertaker-no-plot");
            return false;
        }
        if (m.PackFull(citizen))
        {
            SetStatus("undertaker-pack-full");
            return false;
        }
        foreach (var body in bodies)
        {
            var pos = body.ServerPos.AsBlockPos;
            if (IsUnreachable(pos)) continue;
            m.ClaimBody(body, citizen);
            corpseId = body.EntityId;
            work = Work.Lift;
            SetStatus("undertaker-collecting");
            return GoWork(pos);
        }
        SetStatus("undertaker-unreachable");
        return false;
    }

    protected override string WorkAnimation(ItemSlot? hand) => work == Work.Dig
        ? hand?.Itemstack?.Collectible.GetHeldTpHitAnimation(hand, entity) ?? "breakhand"
        : SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic";

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (work)
        {
            case Work.Lift:
                if (!m.LiftBody(colony, citizen, corpseId)) MarkUnreachable(target);
                break;
            case Work.Unpack:
                m.UnpackBelongings(colony, hut, citizen);
                break;
            case Work.Dig:
                m.DigGrave(colony, hut, target);
                break;
            case Work.Bury:
                if (!m.Bury(colony, hut, citizen, target)) MarkUnreachable(target);
                break;
        }
        work = Work.None;
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-undertaker-" + (work == Work.None ? "idle" : work.ToString().ToLowerInvariant()));
}
