using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A hunter works from a Hunter's Hut (ColonyManager.Hunting.cs). It chases animals around the hut and strikes
/// them with its spear or other weapon until they fall, then skins them with its knife and takes the meat, hide
/// and the rest to the hut. An animal it cannot catch in time it lets go for a while. Without a knife it leaves
/// its kills for settlers without a job to skin. Animals eating the colony's crops come first, wherever they are
/// and whatever the Allowed to collect tab says (ColonyManager.CropPests.cs): the farmer calls the hunter for them.
/// While the colony has a butcher, it skins nothing: it picks up its kills whole and takes them to the Butcher's
/// Hut (ColonyManager.Butchery.cs), a few at a time.
/// </summary>
public class AiTaskSettlerHunter : AiTaskSettlerWorker
{
    const long LetGoMs = 300_000;

    long preyId;
    bool skinning;
    /// <summary>Picking up a kill whole for the butcher, instead of skinning it.</summary>
    bool pickingUp;
    /// <summary>Taking carcasses to this Butcher's Hut.</summary>
    int deliverTo;
    long chaseStartMs;
    /// <summary>Far trip (0.205.0): until then the hunter looks further out; travelling = walking to a far spot.</summary>
    long farUntilMs;
    bool travelling;
    readonly Dictionary<long, long> letGo = new();

    public AiTaskSettlerHunter(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Hunter;
    protected override string HutType => BuildingTypes.Hunter;
    protected override EnumTool Tool => EnumTool.Spear;
    protected override ItemMatch Stock => _ => false;
    protected override bool IsToolStack(ItemStack? stack) => stack != null && ColonyManager.IsWeapon(stack);
    // Carcasses stay in its pack while there is a butcher to take them to; otherwise they go in the hut storage.
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsKnife(stack)
        || ColonyManager.IsCarcass(stack) && Colony is { } c && manager!.ButcherDropOff(c, entity.Pos.XYZ) != null;
    protected override bool WantsInHand(ItemStack? stack) => skinning && !pickingUp ? ColonyManager.IsKnife(stack) : IsToolStack(stack);
    protected override bool WorkNeedsTool => deliverTo == 0;

    protected override float WorkSeconds => travelling || deliverTo != 0 ? 0.5f : pickingUp ? manager!.Config.CarcassPickUpSeconds : skinning
        ? (Hand is { } hand && entity.World.GetEntityById(preyId) is { } carcass ? ColonyManager.SkinSeconds(carcass, hand, entity) : 1f)
        : manager!.Config.HunterSecondsPerHit / (Colony is { } c && Citizen is { } me ? manager.SwingFactor(c, me) : 1f);

    bool LetGo(long id)
    {
        if (!letGo.TryGetValue(id, out long until)) return false;
        if (entity.World.ElapsedMilliseconds < until) return true;
        letGo.Remove(id);
        return false;
    }

    bool HasKnife(CitizenRecord citizen) => ColonyManager.IsKnife(Hand?.Itemstack) || manager!.PackCount(citizen, s => ColonyManager.IsKnife(s)) > 0;

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        long now = entity.World.ElapsedMilliseconds;
        pickingUp = false;
        deliverTo = 0;
        travelling = false;
        int roam = now < farUntilMs ? m.FarTripRadius(hut) : 0;
        bool knife = HasKnife(citizen);
        bool forButcher = m.Config.HunterBringsCarcasses && m.ButcherWorks(colony);
        int carried = m.PackCount(citizen, ColonyManager.IsCarcass);
        // Carrying a full load of carcasses, or nothing else of its own to pick up: off to the butcher.
        if (carried > 0 && forButcher
            && (carried >= System.Math.Max(1, m.Config.CarcassLoad) || m.PackFull(citizen) || m.HunterCarcass(colony, hut, entity.Pos.XYZ, LetGo) is not { } next || !m.ForTheButcher(colony, next)))
        {
            skinning = false;
            workRange = 2f;
            // A Butcher's Hut it cannot get to: into its own hut's storage, where the butcher fetches them.
            var drop = m.ButcherDropOff(colony, entity.Pos.XYZ);
            bool reachable = drop != null && !IsUnreachable(drop.Value.Chest.Pos);
            deliverTo = reachable ? drop!.Value.Hut.BuildingId : hut.BuildingId;
            SetStatus("hunter-to-butcher");
            return GoWork(reachable ? drop!.Value.Chest.Pos : storage[0].Pos);
        }
        if (!knife && !forButcher && storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsKnife(s.Itemstack))) is { } knifeChest)
        {
            return FetchSupplies(knifeChest.Pos, s => ColonyManager.IsKnife(s), 1);
        }
        // An animal eating the crops first: the farmer called for it.
        bool onPest = preyId != 0 && entity.World.GetEntityById(preyId) is { } current && m.IsCropPest(colony, current) && !LetGo(preyId);
        if (!onPest && m.CropPestForHunter(colony, entity.Pos.XYZ, LetGo) is { } pest)
        {
            preyId = pest.EntityId;
            chaseStartMs = now;
            onPest = true;
        }
        // Its own kills next, before they rot: whole for the butcher, else skinned.
        if (!onPest && !m.PackFull(citizen) && m.HunterCarcass(colony, hut, entity.Pos.XYZ, LetGo) is { } carcass
            && (knife || forButcher && m.ForTheButcher(colony, carcass)))
        {
            skinning = true;
            pickingUp = forButcher && m.ForTheButcher(colony, carcass);
            preyId = carcass.EntityId;
            m.ClaimCarcass(carcass, entity);
            workRange = 2f;
            SetStatus(pickingUp ? "hunter-picking-up" : "hunter-skinning");
            return GoWork(carcass.Pos.AsBlockPos);
        }
        if (!onPest && m.PackFull(citizen)) return false;

        skinning = false;
        var prey = entity.World.GetEntityById(preyId);
        if (prey == null || !(m.IsPrey(colony, hut, prey) || m.IsCropPest(colony, prey)) || LetGo(preyId))
        {
            prey = m.FindPrey(colony, hut, entity.Pos.XYZ, LetGo, roam);
            if (prey == null && roam == 0 && m.Config.HunterFarTrips && m.PredatorLeftToGuards(hut, entity.Pos.XYZ) == null)
            {
                // Nothing left near the hut: go further out for a while.
                farUntilMs = now + (long)(m.Config.FarTripMinutes * 60_000);
                roam = m.FarTripRadius(hut);
                prey = m.FindPrey(colony, hut, entity.Pos.XYZ, LetGo, roam);
            }
            if (prey == null && roam > 0 && m.FarTripSpot(hut) is { } spot)
            {
                preyId = 0;
                travelling = true;
                workRange = 4f;
                SetStatus("hunter-far-trip");
                return GoWork(spot);
            }
            if (prey == null)
            {
                preyId = 0;
                // Bears, wolves and hyenas are the guards' (0.202.0): say so instead of "no animals".
                if (m.PredatorLeftToGuards(hut, entity.Pos.XYZ) is { } predator)
                {
                    SetStatus("hunter-leaving-predator");
                    m.SetNote(citizen, Lang.Get("createacolony:note-hunter-leaves", predator.GetName().ToLowerInvariant()));
                }
                else SetStatus("hunter-nothing");
                return false;
            }
            preyId = prey.EntityId;
            chaseStartMs = now;
        }
        if (now - chaseStartMs > m.Config.HunterChaseSeconds * 1000)
        {
            // It got away: leave it be for a while.
            letGo[preyId] = now + LetGoMs;
            preyId = 0;
            SetStatus("hunter-lost");
            return false;
        }
        workRange = 2.2f;
        SetStatus(onPest ? "hunter-pest" : "hunting");
        return GoWork(prey.Pos.AsBlockPos);
    }

    /// <summary>The animal moves: walk to where it is now.</summary>
    protected override Vec3d WalkGoal() => deliverTo == 0 && !travelling && entity.World.GetEntityById(preyId)?.Pos.XYZ is { } at ? at : target.ToVec3d().Add(0.5, 0, 0.5);

    protected override bool CloseEnough() => deliverTo == 0 && !travelling && entity.World.GetEntityById(preyId) is { } e
        ? entity.Pos.DistanceTo(e.Pos.XYZ) <= workRange + 0.5
        : base.CloseEnough();

    protected override string WorkAnimation(ItemSlot? hand) => travelling || deliverTo != 0 || pickingUp ? "interactstatic" : skinning ? "knifecut" : base.WorkAnimation(hand);

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        if (travelling)
        {
            // Got there: the next plan looks for animals round here.
            travelling = false;
            return;
        }
        if (deliverTo != 0)
        {
            if (colony.Buildings.Find(b => b.BuildingId == deliverTo) is { } butcherHut) m.DeliverCarcasses(colony, citizen, butcherHut);
            deliverTo = 0;
            return;
        }
        var e = entity.World.GetEntityById(preyId);
        if (e == null) return;
        if (pickingUp)
        {
            if (m.PickUpCarcass(colony, entity, e) is { } whole && m.AddToPack(colony, citizen, whole, false) == 0)
            {
                entity.World.SpawnItemEntity(whole, entity.Pos.XYZ.Add(0, 0.5, 0));
            }
            m.AddStat(hut, "carried:" + e.Code.Path);
            return;
        }
        if (skinning)
        {
            if (!ColonyManager.Skinnable(e) || Hand is not { } hand) return;
            bool done = e.GetBehavior<EntityBehaviorHarvestable>()?.IsHarvested == true;
            if (!done && !ColonyManager.IsKnife(hand.Itemstack)) return;
            m.Skin(colony, citizen, entity, e, hand);
            SyncHand();
            return;
        }
        // Too far: it ran off while the hunter swung. Plan walks after it again.
        if (!e.Alive || entity.Pos.DistanceTo(e.Pos.XYZ) > workRange + 1) return;
        entity.Pos.Yaw = (float)System.Math.Atan2(e.Pos.X - entity.Pos.X, e.Pos.Z - entity.Pos.Z);
        if (m.HunterStrike(colony, citizen, hut, entity, e)) chaseStartMs = entity.World.ElapsedMilliseconds;
        SyncHand();
    }

    protected override string DescribeWork() => Lang.Get(travelling ? "createacolony:activity-hunter-far" : deliverTo != 0 ? "createacolony:activity-hunter-to-butcher" : pickingUp ? "createacolony:activity-hunter-pick-up"
        : skinning ? "createacolony:activity-hunter-skin" : "createacolony:activity-hunter-hunt");
}
