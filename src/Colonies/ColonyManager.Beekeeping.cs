using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The Apiary. Its beekeeper looks after the beehives near the hut (the Work area range), up to
/// config ApiaryHivesPerLevel for each hut level and ApiaryMaxHives in all, nearest first:
/// - it harvests full skeps the game's way (the bees are lost, the empty skep goes back in its place), but never
///   the last colony while the hut's Keep the last colony switch is on;
/// - from config ApiarySwarmLevel it sets out empty skeps near its colonies, so their swarms move in;
/// - from config ApiaryWildLevel it walks out to wild hives the Gatherer marked, sets an empty skep by each and,
///   once bees moved in, carries the skep home;
/// - with the From Golden Combs mod (optional, never bundled) and from config ApiaryCeramicLevel it scrapes full
///   honey pots on ceramic hives with its knife, puts honey pots on brood pots that have none, and moves a spare
///   skep colony into an empty brood pot. From config ApiaryLangstrothLevel it also works Langstroth hives
///   (ColonyManager.Langstroth.cs): it takes out full frames, scrapes them at the Frame Rack, repairs and relines
///   frames, puts lined frames back and fills empty brood boxes with bees. Its blocks are only reached by name and reflection, so a change in that
///   mod makes the beekeeper skip those hives instead of failing.
/// Honeycomb and wax go to the hut, where couriers pick them up.
/// </summary>
public partial class ColonyManager
{
    public const string GoldenCombsModId = "fromgoldencombs";

    /// <summary>A hive near an Apiary: a skep (empty or with bees) or a From Golden Combs ceramic brood pot.</summary>
    public class ApiaryHive
    {
        public BlockPos Pos = null!;
        public bool Skep;
        /// <summary>A skep or ceramic hive with bees in it.</summary>
        public bool Colony;
        /// <summary>Full of honey: a skep to harvest, or a honey pot to scrape.</summary>
        public bool Full;
        /// <summary>A ceramic brood pot without a honey pot on top.</summary>
        public bool NoTop;
        /// <summary>A From Golden Combs Langstroth hive (the bottom of its stack), or a brood box on its own.</summary>
        public bool Langstroth;
        /// <summary>A brood box set down on its own, not in a hive.</summary>
        public bool LoneBrood;
        /// <summary>A Langstroth hive or brood box whose brood box has no bees yet.</summary>
        public bool EmptyBrood;
        /// <summary>Frames in a Langstroth hive: full ones, lined ones worn down to the repair point, empty (unlined) ones, and free frame spots in its supers.</summary>
        public int FullFrames, WornFrames, EmptyFrames, Room;
    }

    /// <summary>What a beekeeper found around its Apiary, worked out at most every few seconds.</summary>
    public class ApiaryScan
    {
        public List<ApiaryHive> Hives = new();
        public long Until;
        /// <summary>The From Golden Combs Frame Rack nearest the hut, or null.</summary>
        public BlockPos? Rack;
        /// <summary>Brood boxes set down on their own, which the beekeeper can fill with bees.</summary>
        public List<ApiaryHive> Broods = new();
        public IEnumerable<ApiaryHive> Colonies => Hives.Where(h => h.Colony);
    }

    readonly Dictionary<int, ApiaryScan> apiaryScans = new();

    bool? goldenCombs;
    /// <summary>True when the From Golden Combs mod is installed.</summary>
    public bool GoldenCombs => goldenCombs ??= sapi.ModLoader.IsModEnabled(GoldenCombsModId);

    /// <summary>The Apiary's level for its work: 1 for a hut with nothing built yet.</summary>
    public static int ApiaryLevel(Building hut) => Math.Max(1, hut.Level);

    /// <summary>How many hives the beekeeper looks after at this Apiary.</summary>
    public int ApiaryHiveLimit(Building hut) => Math.Max(1, Math.Min(Config.ApiaryMaxHives, Config.ApiaryHivesPerLevel * ApiaryLevel(hut)));

    public bool ApiaryCatchesSwarms(Building hut) => ApiaryLevel(hut) >= Config.ApiarySwarmLevel;
    public bool ApiaryWorksCeramic(Building hut) => GoldenCombs && ApiaryLevel(hut) >= Config.ApiaryCeramicLevel;
    public bool ApiaryBringsWild(Building hut) => ApiaryLevel(hut) >= Config.ApiaryWildLevel;
    public bool ApiaryWorksLangstroth(Building hut) => GoldenCombs && ApiaryLevel(hut) >= Config.ApiaryLangstrothLevel;

    // ---- Skeps (the game's own) ----

    public static bool IsSkepBlock(Block? b) => b is BlockSkep;
    public static bool IsEmptySkepBlock(Block? b) => b is BlockSkep s && s.IsEmpty();
    public static bool IsColonySkepBlock(Block? b) => b is BlockSkep s && !s.IsEmpty();

    public static bool IsSkepStack(ItemStack? s) => s?.Block is BlockSkep;
    public static bool IsEmptySkepStack(ItemStack? s) => s?.Block is BlockSkep b && b.IsEmpty();
    public static bool IsColonySkepStack(ItemStack? s) => s?.Block is BlockSkep b && !b.IsEmpty();

    /// <summary>What a beekeeper keeps at the Apiary and in its pack: skeps, and From Golden Combs hive pots and honey pots.</summary>
    public static bool IsHivePart(ItemStack? s)
    {
        if (s?.Collectible?.Code == null) return false;
        if (IsSkepStack(s)) return true;
        if (s.Collectible.Code.Domain != GoldenCombsModId) return false;
        string first = s.Collectible.FirstCodePart();
        return first is "hivetop" or "ceramicbroodpot" or "beeframe" or "frameliner" or "waxedflaxtwine";
    }

    /// <summary>True when the skep at the spot is full of honey. Read from the saved hive, so it works with From Golden Combs too.</summary>
    bool SkepFull(BlockPos pos)
    {
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBeehive be) return false;
        if (be.Harvestable) return true;
        try
        {
            var tree = new TreeAttribute();
            be.ToTreeAttributes(tree);
            return tree.GetInt("harvestable") > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Ceramic hives (From Golden Combs) ----

    /// <summary>True for a From Golden Combs ceramic brood pot's block entity.</summary>
    static bool IsBroodPot(BlockEntity? be) => be is BlockEntityContainer && be.GetType().Name == "BECeramicBroodPot";

    static readonly Dictionary<Type, FieldInfo?> activeHiveFields = new();

    static FieldInfo? ActiveField(BlockEntity be)
    {
        var type = be.GetType();
        if (!activeHiveFields.TryGetValue(type, out var field))
        {
            field = type.GetField("isActiveHive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.FieldType != typeof(bool)) field = null;
            activeHiveFields[type] = field;
        }
        return field;
    }

    static bool PotHasBees(BlockEntity be) => ActiveField(be)?.GetValue(be) is true;

    /// <summary>The honey pot sitting on a brood pot, or null.</summary>
    static ItemStack? PotTop(BlockEntity be) => be is BlockEntityContainer c && c.Inventory.Count > 0 ? c.Inventory[0].Itemstack : null;

    static bool PotFull(BlockEntity be) => PotTop(be)?.Block?.Variant["type"] == "harvestable";

    /// <summary>A honey pot the beekeeper can put on a brood pot: fired, not raw clay.</summary>
    public static bool IsHoneyPot(ItemStack? s) =>
        s?.Block != null && s.Collectible.Code.Domain == GoldenCombsModId && s.Collectible.FirstCodePart() == "hivetop" && s.Block.Variant["type"] is not ("raw" or null);

    /// <summary>A number from the From Golden Combs server settings, or the fallback when they cannot be read.</summary>
    int GoldenCombsSetting(string name, int fallback)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "FromGoldenCombs") continue;
                var type = asm.GetTypes().FirstOrDefault(t => t.Name == "FGCServerConfig");
                var current = type?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (current?.GetType().GetProperty(name)?.GetValue(current) is int value) return value;
                if (current?.GetType().GetField(name)?.GetValue(current) is int field) return field;
            }
        }
        catch (Exception)
        {
            // An older or newer From Golden Combs: use the fallback.
        }
        return fallback;
    }

    // ---- Looking around ----

    /// <summary>The hives the beekeeper looks after: every skep and ceramic hive in the Work area, nearest first, up to the hut's limit.</summary>
    public ApiaryScan ScanApiary(Building hut, bool fresh = false)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (!fresh && apiaryScans.TryGetValue(hut.BuildingId, out var known) && now < known.Until) return known;
        var scan = new ApiaryScan { Until = now + 5000 };
        int r = GatherRadius(hut);
        var ba = sapi.World.BlockAccessor;
        var c = hut.HutPos;
        var found = new List<ApiaryHive>();
        bool ceramic = GoldenCombs;
        ba.WalkBlocks(new BlockPos(c.X - r, c.Y - 6, c.Z - r), new BlockPos(c.X + r, c.Y + 6, c.Z + r), (block, x, y, z) =>
        {
            if (block is BlockSkep skep)
            {
                var pos = new BlockPos(x, y, z);
                found.Add(new ApiaryHive { Pos = pos, Skep = true, Colony = !skep.IsEmpty() });
            }
            else if (ceramic && block.Code?.Domain == GoldenCombsModId)
            {
                switch (block.FirstCodePart())
                {
                    case "ceramicbroodpot":
                        found.Add(new ApiaryHive { Pos = new BlockPos(x, y, z) });
                        break;
                    case "langstrothstack":
                        // One hive for each stack: the bottom one holds the hive.
                        if (ba.GetBlock(new BlockPos(x, y - 1, z)).FirstCodePart() != "langstrothstack") found.Add(new ApiaryHive { Pos = new BlockPos(x, y, z), Langstroth = true });
                        break;
                    case "langstrothbrood":
                        scan.Broods.Add(new ApiaryHive { Pos = new BlockPos(x, y, z), Langstroth = true, LoneBrood = true, EmptyBrood = block.Variant["populated"] == "empty" });
                        break;
                    case "framerack":
                        var rack = new BlockPos(x, y, z);
                        if (scan.Rack == null || rack.DistanceSqTo(c.X, c.Y, c.Z) < scan.Rack.DistanceSqTo(c.X, c.Y, c.Z)) scan.Rack = rack;
                        break;
                }
            }
        }, true);
        found = found.Where(h => !h.Langstroth || LangstrothStacks(h.Pos).Count > 0).ToList();
        foreach (var hive in found.Where(h => h.Langstroth)) ReadLangstroth(hive);
        scan.Broods = scan.Broods.Where(b => b.EmptyBrood).OrderBy(h => h.Pos.DistanceSqTo(c.X, c.Y, c.Z)).ToList();
        found = found.Where(h => h.Skep || h.Langstroth || IsBroodPot(ba.GetBlockEntity(h.Pos))).ToList();
        foreach (var hive in found)
        {
            if (hive.Langstroth) continue;
            if (hive.Skep)
            {
                if (hive.Colony) hive.Full = SkepFull(hive.Pos);
                continue;
            }
            if (ba.GetBlockEntity(hive.Pos) is not { } be || !IsBroodPot(be)) continue;
            hive.Colony = PotHasBees(be);
            hive.NoTop = PotTop(be) == null;
            hive.Full = PotFull(be);
        }
        scan.Hives = found.OrderBy(h => h.Pos.DistanceSqTo(c.X, c.Y, c.Z)).Take(ApiaryHiveLimit(hut)).ToList();
        apiaryScans[hut.BuildingId] = scan;
        return scan;
    }

    /// <summary>Forgets what the beekeeper saw, after it changed a hive.</summary>
    public void ForgetApiaryScan(Building hut) => apiaryScans.Remove(hut.BuildingId);

    /// <summary>All the hives the Apiary has: those it looks after, plus the skeps it set out by wild hives.</summary>
    public int ApiaryHiveCount(Building hut) => ScanApiary(hut).Hives.Count + hut.WildSkeps.Count;

    /// <summary>True when the beekeeper may take the bees of this skep: the hut does not keep the last colony, or there is another.</summary>
    public bool MayHarvestSkep(Building hut, ApiaryScan scan) => !hut.KeepLastColony || scan.Colonies.Count() >= 2;

    /// <summary>
    /// A spot for a skep near a place: air (or loose plants) over solid ground that is not part of a tree, from
    /// minR to maxR blocks away sideways and from down to up blocks up or down, at least 2 blocks from any other
    /// hive. Spots nearest the target height come first. Null when there is none.
    /// </summary>
    public BlockPos? SkepSpot(Colony colony, BlockPos near, int minR, int maxR, int down, int up)
    {
        var ba = sapi.World.BlockAccessor;
        var best = (Pos: (BlockPos?)null, Score: double.MaxValue);
        var rand = sapi.World.Rand;
        for (int dx = -maxR; dx <= maxR; dx++)
        {
            for (int dz = -maxR; dz <= maxR; dz++)
            {
                int side = Math.Max(Math.Abs(dx), Math.Abs(dz));
                if (side < minR || side > maxR) continue;
                for (int dy = up; dy >= -down; dy--)
                {
                    var pos = new BlockPos(near.X + dx, near.Y + dy, near.Z + dz);
                    var here = ba.GetBlock(pos);
                    if (here.Id != 0 && here.Replaceable < 6000) continue;
                    if (here.Id != 0 && here.IsLiquid()) continue;
                    var below = ba.GetBlock(pos.DownCopy());
                    if (below.Id == 0 || !below.SideSolid[BlockFacing.UP.Index]) continue;
                    if (below.BlockMaterial is EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood || below is BlockSkep || below is BlockLog) continue;
                    if (ba.GetBlock(pos.UpCopy()).Id != 0 && ba.GetBlock(pos.UpCopy()).Replaceable < 6000) continue;
                    if (!CanWorkAt(colony, pos) || HiveWithin(pos, 2)) break;
                    double score = Math.Abs(dy) * 2 + side + rand.NextDouble();
                    if (score < best.Score) best = (pos, score);
                    break;
                }
            }
        }
        return best.Pos;
    }

    /// <summary>True when a skep, wild hive or ceramic hive is within this many blocks of the spot.</summary>
    bool HiveWithin(BlockPos pos, int r)
    {
        var ba = sapi.World.BlockAccessor;
        bool any = false;
        ba.WalkBlocks(pos.AddCopy(-r, -r, -r), pos.AddCopy(r, r, r), (block, _, _, _) =>
        {
            if (block is BlockSkep or BlockBeehive || block.Code?.Domain == GoldenCombsModId && block.FirstCodePart() is "ceramicbroodpot" or "langstrothstack" or "langstrothbrood" or "langstrothbase") any = true;
        }, true);
        return any;
    }

    /// <summary>True when an empty skep sits where the hive's swarm can find it (the game looks 8 blocks out, 7 down and 4 up).</summary>
    public bool EmptySkepNear(BlockPos hive)
    {
        bool any = false;
        sapi.World.BlockAccessor.WalkBlocks(hive.AddCopy(-8, -7, -8), hive.AddCopy(7, 4, 7), (block, _, _, _) =>
        {
            if (IsEmptySkepBlock(block)) any = true;
        }, true);
        return any;
    }

    // ---- Work ----

    /// <summary>Sets a skep the beekeeper carries down at the spot. False if the spot is taken or it carries none.</summary>
    public bool SetSkep(Colony colony, CitizenRecord citizen, BlockPos pos, bool withBees)
    {
        var ba = sapi.World.BlockAccessor;
        var here = ba.GetBlock(pos);
        if (here.Id != 0 && here.Replaceable < 6000) return false;
        var stack = TakeFromPack(colony, citizen, withBees ? IsColonySkepStack : IsEmptySkepStack, 1);
        if (stack?.Block is not BlockSkep skep) return false;
        var block = sapi.World.GetBlock(skep.CodeWithVariant("side", "east")) ?? skep;
        try
        {
            ba.SetBlock(block.BlockId, pos, stack);
            // A full skep stays full (the game marks it from the stack).
            if (withBees && stack.Attributes.GetBool("harvestable") && ba.GetBlockEntity(pos) is BlockEntityBeehive be)
            {
                be.Harvestable = true;
                be.MarkDirty(true);
            }
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[createacolony] Could not set a skep at {0}: {1}", pos, e.Message);
            AddToPack(colony, citizen, stack);
            return false;
        }
        ba.MarkBlockDirty(pos);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/planks"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null);
        return true;
    }

    /// <summary>
    /// Harvests a full skep: its honeycomb and the rest of what the game gives go to the beekeeper, and the empty
    /// skep is set back in its place for the next swarm. The bees are lost, but they never sting: the skep is
    /// taken off quietly instead of broken.
    /// </summary>
    public bool HarvestSkep(Colony colony, Building hut, CitizenRecord citizen, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        if (ba.GetBlock(pos) is not BlockSkep skep || skep.IsEmpty() || !SkepFull(pos)) return false;
        // From Golden Combs keeps its own full flag; the game's drops read this one.
        if (ba.GetBlockEntity(pos) is BlockEntityBeehive be) be.Harvestable = true;
        var drops = DropsOf(skep, pos).Where(d => d != null && d.StackSize > 0).ToList();
        var emptyBlock = sapi.World.GetBlock(skep.CodeWithVariant("type", "empty"));
        // An empty skep among the drops goes straight back; without one, the beekeeper uses one it carries.
        var back = drops.FirstOrDefault(IsEmptySkepStack);
        if (back != null)
        {
            back.StackSize--;
            if (back.StackSize <= 0) drops.Remove(back);
        }
        ba.SetBlock(0, pos);
        if (back != null && emptyBlock != null) ba.SetBlock(emptyBlock.BlockId, pos);
        else if (back == null && PackCount(citizen, IsEmptySkepStack) > 0) SetSkep(colony, citizen, pos, false);
        ba.MarkBlockDirty(pos);
        ba.TriggerNeighbourBlockUpdate(pos);
        int combs = drops.Where(d => d.Collectible.Code.Path.StartsWith("honeycomb")).Sum(d => d.StackSize);
        StoreHarvest(colony, hut, citizen, drops, pos);
        AddStat(hut, "honeycomb", combs);
        AddStat(hut, "skeps");
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/plant"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null);
        ForgetApiaryScan(hut);
        return true;
    }

    /// <summary>Lifts a skep with bees, as a player would carry it, into the beekeeper's pack. False when there is none.</summary>
    public bool LiftSkep(Colony colony, CitizenRecord citizen, Building hut, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        if (ba.GetBlock(pos) is not BlockSkep skep || skep.IsEmpty() || PackFull(citizen)) return false;
        var block = sapi.World.GetBlock(skep.CodeWithVariant("side", "east")) ?? skep;
        var stack = new ItemStack(block);
        if (SkepFull(pos)) stack.Attributes.SetBool("harvestable", true);
        if (AddToPack(colony, citizen, stack) <= 0) return false;
        ba.SetBlock(0, pos);
        ba.MarkBlockDirty(pos);
        ba.TriggerNeighbourBlockUpdate(pos);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/planks"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null);
        ForgetApiaryScan(hut);
        return true;
    }

    /// <summary>Scrapes the full honey pot on a ceramic hive with the knife in hand and puts it back. False when there is nothing to scrape.</summary>
    public bool ScrapeHoneyPot(Colony colony, Building hut, CitizenRecord citizen, BlockPos pos, EntityAgent keeper, ItemSlot knife)
    {
        if (!IsKnife(knife.Itemstack) || sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityContainer be || !IsBroodPot(be) || !PotFull(be)) return false;
        var slot = be.Inventory[0];
        var top = slot.Itemstack!;
        string code = top.Collectible.Code.ToString();
        var fired = code == GoldenCombsModId + ":hivetop-harvestable"
            ? sapi.World.GetBlock(new AssetLocation(GoldenCombsModId, "hivetop-blue-fired"))
            : sapi.World.GetBlock(top.Collectible.CodeWithVariant("type", "fired"));
        if (fired == null) return false;
        int min = GoldenCombsSetting("CeramicPotMinYield", Config.ApiaryPotMinYield);
        int max = Math.Max(min, GoldenCombsSetting("CeramicPotMaxYield", Config.ApiaryPotMaxYield));
        var honeycomb = sapi.World.GetItem(new AssetLocation("game", "honeycomb"));
        if (honeycomb == null) return false;
        slot.Itemstack = new ItemStack(fired);
        slot.MarkDirty();
        if (be is BlockEntityDisplay display) display.updateMeshes();
        be.MarkDirty(true);
        int n = sapi.World.Rand.Next(min, max);
        StoreHarvest(colony, hut, citizen, new[] { new ItemStack(honeycomb, Math.Max(1, n)) }, pos);
        WearTool(keeper, knife);
        AddStat(hut, "honeycomb", Math.Max(1, n));
        AddStat(hut, "honeypots");
        sapi.World.PlaySoundAt(new AssetLocation("sounds/player/scrape"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null);
        ForgetApiaryScan(hut);
        return true;
    }

    /// <summary>Puts a honey pot the beekeeper carries on a brood pot that has none.</summary>
    public bool PutHoneyPot(Colony colony, Building hut, CitizenRecord citizen, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        if (ba.GetBlockEntity(pos) is not BlockEntityContainer be || !IsBroodPot(be) || PotTop(be) != null) return false;
        var top = TakeFromPack(colony, citizen, IsHoneyPot, 1);
        if (top == null) return false;
        // A plain fired honey pot is the blue one on the hive, as the mod does it.
        if (top.Collectible.Code.ToString() == GoldenCombsModId + ":hivetop-empty" && sapi.World.GetBlock(new AssetLocation(GoldenCombsModId, "hivetop-blue-fired")) is { } blue)
        {
            top = new ItemStack(blue);
        }
        var withTop = sapi.World.GetBlock(ba.GetBlock(pos).CodeWithVariant("top", "withtop"));
        be.Inventory[0].Itemstack = top;
        be.Inventory[0].MarkDirty();
        if (withTop != null) ba.ExchangeBlock(withTop.BlockId, pos);
        if (be is BlockEntityDisplay display) display.updateMeshes();
        be.MarkDirty(true);
        ForgetApiaryScan(hut);
        return true;
    }

    /// <summary>Moves the bees of a skep the beekeeper carries into an empty ceramic brood pot. The skep goes with them, as when a player does it.</summary>
    public bool FillBroodPot(Colony colony, Building hut, CitizenRecord citizen, BlockPos pos)
    {
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityContainer be || !IsBroodPot(be) || PotHasBees(be)) return false;
        if (ActiveField(be) is not { } field) return false;
        var skep = TakeFromPack(colony, citizen, IsColonySkepStack, 1);
        if (skep == null) return false;
        try
        {
            field.SetValue(be, true);
            // The pot starts its own hive timers when it loads with bees in it.
            be.Initialize(sapi);
            if (be is BlockEntityDisplay display) display.updateMeshes();
            be.MarkDirty(true);
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[createacolony] Could not move bees into the ceramic hive at {0}: {1}", pos, e.Message);
            field.SetValue(be, false);
            AddToPack(colony, citizen, skep);
            return false;
        }
        AddStat(hut, "moved");
        ForgetApiaryScan(hut);
        return true;
    }

    // ---- Wild hives ----

    /// <summary>The wild hives the colony's gatherers marked within reach of the Apiary that still stand, nearest first.</summary>
    public List<BlockPos> MarkedWildHives(Colony colony, Building hut)
    {
        var ba = sapi.World.BlockAccessor;
        var c = hut.HutPos;
        int range = Config.ApiaryWildRange;
        var list = new List<BlockPos>();
        foreach (var m in colony.Markers.Where(m => m.Kind == "bees-hive"))
        {
            var pos = new BlockPos((int)Math.Floor(m.X), (int)Math.Floor(m.Y), (int)Math.Floor(m.Z));
            if (Math.Abs(pos.X - c.X) > range || Math.Abs(pos.Z - c.Z) > range) continue;
            if (ba.GetChunkAtBlockPos(pos) == null || ba.GetBlock(pos) is not BlockBeehive) continue;
            list.Add(pos);
        }
        return list.OrderBy(p => p.DistanceSqTo(c.X, c.Y, c.Z)).ToList();
    }

    /// <summary>Drops the wild skeps that are gone (taken by a player, or not a skep any more).</summary>
    public void TidyWildSkeps(Colony colony, Building hut)
    {
        var ba = sapi.World.BlockAccessor;
        int before = hut.WildSkeps.Count;
        hut.WildSkeps.RemoveAll(p => ba.GetChunkAtBlockPos(p) != null && ba.GetBlock(p) is not BlockSkep);
        if (hut.WildSkeps.Count != before) MarkDirty(colony);
    }

    /// <summary>The wild skep set out by this wild hive, or null.</summary>
    public BlockPos? WildSkepBy(Building hut, BlockPos wild) =>
        hut.WildSkeps.FirstOrDefault(p => Math.Abs(p.X - wild.X) <= 8 && Math.Abs(p.Z - wild.Z) <= 8 && p.Y - wild.Y is >= -7 and <= 4);

    // ---- Hut window ----

    /// <summary>The Apiary window lines.</summary>
    List<string> ApiaryInfo(Colony colony, Building hut)
    {
        var scan = ScanApiary(hut);
        int skeps = scan.Hives.Count(h => h.Skep && h.Colony), empty = scan.Hives.Count(h => h.Skep && !h.Colony);
        int pots = scan.Hives.Count(h => !h.Skep && !h.Langstroth), potBees = scan.Hives.Count(h => !h.Skep && !h.Langstroth && h.Colony);
        var langs = scan.Hives.Where(h => h.Langstroth).ToList();
        int full = scan.Hives.Count(h => h.Full);
        var lines = new List<string>
        {
            Lang.Get("createacolony:apiary-hives", scan.Hives.Count + hut.WildSkeps.Count, ApiaryHiveLimit(hut)),
            Lang.Get("createacolony:apiary-kinds", skeps, empty, hut.WildSkeps.Count) + (GoldenCombs ? " " + Lang.Get("createacolony:apiary-ceramic", pots, potBees) : ""),
            Lang.Get("createacolony:apiary-full", full),
        };
        if (GoldenCombs && (langs.Count > 0 || scan.Rack != null))
        {
            lines.Add(Lang.Get("createacolony:apiary-langstroth", langs.Count, langs.Count(h => h.Colony), langs.Sum(h => h.FullFrames), langs.Sum(h => h.WornFrames), langs.Sum(h => h.EmptyFrames), langs.Sum(h => h.Room)));
            lines.Add(Lang.Get(scan.Rack != null ? "createacolony:apiary-rack" : "createacolony:apiary-no-rack"));
        }
        lines.AddRange(new[]
        {
            Lang.Get("createacolony:apiary-done", hut.StatTotals.GetValueOrDefault("honeycomb"), hut.StatTotals.GetValueOrDefault("skeps") + hut.StatTotals.GetValueOrDefault("honeypots"),
                hut.StatTotals.GetValueOrDefault("swarms"), hut.StatTotals.GetValueOrDefault("wild")),
        });
        if (hut.StatTotals.GetValueOrDefault("frames") + hut.StatTotals.GetValueOrDefault("repaired") + hut.StatTotals.GetValueOrDefault("brood") > 0)
        {
            lines.Add(Lang.Get("createacolony:apiary-frames-done", hut.StatTotals.GetValueOrDefault("frames"), hut.StatTotals.GetValueOrDefault("repaired"), hut.StatTotals.GetValueOrDefault("relined"), hut.StatTotals.GetValueOrDefault("brood")));
        }
        var can = new List<string> { Lang.Get("createacolony:apiary-can-harvest") };
        if (ApiaryCatchesSwarms(hut)) can.Add(Lang.Get("createacolony:apiary-can-swarms"));
        if (ApiaryWorksCeramic(hut)) can.Add(Lang.Get("createacolony:apiary-can-ceramic"));
        if (ApiaryBringsWild(hut)) can.Add(Lang.Get("createacolony:apiary-can-wild"));
        if (ApiaryWorksLangstroth(hut)) can.Add(Lang.Get("createacolony:apiary-can-langstroth"));
        lines.Add(Lang.Get("createacolony:apiary-level", ApiaryLevel(hut), string.Join(", ", can)));
        if (hut.KeepLastColony && scan.Colonies.Count() == 1 && scan.Hives.Any(h => h.Skep && h.Full)) lines.Add(Lang.Get("createacolony:apiary-keeping-last"));
        return lines;
    }
}
