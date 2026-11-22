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
/// A cook works from a Cook's Hut with the game's own fire pit: it puts raw food the hut keeps too little
/// of cooked in the fire pit by the hut, adds firewood and lights it, and takes the cooked food out when
/// the game has cooked it. Cooked food goes on the hut's Food Shelves shelves when there is room, else into
/// the hut storage, where hungry settlers eat. Raw food and firewood come from the hut storage and shelves,
/// which couriers fill from the warehouse. With a Stone Bake Oven by the hut it also bakes bread and pies
/// there (ColonyManager.StoneOven.cs): it loads the baking spaces, keeps the oven's fire going while
/// something bakes and takes out what is done. Pot meals go into crocks, and crocks and pots of rotten food are
/// washed out in water (ColonyManager.Crocks.cs).
/// </summary>
public class AiTaskSettlerCook : AiTaskSettlerWorker
{
    enum Work { Load, LoadMeal, Fuel, Empty, Shelve, OvenLoad, OvenFuel, OvenEmpty, Wash, Brew, Serve, Ladle, Clear, Water, Station, ClayFuel, ClayCold }

    Work work;
    ColonyManager.KitchenKind? kind;
    ColonyManager.MealPlan? meal;
    ColonyManager.BakePlan? bake;
    /// <summary>The food station recipe the cook is making (ColonyManager.FoodStations.cs).</summary>
    ColonyManager.SawRecipe? stationRecipe;
    ColonyManager.SawKind? stationKind;
    /// <summary>The cool chest or shelf the food just fetched is for, and which food that is.</summary>
    BlockPos? coolTo;
    ItemMatch? coolMatch;

    public AiTaskSettlerCook(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Cook;
    protected override string HutType => BuildingTypes.Cook;
    protected override EnumTool Tool => EnumTool.Knife; // unused: a cook needs no tool
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => manager!.IsGrillable;
    protected override float WorkSeconds => manager!.Config.CookSecondsPerStep;

    /// <summary>
    /// It carries firewood, a cooking pot, the ingredients of the meal it is about to cook, empty crocks to pour meals
    /// into, and rotten crocks and pots it takes to wash.
    /// </summary>
    protected override bool AlsoKeeps(ItemStack stack) =>
        ColonyManager.IsFirewood(stack) || ColonyManager.IsCleanPot(stack) || meal?.Uses(stack) == true || bake?.Uses(stack) == true
        || manager!.IsEmptyCrock(stack) || manager.IsRottenMeal(stack)
        // Juice or honey and what goes with it, for the brewing barrels (ColonyManager.Brewing.cs).
        || brewing && (manager.HoldsBrewLiquid(stack) || manager.IsBrewSolid(stack))
        // Bowls to set out on the tables at meal times, and a bucket of water for the meal (ColonyManager.Serving.cs).
        || ColonyManager.IsBowl(stack) && (manager.IsFilledBowl(stack) || manager.MealTimeNow() >= 0)
        || meal?.Water > 0 && ColonyManager.IsWaterVessel(stack)
        // What a food station recipe takes (ColonyManager.FoodStations.cs).
        || stationRecipe != null && (stationRecipe.IsInput(stack) || stationRecipe.Water > 0 && ColonyManager.IsWaterVessel(stack));

    /// <summary>Whether the cook is brewing now, so it keeps what it fetched for the barrels.</summary>
    bool brewing;

    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    /// <summary>A dish it carries that is not an ingredient of the meal it is about to cook, nor a pot of meal it is about to pour into crocks.</summary>
    bool IsDishToShelve(ItemStack s) => manager!.IsDish(s) && meal?.Uses(s) != true && bake?.Uses(s) != true && !ColonyManager.IsCleanPot(s)
        && !(crockAtHand && manager.IsCookedPot(s));

    /// <summary>Whether an empty crock was at hand when the cook last planned, so cooked pots wait to be poured.</summary>
    bool crockAtHand;

    /// <summary>
    /// Dishes go on the kitchen shelves before anything goes to the hut storage. With nothing to put away, it
    /// takes dishes from the hut storage to shelves that have room for them.
    /// </summary>
    protected override bool PlanFirst(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        // Pot meals go into crocks: they keep there and the pot is free for the next meal (ColonyManager.Crocks.cs).
        crockAtHand = m.CrockAtHand(colony, hut, citizen);
        if (m.FillCrocks(colony, citizen) > 0) SetStatus("filling-crocks");
        if (PlanServe(colony, citizen, hut)) return true;
        if (PlanCrocks(colony, citizen, hut)) return true;
        // Food fetched to keep cool goes down to the cool chest it was fetched for.
        if (coolTo != null)
        {
            var to = coolTo;
            coolTo = null;
            if (coolMatch != null && m.PackItems(citizen).Any(coolMatch) && !IsUnreachable(to))
            {
                if (!ColonyManager.IsFoodShelf(entity.World.BlockAccessor.GetBlockEntity(to)))
                {
                    coolMatch = null;
                    return GoDeposit(to);
                }
                work = Work.Shelve;
                SetStatus("cooling-food");
                return GoWork(to);
            }
            coolMatch = null;
        }
        var shelves = m.KitchenShelves(colony, hut).Where(s => !IsUnreachable(s.Pos)).ToList();
        if (shelves.Count == 0) return false;
        if (m.ShelfFor(colony, hut, citizen, IsDishToShelve, IsUnreachable) is { } shelf)
        {
            work = Work.Shelve;
            SetStatus("shelving");
            return GoWork(shelf.Pos);
        }
        if (m.PackFull(citizen)) return false;
        foreach (var chest in m.HutStorage(colony, hut))
        {
            var dish = chest.Inventory.FirstOrDefault(s => !s.Empty && IsDishToShelve(s.Itemstack) && shelves.Any(sh => m.ShelfRoom(sh, s.Itemstack) > 0));
            if (dish == null) continue;
            var stack = dish.Itemstack!;
            var kind = stack.Collectible;
            int room = shelves.Max(sh => m.ShelfRoom(sh, stack));
            SetStatus("shelving");
            return FetchSupplies(chest.Pos, s => s.Collectible == kind && IsDishToShelve(s), room);
        }
        return false;
    }

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        m.UpgradeKitchenList(colony, hut);
        // Ingredients on the kitchen shelves (a vegetable basket, an egg shelf) are used as well as the hut storage.
        storage = storage.Concat(m.KitchenShelves(colony, hut)).ToList();
        // The oven first: baked bread burns if it is left in too long.
        if (PlanOven(colony, citizen, hut, storage) is { } baking) return baking;
        // Expanded Foods: dough at the mixing bowl, boiled food at the saucepan, as the Crafting recipes tab asks.
        if (PlanStation(colony, citizen, hut, storage) is { } atStation) return atStation;
        var pit = m.CookFirepit(colony, hut);
        if (pit == null)
        {
            if (m.CookOven(colony, hut) == null) SetStatus("no-firepit");
            return false;
        }

        // Cooked food waiting in the fire pit comes out first.
        if (!pit.outputSlot.Empty && !m.PackFull(citizen)) return Go(Work.Empty, pit, "firepit-emptying");

        if (!pit.inputSlot.Empty)
        {
            // Something a player put there is theirs: leave the fire pit alone. A pot that makes a meal the cook lights the fire under.
            if (pit.inputSlot.Itemstack.Block is BlockCookingContainer && !m.PotWillCook(pit))
            {
                SetStatus("firepit-busy");
                return false;
            }
            if (pit.inputSlot.Itemstack.Block is not BlockCookingContainer && !m.IsGrillable(pit.inputSlot.Itemstack))
            {
                SetStatus("firepit-busy");
                return false;
            }
            if (pit.IsBurning)
            {
                // While it cooks, food goes down to the cellar.
                if (CoolFood(colony, citizen, hut)) return true;
                SetStatus("cooking");
                return false;
            }
            if (!pit.fuelSlot.Empty || m.PackCount(citizen, ColonyManager.IsFirewood) > 0) return Go(Work.Fuel, pit, "firepit-lighting");
            return FetchFirewood(colony, hut, storage);
        }

        // Pot meals first: they feed more for the same food.
        meal = m.NextMeal(colony, hut, citizen);
        if (meal != null && PlanMeal(colony, citizen, hut, storage, pit, meal) is { } planned) return planned;
        meal = null;

        var pack = m.PackItems(citizen);
        var wantedFood = m.WantedFood(colony, hut, citizen);
        foreach (var wanted in wantedFood)
        {
            // Raw food on hand: put it on the fire, with firewood unless the fire pit has some.
            if (pack.Any(wanted.IsRaw))
            {
                kind = wanted;
                if (pit.IsBurning || !pit.fuelSlot.Empty || m.PackCount(citizen, ColonyManager.IsFirewood) > 0) return Go(Work.Load, pit, "cooking");
                return FetchFirewood(colony, hut, storage);
            }
            var chest = m.SoonestToSpoil(storage, wanted.IsRaw);
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("fetching-food");
                return FetchSupplies(chest.Pos, wanted.IsRaw, m.Config.CookRawReserve);
            }
            if (m.RawInWarehouse(colony, wanted) is { } raw)
            {
                m.EnsureCookRequest(colony, hut, raw.Collectible.Code.ToString(), m.Config.CookRawReserve);
                SetStatus("waiting-food");
                return false;
            }
        }

        if (CoolFood(colony, citizen, hut)) return true;
        if (PlanWash(colony, citizen, hut, storage)) return true;
        if (PlanBrew(colony, citizen, hut)) return true;
        SetStatus(wantedFood.Count > 0 ? "needs-raw-food" : "kitchen-stocked");
        return false;
    }

    /// <summary>
    /// Serving (ColonyManager.Serving.cs). At a meal time, with tables free: set out the filled bowls it carries, fill
    /// empty bowls at a crock or pot of meal, or get empty bowls (off the tables, or from the kitchen). At any time,
    /// bowls left eaten on the tables are cleared away.
    /// </summary>
    bool PlanServe(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        if (!m.Config.CookServes || m.ServingSpots(hut).Count == 0) return false;
        var here = entity.Pos.AsBlockPos;
        if (m.MealTimeNow() >= 0)
        {
            var free = m.FreeServingSpots(hut).Where(p => !IsUnreachable(p)).OrderBy(p => p.DistanceSqTo(here.X, here.Y, here.Z)).ToList();
            if (free.Count > 0)
            {
                var pack = m.PackItems(citizen);
                if (pack.Any(m.IsFilledBowl))
                {
                    work = Work.Serve;
                    SetStatus("serving");
                    return GoWork(free[0]);
                }
                var source = m.LadleSource(colony, hut, IsUnreachable);
                if (source != null)
                {
                    if (pack.Any(ColonyManager.IsEmptyBowl))
                    {
                        work = Work.Ladle;
                        SetStatus("ladling");
                        return GoWork(source.Pos);
                    }
                    if (!m.PackFull(citizen))
                    {
                        if (m.DirtyServingSpot(hut, IsUnreachable) is { } eaten)
                        {
                            work = Work.Clear;
                            SetStatus("clearing-tables");
                            return GoWork(eaten);
                        }
                        var bowls = m.KitchenStores(colony, hut).FirstOrDefault(c => !IsUnreachable(c.Pos) && c.Inventory.Any(s => !s.Empty && ColonyManager.IsEmptyBowl(s.Itemstack)));
                        if (bowls != null)
                        {
                            SetStatus("fetching-bowls");
                            return FetchSupplies(bowls.Pos, ColonyManager.IsEmptyBowl, free.Count);
                        }
                        var spare = m.WarehouseContainers(colony).SelectMany(c => c.Inventory).FirstOrDefault(s => !s.Empty && ColonyManager.IsEmptyBowl(s.Itemstack));
                        if (spare != null) m.EnsureCookRequest(colony, hut, spare.Itemstack!.Collectible.Code.ToString(), free.Count);
                    }
                }
            }
        }
        if (!m.PackFull(citizen) && m.DirtyServingSpot(hut, IsUnreachable) is { } dirty)
        {
            work = Work.Clear;
            SetStatus("clearing-tables");
            return GoWork(dirty);
        }
        return false;
    }

    /// <summary>
    /// Gets a cooked pot and an empty crock together so the pot can be poured out: a crock from the kitchen for the pot
    /// it carries, or a pot from the kitchen when it has a crock at hand. With no empty crock in the kitchen it asks
    /// couriers for one from the warehouse; with none anywhere the Potter's Hut makes more.
    /// </summary>
    bool PlanCrocks(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        if (m.PackFull(citizen)) return false;
        var stores = m.KitchenStores(colony, hut).Where(c => !IsUnreachable(c.Pos)).ToList();
        var pack = m.PackItems(citizen);
        var pot = pack.FirstOrDefault(m.IsCookedPot);
        if (pot != null)
        {
            float servings = ((IBlockMealContainer)pot.Block).GetQuantityServings(entity.World, pot);
            var crockChest = stores.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && m.IsEmptyCrock(s.Itemstack)));
            if (crockChest != null)
            {
                SetStatus("fetching-crock");
                return FetchSupplies(crockChest.Pos, m.IsEmptyCrock, System.Math.Max(1, (int)System.Math.Ceiling(servings / 4)));
            }
            var warehouseCrock = m.WarehouseContainers(colony).SelectMany(c => c.Inventory).FirstOrDefault(s => !s.Empty && m.IsEmptyCrock(s.Itemstack));
            if (warehouseCrock != null) m.EnsureCookRequest(colony, hut, warehouseCrock.Itemstack!.Collectible.Code.ToString(), m.Config.CookSpareCrocks);
            return false;
        }
        if (!pack.Any(m.IsEmptyCrock)) return false;
        var potChest = stores.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && m.IsCookedPot(s.Itemstack)));
        if (potChest == null) return false;
        SetStatus("fetching-pot-meal");
        return FetchSupplies(potChest.Pos, m.IsCookedPot, 1);
    }

    /// <summary>
    /// A crock or pot of rotten food in the kitchen is taken to water and washed out, as a player does by dropping
    /// it in water. False when there is none, or no water within WashWaterRadius.
    /// </summary>
    bool PlanWash(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        bool carries = m.PackItems(citizen).Any(s => m.IsRottenMeal(s, update: true));
        var chest = carries || m.PackFull(citizen) ? null : storage.FirstOrDefault(c => !IsUnreachable(c.Pos) && c.Inventory.Any(s => !s.Empty && m.IsRottenMeal(s.Itemstack)));
        if (!carries && chest == null) return false;
        var water = m.WashWater(colony, hut.HutPos);
        if (water == null || IsUnreachable(water)) return false;
        if (!carries)
        {
            SetStatus("fetching-rotten");
            return FetchSupplies(chest!.Pos, s => m.IsRottenMeal(s), 1);
        }
        work = Work.Wash;
        SetStatus("washing");
        return GoWork(water);
    }

    /// <summary>
    /// With nothing else to do, takes food that can spoil from a warm chest in the hut down to the coolest chest or
    /// kitchen shelf with room, such as one in the basement (ColonyManager.Freshness.cs).
    /// </summary>
    bool CoolFood(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        // Raw food for the fire is its own stock, which it keeps rather than putting in a chest; a cool shelf takes it.
        if (m.PackFull(citizen) || m.FindCoolingMove(colony, hut, Stock) is not { } move || IsUnreachable(move.From.Pos) || IsUnreachable(move.To.Pos)) return false;
        SetStatus("cooling-food");
        coolTo = move.To.Pos.Copy();
        coolMatch = move.Match;
        return FetchSupplies(move.From.Pos, move.Match, move.Amount);
    }

    /// <summary>
    /// Gets a cooking pot and the meal's ingredients together and puts them on the fire. Null when the
    /// meal cannot be made right now (no pot anywhere), so the cook grills instead.
    /// </summary>
    bool? PlanMeal(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, BlockEntityFirepit pit, ColonyManager.MealPlan plan)
    {
        var m = manager!;
        if (m.PackCount(citizen, ColonyManager.IsCleanPot) == 0)
        {
            var potChest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsCleanPot(s.Itemstack)));
            if (potChest != null && !m.PackFull(citizen))
            {
                SetStatus("fetching-pot");
                return FetchSupplies(potChest.Pos, ColonyManager.IsCleanPot, 1);
            }
            var warehousePot = m.WarehouseContainers(colony).SelectMany(c => c.Inventory).FirstOrDefault(s => !s.Empty && ColonyManager.IsCleanPot(s.Itemstack));
            if (warehousePot == null) return null;
            m.EnsureCookRequest(colony, hut, warehousePot.Itemstack!.Collectible.Code.ToString(), 1);
            SetStatus("waiting-pot");
            return false;
        }

        // Water for the pot: fill a bucket at the nearest water first (ColonyManager.Serving.cs).
        if (plan.Water > 0 && m.CookWaterPortions(citizen) < plan.Water)
        {
            if (!m.PackItems(citizen).Any(ColonyManager.IsWaterVessel))
            {
                var vessel = m.KitchenStores(colony, hut).FirstOrDefault(c => !IsUnreachable(c.Pos) && c.Inventory.Any(s => !s.Empty && ColonyManager.IsWaterVessel(s.Itemstack)));
                if (vessel == null || m.PackFull(citizen)) return null;
                SetStatus("fetching-bucket");
                return FetchSupplies(vessel.Pos, ColonyManager.IsWaterVessel, 1);
            }
            var water = m.WashWater(colony, hut.HutPos);
            if (water == null || IsUnreachable(water)) return null;
            work = Work.Water;
            SetStatus("fetching-water");
            return GoWork(water);
        }

        foreach (var (sample, count) in plan.Slots)
        {
            if (ColonyManager.IsWater(sample)) continue;
            var code = sample.Collectible.Code;
            ItemMatch same = s => s.Collectible.Code.Equals(code);
            // Two slots of the same item need it twice over.
            int need = plan.Slots.Where(x => x.Sample.Collectible.Code.Equals(code)).Sum(x => x.Count);
            int have = m.PackCount(citizen, same);
            if (have >= need) continue;
            var chest = m.SoonestToSpoil(storage, same);
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("fetching-food");
                return FetchSupplies(chest.Pos, same, need - have);
            }
            m.EnsureCookRequest(colony, hut, code.ToString(), need - have);
            SetStatus("waiting-food");
            return false;
        }

        if (pit.IsBurning || !pit.fuelSlot.Empty || m.PackCount(citizen, ColonyManager.IsFirewood) > 0) return Go(Work.LoadMeal, pit, "cooking-meal");
        return FetchFirewood(colony, hut, storage);
    }

    /// <summary>
    /// Stone Bake Oven work: take out what is baked, keep the fire going under what is baking, or start a
    /// bake. Null when the oven needs nothing now, so the cook gets on with the fire pit.
    /// </summary>
    bool? PlanOven(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var oven = m.CookOven(colony, hut);
        bake = null;
        if (oven == null) return null;
        var tops = oven.Tops.Where(t => !IsUnreachable(t.Pos)).ToList();

        var done = tops.FirstOrDefault(m.OvenHasDone);
        if (done != null && !m.PackFull(citizen))
        {
            work = Work.OvenEmpty;
            SetStatus("oven-emptying");
            return GoWork(done.Pos);
        }
        if (oven.Clay is { } clay) return PlanClayOven(colony, citizen, hut, storage, oven, clay);
        if (oven.Fire == null)
        {
            SetStatus("oven-nofire");
            return null;
        }

        if (oven.Tops.Any(m.OvenIsBaking))
        {
            if (oven.Fire.IsBurning || IsUnreachable(oven.Fire.Pos)) return null;
            if (!oven.Fire.fuelSlot.Empty || m.PackCount(citizen, ColonyManager.IsFirewood) > 0)
            {
                work = Work.OvenFuel;
                SetStatus("oven-lighting");
                return GoWork(oven.Fire.Pos);
            }
            return FetchFirewood(colony, hut, storage);
        }

        var plan = m.NextBake(colony, hut, citizen, oven);
        if (plan == null) return null;
        bake = plan;
        foreach (var (code, count) in plan.Needs)
        {
            ItemMatch same = s => s.Collectible.Code.Equals(code);
            int have = m.PackCount(citizen, same);
            if (have >= count) continue;
            var chest = m.SoonestToSpoil(storage, same);
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("fetching-dough");
                return FetchSupplies(chest.Pos, same, count - have);
            }
            // Waiting for a courier: cook on the fire pit meanwhile.
            m.EnsureCookRequest(colony, hut, code.ToString(), count - have);
            return null;
        }
        // Firewood comes along, so the cook can light the oven straight after loading it.
        if (!oven.Fire.IsBurning && oven.Fire.fuelSlot.Empty && m.PackCount(citizen, ColonyManager.IsFirewood) == 0) return FetchFirewood(colony, hut, storage);
        var free = tops.FirstOrDefault(t => plan.Kind.IsPie ? t.Inventory.All(s => s.Empty) : t.Inventory.Any(s => s.Empty));
        if (free == null) return null;
        work = Work.OvenLoad;
        SetStatus(plan.Kind.IsPie ? "oven-loading-pie" : "oven-loading");
        return GoWork(free.Pos);
    }

    /// <summary>
    /// A game clay oven (0.194.0): it is fired first, with firewood and nothing else in it; once the fire has burned
    /// down and it is hot, the bake goes in and bakes as it cools. Bread still baking in an oven that went cold comes
    /// out again, so the oven can be fired once more. Null when the oven needs nothing now.
    /// </summary>
    bool? PlanClayOven(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, ColonyManager.StoneOven oven, BlockEntityOven clay)
    {
        var m = manager!;
        if (IsUnreachable(clay.Pos) || clay.IsBurning) return null;
        if (m.OvenIsBaking(clay))
        {
            if (clay.ovenTemperature >= m.Config.ClayOvenColdTemp || m.PackFull(citizen)) return null;
            work = Work.ClayCold;
            SetStatus("clayoven-cold");
            return GoWork(clay.Pos);
        }
        var plan = m.NextBake(colony, hut, citizen, oven);
        if (plan == null) return null;
        if (!m.ClayOvenReady(clay))
        {
            // Fire it: firewood in, then light it.
            if (clay.HasFuel || m.PackCount(citizen, ColonyManager.IsFirewood) > 0)
            {
                work = Work.ClayFuel;
                SetStatus("clayoven-firing");
                return GoWork(clay.Pos);
            }
            return FetchFirewood(colony, hut, storage);
        }
        bake = plan;
        foreach (var (code, count) in plan.Needs)
        {
            ItemMatch same = s => s.Collectible.Code.Equals(code);
            int have = m.PackCount(citizen, same);
            if (have >= count) continue;
            var chest = m.SoonestToSpoil(storage, same);
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("fetching-dough");
                return FetchSupplies(chest.Pos, same, count - have);
            }
            m.EnsureCookRequest(colony, hut, code.ToString(), count - have);
            if (have == 0) return null;
            break; // bake what it has while the oven is hot
        }
        work = Work.OvenLoad;
        SetStatus(plan.Kind.IsPie ? "oven-loading-pie" : "oven-loading");
        return GoWork(clay.Pos);
    }

    /// <summary>
    /// Food station work (ColonyManager.FoodStations.cs): the first wanted station recipe whose station is by the hut.
    /// It makes it if it carries everything, else fetches what it lacks from the hut, asks couriers for it, or fills a
    /// bucket for one that takes water. Null when there is nothing to make at the stations.
    /// </summary>
    bool? PlanStation(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        stationRecipe = null;
        stationKind = null;
        if (!m.FoodStationsOn || hut.RecipeKeep.Count == 0) return null;
        foreach (var want in m.WantedRecipes(colony, hut, citizen))
        {
            foreach (var r in want.Recipes.Where(r => r.Station != null))
            {
                if (m.StationPos(hut, r.Station!) is not { } at || IsUnreachable(at)) continue;
                bool hasWater = r.Water <= 0 || m.CookWaterPortions(citizen) >= r.Water;
                if (m.HasSawInputs(citizen, r) && hasWater)
                {
                    stationRecipe = r;
                    stationKind = want;
                    work = Work.Station;
                    workRange = 2f;
                    SetStatus(r.Station == ColonyManager.MixingBowlStation ? "station-kneading" : "station-simmering");
                    return GoWork(at);
                }
            }
        }
        // Nothing ready: fetch for the first recipe whose station is there and whose things can be had.
        foreach (var want in m.WantedRecipes(colony, hut, citizen))
        {
            foreach (var r in want.Recipes.Where(r => r.Station != null && m.StationPos(hut, r.Station!) != null))
            {
                bool allFound = true;
                foreach (var (part, quantity) in r.Inputs)
                {
                    ItemMatch fits = s => part.SatisfiesAsIngredient(s, false);
                    int missing = quantity - m.PackCount(citizen, fits);
                    if (missing <= 0) continue;
                    var chest = storage.FirstOrDefault(c => !IsUnreachable(c.Pos) && c.Inventory.Any(s => !s.Empty && fits(s.Itemstack)));
                    if (chest != null && !m.PackFull(citizen))
                    {
                        stationRecipe = r;
                        stationKind = want;
                        SetStatus("recipes-fetching");
                        return FetchSupplies(chest.Pos, fits, missing);
                    }
                    if (m.CodeInWarehouse(colony, fits) is { } code) m.EnsureCraftRequest(colony, hut, code, quantity);
                    allFound = false;
                }
                if (!allFound) continue;
                if (r.Water > 0 && m.CookWaterPortions(citizen) < r.Water)
                {
                    stationRecipe = r;
                    stationKind = want;
                    if (!m.PackItems(citizen).Any(ColonyManager.IsWaterVessel))
                    {
                        var vessel = m.KitchenStores(colony, hut).FirstOrDefault(c => !IsUnreachable(c.Pos) && c.Inventory.Any(s => !s.Empty && ColonyManager.IsWaterVessel(s.Itemstack)));
                        if (vessel == null || m.PackFull(citizen)) continue;
                        SetStatus("fetching-bucket");
                        return FetchSupplies(vessel.Pos, ColonyManager.IsWaterVessel, 1);
                    }
                    var water = m.WashWater(colony, hut.HutPos);
                    if (water == null || IsUnreachable(water)) continue;
                    work = Work.Water;
                    SetStatus("fetching-water");
                    return GoWork(water);
                }
            }
        }
        stationRecipe = null;
        stationKind = null;
        return null;
    }

    bool FetchFirewood(Colony colony, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsFirewood(s.Itemstack)));
        if (chest != null)
        {
            SetStatus("fetching-firewood");
            return FetchSupplies(chest.Pos, ColonyManager.IsFirewood, m.Config.CookFirewood * 2);
        }
        m.EnsureCookRequest(colony, hut, "game:firewood", m.Config.CookFirewood * 4);
        SetStatus("needs-firewood");
        return false;
    }

    bool Go(Work next, BlockEntityFirepit pit, string status)
    {
        work = next;
        SetStatus(status);
        return GoWork(pit.Pos);
    }

    /// <summary>
    /// Brewing for the Tavern (ColonyManager.Brewing.cs), when the kitchen has nothing else to do: juice or honey it
    /// carries goes into a brew barrel, a solid the brew takes goes into its barrel, else it fetches them.
    /// </summary>
    bool PlanBrew(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        brewing = false;
        if (!m.CookBrews(colony)) return false;
        var barrels = m.BrewBarrels(hut).Where(b => !IsUnreachable(b.Pos)).ToList();
        if (barrels.Count == 0) return false;
        var pack = m.PackItems(citizen);
        brewing = true;

        // Carrying juice or honey: into a barrel with it.
        foreach (var jug in pack.Where(m.HoldsBrewLiquid))
        {
            if (m.BarrelForLiquid(barrels, ColonyManager.ContentOf(jug)!) is { } barrel)
            {
                work = Work.Brew;
                SetStatus("brewing");
                workRange = 2.5f;
                return GoWork(barrel.Pos);
            }
        }
        // A brew that needs its solid.
        if (m.BarrelWantingSolid(barrels) is { } wants)
        {
            if (pack.Any(s => wants.Solid.SatisfiesAsIngredient(s, false)))
            {
                work = Work.Brew;
                SetStatus("brewing");
                workRange = 2.5f;
                return GoWork(wants.Barrel.Pos);
            }
            if (!m.PackFull(citizen) && m.BrewSupplyChest(colony, hut, s => wants.Solid.SatisfiesAsIngredient(s, false), IsUnreachable) is { } solidChest)
            {
                SetStatus("brew-fetching");
                return FetchSupplies(solidChest.Pos, s => wants.Solid.SatisfiesAsIngredient(s, false), System.Math.Max(1, wants.Solid.Quantity));
            }
        }
        // Juice or honey for a barrel that has room.
        if (!m.PackFull(citizen) && barrels.Any(b => b.CurrentRecipe == null && (b.Inventory[1].Empty || ColonyManager.BarrelLitres(b) < b.CapacityLitres - 0.5f)))
        {
            ItemMatch fits = s => m.HoldsBrewLiquid(s) && m.BarrelForLiquid(barrels, ColonyManager.ContentOf(s)!) != null;
            if (m.BrewSupplyChest(colony, hut, fits, IsUnreachable) is { } chest)
            {
                SetStatus("brew-fetching");
                return FetchSupplies(chest.Pos, fits, 4);
            }
        }
        brewing = false;
        return false;
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (work)
        {
            case Work.Serve:
                if (!m.SetOutBowl(colony, citizen, target)) MarkUnreachable(target);
                return;
            case Work.Clear:
                if (!m.ClearBowl(colony, citizen, target)) MarkUnreachable(target);
                return;
            case Work.Ladle:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityContainer pot
                    || m.LadleBowls(colony, citizen, pot, System.Math.Max(1, m.FreeServingSpots(hut).Count)) == 0) MarkUnreachable(target);
                return;
            case Work.Station:
                if (stationRecipe == null || !m.CraftAtStation(colony, hut, citizen, stationRecipe)) MarkUnreachable(target);
                stationRecipe = null;
                return;
            case Work.ClayFuel:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityOven fireOven || !m.FuelClayOven(colony, citizen, fireOven)) MarkUnreachable(target);
                return;
            case Work.ClayCold:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is BlockEntityOven coldOven) m.EmptyColdOven(colony, citizen, coldOven);
                return;
            case Work.Water:
                m.FillWater(colony, citizen);
                entity.World.PlaySoundAt(new AssetLocation("sounds/block/water"), target.X + 0.5, target.Y + 0.5, target.Z + 0.5, null);
                return;
        }
        if (work == Work.Shelve)
        {
            if (entity.World.BlockAccessor.GetBlockEntity(target) is BlockEntityContainer shelf && ColonyManager.IsFoodShelf(shelf)
                && m.ShelveFromPack(colony, citizen, shelf, s => IsDishToShelve(s) || coolMatch?.Invoke(s) == true) == 0)
            {
                MarkUnreachable(target); // full after all: try another shelf, or the hut storage
            }
            coolMatch = null;
            return;
        }
        if (work is Work.OvenLoad or Work.OvenEmpty)
        {
            if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityContainer top) return;
            if (work == Work.OvenEmpty)
            {
                m.EmptyOven(colony, citizen, top);
                return;
            }
            if (bake == null) return;
            // Every baking space within reach gets some, nearest first.
            var near = (m.CookOven(colony, hut)?.Tops ?? new List<BlockEntityContainer> { top })
                .Where(t => t.Pos.DistanceTo(target) <= 3)
                .OrderBy(t => t.Pos.DistanceTo(target));
            if (m.LoadOven(colony, citizen, near, bake) == 0) MarkUnreachable(target);
            bake = null;
            return;
        }
        if (work == Work.Wash)
        {
            m.WashCarried(colony, citizen, entity.Pos.XYZ);
            return;
        }
        if (work == Work.Brew)
        {
            if (entity.World.BlockAccessor.GetBlockEntity(target) is BlockEntityBarrel barrel && !m.TendBrew(colony, citizen, hut, barrel)) MarkUnreachable(target);
            return;
        }
        if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityFirepit pit) return;
        switch (work)
        {
            case Work.Load:
                if (kind != null) m.LoadFirepit(colony, citizen, pit, kind);
                break;
            case Work.LoadMeal:
                if (meal != null) m.LoadMeal(colony, citizen, pit, meal);
                break;
            case Work.Fuel:
            case Work.OvenFuel:
                m.FuelFirepit(colony, citizen, pit);
                break;
            case Work.Empty:
                m.EmptyFirepit(colony, citizen, pit);
                break;
        }
    }

    protected override string DescribeWork() => work == Work.LoadMeal && meal != null
        ? Lang.Get("createacolony:activity-cook-loadmeal", manager!.MealName(meal.Recipe))
        : work == Work.Station && stationKind != null ? Lang.Get("createacolony:activity-cook-station", stationKind.Sample.GetName())
        : Lang.Get("createacolony:activity-cook-" + work.ToString().ToLowerInvariant());
}
