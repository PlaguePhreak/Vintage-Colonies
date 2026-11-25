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
/// What a settler without a job does for its household: it looks after its home, the building its bed is in
/// (ColonyManager.Zones.cs). For now that is the farmland inside the home: harvest ripe crops and sow again,
/// pull weeds, water dry crops, sow bare farmland and till soil where the blueprint has farmland, most urgent
/// first, like a farmer. It also skins dead animals on the colony's land with a knife and cooks raw food from
/// home on a fire pit in or by the home (ColonyManager.HomeCooking.cs). Seeds, a hoe and a watering can come from a chest in the home, else the warehouse;
/// when the plot needs nothing more, what the settler took and harvested goes into a chest in the home (with
/// no chest, it takes it to the warehouse like anything else it picks up). With nothing to do at home it helps
/// with hauling (AiTaskSettlerCourier) or idles.
///
/// New duties go in Plan, ahead of or after the plot work, each with its own Step for its work.
/// </summary>
public class AiTaskSettlerChores : SettlerTaskBase
{
    const long IdleRetryMs = 15_000;
    const long UnreachableForgetMs = 300_000;
    const float PlotReach = 2f;
    const float WaterReach = 4.5f;

    enum Step { Fetch, Work, Store, Skin, Cook, Collect, Feed, Eggs }

    Step step;
    FieldWork work;
    BlockPos target = new(0);
    int homeId;
    long nextSearchMs;
    bool walking;
    float workTime;
    float stepSeconds;
    long carcassId;
    string? workAnim;
    ItemMatch fetchMatch = _ => false;
    int fetchWant;
    readonly Dictionary<BlockPos, long> unreachable = new();
    /// <summary>Codes of what the settler harvested or took for the plot, so it puts those away and nothing else (not its lunch).</summary>
    readonly HashSet<string> gathered = new();

    public AiTaskSettlerChores(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    static bool IsHoe(ItemStack? s) => s?.Collectible.Tool == EnumTool.Hoe;

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextSearchMs) return false;
        nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
        return Plan();
    }

    bool Plan()
    {
        if (Colony is not { } colony || Citizen is not { } citizen || !ColonyManager.IsJobless(citizen)) return false;
        if (!manager!.Config.JoblessTendHome || manager.IsBrokenDown(citizen)) return false;
        // Halfway through a delivery it is helping with: that comes first.
        if (colony.Requests.Exists(r => r.LeasedByCitizenId == citizen.CitizenId)) return false;
        if (manager.HomeOf(colony, citizen) is not { } home)
        {
            // No home yet (0.202.0): a dead animal on the colony's land is still skinned, and what comes off it goes to the Warehouse.
            homeId = 0;
            return PlanSkin(colony, citizen, null);
        }
        homeId = home.BuildingId;
        // Hands full: put the harvest away first.
        if (manager.PackFull(citizen) && PlanStore(colony, citizen, home)) return true;
        // A carcass rots, so it comes before the plot; the meat is cooked once the plot needs nothing.
        return PlanSkin(colony, citizen, home) || PlanPlot(colony, citizen, home) || PlanHens(colony, citizen, home) || PlanCook(colony, citizen, home) || PlanStore(colony, citizen, home);
    }

    /// <summary>A dead animal on the colony's land to skin, or a knife to fetch for it first.</summary>
    bool PlanSkin(Colony colony, CitizenRecord citizen, Building? home)
    {
        var m = manager!;
        if (!m.Config.JoblessSkinAnimals || m.CarcassToSkin(colony, entity, IsUnreachable) is not { } carcass) return false;
        bool skinned = carcass.GetBehavior<EntityBehaviorHarvestable>()?.IsHarvested == true;
        bool hasKnife = ColonyManager.IsKnife(Hand?.Itemstack) || m.PackCount(citizen, s => ColonyManager.IsKnife(s)) > 0;
        if (!skinned && !hasKnife)
        {
            // A knife from home, else from the Town Hall's storage or the Warehouse (0.202.0), else the colony's
            // starting knife. With none anywhere the settler asks for one and says so.
            var knife = (home == null ? null : m.HomeSupply(colony, home, s => ColonyManager.IsKnife(s))) ?? m.SpareToolChest(colony, s => ColonyManager.IsKnife(s));
            if (knife != null && !IsUnreachable(knife.Pos)) return Fetch(knife.Pos, s => ColonyManager.IsKnife(s), 1);
            if (!m.GiveStarterTool(colony, citizen, EnumTool.Knife, 0))
            {
                m.EnsureSkinningKnifeRequest(colony);
                m.SetNote(citizen, Lang.Get("createacolony:note-needs-skinning-knife"));
                return false;
            }
        }
        m.ClearNote(citizen);
        m.ClaimCarcass(carcass, entity);
        carcassId = carcass.EntityId;
        step = Step.Skin;
        target = carcass.Pos.AsBlockPos;
        return true;
    }

    /// <summary>
    /// The household's hens (ColonyManager.Herding.cs): fills their trough from the home chests or the warehouse,
    /// and collects the eggs. Fertile eggs stay to hatch until the home has config JoblessHensMax chickens.
    /// </summary>
    bool PlanHens(Colony colony, CitizenRecord citizen, Building home)
    {
        var m = manager!;
        if (!m.Config.JoblessKeepHens) return false;
        var hens = m.HomeHens(home);
        if (hens.Count == 0) return false;
        var kinds = m.HenKinds();
        foreach (var trough in m.HomeTroughs(home).Where(t => !IsUnreachable(t.Pos) && m.TroughWantsFeed(t)))
        {
            if (m.PackItems(citizen).Any(s => m.FeedFits(trough, s, kinds))) return At(Step.Feed, trough.Pos);
            if (!m.PackFull(citizen) && m.HomeSupply(colony, home, s => m.FeedFits(trough, s, kinds)) is { } chest)
            {
                return Fetch(chest.Pos, s => m.FeedFits(trough, s, kinds), m.Config.JoblessSeedTake * 2);
            }
        }
        if (m.PackFull(citizen)) return false;
        bool enough = hens.Count >= m.Config.JoblessHensMax;
        var nest = m.HomeNests(home).FirstOrDefault(n => !IsUnreachable(n.Pos) && ColonyManager.HasEggsToTake(n, enough));
        return nest != null && At(Step.Eggs, nest.Pos);
    }

    /// <summary>Cooks the raw food it brought home on the home's fire pit, or takes cooked food off it.</summary>
    bool PlanCook(Colony colony, CitizenRecord citizen, Building home)
    {
        var m = manager!;
        if (!m.Config.JoblessCookAtHome || m.HomeFirepit(colony, home) is not { } pit || IsUnreachable(pit.Pos)) return false;
        if (ColonyManager.FireHasFood(pit)) return At(Step.Collect, pit.Pos);
        // Something is cooking already, or there is nothing to burn.
        if (!pit.inputSlot.Empty || !m.CanFuel(colony, home, citizen, pit)) return false;
        if (m.PackCount(citizen, RawFood) == 0)
        {
            // The raw food that will spoil soonest goes on the fire first.
            var store = m.SoonestToSpoil(m.HomeChests(home), m.IsGrillable);
            return store != null && Fetch(store.Pos, m.IsGrillable, m.Config.CookBatch);
        }
        if (!pit.IsBurning && pit.fuelSlot.Empty && m.PackCount(citizen, ColonyManager.IsFirewood) == 0
            && m.HomeSupply(colony, home, ColonyManager.IsFirewood) is { } wood)
        {
            return Fetch(wood.Pos, ColonyManager.IsFirewood, m.Config.CookFirewood);
        }
        return At(Step.Cook, pit.Pos);
    }

    /// <summary>Raw food the settler brought home or skinned, not food it carries for someone else.</summary>
    bool RawFood(ItemStack s) => Gathered(s) && manager!.IsGrillable(s);

    bool At(Step next, BlockPos pos)
    {
        step = next;
        target = pos.Copy();
        return true;
    }

    /// <summary>Picks the most urgent work on the home's crop plots, or what to fetch for it first.</summary>
    bool PlanPlot(Colony colony, CitizenRecord citizen, Building home)
    {
        var m = manager!;
        var plots = m.HomePlots(colony, home);
        if (plots.Count == 0) return false;

        // Only seeds that would live until ripe here, and none in winter (ColonyManager.Seasons.cs).
        ItemMatch sowable = m.SowableNow(plots[0], m.IsSowable);
        bool hasSeed = m.PackCount(citizen, sowable) > 0;
        bool hasHoe = IsHoe(Hand?.Itemstack) || m.PackCount(citizen, s => IsHoe(s)) > 0;
        var (hasCan, water) = m.WaterCarried(citizen);
        var here = entity.Pos.XYZ;
        (FieldWork Work, BlockPos Pos)? best = null;
        bool wantSeed = false, wantHoe = false, wantCan = false, thirsty = false;

        foreach (var ground in plots)
        {
            if (IsUnreachable(ground)) continue;
            var w = m.WorkAt(colony, ground);
            switch (w)
            {
                case FieldWork.None:
                    continue;
                case FieldWork.Sow when !hasSeed:
                    wantSeed = true;
                    continue;
                case FieldWork.Till when !hasHoe:
                    wantHoe = true;
                    continue;
                case FieldWork.Water when !hasCan:
                    wantCan = true;
                    continue;
                case FieldWork.Water when water <= 0:
                    thirsty = true;
                    continue;
            }
            if (best is { } b && (b.Work < w || (b.Work == w && b.Pos.DistanceSqTo(here.X, here.Y, here.Z) <= ground.DistanceSqTo(here.X, here.Y, here.Z)))) continue;
            best = (w, ground);
        }

        // Fetch what the plot needs before the work that does not need it is done, so it is one trip.
        if (best == null || best.Value.Work > FieldWork.Sow)
        {
            if (wantSeed && m.HomeSupply(colony, home, sowable) is { } seeds) return Fetch(seeds.Pos, sowable, m.Config.JoblessSeedTake);
            if (wantCan && m.HomeSupply(colony, home, ColonyManager.IsWateringCan) is { } can) return Fetch(can.Pos, ColonyManager.IsWateringCan, 1);
            if (wantHoe && m.HomeSupply(colony, home, s => IsHoe(s)) is { } hoe) return Fetch(hoe.Pos, s => IsHoe(s), 1);
        }
        if (thirsty && (best == null || best.Value.Work > FieldWork.Water) && m.FindWater(colony, home) is { } waterPos && !IsUnreachable(waterPos))
        {
            best = (FieldWork.Refill, waterPos);
        }
        if (best == null) return false;
        work = best.Value.Work;
        step = Step.Work;
        target = best.Value.Pos.Copy();
        return true;
    }

    /// <summary>The plot needs nothing more: put what was taken and harvested for it into a chest in the home.</summary>
    bool PlanStore(Colony colony, CitizenRecord citizen, Building home)
    {
        if (gathered.Count == 0 || !manager!.PackItems(citizen).Any(Gathered)) return false;
        var chest = manager.HomeChests(home).FirstOrDefault();
        if (chest == null)
        {
            // No chest at home: it goes to the warehouse with whatever else the settler carries.
            gathered.Clear();
            return false;
        }
        step = Step.Store;
        target = chest.Pos.Copy();
        return true;
    }

    bool Gathered(ItemStack s) => gathered.Contains(s.Collectible.Code.ToString());

    bool Fetch(BlockPos chest, ItemMatch match, int want)
    {
        step = Step.Fetch;
        target = chest.Copy();
        fetchMatch = match;
        fetchWant = want;
        return true;
    }

    bool IsUnreachable(BlockPos pos)
    {
        if (!unreachable.TryGetValue(pos, out long until)) return false;
        if (entity.World.ElapsedMilliseconds < until) return true;
        unreachable.Remove(pos);
        return false;
    }

    float Reach => step switch
    {
        Step.Work => work == FieldWork.Refill ? WaterReach : PlotReach,
        Step.Skin or Step.Cook or Step.Collect or Step.Feed or Step.Eggs => PlotReach,
        _ => 1.5f
    };

    public override void StartExecute()
    {
        base.StartExecute();
        BeginStep();
    }

    void BeginStep()
    {
        workTime = 0;
        walking = entity.Pos.XYZ.DistanceTo(target.ToVec3d().Add(0.5, 0.5, 0.5)) > Reach + 0.5;
        if (walking) WalkTo(target.ToVec3d().Add(0.5, 0, 0.5), Reach);
    }

    public override bool ContinueExecute(float dt)
    {
        if (walking)
        {
            if (PathFailed)
            {
                unreachable[target.Copy()] = entity.World.ElapsedMilliseconds + UnreachableForgetMs;
                return false;
            }
            if (!Arrived)
            {
                KeepWalking();
                return true;
            }
            StopWalking();
            walking = false;
        }

        if (Colony is not { } colony || Citizen is not { } citizen) return false;
        var home = colony.Buildings.Find(b => b.BuildingId == homeId);
        // Without a home only skinning goes on, and fetching the knife for it.
        if (home == null && step is not (Step.Skin or Step.Fetch)) return false;

        switch (step)
        {
            case Step.Fetch:
                if (!Take(colony, citizen))
                {
                    // Nothing there after all, or no room to carry it: try again later.
                    nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
                    return false;
                }
                break;
            case Step.Store:
                manager!.UnloadPack(colony, citizen, manager.HomeChests(home!), Gathered);
                gathered.Clear();
                break;
            case Step.Work:
            case Step.Skin:
            case Step.Cook:
            case Step.Collect:
            case Step.Feed:
            case Step.Eggs:
                if (workAnim == null)
                {
                    PrepareHand(colony, citizen);
                    stepSeconds = StepSeconds() * manager!.WorkTimeFactor(colony, citizen);
                    workAnim = PlayShapeAnimation(WorkAnimation(), "build");
                }
                workTime += dt;
                if (workTime < stepSeconds) return true;
                if (step == Step.Work) DoWork(colony, citizen, home!);
                else DoHomeWork(colony, citizen);
                StopWorkAnimation();
                break;
        }

        // Straight on to the next chore; only walk if it is out of reach.
        if (!Plan()) return false;
        BeginStep();
        return true;
    }

    /// <summary>Takes up to fetchWant matching items from the chest at the target. False if nothing moved.</summary>
    bool Take(Colony colony, CitizenRecord citizen)
    {
        if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityContainer chest) return false;
        int moved = 0;
        while (moved < fetchWant)
        {
            var part = manager!.TakeItems(new[] { chest }, fetchMatch, fetchWant - moved);
            if (part == null) break;
            gathered.Add(part.Collectible.Code.ToString());
            moved += manager.AddToPack(colony, citizen, part);
            if (part.StackSize > 0)
            {
                manager.PutItems(chest, part);
                break;
            }
        }
        return moved > 0;
    }

    float StepSeconds() => step switch
    {
        Step.Skin => Hand is { } hand && entity.World.GetEntityById(carcassId) is { } carcass ? ColonyManager.SkinSeconds(carcass, hand, entity) : 1f,
        Step.Cook or Step.Collect => manager!.Config.CookSecondsPerStep,
        _ => manager!.Config.FarmerSecondsPerBlock
    };

    /// <summary>Skins the carcass, puts raw food on the fire, or takes cooked food off it; what it gets goes home later.</summary>
    void DoHomeWork(Colony colony, CitizenRecord citizen)
    {
        var m = manager!;
        switch (step)
        {
            case Step.Skin:
                if (entity.World.GetEntityById(carcassId) is not { } carcass || !ColonyManager.Skinnable(carcass) || Hand is not { } hand) return;
                bool skinned = carcass.GetBehavior<EntityBehaviorHarvestable>()?.IsHarvested == true;
                if (!skinned && !ColonyManager.IsKnife(hand.Itemstack)) return;
                // Meat, hide and the rest go to the Warehouse when the colony has one (0.202.0); else home, as before.
                var skinnedOff = m.Skin(colony, citizen, entity, carcass, hand);
                if (colony.WarehousePos == null && homeId != 0) foreach (var code in skinnedOff) gathered.Add(code);
                SyncHand(); // a knife that just broke must vanish from the model too
                m.GainWorkXp(colony, citizen, Jobs.Hunter);
                break;
            case Step.Cook:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityFirepit pit || !pit.inputSlot.Empty) return;
                var raw = m.PackItems(citizen).FirstOrDefault(RawFood);
                if (raw == null || m.KitchenKindOf(raw) is not { } kind) return;
                m.LoadFirepit(colony, citizen, pit, kind);
                m.GainWorkXp(colony, citizen, Jobs.Cook);
                break;
            case Step.Feed:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is BlockEntityTrough trough) m.FillTrough(colony, citizen, trough, m.HenKinds());
                break;
            case Step.Eggs:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityHenBox nest) return;
                if (colony.Buildings.Find(b => b.BuildingId == homeId) is not { } henHome) return;
                m.TakeEggs(colony, citizen, nest, m.HomeHens(henHome).Count >= m.Config.JoblessHensMax);
                // The eggs are the household's: they go into the home chest later.
                foreach (var egg in m.PackItems(citizen).Where(s => s.Collectible.Code.Path.StartsWith("egg-"))) gathered.Add(egg.Collectible.Code.ToString());
                m.GainWorkXp(colony, citizen, Jobs.Herdsman);
                break;
            case Step.Collect:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityFirepit fire || !ColonyManager.FireHasFood(fire)) return;
                gathered.Add(fire.outputSlot.Itemstack.Collectible.Code.ToString());
                m.EmptyFirepit(colony, citizen, fire);
                break;
        }
    }

    void DoWork(Colony colony, CitizenRecord citizen, Building home)
    {
        var m = manager!;
        if (work == FieldWork.Refill)
        {
            m.RefillCans(colony, citizen, target);
            return;
        }
        if (m.WorkAt(colony, target) != work) return; // someone got here first, or it changed
        var before = m.PackItems(citizen).Select(s => s.Collectible.Code.ToString()).ToHashSet();
        switch (work)
        {
            case FieldWork.Harvest:
                string? crop = m.HarvestAtHome(colony, home, citizen, target);
                m.Sow(colony, citizen, entity, target, ColonyManager.AnyCropField, crop);
                break;
            case FieldWork.Weed:
                m.WeedAtHome(colony, home, citizen, target);
                break;
            case FieldWork.Water:
                m.Water(colony, citizen, target);
                break;
            case FieldWork.Sow:
                m.Sow(colony, citizen, entity, target, ColonyManager.AnyCropField, null);
                break;
            case FieldWork.Till:
                var hoe = Hand;
                if (hoe == null || !IsHoe(hoe.Itemstack)) return;
                m.TillAtHome(colony, home, citizen, entity, target, hoe);
                SyncHand(); // a hoe that just broke must vanish from the model too
                break;
        }
        // The harvest and the weeds pulled are the household's: they go into the home chest later.
        foreach (var s in m.PackItems(citizen))
        {
            string code = s.Collectible.Code.ToString();
            if (!before.Contains(code)) gathered.Add(code);
        }
        m.GainWorkXp(colony, citizen, Jobs.Farmer);
    }

    /// <summary>Holds the hoe for tilling and the knife for skinning, and puts them away for work done by hand.</summary>
    void PrepareHand(Colony colony, CitizenRecord citizen)
    {
        var hand = Hand;
        if (hand == null) return;
        ItemMatch? tool = step == Step.Skin ? s => ColonyManager.IsKnife(s) : step == Step.Work && work == FieldWork.Till ? s => IsHoe(s) : null;
        if (tool == null)
        {
            PutAwayHand(colony, citizen);
            return;
        }
        if (hand.Itemstack != null && tool(hand.Itemstack)) return;
        var hoe = manager!.TakeFromPack(colony, citizen, tool, 1);
        if (hoe == null) return;
        PutAwayHand(colony, citizen);
        hand.Itemstack = hoe;
        SyncHand();
    }

    string WorkAnimation()
    {
        var hand = Hand;
        if (step == Step.Skin) return "knifecut";
        if (step is Step.Cook or Step.Collect or Step.Feed or Step.Eggs) return SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic";
        return work switch
        {
            FieldWork.Till => hand?.Itemstack?.Collectible.GetHeldTpUseAnimation(hand, entity) ?? "interactstatic",
            FieldWork.Sow or FieldWork.Water or FieldWork.Refill => SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic",
            _ => "breakhand"
        };
    }

    void StopWorkAnimation()
    {
        if (workAnim != null) StopAnimation(workAnim);
        workAnim = null;
    }

    public override void FinishExecute(bool cancelled)
    {
        StopWorkAnimation();
        if (Colony is { } colony && Citizen is { } citizen) PutAwayHand(colony, citizen);
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen) => step switch
    {
        Step.Fetch => Lang.Get("createacolony:activity-chores-fetch"),
        Step.Store => Lang.Get("createacolony:activity-chores-store"),
        Step.Skin => Lang.Get("createacolony:activity-chores-skin"),
        Step.Cook => Lang.Get("createacolony:activity-chores-cook"),
        Step.Collect => Lang.Get("createacolony:activity-chores-collect"),
        Step.Feed => Lang.Get("createacolony:activity-chores-feed"),
        Step.Eggs => Lang.Get("createacolony:activity-chores-eggs"),
        _ => Lang.Get("createacolony:activity-chores-" + work.ToString().ToLowerInvariant())
    };
}
