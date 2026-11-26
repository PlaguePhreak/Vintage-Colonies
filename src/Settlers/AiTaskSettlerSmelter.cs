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
/// A smelter works from a Smelter's Hut with the game's own fire pit, crucible and ingot molds: it sets
/// fired ingot molds out beside the fire pit, fills a crucible with nuggets (or a mix of nuggets for an
/// alloy such as bronze), puts it on the fire with charcoal and lights it, pours the molten metal into the molds once the game has melted it, and takes
/// the ingots out when they have cooled. With a bloomery by the hut it also makes iron blooms from iron
/// nuggets and charcoal, for the blacksmith to hammer into iron ingots. Ore, fuel, crucibles and molds come from the hut storage, which
/// couriers fill from the warehouse.
/// </summary>
public class AiTaskSettlerSmelter : AiTaskSettlerWorker
{
    enum Work { PlaceMold, Load, Fuel, Pour, Collect, Reheat, Alloy, Bloomery, TakeBloom }

    Work work;
    ColonyManager.SmeltKind? kind;
    int batch;
    /// <summary>The tool mold type being set out (the copper tool chain), or null for an ingot mold.</summary>
    string? placingTool;

    public AiTaskSettlerSmelter(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Smelter;
    protected override string HutType => BuildingTypes.Smelter;
    protected override EnumTool Tool => EnumTool.Knife; // unused: a smelter needs no tool
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => manager!.IsSmeltable;
    protected override float WorkSeconds => manager!.Config.SmelterSecondsPerStep;

    /// <summary>It carries fuel, crucibles and molds; ingots and blooms go to the hut.</summary>
    protected override bool AlsoKeeps(ItemStack stack) =>
        ColonyManager.IsEmptyCrucible(stack) || ColonyManager.IsFullCrucible(stack) || ColonyManager.IsIngotMold(stack) || ColonyManager.IsToolMold(stack)
        || ColonyManager.IsSmeltFuel(stack, manager!.Config.SmelterKeepFuelTemperature) || ColonyManager.IsFirewood(stack);

    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var pit = m.SmelterFirepit(hut);
        var bloomery = m.SmelterBloomery(hut);
        if (pit == null && bloomery == null)
        {
            SetStatus("no-firepit");
            return false;
        }

        // Cooled ingots come out of the molds first, so the molds are free again; then a finished bloom.
        if (!m.PackFull(citizen) && m.MoldWithIngot(hut) is { } done) return Go(Work.Collect, done.Pos, "smelter-collecting");
        if (!m.PackFull(citizen) && m.CastReady(hut) is { } cast) return Go(Work.Collect, cast.Pos, "smelter-collecting-cast");
        placingTool = null;
        if (bloomery != null && !m.PackFull(citizen) && ColonyManager.BloomReady(bloomery)) return Go(Work.TakeBloom, bloomery.Pos, "smelter-taking-bloom");

        // Why the fire pit cannot take a new crucible right now (null when it can).
        string? pitBusy = pit == null ? "no-firepit" : null;
        if (pit != null && ColonyManager.FullCrucibleSlot(pit) is { } full)
        {
            // Molten metal waiting: pour it while it is hot.
            bool molten = m.IsMolten(full.Itemstack);
            if (molten && m.FreeMoldSides(hut) > 0) return Go(Work.Pour, pit.Pos, "smelter-pouring");
            if (m.FreeMoldSides(hut) == 0 && PlanMolds(colony, citizen, hut, storage, 1) is { } moldPlan) return moldPlan;
            // Cooled in the crucible: back on the fire to melt again.
            if (!molten && full == pit.outputSlot && pit.inputSlot.Empty) return Go(Work.Reheat, pit.Pos, "smelter-reheating");
            if (!molten && full == pit.inputSlot && !pit.IsBurning) return PlanFuel(colony, citizen, hut, storage, pit, m.SmelterHeatNeeded(pit));
            pitBusy = molten ? "smelter-needs-molds" : full == pit.outputSlot ? "smelter-cooled" : "smelter-reheating";
        }
        else if (pit != null && !pit.inputSlot.Empty)
        {
            // Something a player put there is theirs: leave the fire pit alone.
            if (!ColonyManager.IsEmptyCrucible(pit.inputSlot.Itemstack)) pitBusy = "smelter-firepit-busy";
            else if (pit.IsBurning) pitBusy = "smelter-melting";
            else return PlanFuel(colony, citizen, hut, storage, pit, m.SmelterHeatNeeded(pit));
        }

        // The copper tool chain (0.205.0): a tool mold by the fire for the next tool the colony has none of.
        if (pit != null && pitBusy == null && m.NextCast(colony) is { } castType
            && !m.OpenCastMolds(colony, hut).Any(t => ColonyManager.MoldType(t.Block) == castType)
            && PlanCastMold(colony, citizen, hut, storage, castType) is { } castPlan) return castPlan;

        var wanted = m.WantedIngots(colony, hut, citizen);
        int castIngots = m.CastIngots(colony, hut);
        if (castIngots > 0 && m.CastMetal() is { } castMetal && !wanted.Contains(castMetal)) wanted.Insert(0, castMetal);
        if (wanted.Count == 0)
        {
            SetStatus(pitBusy ?? "smelter-stocked");
            return false;
        }

        // The bloomery works beside the fire pit: fill it whenever blooms are wanted and it is free.
        string? bloomWait = null;
        foreach (var want in wanted.Where(w => w.Bloomery))
        {
            if (bloomery == null)
            {
                bloomWait ??= "smelter-no-bloomery";
                break;
            }
            if (!m.HasChimney(bloomery))
            {
                bloomWait = "smelter-bloomery-nochimney";
                break;
            }
            if (bloomery.IsBurning)
            {
                bloomWait = "smelter-bloomery-burning";
                break;
            }
            if (!m.BloomeryFree(bloomery, want))
            {
                bloomWait = "smelter-bloomery-busy";
                break;
            }
            if (PlanBloomery(colony, citizen, hut, storage, bloomery, want, ref bloomWait) is { } acted) return acted;
        }

        var crucibleWants = wanted.Where(w => !w.Bloomery).ToList();
        if (pitBusy != null || crucibleWants.Count == 0)
        {
            SetStatus(crucibleWants.Count == 0 || pitBusy == "no-firepit" ? bloomWait ?? pitBusy ?? "smelter-stocked" : pitBusy!);
            return false;
        }

        // Molds first: the metal has to go somewhere when it is melted.
        int free = m.FreeMoldSides(hut);
        if (free < m.Config.SmelterMolds && PlanMolds(colony, citizen, hut, storage, m.Config.SmelterMolds - free) is { } placing) return placing;
        if (free == 0)
        {
            SetStatus("smelter-needs-molds");
            return false;
        }

        if (m.PackCount(citizen, ColonyManager.IsEmptyCrucible) == 0)
        {
            var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsEmptyCrucible(s.Itemstack)));
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("smelter-fetching-crucible");
                return FetchSupplies(chest.Pos, ColonyManager.IsEmptyCrucible, 1);
            }
            if (m.CodeInWarehouse(colony, ColonyManager.IsEmptyCrucible) is { } code) m.EnsureCraftRequest(colony, hut, code, 1);
            SetStatus("smelter-needs-crucible");
            return false;
        }

        foreach (var want in crucibleWants)
        {
            int shortBy = m.CraftWant(colony, hut, want.Key) - m.SmeltStock(colony, hut, want, citizen);
            int ingots = System.Math.Max(1, System.Math.Min(shortBy, System.Math.Min(free, m.Config.SmelterIngotsPerBatch)));
            // Enough for the tool molds too (an anvil takes 9 ingots' worth, over several pours).
            if (castIngots > 0 && want == m.CastMetal()) ingots = System.Math.Max(ingots, System.Math.Min(castIngots, m.Config.SmelterIngotsPerBatch));
            if (want.Alloy != null)
            {
                if (PlanAlloy(colony, citizen, hut, storage, pit!, want, ingots) is { } alloyPlan) return alloyPlan;
                continue;
            }
            var pack = m.PackItems(citizen);
            var ore = pack.Where(want.IsOre).GroupBy(s => s.Collectible.Code).OrderByDescending(g => g.Sum(s => s.StackSize)).FirstOrDefault();
            int perIngot = ore == null ? 20 : ColonyManager.PiecesPerIngot(want, ore.Key);
            int have = ore?.Sum(s => s.StackSize) ?? 0;
            if (have >= perIngot)
            {
                kind = want;
                batch = System.Math.Min(ingots, have / perIngot);
                int heat = ColonyManager.MeltTemperature(want, pack);
                if (m.PackCount(citizen, s => ColonyManager.IsSmeltFuel(s, heat)) > 0) return Go(Work.Load, pit!.Pos, "smelter-loading");
                return FetchFuel(colony, hut, storage, heat);
            }
            var oreChest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && want.IsOre(s.Itemstack)));
            if (oreChest != null && !m.PackFull(citizen))
            {
                SetStatus("smelter-fetching-ore");
                return FetchSupplies(oreChest.Pos, want.IsOre, perIngot * ingots);
            }
            if (m.CodeInWarehouse(colony, want.IsOre) is { } oreCode)
            {
                int each = ColonyManager.PiecesPerIngot(want, new AssetLocation(oreCode));
                m.EnsureCraftRequest(colony, hut, oreCode, each * ingots);
                SetStatus("smelter-waiting-ore");
                return false;
            }
        }
        SetStatus(bloomWait ?? "smelter-needs-ore");
        return false;
    }

    /// <summary>
    /// The next step for an alloy: load the crucible when the mix is on hand, else fetch the ore it is short of from
    /// the hut storage or ask couriers for it. Null when the colony has none of some metal, so the next metal is tried.
    /// </summary>
    bool? PlanAlloy(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, BlockEntityFirepit pit, ColonyManager.SmeltKind want, int ingots)
    {
        var m = manager!;
        var pack = m.PackItems(citizen);
        var crucible = pack.FirstOrDefault(ColonyManager.IsEmptyCrucible);
        var (slots, size) = ColonyManager.CrucibleRoom(pit, crucible);
        var plan = m.AlloyPlan(want, pack, ingots, slots, size);
        if (plan != null)
        {
            kind = want;
            batch = plan.Sum(p => p.Pieces * want.UnitsPer[p.Code]) / 100;
            int heat = ColonyManager.MeltTemperature(want, pack.Where(want.IsOre));
            if (m.PackCount(citizen, s => ColonyManager.IsSmeltFuel(s, heat)) > 0) return Go(Work.Alloy, pit.Pos, "smelter-mixing");
            return FetchFuel(colony, hut, storage, heat);
        }

        // Short of some metal: from the hut storage first, then from the warehouse by courier.
        var stored = storage.SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack!).ToList();
        bool asked = false;
        foreach (var (part, units) in m.AlloyShort(want, pack, ingots))
        {
            int pieces = (int)System.Math.Ceiling(units / (double)System.Math.Max(1, part.UnitsPer.Values.Min()));
            var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && part.IsOre(s.Itemstack)));
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("smelter-fetching-ore");
                return FetchSupplies(chest.Pos, part.IsOre, pieces);
            }
            if (m.CodeInWarehouse(colony, part.IsOre) is { } code)
            {
                m.EnsureCraftRequest(colony, hut, code, pieces);
                asked = true;
                continue;
            }
            if (!stored.Any(part.IsOre)) return null;
        }
        if (asked)
        {
            SetStatus("smelter-waiting-ore");
            return false;
        }
        // Enough of each metal but not in shares the game takes (say too much tin): try the next metal.
        return null;
    }

    /// <summary>
    /// The next step for the bloomery: fill and light it when the ore and fuel are on hand, else fetch them or ask
    /// couriers. Null when the colony has no ore for it (wait says why), so other work can go on.
    /// </summary>
    bool? PlanBloomery(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage,
        BlockEntityBloomery bloomery, ColonyManager.SmeltKind want, ref string? wait)
    {
        var m = manager!;
        int blooms = System.Math.Max(1, m.CraftWant(colony, hut, want.Key) - m.SmeltStock(colony, hut, want, citizen));
        var pack = m.PackItems(citizen);
        var ore = pack.Where(want.IsOre).GroupBy(s => s.Collectible.Code).OrderByDescending(g => g.Sum(s => s.StackSize)).FirstOrDefault();
        int per = ore == null ? 20 : ColonyManager.PiecesPerIngot(want, ore.Key);
        // Ore already in the bloomery (it ran out of fuel last time) only needs fuel.
        if ((ore != null && ore.Sum(s => s.StackSize) >= per) || ColonyManager.BloomeryOre(bloomery) > 0)
        {
            kind = want;
            batch = blooms;
            if (m.PackCount(citizen, ColonyManager.IsBloomeryFuel) > 0) return Go(Work.Bloomery, bloomery.Pos, "smelter-filling-bloomery");
            var fuelChest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsBloomeryFuel(s.Itemstack)));
            if (fuelChest != null && !m.PackFull(citizen))
            {
                SetStatus("smelter-fetching-fuel");
                return FetchSupplies(fuelChest.Pos, ColonyManager.IsBloomeryFuel, m.Config.SmelterFuel * 2);
            }
            string code = m.CodeInWarehouse(colony, ColonyManager.IsBloomeryFuel) ?? m.Config.SmelterFuelCode;
            m.EnsureCraftRequest(colony, hut, code, m.Config.SmelterFuel * 4);
            wait = "smelter-needs-fuel";
            return null;
        }
        var oreChest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && want.IsOre(s.Itemstack)));
        if (oreChest != null && !m.PackFull(citizen))
        {
            SetStatus("smelter-fetching-ore");
            return FetchSupplies(oreChest.Pos, want.IsOre, per * blooms);
        }
        if (m.CodeInWarehouse(colony, want.IsOre) is { } oreCode)
        {
            int each = ColonyManager.PiecesPerIngot(want, new AssetLocation(oreCode));
            m.EnsureCraftRequest(colony, hut, oreCode, each * blooms);
            wait = "smelter-waiting-ore";
            return null;
        }
        wait ??= "smelter-needs-iron";
        return null;
    }

    /// <summary>
    /// Sets a tool mold of this type out by the fire, fetching it from the hut first, or asks couriers for one (the
    /// Potter keeps one of each). Null when there is none to be had, so the smelter goes on with its ingots.
    /// </summary>
    bool? PlanCastMold(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, string type)
    {
        var m = manager!;
        bool Fits(ItemStack s) => ColonyManager.IsToolMold(s) && ColonyManager.MoldType(s.Block) == type;
        if (m.PackItems(citizen).FirstOrDefault(Fits) is { } mold)
        {
            var spot = m.FindMoldSpot(colony, hut, mold.Block);
            if (spot == null) return null;
            placingTool = type;
            return Go(Work.PlaceMold, spot, "smelter-placing-toolmold");
        }
        var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && Fits(s.Itemstack)));
        if (chest != null && !m.PackFull(citizen))
        {
            SetStatus("smelter-fetching-molds");
            return FetchSupplies(chest.Pos, Fits, 1);
        }
        if (m.CodeInWarehouse(colony, Fits) is { } code) m.EnsureCraftRequest(colony, hut, code, 1);
        return null;
    }

    /// <summary>Sets a mold out, fetching one from the hut first, or asks for some. Null when there are none to be had.</summary>
    bool? PlanMolds(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, int want)
    {
        var m = manager!;
        if (m.PackCount(citizen, ColonyManager.IsIngotMold) > 0)
        {
            var block = m.PackItems(citizen).First(ColonyManager.IsIngotMold).Block;
            var spot = m.FindMoldSpot(colony, hut, block);
            if (spot == null)
            {
                SetStatus("smelter-no-mold-spot");
                return null;
            }
            return Go(Work.PlaceMold, spot, "smelter-placing-mold");
        }
        var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsIngotMold(s.Itemstack)));
        if (chest != null && !m.PackFull(citizen))
        {
            SetStatus("smelter-fetching-molds");
            return FetchSupplies(chest.Pos, ColonyManager.IsIngotMold, want);
        }
        if (m.CodeInWarehouse(colony, ColonyManager.IsIngotMold) is { } code) m.EnsureCraftRequest(colony, hut, code, want);
        return null;
    }

    bool PlanFuel(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, BlockEntityFirepit pit, int heat)
    {
        var m = manager!;
        if ((!pit.fuelSlot.Empty && ColonyManager.IsSmeltFuel(pit.fuelSlot.Itemstack, heat)) || m.PackCount(citizen, s => ColonyManager.IsSmeltFuel(s, heat)) > 0)
        {
            return Go(Work.Fuel, pit.Pos, "smelter-lighting");
        }
        return FetchFuel(colony, hut, storage, heat);
    }

    bool FetchFuel(Colony colony, Building hut, List<BlockEntityContainer> storage, int heat)
    {
        var m = manager!;
        ItemMatch hot = s => ColonyManager.IsSmeltFuel(s, heat);
        var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && hot(s.Itemstack)));
        if (chest != null)
        {
            SetStatus("smelter-fetching-fuel");
            return FetchSupplies(chest.Pos, hot, m.Config.SmelterFuel * 2);
        }
        string code = m.CodeInWarehouse(colony, hot) ?? m.Config.SmelterFuelCode;
        m.EnsureCraftRequest(colony, hut, code, m.Config.SmelterFuel * 4);
        SetStatus("smelter-needs-fuel");
        return false;
    }

    bool Go(Work next, BlockPos pos, string status)
    {
        work = next;
        SetStatus(status);
        return GoWork(pos);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        var ba = entity.World.BlockAccessor;
        switch (work)
        {
            case Work.PlaceMold:
                if (placingTool != null) m.PlaceToolMold(colony, citizen, target, placingTool);
                else m.PlaceMold(colony, citizen, target);
                placingTool = null;
                break;
            case Work.Collect:
                if (ba.GetBlockEntity(target) is BlockEntityIngotMold mold) m.TakeIngots(colony, citizen, mold);
                else if (ba.GetBlockEntity(target) is BlockEntityToolMold toolMold) m.TakeCast(colony, hut, citizen, toolMold);
                break;
            case Work.Load:
                if (ba.GetBlockEntity(target) is BlockEntityFirepit loadPit && kind != null) m.LoadCrucible(colony, citizen, loadPit, kind, batch);
                break;
            case Work.Fuel:
                if (ba.GetBlockEntity(target) is BlockEntityFirepit fuelPit) m.FuelSmelterFire(colony, citizen, fuelPit, m.SmelterHeatNeeded(fuelPit));
                break;
            case Work.Reheat:
                if (ba.GetBlockEntity(target) is BlockEntityFirepit reheatPit) m.ReheatCrucible(colony, citizen, reheatPit);
                break;
            case Work.Alloy:
                if (ba.GetBlockEntity(target) is BlockEntityFirepit alloyPit && kind != null) m.LoadAlloy(colony, citizen, alloyPit, kind, batch);
                break;
            case Work.Bloomery:
                if (ba.GetBlockEntity(target) is BlockEntityBloomery fillBloomery && kind != null) m.LoadBloomery(colony, citizen, fillBloomery, kind, batch);
                break;
            case Work.TakeBloom:
                if (ba.GetBlockEntity(target) is BlockEntityBloomery doneBloomery) m.TakeBloom(colony, hut, citizen, doneBloomery);
                break;
            case Work.Pour:
                if (ba.GetBlockEntity(target) is BlockEntityFirepit pourPit) m.PourCrucible(colony, hut, citizen, pourPit);
                break;
        }
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-smelter-" + work.ToString().ToLowerInvariant());
}
