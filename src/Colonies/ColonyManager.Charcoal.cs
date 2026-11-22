using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The Charcoal Burner (0.185.0): its burner makes charcoal the way a player does, in the game's own charcoal pit.
/// Each pit is a spot of soft ground near the hut. The burner digs it out (CharcoalPilesPerPit blocks deep), stacks a
/// firewood pile in each block, puts the game's charcoal pit block on top, seals it with the soil it dug and lights it.
/// The game burns it (about 18 hours) and turns the firewood into charcoal piles. The burner then opens the pit, takes
/// the charcoal, and fills the hole back in with what came out of it. More pits as the hut grows (CharcoalPitsByLevel),
/// and it stops once the colony has CharcoalKeep charcoal plus what other huts asked for.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The block the game lights a charcoal pit with.</summary>
    Block? CharcoalPitBlock => sapi.World.GetBlock(new AssetLocation("game:charcoalpit"));
    Block? GroundStorageBlock => sapi.World.GetBlock(new AssetLocation("game:groundstorage"));

    static bool IsCharcoal(ItemStack s) => s.Collectible.Code.Path == "charcoal";

    /// <summary>Ground a pit can be dug in: soil, gravel and sand.</summary>
    static bool IsDiggable(Block block) => block.Code != null && block.BlockMaterial is EnumBlockMaterial.Soil or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand;

    /// <summary>How many pits the hut has at its level.</summary>
    public int CharcoalPitCount(Building hut)
    {
        var byLevel = Config.CharcoalPitsByLevel;
        if (byLevel.Length == 0) return 1;
        return Math.Max(1, byLevel[Math.Clamp(hut.Level, 0, byLevel.Length - 1)]);
    }

    /// <summary>How much charcoal the colony has: the warehouse, the hut and the burner.</summary>
    public int CharcoalStock(Colony colony, Building hut, CitizenRecord? burner)
    {
        int count = CountItems(WarehouseContainers(colony), IsCharcoal) + CountItems(HutStorage(colony, hut), IsCharcoal);
        if (burner != null) count += PackCount(burner, IsCharcoal);
        return count;
    }

    /// <summary>How much charcoal the burner works towards: the hut's keep plus what huts asked couriers for.</summary>
    public int CharcoalWant(Colony colony) =>
        Config.CharcoalKeep + colony.Requests.Where(r => r.Remaining > 0 && r.ItemCode.EndsWith(":charcoal")).Sum(r => r.Remaining);

    /// <summary>Firewood one pit takes: a full pile in each block of it.</summary>
    public int FirewoodPerPit()
    {
        var firewood = sapi.World.GetItem(new AssetLocation("game:firewood"));
        int pile = firewood?.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps?.StackingCapacity ?? 32;
        return Math.Max(1, Math.Min(pile, Config.CharcoalFirewoodPerPile)) * Math.Max(1, Config.CharcoalPilesPerPit);
    }

    /// <summary>The hut's pits, finding new spots when it has room for more (after an upgrade).</summary>
    public List<CharcoalPit> CharcoalPitsOf(Colony colony, Building hut)
    {
        int want = CharcoalPitCount(hut);
        if (hut.CharcoalPits.Count < want)
        {
            foreach (var spot in FindPitSpots(colony, hut, want - hut.CharcoalPits.Count))
            {
                hut.CharcoalPits.Add(new CharcoalPit { Pos = spot });
                MarkDirty(colony);
            }
        }
        return hut.CharcoalPits;
    }

    /// <summary>
    /// Spots of soft ground near the hut for new pits, nearest first: outside every building, far enough from the hut
    /// and from each other that solid ground stays between them, and with nothing that burns beside the hole.
    /// </summary>
    List<BlockPos> FindPitSpots(Colony colony, Building hut, int count)
    {
        var found = new List<BlockPos>();
        var ba = sapi.World.BlockAccessor;
        var boxes = colony.Buildings.Where(b => b != hut).Select(b => BuildingBox(b)).ToList();
        int range = Math.Max(Config.CharcoalPitMinDistance + 1, Config.CharcoalPitRange);
        var candidates = new List<(BlockPos Pos, int Dist)>();
        for (int dx = -range; dx <= range; dx++)
        {
            for (int dz = -range; dz <= range; dz++)
            {
                int dist = dx * dx + dz * dz;
                if (dist < Config.CharcoalPitMinDistance * Config.CharcoalPitMinDistance || dist > range * range) continue;
                candidates.Add((new BlockPos(hut.HutPos.X + dx, hut.HutPos.Y, hut.HutPos.Z + dz, hut.HutPos.dimension), dist));
            }
        }
        foreach (var (column, _) in candidates.OrderBy(c => c.Dist))
        {
            if (found.Count >= count) break;
            var top = SurfaceAt(column);
            if (top == null) continue;
            if (hut.CharcoalPits.Any(p => Near(p.Pos, top)) || found.Any(p => Near(p, top))) continue;
            if (boxes.Any(b => InBox(top, b.Min.AddCopy(-1, -4, -1), b.Max.AddCopy(1, 2, 1)))) continue;
            if (!CanWorkAt(colony, top) || !GoodPitSpot(ba, top)) continue;
            found.Add(top);
        }
        return found;

        static bool Near(BlockPos a, BlockPos b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Z - b.Z) <= 2;
    }

    /// <summary>The highest solid block of a column near the hut's height, with open air above it; null for none.</summary>
    BlockPos? SurfaceAt(BlockPos column)
    {
        var ba = sapi.World.BlockAccessor;
        var pos = column.UpCopy(6);
        for (int i = 0; i < 14; i++, pos.Down())
        {
            var block = ba.GetBlock(pos);
            if (block.Id == 0 || block.Replaceable >= 6000 || block.BlockMaterial == EnumBlockMaterial.Plant) continue;
            if (ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) return null;
            return block.SideSolid[BlockFacing.UP.Index] ? pos.Copy() : null;
        }
        return null;
    }

    /// <summary>
    /// True when a pit dug down from top would be sealed as the game wants: every block of it soft ground, solid ground
    /// all round and below, nothing that burns next to it, and room above for the cover.
    /// </summary>
    bool GoodPitSpot(IBlockAccessor ba, BlockPos top)
    {
        int depth = Math.Max(1, Config.CharcoalPilesPerPit);
        var above = ba.GetBlock(top.UpCopy());
        if (!(above.Id == 0 || above.Replaceable >= 6000)) return false;
        if (ba.GetBlock(top.UpCopy(), BlockLayersAccess.Fluid).Id != 0) return false;
        for (int d = 0; d <= depth; d++)
        {
            var pos = top.DownCopy(d);
            if (!IsDiggable(ba.GetBlock(pos))) return false;
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                var side = pos.AddCopy(face);
                var block = ba.GetBlock(side);
                if (block.CombustibleProps != null || block.GetLiquidBarrierHeightOnSide(face.Opposite, side) != 1f) return false;
                if (ba.GetBlockEntity(side) != null) return false;
            }
        }
        var below = ba.GetBlock(top.DownCopy(depth + 1));
        return below.CombustibleProps == null && below.SideSolid[BlockFacing.UP.Index];
    }

    /// <summary>True once the game has turned the pit's firewood into charcoal (or the pit went out some other way).</summary>
    public bool CharcoalPitDone(CharcoalPit pit)
    {
        if (pit.Stage != 1) return false;
        var ba = sapi.World.BlockAccessor;
        return ba.GetBlock(pit.Pos).Code?.Path != "charcoalpit";
    }

    /// <summary>Hours until a burning pit is done, or 0.</summary>
    public double CharcoalPitHoursLeft(CharcoalPit pit) =>
        pit.Stage != 1 ? 0 : Math.Max(0, pit.LitHours + Config.CharcoalBurnHours - sapi.World.Calendar.TotalHours);

    /// <summary>
    /// Digs the pit, stacks the burner's firewood in it, puts the charcoal pit block on top, seals it with the soil
    /// that came out and lights it. False, changing nothing, when the spot is no longer good or wood is short.
    /// </summary>
    public bool BuildCharcoalPit(Colony colony, Building hut, CitizenRecord burner, CharcoalPit pit)
    {
        var ba = sapi.World.BlockAccessor;
        var pitBlock = CharcoalPitBlock;
        var pileBlock = GroundStorageBlock;
        var firewood = sapi.World.GetItem(new AssetLocation("game:firewood"));
        if (pitBlock == null || pileBlock == null || firewood == null || pit.Stage != 0) return false;
        int piles = Math.Max(1, Config.CharcoalPilesPerPit);
        int perPile = FirewoodPerPit() / piles;
        if (PackCount(burner, IsFirewood) < perPile * piles) return false;
        if (!GoodPitSpot(ba, pit.Pos))
        {
            // The ground changed (someone built here): find another spot later.
            hut.CharcoalPits.Remove(pit);
            MarkDirty(colony);
            return false;
        }

        pit.Dug.Clear();
        for (int d = 0; d <= piles; d++)
        {
            var pos = pit.Pos.DownCopy(d);
            pit.Dug.Add(ba.GetBlock(pos).Code.ToString());
            ba.SetBlock(0, pos);
        }
        // Firewood piles from the bottom up, then the pit block on top of them.
        for (int d = piles; d >= 1; d--)
        {
            var pos = pit.Pos.DownCopy(d);
            var wood = TakeFromPack(colony, burner, IsFirewood, perPile);
            if (wood == null) break;
            ba.SetBlock(pileBlock.BlockId, pos);
            if (ba.GetBlockEntity(pos) is BlockEntityGroundStorage pile)
            {
                pile.Inventory[0].Itemstack = wood;
                pile.Inventory[0].MarkDirty();
                pile.DetermineStorageProperties(null);
                pile.MarkDirty(true);
            }
        }
        ba.SetBlock(pitBlock.BlockId, pit.Pos);
        // Sealed with the top block that came out (soil), as a player covers a pit.
        pit.Cover = pit.Dug[0];
        var cover = sapi.World.GetBlock(new AssetLocation(pit.Cover));
        if (cover != null && (cover.BlockMaterial is EnumBlockMaterial.Soil or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand))
        {
            ba.SetBlock(cover.BlockId, pit.Pos.UpCopy());
        }
        ClearAroundFire(colony, burner, pit.Pos.UpCopy());
        if (ba.GetBlockEntity(pit.Pos) is BlockEntityCharcoalPit lit)
        {
            lit.Init(null);
            lit.IgniteNow();
        }
        pit.Stage = 1;
        pit.LitHours = sapi.World.Calendar.TotalHours;
        sapi.World.PlaySoundAt(new AssetLocation("game:sounds/block/dirt1"), pit.Pos.X + 0.5, pit.Pos.Y + 1, pit.Pos.Z + 0.5);
        LogEvent(colony, "event-charcoal-lit", burner.Name);
        MarkDirty(colony);
        return true;
    }

    /// <summary>
    /// Opens a burnt-out pit: takes off the cover, takes the charcoal (into the burner's inventory or the hut), and fills
    /// the hole back in with the blocks that came out of it. Returns how much charcoal it took.
    /// </summary>
    public int OpenCharcoalPit(Colony colony, Building hut, CitizenRecord burner, CharcoalPit pit)
    {
        var ba = sapi.World.BlockAccessor;
        var made = new List<ItemStack>();
        var coverPos = pit.Pos.UpCopy();
        if (pit.Cover.Length > 0 && ba.GetBlock(coverPos).Code?.ToString() == pit.Cover) ba.SetBlock(0, coverPos);
        for (int d = 0; d < Math.Max(1, pit.Dug.Count); d++)
        {
            var pos = pit.Pos.DownCopy(d);
            var block = ba.GetBlock(pos);
            if (block.Id == 0) continue;
            string path = block.Code?.Path ?? "";
            if (path.StartsWith("charcoalpile") || path == "charcoalpit")
            {
                made.AddRange(DropsOf(block, pos));
                ba.SetBlock(0, pos);
            }
            else if (ba.GetBlockEntity(pos) is BlockEntityGroundStorage pile)
            {
                // Wood that did not burn (the pit went out): it comes back.
                foreach (var slot in pile.Inventory) if (!slot.Empty) made.Add(slot.Itemstack.Clone());
                ba.SetBlock(0, pos);
            }
        }
        // Fill the hole back in, bottom first.
        for (int d = pit.Dug.Count - 1; d >= 0; d--)
        {
            var pos = pit.Pos.DownCopy(d);
            if (ba.GetBlock(pos).Id != 0) continue;
            var block = sapi.World.GetBlock(new AssetLocation(pit.Dug[d]));
            if (block != null) ba.SetBlock(block.BlockId, pos);
        }
        int charcoal = made.Where(IsCharcoal).Sum(s => s.StackSize);
        StoreHarvest(colony, hut, burner, made, pit.Pos.UpCopy());
        pit.Stage = 0;
        pit.Dug.Clear();
        pit.Cover = "";
        if (charcoal > 0)
        {
            AddStat(hut, "charcoal", charcoal);
            LogEvent(colony, "event-charcoal-done", burner.Name, charcoal.ToString());
        }
        sapi.World.PlaySoundAt(new AssetLocation("game:sounds/block/dirt1"), pit.Pos.X + 0.5, pit.Pos.Y + 1, pit.Pos.Z + 0.5);
        MarkDirty(colony);
        return charcoal;
    }

    /// <summary>The hut window's lines: pits and charcoal.</summary>
    public List<string> CharcoalInfo(Colony colony, Building hut)
    {
        var pits = CharcoalPitsOf(colony, hut);
        int burning = pits.Count(p => p.Stage == 1);
        var lines = new List<string>
        {
            Lang.Get("createacolony:charcoal-pits", pits.Count, CharcoalPitCount(hut), burning),
            Lang.Get("createacolony:charcoal-stock", CharcoalStock(colony, hut, WorkerAt(colony, hut, Jobs.CharcoalBurner)), CharcoalWant(colony))
        };
        if (pits.Count < CharcoalPitCount(hut)) lines.Add(Lang.Get("createacolony:charcoal-no-spot"));
        var next = pits.Where(p => p.Stage == 1).OrderBy(CharcoalPitHoursLeft).FirstOrDefault();
        if (next != null) lines.Add(Lang.Get("createacolony:charcoal-next", Math.Ceiling(CharcoalPitHoursLeft(next))));
        return lines;
    }
}
