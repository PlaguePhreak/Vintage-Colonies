using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Langstroth hives from the From Golden Combs mod (optional, never bundled), worked by the beekeeper from config
/// ApiaryLangstrothLevel:
/// - it takes full frames out of the supers and scrapes them at a Frame Rack in the Work area, as a player does:
///   honeycomb goes to the hut and the frame loses a use (with its last use it is left unlined);
/// - a lined frame with config ApiaryFrameRepairAt uses left or fewer is taken out and repaired with waxed flax
///   twine, and an empty frame is lined again with a frame liner, when it has some;
/// - lined frames go back into the supers, and free spots are filled with lined frames from the hut;
/// - an empty brood box gets the bees of a skep, as when a player does it.
/// The mod's blocks are only reached by name and reflection: if they change, the beekeeper skips these hives.
/// </summary>
public partial class ColonyManager
{
    const int FramesPerSuper = 10;
    public const int FramesPerSuperCount = FramesPerSuper;

    static readonly Dictionary<(Type, string), MethodInfo?> fgcMethods = new();

    static MethodInfo? FgcMethod(object o, string name)
    {
        var key = (o.GetType(), name);
        if (!fgcMethods.TryGetValue(key, out var method))
        {
            method = o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == name);
            fgcMethods[key] = method;
        }
        return method;
    }

    static bool IsLangstrothStack(BlockEntity? be) => be is BlockEntityContainer && be.GetType().Name == "BELangstrothStack";

    /// <summary>The parts of a Langstroth hive, from the bottom one up. Empty when there is no hive here.</summary>
    List<BlockEntityContainer> LangstrothStacks(BlockPos bottom)
    {
        var list = new List<BlockEntityContainer>();
        var ba = sapi.World.BlockAccessor;
        for (int i = 0; i < 12; i++)
        {
            if (ba.GetBlockEntity(bottom.UpCopy(i)) is not BlockEntityContainer be || !IsLangstrothStack(be)) break;
            list.Add(be);
        }
        return list;
    }

    /// <summary>The supers in a hive: their slots in the stacks.</summary>
    static IEnumerable<ItemSlot> Supers(List<BlockEntityContainer> stacks) =>
        stacks.SelectMany(s => s.Inventory).Where(slot => slot.Itemstack?.Block?.FirstCodePart() == "langstrothsuper");

    /// <summary>The frames tree of a super; made with ten free spots when make is true and it has none.</summary>
    static ITreeAttribute? FrameTree(ItemStack super, bool make)
    {
        var tree = super.Attributes.GetTreeAttribute("contents");
        if (tree == null && make)
        {
            tree = new TreeAttribute();
            super.Attributes["contents"] = tree;
        }
        if (tree != null && make)
        {
            for (int i = 0; i < FramesPerSuper; i++)
            {
                if (!tree.HasAttribute(i.ToString())) tree[i.ToString()] = new ItemstackAttribute(null);
            }
        }
        return tree;
    }

    /// <summary>The frame in one spot of a super, or null.</summary>
    ItemStack? FrameAt(ITreeAttribute tree, int i)
    {
        var frame = tree.GetItemstack(i.ToString());
        if (frame == null) return null;
        if (frame.Collectible == null) frame.ResolveBlockOrItem(sapi.World);
        return frame.Collectible == null ? null : frame;
    }

    public static bool IsBeeFrame(ItemStack? s) => s?.Collectible?.Code?.Domain == GoldenCombsModId && s.Collectible.FirstCodePart() == "beeframe";
    static string FrameState(ItemStack s) => s.Collectible.Variant["harvestable"] ?? "";
    public static bool IsLinedFrame(ItemStack? s) => IsBeeFrame(s) && FrameState(s!) == "lined";
    public static bool IsFrameTwine(ItemStack? s) => s?.Collectible?.Code?.Domain == GoldenCombsModId && s.Collectible.FirstCodePart() == "waxedflaxtwine";
    public static bool IsFrameLiner(ItemStack? s) => s?.Collectible?.Code?.Domain == GoldenCombsModId && s.Collectible.FirstCodePart() == "frameliner";

    int FrameBaseUses => Math.Max(2, GoldenCombsSetting("baseframedurability", Config.ApiaryFrameUses));

    /// <summary>Uses a frame has left (the mod saves none until the first harvest).</summary>
    int FrameUses(ItemStack s)
    {
        int d = s.Attributes.GetInt("durability");
        return d <= 0 ? FrameBaseUses : d;
    }

    bool FrameWorn(ItemStack s) => FrameState(s) == "lined" && FrameUses(s) <= Config.ApiaryFrameRepairAt;

    /// <summary>True when the beekeeper carries a frame it can work at the Frame Rack with what it has.</summary>
    public bool CarriesRackWork(CitizenRecord citizen)
    {
        var items = CarriedItems(citizen);
        bool twine = items.Any(IsFrameTwine), liner = items.Any(IsFrameLiner);
        return items.Where(IsBeeFrame).Any(f => FrameState(f) == "harvestable" || twine && FrameWorn(f) || liner && FrameState(f) == "empty");
    }

    /// <summary>Reads a Langstroth hive for the scan: bees, frames, free spots and its brood box.</summary>
    void ReadLangstroth(ApiaryHive hive)
    {
        var stacks = LangstrothStacks(hive.Pos);
        if (stacks.Count == 0) return;
        try
        {
            hive.Colony = FgcMethod(stacks[0], "isHiveActive")?.Invoke(stacks[0], null) is true;
        }
        catch (Exception)
        {
            hive.Colony = false;
        }
        foreach (var slot in Supers(stacks))
        {
            var tree = FrameTree(slot.Itemstack!, false);
            int frames = 0;
            for (int i = 0; tree != null && i < FramesPerSuper; i++)
            {
                if (FrameAt(tree, i) is not { } f || !IsBeeFrame(f)) continue;
                frames++;
                switch (FrameState(f))
                {
                    case "harvestable": hive.FullFrames++; break;
                    case "empty": hive.EmptyFrames++; break;
                    default: if (FrameWorn(f)) hive.WornFrames++; break;
                }
            }
            hive.Room += Math.Max(0, FramesPerSuper - frames);
        }
        // The brood box sits on top of the hive.
        var top = stacks[stacks.Count - 1].Inventory.LastOrDefault(s => !s.Empty)?.Itemstack?.Block;
        hive.EmptyBrood = top?.FirstCodePart() == "langstrothbrood" && top.Variant["populated"] == "empty";
        hive.Full = hive.FullFrames > 0;
    }

    /// <summary>Lets the mod count the frames again and redraw the hive after the beekeeper changed it.</summary>
    void RefreshLangstroth(List<BlockEntityContainer> stacks)
    {
        if (stacks.Count == 0) return;
        try
        {
            FgcMethod(stacks[0], "CountFrames")?.Invoke(stacks[0], null);
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[createacolony] Could not recount the frames of the Langstroth hive at {0}: {1}", stacks[0].Pos, e.InnerException?.Message ?? e.Message);
        }
        foreach (var be in stacks)
        {
            if (be is BlockEntityDisplay display) display.updateMeshes();
            be.MarkDirty(true);
        }
    }

    /// <summary>
    /// Takes out of a hive the frames that need the Frame Rack: full ones, worn ones when it carries waxed twine,
    /// and empty ones when it carries frame liners. Returns how many it took.
    /// </summary>
    public int TakeFrames(Colony colony, Building hut, CitizenRecord citizen, BlockPos pos)
    {
        var stacks = LangstrothStacks(pos);
        if (stacks.Count == 0) return 0;
        int twine = PackCount(citizen, IsFrameTwine), liner = PackCount(citizen, IsFrameLiner), taken = 0;
        bool full = false;
        foreach (var slot in Supers(stacks).ToList())
        {
            if (full) break;
            var tree = FrameTree(slot.Itemstack!, false);
            if (tree == null) continue;
            for (int i = 0; i < FramesPerSuper; i++)
            {
                if (FrameAt(tree, i) is not { } f || !IsBeeFrame(f)) continue;
                bool worn = FrameWorn(f), empty = FrameState(f) == "empty";
                if (!(FrameState(f) == "harvestable" || worn && twine > 0 || empty && liner > 0)) continue;
                if (AddToPack(colony, citizen, f.Clone(), false) <= 0)
                {
                    full = true;
                    break;
                }
                if (worn) twine--;
                if (empty) liner--;
                tree.SetItemstack(i.ToString(), null);
                taken++;
            }
            slot.MarkDirty();
        }
        if (taken > 0)
        {
            RefreshLangstroth(stacks);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/block/planks"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null);
        }
        ForgetApiaryScan(hut);
        return taken;
    }

    /// <summary>
    /// Works the carried frames at the Frame Rack, as a player does there: full frames are scraped with the knife
    /// (honeycomb to the hut, one use less, left unlined with the last use), worn frames repaired with waxed twine
    /// (16 uses, up to new) and empty frames lined with a frame liner. False when there was nothing to do.
    /// </summary>
    public bool WorkFrames(Colony colony, Building hut, CitizenRecord citizen, BlockPos rack, EntityAgent keeper, ItemSlot? knife)
    {
        var ba = sapi.World.BlockAccessor;
        if (ba.GetBlock(rack).FirstCodePart() != "framerack") return false;
        var items = PackItems(citizen);
        int twine = items.Where(IsFrameTwine).Sum(s => s.StackSize), liner = items.Where(IsFrameLiner).Sum(s => s.StackSize);
        int baseUses = FrameBaseUses;
        int min = GoldenCombsSetting("FrameMinYield", Config.ApiaryFrameMinYield);
        int max = Math.Max(min + 1, GoldenCombsSetting("FrameMaxYield", Config.ApiaryFrameMaxYield));
        bool canScrape = knife != null && IsKnife(knife.Itemstack);
        int combs = 0, scraped = 0, repaired = 0, relined = 0, usedTwine = 0, usedLiner = 0;
        var done = new List<ItemStack>();
        foreach (var stack in items.Where(IsBeeFrame).ToList())
        {
            int n = stack.StackSize;
            for (int k = 0; k < n; k++)
            {
                var frame = stack.Clone();
                frame.StackSize = 1;
                if (FrameState(frame) == "harvestable" && canScrape)
                {
                    int uses = FrameUses(frame);
                    if ((uses <= 1 ? NewFrame(frame, "empty", 0) : NewFrame(frame, "lined", uses - 1)) is { } after)
                    {
                        combs += Math.Max(1, sapi.World.Rand.Next(min, max));
                        scraped++;
                        frame = after;
                    }
                }
                if (FrameWorn(frame) && FrameUses(frame) < baseUses && usedTwine < twine)
                {
                    int uses = FrameUses(frame);
                    frame.Attributes.SetInt("durability", baseUses - uses < 16 ? baseUses : uses + 16);
                    usedTwine++;
                    repaired++;
                }
                if (FrameState(frame) == "empty" && usedLiner < liner && NewFrame(frame, "lined", baseUses) is { } lined)
                {
                    frame = lined;
                    usedLiner++;
                    relined++;
                }
                done.Add(frame);
            }
            stack.StackSize = 0;
        }
        if (scraped + repaired + relined == 0) return false;
        Use(items, IsFrameTwine, usedTwine);
        Use(items, IsFrameLiner, usedLiner);
        SavePack(colony, citizen, items);
        foreach (var frame in done)
        {
            // A frame that no longer fits in the pack goes to the hut.
            AddToPack(colony, citizen, frame, false);
            if (frame.StackSize > 0) StoreHarvest(colony, hut, citizen, new[] { frame }, rack);
        }
        if (combs > 0 && sapi.World.GetItem(new AssetLocation("game", "honeycomb")) is { } honeycomb)
        {
            StoreHarvest(colony, hut, citizen, new[] { new ItemStack(honeycomb, combs) }, rack);
            AddStat(hut, "honeycomb", combs);
        }
        if (scraped > 0)
        {
            AddStat(hut, "frames", scraped);
            if (knife != null) for (int i = 0; i < scraped && !knife.Empty; i++) WearTool(keeper, knife);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/player/scrape"), rack.X + 0.5, rack.Y + 0.5, rack.Z + 0.5, null);
        }
        if (repaired > 0) AddStat(hut, "repaired", repaired);
        if (relined > 0) AddStat(hut, "relined", relined);
        return true;
    }

    /// <summary>A frame of the same wood in another state, with this many uses (0: none saved).</summary>
    ItemStack? NewFrame(ItemStack frame, string state, int uses)
    {
        var item = sapi.World.GetItem(frame.Collectible.CodeWithVariant("harvestable", state));
        if (item == null) return null;
        var stack = new ItemStack(item);
        if (uses > 0) stack.Attributes.SetInt("durability", uses);
        return stack;
    }

    static void Use(List<ItemStack> items, System.Func<ItemStack?, bool> match, int count)
    {
        foreach (var s in items.Where(s => match(s)))
        {
            if (count <= 0) break;
            int n = Math.Min(count, s.StackSize);
            s.StackSize -= n;
            count -= n;
        }
    }

    /// <summary>Puts the lined frames the beekeeper carries into the free spots of a hive's supers. Returns how many went in.</summary>
    public int PutFrames(Colony colony, Building hut, CitizenRecord citizen, BlockPos pos)
    {
        var stacks = LangstrothStacks(pos);
        if (stacks.Count == 0) return 0;
        int put = 0;
        bool none = false;
        foreach (var slot in Supers(stacks).ToList())
        {
            if (none) break;
            var tree = FrameTree(slot.Itemstack!, true)!;
            for (int i = 0; i < FramesPerSuper; i++)
            {
                if (FrameAt(tree, i) != null) continue;
                // The best frames go in first.
                var best = PackItems(citizen).Where(IsLinedFrame).OrderByDescending(FrameUses).FirstOrDefault();
                var frame = best == null ? null : TakeFromPack(colony, citizen, s => IsLinedFrame(s) && s.Equals(sapi.World, best, GlobalConstantsIgnored), 1);
                if (frame == null)
                {
                    none = true;
                    break;
                }
                tree.SetItemstack(i.ToString(), frame);
                put++;
            }
            slot.MarkDirty();
        }
        if (put > 0)
        {
            RefreshLangstroth(stacks);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/block/planks"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null);
        }
        ForgetApiaryScan(hut);
        return put;
    }

    static readonly string[] GlobalConstantsIgnored = Vintagestory.API.Config.GlobalConstants.IgnoredStackAttributes;

    /// <summary>Moves the bees of a carried skep into the empty brood box of a hive, or of a brood box set down on its own. The skep goes with them, as when a player does it.</summary>
    public bool FillBroodBox(Colony colony, Building hut, CitizenRecord citizen, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var block = ba.GetBlock(pos);
        if (block.Code?.Domain != GoldenCombsModId) return false;
        if (block.FirstCodePart() == "langstrothbrood")
        {
            if (block.Variant["populated"] != "empty" || sapi.World.GetBlock(block.CodeWithVariant("populated", "populated")) is not { } populated) return false;
            if (TakeFromPack(colony, citizen, IsColonySkepStack, 1) == null) return false;
            ba.SetBlock(populated.BlockId, pos);
            ba.MarkBlockDirty(pos);
        }
        else
        {
            var stacks = LangstrothStacks(pos);
            if (stacks.Count == 0 || FgcMethod(stacks[0], "UpdateBroodBox") is not { } update) return false;
            var skep = TakeFromPack(colony, citizen, IsColonySkepStack, 1);
            if (skep == null) return false;
            var slot = new DummySlot(skep);
            try
            {
                update.Invoke(stacks[0], new object[] { slot });
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[createacolony] Could not move bees into the Langstroth hive at {0}: {1}", pos, e.InnerException?.Message ?? e.Message);
            }
            if (slot.Itemstack != null)
            {
                // The brood box did not take them: the bees stay in their skep.
                AddToPack(colony, citizen, slot.Itemstack, false);
                return false;
            }
            RefreshLangstroth(stacks);
        }
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/planks"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null);
        AddStat(hut, "brood");
        ForgetApiaryScan(hut);
        return true;
    }
}
