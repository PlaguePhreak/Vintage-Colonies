using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The copper tool chain at level 0 (0.205.0, Aaron). Huts work from their hut block, so the colony reaches copper
/// before it has proper buildings:
/// 1. the Potter keeps fired tool molds (a pickaxe, a hammer and an anvil mold, config PotteryDefaultKeep);
/// 2. the Smelter sets a mold out by its fire pit and pours copper into it when the colony has no such tool
///    (config SmelterCasts), then takes the cast out and puts a head on a stick (pickaxe, hammer);
/// 3. the Blacksmith sets the anvil and a forge down by its hut block and smiths a saw while the colony has none,
///    so boards (and everything left to fill in later) can be made.
/// </summary>
public partial class ColonyManager
{
    public static bool IsToolMold(ItemStack? s) => s?.Block is BlockToolMold b && b.Code.Path.Contains("-fired-");

    /// <summary>What a tool mold casts: "pickaxe", "hammer", "anvil"...</summary>
    public static string? MoldType(Block? b) => b is BlockToolMold ? b.Variant?["tooltype"] : null;

    /// <summary>Metal units a tool mold takes when full (the block's "requiredUnits", 100 for most heads).</summary>
    static int MoldUnits(BlockEntityToolMold mold) => Math.Max(1, mold.Block?.Attributes?["requiredUnits"].AsInt(100) ?? 100);

    readonly Dictionary<(string Colony, string Type), (long Ms, bool Wanted)> castWanted = new();

    /// <summary>
    /// True when the colony should cast this tool: it is on config SmelterCasts and the colony has no metal one (nor a
    /// head waiting for its stick). An anvil is wanted while no Blacksmith's Hut has one by it and none is stored.
    /// </summary>
    public bool CastWanted(Colony colony, string type)
    {
        if (!Config.SmelterCastsTools || !(Config.SmelterCasts ?? Array.Empty<string>()).Contains(type)) return false;
        long now = sapi.World.ElapsedMilliseconds;
        if (castWanted.TryGetValue((colony.ColonyId, type), out var known) && now - known.Ms < 20_000) return known.Wanted;
        var stores = ColonyStores(colony).ToList();
        bool Has(ItemMatch match) => CountItems(stores, match) > 0 || colony.Citizens.Any(c => c.Alive && !c.Visitor && PackCount(c, match) > 0);
        bool wanted;
        // The hammer and the anvil are the Blacksmith's: only once the colony has a Blacksmith's Hut (a hut block will do).
        bool smithy = colony.Buildings.Any(b => b.Type == BuildingTypes.Blacksmith && !b.Removing);
        if (type is "anvil" or "hammer" && !smithy) wanted = false;
        else if (type == "anvil")
        {
            bool placed = colony.Buildings.Where(b => b.Type == BuildingTypes.Blacksmith && !b.Removing)
                .Any(b => AllNear<BlockEntityAnvil>(b.HutPos, Config.BlacksmithRadius).Count > 0);
            wanted = !placed && !Has(s => s.Block is BlockAnvil);
        }
        else
        {
            var tool = type switch
            {
                "pickaxe" => EnumTool.Pickaxe,
                "hammer" => EnumTool.Hammer,
                "axe" => EnumTool.Axe,
                "shovel" => EnumTool.Shovel,
                "hoe" => EnumTool.Hoe,
                "prospectingpick" => EnumTool.Pickaxe,
                _ => (EnumTool?)null
            };
            string head = (type == "prospectingpick" ? "prospectingpickhead" : type + "head") + "-";
            wanted = tool != null && !Has(s => s.Collectible.Tool == tool && s.Collectible.ToolTier >= 2
                    && (type != "prospectingpick") == !IsProspectingPick(s))
                && !Has(s => s.Collectible.Code.Path.StartsWith(head));
        }
        castWanted[(colony.ColonyId, type)] = (now, wanted);
        return wanted;
    }

    /// <summary>The first tool the colony should cast now, in config SmelterCasts order, or null.</summary>
    public string? NextCast(Colony colony) => (Config.SmelterCasts ?? Array.Empty<string>()).FirstOrDefault(t => CastWanted(colony, t));

    /// <summary>Tool molds set out by the smelter's fire pit (or its hut).</summary>
    public List<BlockEntityToolMold> SmelterToolMolds(Building hut)
    {
        var at = SmelterFirepit(hut)?.Pos ?? hut.HutPos;
        return AllNear<BlockEntityToolMold>(at, Config.SmelterMoldRadius);
    }

    /// <summary>A tool mold that takes metal now: not broken, not full, not set hard half way, and its tool still wanted.</summary>
    bool CastMoldOpen(Colony colony, BlockEntityToolMold mold) =>
        !mold.Shattered && !mold.IsFull && (mold.MetalContent == null || mold.FillLevel == 0 || mold.IsLiquid)
        && MoldType(mold.Block) is { } type && CastWanted(colony, type);

    /// <summary>Tool molds by the fire that take metal now.</summary>
    public List<BlockEntityToolMold> OpenCastMolds(Colony colony, Building hut) => SmelterToolMolds(hut).Where(m => CastMoldOpen(colony, m)).ToList();

    /// <summary>Ingots' worth of metal the open tool molds still take (an anvil takes 9).</summary>
    public int CastIngots(Colony colony, Building hut) =>
        (int)Math.Ceiling(OpenCastMolds(colony, hut).Sum(m => MoldUnits(m) - m.FillLevel) / 100.0);

    /// <summary>A tool mold with a hard, full cast in it to take out.</summary>
    public BlockEntityToolMold? CastReady(Building hut) => SmelterToolMolds(hut).FirstOrDefault(m => m.IsFull && m.IsHardened && !m.Shattered);

    /// <summary>The kind of metal to melt for the tool molds: copper first.</summary>
    public SmeltKind? CastMetal() => SmeltKinds().FirstOrDefault(k => k.Key == "game:ingot-copper" && k.Alloy == null && !k.Bloomery);

    /// <summary>Pours molten metal into the open tool molds first. Returns the units left in the crucible.</summary>
    int PourIntoToolMolds(Colony colony, Building hut, ItemStack metal, int units, float temp, ref int poured)
    {
        foreach (var mold in OpenCastMolds(colony, hut))
        {
            if (units <= 0) break;
            if (!mold.CanReceive(metal)) continue;
            int amount = Math.Min(units, MoldUnits(mold) - mold.FillLevel);
            if (amount <= 0) continue;
            int before = amount;
            mold.ReceiveLiquidMetal(metal, ref amount, temp);
            units -= before - amount;
            if (before - amount > 0) poured++;
            mold.MarkDirty(true);
        }
        return units;
    }

    /// <summary>
    /// Takes the cast out of a tool mold, as a player does: the mold is ready again (unless it breaks when filled), and
    /// a head goes on a stick at once when there is one in the smelter's pack, its hut or the warehouse.
    /// </summary>
    public void TakeCast(Colony colony, Building hut, CitizenRecord smelter, BlockEntityToolMold mold)
    {
        if (!mold.IsFull || !mold.IsHardened) return;
        var stacks = mold.GetStateAwareMoldedStacks() ?? Array.Empty<ItemStack>();
        if (stacks.Length == 0) return;
        if (mold.BreaksWhenFilled) sapi.World.BlockAccessor.SetBlock(0, mold.Pos);
        else
        {
            mold.MetalContent = null;
            mold.FillLevel = 0;
            mold.MarkDirty(true);
        }
        foreach (var cast in stacks)
        {
            var made = OnAStick(colony, hut, smelter, cast) ?? cast;
            if (AddToPack(colony, smelter, made) == 0 || made.StackSize > 0) PutBack(HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList(), made);
            AddStat(hut, "cast:" + made.Collectible.Code.Path);
            LogEvent(colony, "event-smelter-cast", smelter.Name, made.GetName());
        }
        foreach (var type in Config.SmelterCasts ?? Array.Empty<string>()) castWanted.Remove((colony.ColonyId, type));
        MarkDirty(colony);
    }

    /// <summary>A head put on a stick, as the game's grid recipe does (pickaxe head and stick make a pickaxe); null when it needs none or there is no stick.</summary>
    ItemStack? OnAStick(Colony colony, Building hut, CitizenRecord worker, ItemStack head)
    {
        var assemblies = sapi.World.GridRecipes.Where(r => r.Output?.ResolvedItemStack != null && r.ResolvedIngredients != null
            && r.ResolvedIngredients.Count(i => i != null) == 2).ToList();
        var (recipe, handle) = FindAssembly(assemblies, head);
        if (recipe == null || handle == null) return null;
        ItemMatch isHandle = s => handle.SatisfiesAsIngredient(s, false);
        int need = Math.Max(1, handle.Quantity);
        var stick = TakeFromPack(colony, worker, isHandle, need)
            ?? TakeItems(HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList(), isHandle, need)
            ?? TakeItems(WarehouseContainers(colony).Cast<BlockEntityContainer>().ToList(), isHandle, need);
        if (stick == null || stick.StackSize < need)
        {
            EnsureCraftRequest(colony, hut, "game:stick", need);
            if (stick != null) AddToPack(colony, worker, stick);
            return null;
        }
        var tool = recipe.Output!.ResolvedItemStack!.Clone();
        // The head's metal: the recipe's {metal} comes from the head's own code.
        if (head.Collectible.Variant?["metal"] is { } metal)
        {
            var code = tool.Collectible.Code.Path.Contains('-') ? tool.Collectible.CodeWithVariant("metal", metal) : null;
            if (code != null && sapi.World.GetItem(code) is { } item) tool = new ItemStack(item);
        }
        return tool;
    }

    /// <summary>Sets a fired tool mold of this type down at the spot, as a player does.</summary>
    public bool PlaceToolMold(Colony colony, CitizenRecord smelter, BlockPos pos, string type)
    {
        var mold = TakeFromPack(colony, smelter, s => IsToolMold(s) && MoldType(s.Block) == type, 1);
        if (mold?.Block == null) return false;
        var ba = sapi.World.BlockAccessor;
        if (!ba.GetBlock(pos).IsReplacableBy(mold.Block))
        {
            AddToPack(colony, smelter, mold);
            return false;
        }
        ba.SetBlock(mold.Block.BlockId, pos, mold);
        ba.MarkBlockDirty(pos);
        if (mold.Block.Sounds?.Place != null) sapi.World.PlaySoundAt(mold.Block.Sounds.Place, pos, -0.5);
        return true;
    }

    /// <summary>A spot beside a hut block to set a work block (an anvil, a forge) down: free, on solid ground, in the colony.</summary>
    public BlockPos? StationSpot(Colony colony, Building hut, Block block)
    {
        var ba = sapi.World.BlockAccessor;
        for (int ring = 2; ring <= 4; ring++)
        for (int dx = -ring; dx <= ring; dx++)
        for (int dz = -ring; dz <= ring; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
            for (int dy = 1; dy >= -1; dy--)
            {
                var pos = hut.HutPos.AddCopy(dx, dy, dz);
                if (!ba.GetBlock(pos).IsReplacableBy(block) || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) continue;
                if (!ba.GetBlock(pos.UpCopy()).IsReplacableBy(block)) continue;
                var below = pos.DownCopy();
                if (!ba.GetBlock(below).SideSolid[BlockFacing.UP.Index]) continue;
                if (ba.GetBlock(pos.UpCopy()) is BlockDoor || ba.GetBlock(pos) is BlockDoor) continue;
                if (GetAt(pos) != colony || !CanWorkAt(colony, pos) || NearBuildingOtherThan(colony, pos, hut)) continue;
                return pos;
            }
        }
        return null;
    }

    /// <summary>True when a spot is inside another building's space (a level 0 hut's own spot is free to use).</summary>
    bool NearBuildingOtherThan(Colony colony, BlockPos pos, Building hut) =>
        colony.Buildings.Any(b => b.BuildingId != hut.BuildingId && !b.Removing && Footprint(b).Contains(pos.X, pos.Y, pos.Z));

    /// <summary>
    /// A level 0 Blacksmith sets its own work blocks down (Aaron): an anvil or a forge from its pack, its hut storage or
    /// the warehouse goes beside the hut block. Returns true when it set one down; asks for a forge when there is none.
    /// </summary>
    public bool SetUpStation(Colony colony, Building hut, CitizenRecord smith, bool anvil)
    {
        if (!Config.BlacksmithSetsUpStations) return false;
        ItemMatch match = anvil ? s => s.Block is BlockAnvil : s => s.Block is BlockForge;
        var stack = TakeFromPack(colony, smith, match, 1)
            ?? TakeItems(HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList(), match, 1)
            ?? TakeItems(WarehouseContainers(colony).Cast<BlockEntityContainer>().ToList(), match, 1);
        if (stack?.Block is not { } block)
        {
            // The Stonemason makes a forge from cobblestone; the Smelter casts the anvil.
            if (!anvil) EnsureCraftRequest(colony, hut, "game:forge", 1);
            return false;
        }
        var spot = StationSpot(colony, hut, block);
        if (spot == null)
        {
            AddToPack(colony, smith, stack);
            return false;
        }
        var ba = sapi.World.BlockAccessor;
        ba.SetBlock(block.BlockId, spot, stack);
        ba.MarkBlockDirty(spot);
        if (block.Sounds?.Place != null) sapi.World.PlaySoundAt(block.Sounds.Place, spot, -0.5);
        Mod.Logger.Notification("[createacolony] {0} set a {1} down by {2} at {3}", smith.Name, block.Code, BuildingName(hut), spot);
        LogEvent(colony, "event-smith-station", smith.Name, stack.GetName(), BuildingName(hut));
        MarkDirty(colony);
        return true;
    }

    /// <summary>
    /// A level 0 Smelter lays its own fire pit beside the hut block from firewood (FirepitFirewood pieces, as a player
    /// builds one up), so it can smelt before its building is up. False when there is no firewood or no spot.
    /// </summary>
    public bool SetUpFirepit(Colony colony, Building hut, CitizenRecord smelter)
    {
        if (!Config.SmelterLaysFirepit) return false;
        var block = sapi.World.GetBlock(new AssetLocation("game:firepit-cold"));
        if (block == null || block.Id == 0) return false;
        int need = Math.Max(1, Config.FirepitFirewood);
        var spot = StationSpot(colony, hut, block);
        if (spot == null) return false;
        var hutStores = HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList();
        var warehouse = WarehouseContainers(colony).Cast<BlockEntityContainer>().ToList();
        if (PackCount(smelter, IsFirewood) + CountItems(hutStores, IsFirewood) + CountItems(warehouse, IsFirewood) < need)
        {
            EnsureCraftRequest(colony, hut, "game:firewood", need);
            return false;
        }
        int left = need;
        while (left > 0 && (TakeFromPack(colony, smelter, IsFirewood, left) ?? TakeItems(hutStores, IsFirewood, left) ?? TakeItems(warehouse, IsFirewood, left)) is { } part)
        {
            left -= part.StackSize;
        }
        var ba = sapi.World.BlockAccessor;
        ba.SetBlock(block.BlockId, spot);
        ba.MarkBlockDirty(spot);
        LogEvent(colony, "event-smith-station", smelter.Name, block.GetPlacedBlockName(sapi.World, spot), BuildingName(hut));
        MarkDirty(colony);
        return true;
    }

    /// <summary>One saw is wanted at the Blacksmith's Hut while the colony has none (Aaron: the saw opens up boards).</summary>
    public int SawWant(Colony colony, Building hut, string key) =>
        Config.BlacksmithMakesSaw && hut.Type == BuildingTypes.Blacksmith && key == "game:saw-copper" && !ColonyHasSaw(colony) ? 1 : 0;
}
