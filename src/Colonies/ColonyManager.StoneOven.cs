using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Stone Bake Oven (Captain Oats and G3rste) support. The mod builds an oven out of three kinds of block:
/// a heat source (a fire pit in a stone case), heat distribution blocks next to it and baking spaces next
/// to those, which bake like the game's clay oven. Builders place its blocks as they come from crafting
/// (fire out, cold) and do not mistake the heat source for a fire pit. The cook of a Cook's Hut bakes in an
/// oven near the hut: it puts dough and pies on the baking spaces, adds firewood to the heat source and
/// lights it, and takes bread and pies out once they are baked. Which bakes it knows depends on its Cooking
/// level (config/cooking.json, "baking"). The mod is optional and is only reached through the game's own
/// classes and reflection, so nothing changes without it.
/// </summary>
public partial class ColonyManager
{
    const string StoneOvenDomain = "stonebakeoven";
    const string OvenBakingTopEntity = "BlockEntityOvenBakingTop";
    const string OvenControllerEntity = "BlockEntityOvenController";

    // ---- Building it --------------------------------------------------------------------------

    public static bool IsStoneOvenBlock(Block? block) => block?.Code?.Domain == StoneOvenDomain;

    /// <summary>
    /// The block as it comes from crafting: a heat source with its fire out, a heat distribution block cold.
    /// Other blocks are returned as they are. Its facing and stone stay the blueprint's.
    /// </summary>
    public Block OvenRestingBlock(Block block)
    {
        if (!IsStoneOvenBlock(block)) return block;
        var code = block.Code;
        if (VariantOf(block, "burnstate") is { } burn && burn != "extinct") code = block.CodeWithVariant("burnstate", "extinct");
        if (VariantOf(block, "temperature") is { } temp && temp != "cold") code = block.CodeWithVariant("temperature", "cold");
        return code.Equals(block.Code) ? block : sapi.World.GetBlock(code) ?? block;
    }

    /// <summary>
    /// The item a Stone Bake Oven block is made from: its recipes make every turnable block facing east, the heat
    /// source with its fire out and the rest cold. Null for other blocks.
    /// </summary>
    ItemStack? OvenItemStack(Block block)
    {
        if (!IsStoneOvenBlock(block)) return null;
        var item = OvenRestingBlock(block);
        if (VariantOf(item, "side") is { } side && side != "east" && sapi.World.GetBlock(item.CodeWithVariant("side", "east")) is { } east) item = east;
        return new ItemStack(item);
    }

    // ---- Finding the oven ---------------------------------------------------------------------

    /// <summary>A Stone Bake Oven near a Cook's Hut: its baking spaces and the heat source that warms them.</summary>
    public sealed class StoneOven
    {
        public required List<BlockEntityContainer> Tops;
        public BlockEntityFirepit? Fire;
        /// <summary>A game clay oven instead (0.194.0, with Expanded Foods): its own fire, fuelled before the bake. Tops holds just it.</summary>
        public BlockEntityOven? Clay;
    }

    public static bool IsOvenFire(BlockEntity? be) => be?.GetType().Name == OvenControllerEntity;

    static bool IsOvenTop(BlockEntity? be) => be?.GetType().Name == OvenBakingTopEntity;

    /// <summary>The oven the cook uses: baking spaces within CookFirepitRadius of the hut, and the heat source nearest them. Null without one.</summary>
    public StoneOven? CookOven(Colony colony, Building hut)
    {
        int r = Config.CookFirepitRadius;
        var h = hut.HutPos;
        var area = new Cuboidi(h.X - r, h.Y - 4, h.Z - r, h.X + r, h.Y + 4, h.Z + r);
        if (!sapi.ModLoader.IsModEnabled(StoneOvenDomain)) return ClayOven(hut, area);
        var tops = FindBlockEntities<BlockEntityContainer>(area).Where(IsOvenTop).OrderBy(t => t.Pos.DistanceSqTo(h.X, h.Y, h.Z)).ToList();
        if (tops.Count == 0) return ClayOven(hut, area);
        var first = tops[0].Pos;
        var fire = FindBlockEntities<BlockEntityFirepit>(new Cuboidi(h.X - r - 4, h.Y - 4, h.Z - r - 4, h.X + r + 4, h.Y + 4, h.Z + r + 4)).Where(IsOvenFire)
            .OrderBy(p => p.Pos.DistanceSqTo(first.X, first.Y, first.Z)).FirstOrDefault();
        return new StoneOven { Tops = tops, Fire = fire };
    }

    /// <summary>The game clay oven nearest the Cook's Hut, when ClayOvensOn; null otherwise.</summary>
    StoneOven? ClayOven(Building hut, Cuboidi area)
    {
        if (!ClayOvensOn) return null;
        var h = hut.HutPos;
        var clay = FindBlockEntities<BlockEntityOven>(area).OrderBy(o => o.Pos.DistanceSqTo(h.X, h.Y, h.Z)).FirstOrDefault();
        return clay == null ? null : new StoneOven { Tops = new List<BlockEntityContainer> { clay }, Clay = clay };
    }

    static MethodInfo? clayAddFuel;

    /// <summary>Puts the cook's firewood in a cold, empty clay oven (up to what it holds) and lights it. True when it is burning.</summary>
    public bool FuelClayOven(Colony colony, CitizenRecord cook, BlockEntityOven oven)
    {
        if (oven.IsBurning) return true;
        if (!oven.HasFuel || oven.FuelSlot.StackSize < oven.fuelitemCapacity)
        {
            clayAddFuel ??= typeof(BlockEntityOven).GetMethod("TryAddFuel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(ItemSlot) }, null);
            int room = oven.fuelitemCapacity - (oven.FuelSlot.Empty ? 0 : oven.FuelSlot.StackSize);
            if (clayAddFuel != null && room > 0 && TakeFromPack(colony, cook, IsFirewood, room) is { } wood)
            {
                var slot = new DummySlot(wood);
                try { clayAddFuel.Invoke(oven, new object[] { slot }); }
                catch (Exception e) { sapi.Logger.Warning("[createacolony] Could not fuel a clay oven: {0}", e.InnerException?.Message ?? e.Message); }
                if (!slot.Empty) AddToPack(colony, cook, slot.Itemstack);
            }
        }
        bool lit = oven.TryIgnite();
        oven.MarkDirty(true);
        return lit || oven.IsBurning;
    }

    /// <summary>Takes everything still baking out of a clay oven that went cold, so it can be fired again.</summary>
    public int EmptyColdOven(Colony colony, CitizenRecord cook, BlockEntityOven oven)
    {
        int taken = 0;
        foreach (var slot in oven.Inventory)
        {
            if (slot.Empty || IsFirewood(slot.Itemstack)) continue;
            AddToPack(colony, cook, slot.TakeOutWhole());
            slot.MarkDirty();
            taken++;
        }
        if (taken > 0) oven.MarkDirty(true);
        return taken;
    }

    /// <summary>True when a clay oven is hot enough to bake in and its fire has burned down.</summary>
    public bool ClayOvenReady(BlockEntityOven oven) => !oven.IsBurning && !oven.HasFuel && oven.ovenTemperature >= Config.ClayOvenLoadTemp;

    // ---- What it bakes ------------------------------------------------------------------------

    /// <summary>
    /// One thing the oven bakes: the baked food (bread, a pie) and the raw things that bake into it. Pies are one
    /// kind whatever their filling. Its key is "bake:" and the baked food's code.
    /// </summary>
    public sealed class OvenKind
    {
        public required string Key;
        public required ItemStack Sample;
        /// <summary>What the cook puts in the oven (dough). Empty for pies, which the cook makes.</summary>
        public readonly HashSet<AssetLocation> Raw = new();
        /// <summary>Everything before the baked food: the raw things and the half-baked stages in between.</summary>
        public readonly HashSet<AssetLocation> Baking = new();
        public bool IsPie => Sample.Block is BlockPie;

        public bool IsRaw(ItemStack s) => Raw.Contains(s.Collectible.Code);
        public bool IsMine(ItemStack s) => s.Collectible.Code.Equals(Sample.Collectible.Code);
        public bool IsBaking(ItemStack s) => Baking.Contains(s.Collectible.Code);
    }

    List<OvenKind>? ovenKinds;

    /// <summary>What a stage in the oven turns into, from its bakingProperties or, as the oven does it, its baking smelt.</summary>
    AssetLocation? BakesInto(CollectibleObject obj)
    {
        var props = obj.Attributes?["bakingProperties"]?.AsObject<BakingProperties>();
        if (props?.ResultCode is { } result) return new AssetLocation(result);
        var comb = obj.CombustibleProps;
        if (comb is { SmeltingType: EnumSmeltType.Bake } && comb.MeltingPoint < 260 && comb.SmeltedStack?.ResolvedItemstack is { } smelted) return smelted.Collectible.Code;
        return null;
    }

    static bool LooksBurnt(AssetLocation code) =>
        code.Path.Contains("charred") || code.Path.Contains("burnt") || code.Path.Contains("burned");

    /// <summary>
    /// Everything the oven bakes into food. Each chain of stages (dough, part-baked bread, bread, charred
    /// bread) ends at the last stage before a burnt one: that is what the cook takes out.
    /// </summary>
    public List<OvenKind> OvenKinds()
    {
        if (ovenKinds != null) return ovenKinds;
        if (!sapi.ModLoader.IsModEnabled(StoneOvenDomain) && !ClayOvensOn) return ovenKinds = new List<OvenKind>();
        var next = new Dictionary<AssetLocation, AssetLocation>();
        var byCode = new Dictionary<AssetLocation, CollectibleObject>();
        foreach (var obj in sapi.World.Collectibles)
        {
            if (obj?.Code == null || obj.IsMissing) continue;
            byCode[obj.Code] = obj;
            if (BakesInto(obj) is { } into && !into.Equals(obj.Code)) next[obj.Code] = into;
        }
        var results = next.Values.ToHashSet();
        var kinds = new Dictionary<string, OvenKind>();
        foreach (var start in next.Keys.Where(c => !results.Contains(c)))
        {
            var chain = new List<AssetLocation> { start };
            var at = start;
            while (next.TryGetValue(at, out var after) && !LooksBurnt(after) && !chain.Contains(after) && byCode.ContainsKey(after))
            {
                chain.Add(after);
                at = after;
            }
            if (chain.Count < 2 || !byCode.TryGetValue(at, out var baked)) continue;
            if (baked.NutritionProps == null && baked is not BlockPie) continue;
            string key = "bake:" + at;
            if (!kinds.TryGetValue(key, out var kind)) kinds[key] = kind = new OvenKind { Key = key, Sample = BakedSample(baked) };
            if (baked is not BlockPie) kind.Raw.Add(start);
            foreach (var stage in chain.Take(chain.Count - 1)) kind.Baking.Add(stage);
        }
        return ovenKinds = kinds.Values.OrderBy(k => k.Key).ToList();
    }

    /// <summary>The baked food for the Kitchen tab; a pie gets a filling so it can be drawn.</summary>
    ItemStack BakedSample(CollectibleObject baked)
    {
        var stack = new ItemStack(baked);
        if (baked is not BlockPie pie) return stack;
        var dough = sapi.World.GetItem(new AssetLocation("game:dough-spelt"));
        var fruit = sapi.World.GetItem(new AssetLocation("game:fruit-redapple"));
        if (dough == null || fruit == null) return stack;
        FillPie(pie, stack, new ItemStack(dough, 2), new ItemStack(fruit, 2), new ItemStack(dough, 2));
        return stack;
    }

    static void FillPie(BlockPie pie, ItemStack stack, ItemStack dough, ItemStack filling, ItemStack? top)
    {
        pie.SetContents(stack, new[] { dough, filling.Clone(), filling.Clone(), filling.Clone(), filling.Clone(), top });
        stack.Attributes.SetInt("pieSize", 4);
        stack.Attributes.SetString("topCrustType", top != null ? "full" : "square");
        stack.Attributes.SetBool("bakeable", true);
    }

    public OvenKind? FindOvenKind(string key) => OvenKinds().FirstOrDefault(k => k.Key == key);

    /// <summary>True for dough and anything else the cook bakes in the oven.</summary>
    public bool IsBakeable(ItemStack s) => IsBakeableCode(s.Collectible.Code);

    public bool IsBakeableCode(AssetLocation code) => OvenKinds().Any(k => k.Raw.Contains(code));

    /// <summary>A pie's part: "crust" for dough, "filling" for fruit, vegetables, meat and cheese, else null.</summary>
    static string? PiePart(ItemStack s) => s.ItemAttributes?["inPieProperties"]?["partType"]?.AsString()?.ToLowerInvariant();

    // ---- Levels and stock ---------------------------------------------------------------------

    /// <summary>The Cooking level a bake takes: cooking.json "baking" by the food's name (bread, pie), else its "default".</summary>
    public int BakeLevel(OvenKind kind)
    {
        if (!Config.Skills) return 0;
        var levels = Cooking().Baking;
        string name = kind.Sample.Collectible.FirstCodePart();
        int level = levels.TryGetValue(kind.Sample.Collectible.Code.ToShortString(), out int exact) ? exact
            : levels.TryGetValue(name, out int byName) ? byName
            : levels.GetValueOrDefault("default", 2);
        return Math.Clamp(level, 0, MaxLevel);
    }

    public bool KnowsBake(Colony colony, CitizenRecord cook, OvenKind kind) => !Config.Skills || BakeLevel(kind) <= CookLevel(colony, cook);

    public int BakeKeep(Building hut, OvenKind kind) =>
        hut.KitchenChosen ? hut.KitchenKeep.GetValueOrDefault(kind.Key) : kind.IsPie ? Config.OvenDefaultPieKeep : Config.KitchenAutoKeep;

    /// <summary>How many of a baked food are ready or baking: at the hut, in the warehouse, with the cook and in the oven.</summary>
    public int BakeStock(Colony colony, Building hut, OvenKind kind, CitizenRecord? cook) =>
        BakeStock(colony, hut, kind, cook, CookOven(colony, hut));

    int BakeStock(Colony colony, Building hut, OvenKind kind, CitizenRecord? cook, StoneOven? oven)
    {
        ItemMatch mine = kind.IsMine;
        int count = CountItems(WarehouseContainers(colony), mine) + CountItems(KitchenStores(colony, hut), mine);
        if (cook != null) count += PackCount(cook, mine);
        if (oven != null) count += oven.Tops.SelectMany(t => t.Inventory).Where(s => !s.Empty && (mine(s.Itemstack) || kind.IsBaking(s.Itemstack))).Sum(s => s.StackSize);
        return count;
    }

    // ---- Planning a bake ----------------------------------------------------------------------

    /// <summary>What the cook puts in the oven next: the items it needs, by code, and how many of each.</summary>
    public sealed class BakePlan
    {
        public required OvenKind Kind;
        public readonly List<(AssetLocation Code, int Count)> Needs = new();
        /// <summary>For a pie: the filling it takes.</summary>
        public AssetLocation? Filling;
        public AssetLocation? Dough;

        public bool Uses(ItemStack s) => Needs.Any(n => n.Code.Equals(s.Collectible.Code));
    }

    /// <summary>
    /// The bake the cook should start next: the most wanted one it knows and has the things for, counting its
    /// inventory, the hut and the warehouse. Bread is baked as much as the empty baking spaces hold (four a
    /// space), a pie takes a whole baking space: 4 dough (bottom and top crust) and 8 of one filling.
    /// </summary>
    public BakePlan? NextBake(Colony colony, Building hut, CitizenRecord cook, StoneOven oven)
    {
        int freeTops = oven.Tops.Count(t => t.Inventory.All(s => s.Empty));
        int freeSlots = oven.Tops.Sum(t => OvenFreeSlots(t));
        if (freeSlots == 0) return null;
        var wanted = OvenKinds()
            .Where(k => KnowsBake(colony, cook, k))
            // Bread is the Bakery's once it has a baker; the cook still bakes pies.
            .Where(k => k.IsPie || !BakerWorks(colony))
            .Select(k => (Kind: k, Short: BakeKeep(hut, k) - BakeStock(colony, hut, k, cook, oven)))
            .Where(w => w.Short > 0)
            .OrderBy(w => hut.Priorities.Count == 0 ? 0 : PriorityIndex(hut.Priorities, w.Kind.Key))
            .ThenByDescending(w => w.Short)
            .ToList();
        if (wanted.Count == 0) return null;

        var larder = Larder(colony, hut, cook);
        foreach (var (kind, shortBy) in wanted)
        {
            if (kind.IsPie)
            {
                if (freeTops == 0) continue;
                var dough = larder.Where(a => PiePart(a.Value.Sample) == "crust" && a.Value.Count >= 4).OrderByDescending(a => a.Value.Count).Select(a => a.Key).FirstOrDefault();
                var filling = larder.Where(a => PiePart(a.Value.Sample) == "filling" && a.Value.Count >= 8 && !a.Key.Equals(dough)).OrderByDescending(a => a.Value.Count).Select(a => a.Key).FirstOrDefault();
                if (dough == null || filling == null) continue;
                var pie = new BakePlan { Kind = kind, Dough = dough, Filling = filling };
                pie.Needs.Add((dough, 4));
                pie.Needs.Add((filling, 8));
                return pie;
            }
            // Raw dough first; part-baked bread taken out of a clay oven that went cold goes back in too.
            var raw = larder.Where(a => kind.IsRaw(a.Value.Sample) || oven.Clay != null && kind.IsBaking(a.Value.Sample))
                .OrderByDescending(a => kind.IsRaw(a.Value.Sample) ? 0 : 1).ThenByDescending(a => a.Value.Count).FirstOrDefault();
            if (raw.Key == null) continue;
            var plan = new BakePlan { Kind = kind };
            plan.Needs.Add((raw.Key, Math.Min(Math.Min(shortBy, freeSlots), raw.Value.Count)));
            return plan;
        }
        return null;
    }

    /// <summary>Empty places on a baking space: none while a pie (or another large item) is on it.</summary>
    static int OvenFreeSlots(BlockEntityContainer top)
    {
        var first = top.Inventory.FirstOrDefault(s => !s.Empty);
        if (first != null && BakingProperties.ReadFrom(first.Itemstack)?.LargeItem == true) return 0;
        return top.Inventory.Count(s => s.Empty);
    }

    /// <summary>True when something in the oven is still baking.</summary>
    public bool OvenIsBaking(BlockEntityContainer top) =>
        top.Inventory.Any(s => !s.Empty && OvenKinds().Any(k => k.IsBaking(s.Itemstack)));

    /// <summary>True when the baking space holds baked (or burnt) food to take out.</summary>
    public bool OvenHasDone(BlockEntityContainer top) => top.Inventory.Any(s => !s.Empty && IsOvenDone(s.Itemstack));

    bool IsOvenDone(ItemStack s)
    {
        if (OvenKinds().Any(k => k.IsBaking(s))) return false;
        // Baked food, or something left too long; anything else (a player's own bake the cook does not know) stays.
        return OvenKinds().Any(k => k.IsMine(s)) || LooksBurnt(s.Collectible.Code) && s.Collectible.Code.Domain == "game";
    }

    // ---- Working the oven ---------------------------------------------------------------------

    static MethodInfo? ovenTryPut;

    /// <summary>Puts one item on a baking space the way the oven takes it from a player. False when it does not fit.</summary>
    bool PutInOven(BlockEntityContainer top, ItemStack one)
    {
        ovenTryPut ??= top.GetType().GetMethod("TryPut", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(ItemSlot) }, null);
        var slot = new DummySlot(one);
        if (ovenTryPut != null)
        {
            try
            {
                return ovenTryPut.Invoke(top, new object[] { slot }) is true && slot.Empty;
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[createacolony] Could not put {0} in a Stone Bake Oven: {1}", one.Collectible.Code, e.InnerException?.Message ?? e.Message);
            }
        }
        return false;
    }

    /// <summary>
    /// Puts the bake on the baking spaces: dough a piece to a place, or a pie the cook makes from dough and
    /// filling on an empty space. What does not fit goes back into the cook's inventory.
    /// </summary>
    public int LoadOven(Colony colony, CitizenRecord cook, IEnumerable<BlockEntityContainer> tops, BakePlan plan)
    {
        int placed = 0;
        foreach (var top in tops)
        {
            if (plan.Kind.IsPie)
            {
                if (top.Inventory.Any(s => !s.Empty) || plan.Dough == null || plan.Filling == null) continue;
                if (MakePie(colony, cook, plan) is not { } pie) return placed;
                if (PutInOven(top, pie)) return placed + 1;
                // The oven would not take it: take the pie apart again.
                foreach (var part in (pie.Block as BlockPie)!.GetContents(sapi.World, pie)) if (part != null) AddToPack(colony, cook, part);
                return placed;
            }
            var code = plan.Needs[0].Code;
            while (OvenFreeSlots(top) > 0 && TakeFromPack(colony, cook, s => s.Collectible.Code.Equals(code), 1) is { } one)
            {
                if (!PutInOven(top, one))
                {
                    AddToPack(colony, cook, one);
                    break;
                }
                placed++;
            }
        }
        return placed;
    }

    /// <summary>A raw pie from the cook's inventory: 2 dough under, 4 times 2 of the filling, 2 dough on top.</summary>
    ItemStack? MakePie(Colony colony, CitizenRecord cook, BakePlan plan)
    {
        if (sapi.World.GetBlock(new AssetLocation("game:pie-raw")) is not BlockPie pieBlock) return null;
        if (PackCount(cook, s => s.Collectible.Code.Equals(plan.Dough)) < 4 || PackCount(cook, s => s.Collectible.Code.Equals(plan.Filling)) < 8) return null;
        var bottom = TakeFromPack(colony, cook, s => s.Collectible.Code.Equals(plan.Dough), 2);
        var top = TakeFromPack(colony, cook, s => s.Collectible.Code.Equals(plan.Dough), 2);
        var filling = TakeFromPack(colony, cook, s => s.Collectible.Code.Equals(plan.Filling), 8);
        if (bottom == null || top == null || filling == null) return null;
        filling.StackSize = 2;
        var pie = new ItemStack(pieBlock);
        FillPie(pieBlock, pie, bottom, filling, top);
        return pie;
    }

    /// <summary>Takes baked (and burnt) food off a baking space into the cook's inventory.</summary>
    public int EmptyOven(Colony colony, CitizenRecord cook, BlockEntityContainer top)
    {
        int taken = 0;
        foreach (var slot in top.Inventory)
        {
            if (slot.Empty || !IsOvenDone(slot.Itemstack)) continue;
            var stack = slot.TakeOutWhole();
            // A whole pie fresh from the oven is four slices, as when a player sets it down.
            if (stack.Block is BlockPie && !stack.Attributes.HasAttribute("quantityServings"))
            {
                stack.Attributes.SetFloat("quantityServings", stack.Attributes.GetAsInt("pieSize", 4) * 0.25f);
            }
            AddToPack(colony, cook, stack);
            slot.MarkDirty();
            taken++;
        }
        if (taken > 0) top.MarkDirty(true);
        return taken;
    }

    /// <summary>A line about the oven for the hut window, or null without one.</summary>
    public string? OvenInfo(Colony colony, Building hut)
    {
        var oven = CookOven(colony, hut);
        if (oven == null) return null;
        if (oven.Clay is { } clay)
        {
            var inside = clay.Inventory.Where(s => !s.Empty && !IsFirewood(s.Itemstack)).Select(s => s.Itemstack!).ToList();
            if (clay.IsBurning) return Lang.Get("createacolony:clayoven-heating", (int)clay.ovenTemperature);
            if (inside.Count == 0) return Lang.Get("createacolony:clayoven-empty", (int)clay.ovenTemperature);
            return Lang.Get("createacolony:clayoven-baking", string.Join(", ", inside.GroupBy(s => s.GetName()).Select(g => g.Sum(s => s.StackSize) + "x " + g.Key)), (int)clay.ovenTemperature);
        }
        if (oven.Fire == null) return Lang.Get("createacolony:oven-nofire");
        var baking = oven.Tops.SelectMany(t => t.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack!).ToList();
        if (baking.Count == 0) return Lang.Get("createacolony:oven-empty", oven.Tops.Count);
        return Lang.Get(oven.Fire.IsBurning ? "createacolony:oven-baking" : "createacolony:oven-waiting",
            string.Join(", ", baking.GroupBy(s => s.GetName()).Select(g => g.Sum(s => s.StackSize) + "x " + g.Key)));
    }
}
