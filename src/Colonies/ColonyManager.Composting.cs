using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Composter (0.184.0). Its composter goes round the warehouse and the huts' storage for rotten food and bones,
/// grinds bones into bone meal at a quern by the hut (the game's grinding), and takes fertilizer to the Farmer's Huts
/// that are short of it. Rot and dry grass go on the hut's compost heap; once the heap has stood CompostDays, it turns a
/// block of poor soil into the game's compost soil, which farmers lay on their fields (ColonyManager.SoilUpgrade.cs).
/// </summary>
public partial class ColonyManager
{
    public static bool IsRot(ItemStack s) => s.Collectible.Code.Path == "rot";
    public static bool IsBone(ItemStack s) => s.Collectible.Code.Path == "bone";
    public static bool IsDryGrass(ItemStack s) => s.Collectible.Code.Path == "drygrass";

    /// <summary>Soil worse than compost (very low, low, medium), as a block in storage.</summary>
    public static bool IsPoorSoil(ItemStack s) => s.Block is { } b && b.Code.Path.StartsWith("soil-") && SoilRank(b) is >= 0 and < 3;

    /// <summary>Bones and the like a quern grinds into something that feeds the soil (bone meal).</summary>
    public static bool IsGrindableToFertilizer(ItemStack s) =>
        s.Collectible.GrindingProps?.GroundStack?.ResolvedItemstack is { } ground && IsFertilizer(ground);

    /// <summary>The game's compost soil block, or null if the game has none.</summary>
    Block? CompostSoil => sapi.World.GetBlock(new AssetLocation("game:soil-compost-none"));

    public BlockEntityQuern? ComposterQuern(Building hut) => NearHut<BlockEntityQuern>(hut, Config.BakeryWorkRadius);

    /// <summary>
    /// Storage with rot or bones for the composter to pick up: the warehouse and every hut's storage but its own, nearest
    /// first, within its Work area range. Homes and the Cook's kitchen are left alone.
    /// </summary>
    public BlockEntityContainer? ComposterPickup(Colony colony, Building hut, ItemMatch match, System.Func<BlockEntityContainer, bool> skip)
    {
        int r = GatherRadius(hut);
        var h = hut.HutPos;
        return WarehouseContainers(colony).Cast<BlockEntityContainer>()
            .Concat(colony.Buildings.Where(b => b != hut && IsWorkerHut(b.Type)).SelectMany(b => HutStorage(colony, b)))
            .Where(c => c.Pos.DistanceSqTo(h.X, h.Y, h.Z) <= r * r && !skip(c) && c.Inventory.Any(s => !s.Empty && match(s.Itemstack)))
            .OrderBy(c => c.Pos.DistanceSqTo(h.X, h.Y, h.Z))
            .FirstOrDefault();
    }

    /// <summary>A Farmer's Hut with too little fertilizer in its storage, nearest first.</summary>
    public BlockEntityContainer? FarmWantingFertilizer(Colony colony, Building hut)
    {
        var h = hut.HutPos;
        foreach (var farm in colony.Buildings.Where(b => b.Type == BuildingTypes.Farmer && b.Level > 0 && !b.Removing)
            .OrderBy(b => b.HutPos.DistanceSqTo(h.X, h.Y, h.Z)))
        {
            var chests = HutStorage(colony, farm).ToList();
            if (chests.Count == 0 || CountItems(chests, IsFertilizer) >= Config.ComposterFertilizerPerFarm) continue;
            var room = chests.FirstOrDefault(c => c.Inventory.Any(s => s.Empty));
            if (room != null) return room;
        }
        return null;
    }

    /// <summary>Puts the composter's fertilizer into a farm's storage, keeping the rot the heap still wants. Returns how much it left.</summary>
    public int DeliverFertilizer(Colony colony, CitizenRecord composter, BlockEntityContainer chest, Building hut)
    {
        int keepRot = Math.Max(0, Config.CompostHeapRot - hut.CompostRot);
        int given = 0;
        foreach (var stack in PackItems(composter).Where(IsFertilizer).ToList())
        {
            int spare = IsRot(stack) ? PackCount(composter, IsRot) - keepRot : stack.StackSize;
            if (spare <= 0) continue;
            var kind = stack.Collectible;
            var taken = TakeFromPack(colony, composter, s => s.Collectible == kind, Math.Min(spare, stack.StackSize));
            if (taken == null) continue;
            int had = taken.StackSize;
            PutItems(chest, taken);
            given += had - taken.StackSize;
            if (taken.StackSize > 0) AddToPack(colony, composter, taken);
        }
        if (given > 0) AddStat(hut, "fertilizer", given);
        return given;
    }

    /// <summary>Rot and dry grass from the composter onto the heap, up to what the heap holds.</summary>
    public void FeedHeap(Colony colony, Building hut, CitizenRecord composter)
    {
        int rotRoom = Math.Max(0, Config.CompostHeapRot - hut.CompostRot);
        int grassRoom = Math.Max(0, Config.CompostHeapGrass - hut.CompostGrass);
        bool wasEmpty = hut.CompostRot == 0;
        if (rotRoom > 0 && TakeFromPack(colony, composter, IsRot, rotRoom) is { } rot) hut.CompostRot += rot.StackSize;
        if (grassRoom > 0 && TakeFromPack(colony, composter, IsDryGrass, grassRoom) is { } grass) hut.CompostGrass += grass.StackSize;
        // The heap starts rotting down with its first load.
        if (wasEmpty && hut.CompostRot > 0) hut.CompostReadyDay = sapi.World.Calendar.TotalDays + Config.CompostDays;
        MarkDirty(colony);
    }

    /// <summary>True when the heap has stood long enough and has the rot and grass for one block of compost.</summary>
    public bool HeapReady(Building hut) => CompostSoil != null && hut.CompostRot >= Config.CompostRotPerSoil && hut.CompostGrass >= Config.CompostGrassPerSoil
        && sapi.World.Calendar.TotalDays >= hut.CompostReadyDay;

    /// <summary>True when the heap still wants rot or dry grass.</summary>
    public bool HeapWants(Building hut, bool rot) => rot ? hut.CompostRot < Config.CompostHeapRot : hut.CompostGrass < Config.CompostHeapGrass;

    /// <summary>Turns one block of the composter's poor soil into compost soil with the heap's rot and grass.</summary>
    public bool MakeCompost(Colony colony, Building hut, CitizenRecord composter)
    {
        var compost = CompostSoil;
        if (compost == null || !HeapReady(hut)) return false;
        var soil = TakeFromPack(colony, composter, IsPoorSoil, 1);
        if (soil == null) return false;
        hut.CompostRot -= Config.CompostRotPerSoil;
        hut.CompostGrass -= Config.CompostGrassPerSoil;
        StoreHarvest(colony, hut, composter, new[] { new ItemStack(compost) }, hut.HutPos);
        AddStat(hut, "compost");
        MarkDirty(colony);
        return true;
    }

    /// <summary>Grinds the composter's bones into bone meal (what the quern makes of them).</summary>
    public int GrindFertilizer(Colony colony, Building hut, CitizenRecord composter)
    {
        var bones = TakeFromPack(colony, composter, IsGrindableToFertilizer, Config.BakerGrindBatch);
        if (bones?.Collectible.GrindingProps?.GroundStack?.ResolvedItemstack is not { } ground) return 0;
        var meal = ground.Clone();
        meal.StackSize = Math.Max(1, ground.StackSize) * bones.StackSize;
        StoreHarvest(colony, hut, composter, new[] { meal }, hut.HutPos);
        return meal.StackSize;
    }

    /// <summary>At a Composter's Hut poor soil, dry grass and bones stay; compost and fertilizer go to the warehouse.</summary>
    ItemMatch ComposterHutJunk(Building hut) => s => !IsPoorSoil(s) && !IsDryGrass(s) && !IsGrindableToFertilizer(s) && !(IsRot(s) && HeapWants(hut, true));

    public List<string> ComposterInfo(Building hut)
    {
        var lines = new List<string> { Lang.Get("createacolony:composter-heap", hut.CompostRot, Config.CompostHeapRot, hut.CompostGrass, Config.CompostHeapGrass) };
        if (CompostSoil == null) lines.Add(Lang.Get("createacolony:composter-no-compost"));
        else if (hut.CompostRot > 0 && sapi.World.Calendar.TotalDays < hut.CompostReadyDay)
            lines.Add(Lang.Get("createacolony:composter-ready-in", Math.Ceiling(hut.CompostReadyDay - sapi.World.Calendar.TotalDays)));
        if (ComposterQuern(hut) == null) lines.Add(Lang.Get("createacolony:composter-no-quern"));
        return lines;
    }
}
