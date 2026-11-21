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
/// A herder works from one of the herder huts (ColonyManager.Herding.cs). In order: it skins its culls before
/// they rot, fills troughs that run low with feed the animals eat (from the hut storage, or asks couriers to
/// bring some from the warehouse), collects eggs, milks lactating animals into empty buckets, and culls spare
/// grown animals once the pen is over the herd size. Each herder hut has its own small task class below; they
/// differ only in the job and hut.
/// </summary>
public abstract class AiTaskSettlerHerder : AiTaskSettlerWorker
{
    enum Work { None, Feed, Eggs, Milk, Cull, Skin }

    const long LetGoMs = 300_000;

    Work work;
    long animalId;
    long chaseStartMs;
    readonly Dictionary<long, long> letGo = new();

    protected AiTaskSettlerHerder(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override EnumTool Tool => EnumTool.Knife;
    protected override ItemMatch Stock => _ => false;
    protected override bool WorkNeedsTool => work is Work.Cull or Work.Skin;

    List<EntityProperties> Kinds => manager!.HerdTypes(HutType, manager.HerdPatterns(HutType));

    /// <summary>Keeps its empty buckets and the feed it is carrying to a trough.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsEmptyBucket(stack) || IsFeed(stack);

    List<BlockEntityTrough> troughCache = new();
    long troughCacheUntil;

    bool IsFeed(ItemStack stack)
    {
        if (Colony is not { } colony || colony.Buildings.Find(b => b.BuildingId == hutId) is not { } hut) return false;
        long now = entity.World.ElapsedMilliseconds;
        if (now > troughCacheUntil)
        {
            troughCache = manager!.TroughsNear(hut.HutPos, manager.PenRadius(hut));
            troughCacheUntil = now + 10_000;
        }
        var kinds = Kinds;
        return troughCache.Any(t => manager!.FeedFits(t, stack, kinds));
    }

    protected override float WorkSeconds => work switch
    {
        Work.Skin => Hand is { } hand && entity.World.GetEntityById(animalId) is { } carcass ? ColonyManager.SkinSeconds(carcass, hand, entity) : 1f,
        Work.Cull => manager!.Config.HunterSecondsPerHit / (Colony is { } c && Citizen is { } me ? manager.SwingFactor(c, me) : 1f),
        Work.Milk => manager!.Config.HerderMilkSeconds,
        _ => manager!.Config.HerderSecondsPerTask
    };

    bool LetGo(long id)
    {
        if (!letGo.TryGetValue(id, out long until)) return false;
        if (entity.World.ElapsedMilliseconds < until) return true;
        letGo.Remove(id);
        return false;
    }

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var kinds = Kinds;
        work = Work.None;
        if (kinds.Count == 0)
        {
            SetStatus("herder-no-kinds");
            return false;
        }
        int r = m.PenRadius(hut);
        var herd = m.Herd(hut);
        var here = entity.Pos.XYZ;

        // Its culls first, before they rot.
        if (!m.HerderLeavesCulls(colony) && !m.PackFull(citizen) && m.HunterCarcass(colony, hut, here, LetGo) is { } carcass
            && carcass.Pos.HorDistanceTo(hut.HutPos.ToVec3d()) <= r + 8)
        {
            work = Work.Skin;
            animalId = carcass.EntityId;
            m.ClaimCarcass(carcass, entity);
            workRange = 2f;
            SetStatus("herder-skinning");
            return GoWork(carcass.Pos.AsBlockPos);
        }

        // Troughs running low.
        var troughs = m.TroughsNear(hut.HutPos, r).Where(t => !IsUnreachable(t.Pos)).ToList();
        foreach (var trough in troughs.Where(m.TroughWantsFeed).OrderBy(t => t.Pos.DistanceSqTo(here.X, here.Y, here.Z)))
        {
            if (m.PackItems(citizen).Any(s => m.FeedFits(trough, s, kinds)))
            {
                work = Work.Feed;
                workRange = 2.5f;
                SetStatus("herder-feeding");
                return GoWork(trough.Pos);
            }
            if (!m.PackFull(citizen) && m.FeedIn(storage, trough, kinds) is { } chest)
            {
                SetStatus("herder-fetching-feed");
                return FetchSupplies(chest.Pos, s => m.FeedFits(trough, s, kinds), m.Config.HerderFeedTake);
            }
            if (m.CodeInWarehouse(colony, s => m.FeedFits(trough, s, kinds)) is { } code) m.EnsureCraftRequest(colony, hut, code, m.Config.HerderFeedTake);
        }

        // Eggs: fertile ones stay to hatch until the flock is full.
        bool full = herd.Count >= m.HerdSize(hut);
        if (!m.PackFull(citizen))
        {
            var nest = m.NestsNear(hut.HutPos, r)
                .Where(n => !IsUnreachable(n.Pos) && ColonyManager.HasEggsToTake(n, full))
                .OrderBy(n => n.Pos.DistanceSqTo(here.X, here.Y, here.Z))
                .FirstOrDefault();
            if (nest != null)
            {
                work = Work.Eggs;
                workRange = 2.5f;
                SetStatus("herder-eggs");
                return GoWork(nest.Pos);
            }
        }

        // Milk, with an empty bucket: it fetches one from the hut storage if there is one.
        if (m.MilkReady(herd, here, LetGo) is { } milker)
        {
            if (m.PackCount(citizen, ColonyManager.IsEmptyBucket) > 0)
            {
                work = Work.Milk;
                animalId = milker.EntityId;
                chaseStartMs = entity.World.ElapsedMilliseconds;
                workRange = 2f;
                SetStatus("herder-milking");
                return GoWork(milker.Pos.AsBlockPos);
            }
            if (!m.PackFull(citizen) && storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsEmptyBucket(s.Itemstack))) is { } buckets)
            {
                SetStatus("herder-fetching-bucket");
                return FetchSupplies(buckets.Pos, ColonyManager.IsEmptyBucket, 2);
            }
        }

        // Too many animals: cull a spare grown one.
        if (!m.PackFull(citizen))
        {
            var cull = entity.World.GetEntityById(animalId) is { } current && current.Alive && herd.Contains(current) && !LetGo(animalId) && herd.Count > m.HerdSize(hut)
                ? current : m.CullPick(hut, herd.Where(e => !LetGo(e.EntityId)).ToList(), here);
            if (cull != null)
            {
                long now = entity.World.ElapsedMilliseconds;
                if (cull.EntityId != animalId) chaseStartMs = now;
                if (now - chaseStartMs > m.Config.HunterChaseSeconds * 1000)
                {
                    letGo[cull.EntityId] = now + LetGoMs;
                    animalId = 0;
                }
                else
                {
                    work = Work.Cull;
                    animalId = cull.EntityId;
                    workRange = 2.2f;
                    SetStatus("herder-culling");
                    return GoWork(cull.Pos.AsBlockPos);
                }
            }
        }

        animalId = 0;
        SetStatus(herd.Count == 0 ? "herder-no-animals" : troughs.Count == 0 ? "herder-no-trough" : "herder-tending");
        return false;
    }

    /// <summary>An animal moves: walk to where it is now.</summary>
    protected override Vec3d WalkGoal() => work is Work.Cull or Work.Milk or Work.Skin && entity.World.GetEntityById(animalId) is { } e
        ? e.Pos.XYZ : base.WalkGoal();

    protected override bool CloseEnough() => work is Work.Cull or Work.Milk or Work.Skin && entity.World.GetEntityById(animalId) is { } e
        ? entity.Pos.DistanceTo(e.Pos.XYZ) <= workRange + 0.5
        : base.CloseEnough();

    protected override string WorkAnimation(ItemSlot? hand) => work switch
    {
        Work.Skin => "knifecut",
        Work.Cull => base.WorkAnimation(hand),
        _ => SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic"
    };

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (work)
        {
            case Work.Feed:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is BlockEntityTrough trough) m.FillTrough(colony, citizen, trough, Kinds);
                break;
            case Work.Eggs:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is BlockEntityHenBox nest)
                {
                    int got = m.TakeEggs(colony, citizen, nest, m.Herd(hut).Count >= m.HerdSize(hut));
                    m.AddStat(hut, "eggs", got);
                }
                break;
            case Work.Milk:
                if (entity.World.GetEntityById(animalId) is { } animal && entity.Pos.DistanceTo(animal.Pos.XYZ) <= workRange + 1)
                {
                    if (!m.Milk(colony, citizen, entity, hut, animal)) letGo[animalId] = entity.World.ElapsedMilliseconds + LetGoMs;
                }
                animalId = 0;
                break;
            case Work.Skin:
                if (entity.World.GetEntityById(animalId) is not { } carcass || !ColonyManager.Skinnable(carcass) || Hand is not { } hand) return;
                bool done = carcass.GetBehavior<EntityBehaviorHarvestable>()?.IsHarvested == true;
                if (!done && !ColonyManager.IsKnife(hand.Itemstack)) return;
                m.Skin(colony, citizen, entity, carcass, hand);
                SyncHand();
                break;
            case Work.Cull:
                if (entity.World.GetEntityById(animalId) is not { } prey || !prey.Alive || entity.Pos.DistanceTo(prey.Pos.XYZ) > workRange + 1) return;
                entity.Pos.Yaw = (float)System.Math.Atan2(prey.Pos.X - entity.Pos.X, prey.Pos.Z - entity.Pos.Z);
                if (m.HerderStrike(colony, citizen, hut, entity, prey)) chaseStartMs = entity.World.ElapsedMilliseconds;
                SyncHand();
                break;
        }
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-herder-" + (work == Work.None ? "tend" : work.ToString().ToLowerInvariant()));
}

public class AiTaskSettlerChickenHerder : AiTaskSettlerHerder
{
    public AiTaskSettlerChickenHerder(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }
    protected override string Job => Jobs.ChickenHerder;
    protected override string HutType => BuildingTypes.ChickenCoop;
}

public class AiTaskSettlerSwineherd : AiTaskSettlerHerder
{
    public AiTaskSettlerSwineherd(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }
    protected override string Job => Jobs.Swineherd;
    protected override string HutType => BuildingTypes.Piggery;
}

public class AiTaskSettlerShepherd : AiTaskSettlerHerder
{
    public AiTaskSettlerShepherd(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }
    protected override string Job => Jobs.Shepherd;
    protected override string HutType => BuildingTypes.Shepherd;
}

public class AiTaskSettlerGoatherd : AiTaskSettlerHerder
{
    public AiTaskSettlerGoatherd(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }
    protected override string Job => Jobs.Goatherd;
    protected override string HutType => BuildingTypes.Goatherd;
}

public class AiTaskSettlerHerdsman : AiTaskSettlerHerder
{
    public AiTaskSettlerHerdsman(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }
    protected override string Job => Jobs.Herdsman;
    protected override string HutType => BuildingTypes.Herdsman;
}
