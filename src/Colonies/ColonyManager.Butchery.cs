using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The Butcher's Hut. While the colony has a butcher, dead animals are not skinned where they fell: the hunter
/// (and the butcher itself, for animals that died on the colony's land or that herders culled) picks them up whole
/// as a carcass item (createacolony:carcass) and brings them to the hut. The butcher cuts them up there with a
/// knife and gets more meat and fat than skinning in the field, plus bones. What it cuts goes in the hut storage
/// (food in the coolest chest first), and couriers take it to the warehouse and its cellar.
///
/// Optional support for the Butchering mod by CaptainOats, only when it is installed (nothing of it is copied or
/// bundled, and nothing is compiled against it): its codes are looked up by name from the config. With one of its
/// hooks or tables in or by the hut, the butcher works there and gets a little more, and it fills empty buckets in
/// the hut storage with the mod's blood. Without the mod the butcher works at the hut block with a knife, as a
/// player skins with one.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The item a dead animal becomes when it is picked up whole.</summary>
    public static readonly AssetLocation CarcassCode = new("createacolony", "carcass");

    // Attributes of a carcass item.
    public const string CarcassAnimalAttr = "animal";
    public const string CarcassDropsAttr = "drops";
    public const string CarcassSizeAttr = "size";

    /// <summary>Set once the Butchering mod is found, so the static hut-junk test can keep empty buckets.</summary>
    static bool butcheringModLoaded;

    public static bool IsCarcass(ItemStack? s) => s?.Collectible?.Code != null && s.Collectible.Code.Equals(CarcassCode);

    /// <summary>An empty bucket or other open liquid container, kept at the Butcher's Hut for blood when the Butchering mod is installed.</summary>
    public static bool IsEmptyBucketForBlood(ItemStack? s) =>
        butcheringModLoaded && s?.Block is BlockLiquidContainerTopOpened lc && lc.GetContent(s) == null;

    /// <summary>True when the Butchering mod is installed.</summary>
    public bool ButcheringMod => butcheringModLoaded = !string.IsNullOrEmpty(Config.ButcheringModId) && sapi.ModLoader.IsModEnabled(Config.ButcheringModId);

    /// <summary>
    /// True while a living butcher works at a Butcher's Hut. Then hunters (and herders) bring dead animals home
    /// whole, and settlers without a job leave them for the butcher.
    /// </summary>
    public bool ButcherWorks(Colony colony) =>
        colony.Citizens.Any(c => c.Alive && c.JobCode == Jobs.Butcher && c.WorkplacePos != null && GetBuilding(colony, c.WorkplacePos) is { Type: BuildingTypes.Butcher, Removing: false });

    /// <summary>True when this dead animal should go to the butcher whole: there is one, and nobody has started skinning it.</summary>
    public bool ForTheButcher(Colony colony, Entity carcass) =>
        !carcass.Alive && carcass.GetBehavior<EntityBehaviorHarvestable>() is { IsHarvested: false } && ButcherWorks(colony);

    /// <summary>The size of the animal: 0 small, 1 medium, 2 large, 3 huge. By the hide it gives, else by its body.</summary>
    static int SizeOf(Entity animal, IEnumerable<ItemStack> drops)
    {
        foreach (var d in drops)
        {
            string path = d.Collectible.Code.Path;
            if (!path.StartsWith("hide-")) continue;
            if (path.EndsWith("-huge")) return 3;
            if (path.EndsWith("-large")) return 2;
            if (path.EndsWith("-medium")) return 1;
            if (path.EndsWith("-small")) return 0;
        }
        var box = animal.CollisionBox;
        float volume = box == null ? 0.5f : box.Width * box.Height * box.Length;
        return volume < 0.15f ? 0 : volume < 0.8f ? 1 : volume < 1.8f ? 2 : 3;
    }

    static T BySize<T>(T[]? values, int size, T fallback) =>
        values == null || values.Length == 0 ? fallback : values[Math.Clamp(size, 0, values.Length - 1)];

    /// <summary>
    /// Picks up a dead animal whole: what the game would put on it for skinning goes into a carcass item, and the
    /// dead animal is gone. Null when it has already been skinned (take what is left on it with Skin instead).
    /// </summary>
    public ItemStack? PickUpCarcass(Colony colony, EntityAgent settler, Entity animal)
    {
        if (animal.Alive || animal.GetBehavior<EntityBehaviorHarvestable>() is not { } h || h.IsHarvested || h.Inventory is not InventoryGeneric inv) return null;
        var item = sapi.World.GetItem(CarcassCode);
        if (item == null) return null;
        if (!h.DropsGenerated) FillCarcass(animal, h, inv, settler);
        var drops = inv.Where(s => !s.Empty).Select(s => s.Itemstack!).ToList();

        var stack = new ItemStack(item);
        stack.Attributes.SetString(CarcassAnimalAttr, animal.Code.ToString());
        stack.Attributes.SetInt(CarcassSizeAttr, SizeOf(animal, drops));
        var tree = new TreeAttribute();
        for (int i = 0; i < drops.Count; i++)
        {
            var drop = drops[i].Clone();
            // The carcass itself spoils; what comes off it starts fresh.
            drop.Attributes.RemoveAttribute("transitionstate");
            tree.SetItemstack(i.ToString(), drop);
        }
        stack.Attributes[CarcassDropsAttr] = tree;

        carcassClaims.Remove(animal.EntityId);
        sapi.World.DespawnEntity(animal, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        sapi.World.PlaySoundAt(new AssetLocation("sounds/player/collect"), settler, null, true, 16);
        return stack;
    }

    /// <summary>The name of the animal a carcass came from, such as "Hare".</summary>
    public static string CarcassAnimalName(ItemStack carcass)
    {
        string? code = carcass.Attributes.GetString(CarcassAnimalAttr);
        if (code == null) return Lang.Get("createacolony:carcass-unknown");
        var loc = new AssetLocation(code);
        string key = loc.Domain + ":item-creature-" + loc.Path;
        return Lang.HasTranslation(key) ? Lang.Get(key) : loc.Path;
    }

    /// <summary>What the carcass holds, as the game would give for skinning it.</summary>
    List<ItemStack> CarcassDrops(ItemStack carcass)
    {
        var list = new List<ItemStack>();
        if (carcass.Attributes[CarcassDropsAttr] is not ITreeAttribute tree) return list;
        foreach (var (_, value) in tree)
        {
            if (value is not ItemstackAttribute { value: { } stack }) continue;
            if (!stack.ResolveBlockOrItem(sapi.World) || stack.StackSize <= 0) continue;
            list.Add(stack.Clone());
        }
        return list;
    }

    /// <summary>Seconds this carcass takes the butcher to cut up, before skill.</summary>
    public float ButcherSeconds(ItemStack? carcass) =>
        BySize(Config.ButcherSecondsBySize, carcass?.Attributes.GetInt(CarcassSizeAttr, 1) ?? 1, 10f);

    bool IsMeatOrFat(ItemStack s)
    {
        if (s.Collectible.Code.Path.StartsWith("fat")) return true;
        if (s.Collectible.GetNutritionProperties(sapi.World, s, null) != null) return true;
        var smelted = s.Collectible.GetCombustibleProperties(sapi.World, s, null)?.SmeltedStack?.ResolvedItemstack;
        return smelted?.Collectible.GetNutritionProperties(sapi.World, smelted, null) != null;
    }

    /// <summary>
    /// What the butcher gets from cutting up a carcass: what skinning would give, with ButcherYieldBonus more meat
    /// and fat (and ButcheringStationBonus more at a Butchering mod hook or table), bones if the animal gives none,
    /// and a chance of one more piece from the Butchery skill's perks.
    /// </summary>
    public List<ItemStack> CutUp(Colony colony, CitizenRecord butcher, ItemStack carcass, bool atStation)
    {
        var drops = CarcassDrops(carcass);
        int size = carcass.Attributes.GetInt(CarcassSizeAttr, 1);
        float bonus = Math.Max(0, Config.ButcherYieldBonus) + (atStation ? Math.Max(0, Config.ButcheringStationBonus) : 0);
        var rand = sapi.World.Rand;
        foreach (var d in drops)
        {
            if (!IsMeatOrFat(d)) continue;
            float more = d.StackSize * bonus;
            int whole = (int)more;
            if (rand.NextDouble() < more - whole) whole++;
            d.StackSize += whole;
        }
        if (!drops.Any(d => d.Collectible.Code.Path.StartsWith("bone")) && !string.IsNullOrEmpty(Config.ButcherBoneCode)
            && sapi.World.GetItem(new AssetLocation(Config.ButcherBoneCode)) is { } bone)
        {
            int n = BySize(Config.ButcherBonesBySize, size, 0);
            if (n > 0) drops.Add(new ItemStack(bone, n));
        }
        if (drops.Count > 0 && RollPerk(colony, butcher, "yield")) drops.Where(IsMeatOrFat).DefaultIfEmpty(drops[0]).OrderByDescending(d => d.StackSize).First().StackSize++;

        // Split stacks bigger than the game allows.
        var result = new List<ItemStack>();
        foreach (var d in drops)
        {
            while (d.StackSize > d.Collectible.MaxStackSize)
            {
                var part = d.Clone();
                part.StackSize = d.Collectible.MaxStackSize;
                d.StackSize -= part.StackSize;
                result.Add(part);
            }
            if (d.StackSize > 0) result.Add(d);
        }
        return result;
    }

    /// <summary>Puts what the butcher cut into the hut storage (food in the coolest chest first); what does not fit lands by the hut.</summary>
    public void StoreCuts(Colony colony, Building hut, IEnumerable<ItemStack> cuts)
    {
        var storage = HutStorage(colony, hut).ToList();
        foreach (var cut in cuts)
        {
            foreach (var chest in CanSpoil(cut) ? CoolestFirst(storage, cut) : storage)
            {
                if (cut.StackSize <= 0) break;
                PutItems(chest, cut);
            }
            if (cut.StackSize > 0) sapi.World.SpawnItemEntity(cut, hut.HutPos.ToVec3d().Add(0.5, 1, 0.5));
        }
    }

    /// <summary>
    /// With the Butchering mod: fills an empty (or part-filled) bucket in the hut storage with the animal's blood.
    /// Returns the litres put in.
    /// </summary>
    public float CollectBlood(Colony colony, Building hut, ItemStack carcass)
    {
        if (!ButcheringMod || string.IsNullOrEmpty(Config.ButcheringBloodCode)) return 0;
        var code = new AssetLocation(Config.ButcheringBloodCode);
        CollectibleObject? blood = sapi.World.GetItem(code) ?? (CollectibleObject?)sapi.World.GetBlock(code);
        if (blood == null) return 0;
        float litres = BySize(Config.ButcheringBloodLitresBySize, carcass.Attributes.GetInt(CarcassSizeAttr, 1), 1f);
        float put = 0;
        foreach (var chest in HutStorage(colony, hut))
        {
            foreach (var slot in chest.Inventory)
            {
                if (put >= litres) break;
                if (slot.Empty || slot.Itemstack.StackSize != 1 || slot.Itemstack.Block is not BlockLiquidContainerTopOpened lc) continue;
                var content = lc.GetContent(slot.Itemstack);
                if (content != null && content.Collectible != blood) continue;
                var props = BlockLiquidContainerBase.GetContainableProps(new ItemStack(blood));
                if (props == null) return put;
                var portion = new ItemStack(blood, (int)Math.Ceiling((litres - put) * props.ItemsPerLitre));
                int moved = lc.TryPutLiquid(slot.Itemstack, portion, litres - put);
                if (moved <= 0) continue;
                put += moved / props.ItemsPerLitre;
                slot.MarkDirty();
                chest.MarkDirty(true);
            }
        }
        return put;
    }

    /// <summary>
    /// A Butchering mod hook or table in the Butcher's Hut (or within a few blocks of its block), where the butcher
    /// works. Null without the mod or without one.
    /// </summary>
    public BlockPos? ButcheringStation(Colony colony, Building hut)
    {
        if (!ButcheringMod || Config.ButcheringStations is not { Length: > 0 } codes) return null;
        long now = sapi.World.ElapsedMilliseconds;
        if (stationCache.TryGetValue(hut.BuildingId, out var cached) && now < cached.Until) return cached.Pos;
        var patterns = codes.Select(c => new AssetLocation(c)).ToArray();
        BlockPos min, max;
        if (hut.Level > 0)
        {
            (min, max) = ZoneOf(hut);
        }
        else
        {
            min = hut.HutPos.AddCopy(-6, -2, -6);
            max = hut.HutPos.AddCopy(6, 3, 6);
        }
        BlockPos? found = null;
        double best = double.MaxValue;
        var h = hut.HutPos;
        sapi.World.BlockAccessor.WalkBlocks(min, max, (block, x, y, z) =>
        {
            if (block.Id == 0 || block.Code == null || !patterns.Any(p => WildcardUtil.Match(p, block.Code))) return;
            double d = (x - h.X) * (x - h.X) + (y - h.Y) * (y - h.Y) + (z - h.Z) * (z - h.Z);
            if (d < best)
            {
                best = d;
                found = new BlockPos(x, y, z, h.dimension);
            }
        });
        stationCache[hut.BuildingId] = (found, now + 30_000);
        return found;
    }

    readonly Dictionary<int, (BlockPos? Pos, long Until)> stationCache = new();

    /// <summary>A dead animal for this butcher to pick up: within its work area, on the colony's land or killed by its hunters or herders, not skinned yet and not taken by someone else.</summary>
    public Entity? CarcassForButcher(Colony colony, Building hut, EntityAgent butcher, System.Func<BlockPos, bool> skip)
    {
        long now = sapi.World.ElapsedMilliseconds;
        int r = GatherRadius(hut);
        var center = hut.HutPos.ToVec3d().Add(0.5, 0, 0.5);
        return sapi.World.GetEntitiesAround(center, Math.Min(r, 64), 24, e => !e.Alive && e.GetBehavior<EntityBehaviorHarvestable>() is { IsHarvested: false })
            .Where(e => e.Pos.HorDistanceTo(center) <= r && !skip(e.Pos.AsBlockPos) && e is not EntityPlayer && e is not Settlers.EntitySettler)
            .Where(e => e.WatchedAttributes.GetString(HuntedByAttr) == colony.ColonyId || InClaim(colony, e.Pos.AsBlockPos) || ZoneAt(colony, e.Pos.AsBlockPos) != null)
            .Where(e => !carcassClaims.TryGetValue(e.EntityId, out var c) || c.By == butcher.EntityId || c.Until < now)
            .OrderBy(e => e.Pos.SquareDistanceTo(butcher.Pos.XYZ))
            .FirstOrDefault();
    }

    /// <summary>A chest of the colony's other huts or the warehouse with carcasses in it (a Hunter's Hut first), nearest first.</summary>
    public BlockEntityContainer? CarcassChest(Colony colony, Building butcherHut, Vec3d from)
    {
        var chests = colony.Buildings.Where(b => b.Type is BuildingTypes.Hunter && !b.Removing).SelectMany(b => HutStorage(colony, b))
            .Concat(WarehouseContainers(colony))
            .Where(c => c.Inventory.Any(s => !s.Empty && IsCarcass(s.Itemstack)));
        return chests.OrderBy(c => c.Pos.DistanceSqTo(from.X, from.Y, from.Z)).FirstOrDefault();
    }

    /// <summary>The Butcher's Hut storage a hunter takes its carcasses to: the nearest hut with a butcher and room. Null when there is none.</summary>
    public (Building Hut, BlockEntityContainer Chest)? ButcherDropOff(Colony colony, Vec3d from)
    {
        foreach (var hut in colony.Buildings.Where(b => b.Type == BuildingTypes.Butcher && !b.Removing && WorkerAt(colony, b, Jobs.Butcher) != null)
            .OrderBy(b => b.HutPos.DistanceSqTo(from.X, from.Y, from.Z)))
        {
            var chest = HutStorage(colony, hut).FirstOrDefault(c => c.Inventory.Any(s => s.Empty));
            if (chest != null) return (hut, chest);
        }
        return null;
    }

    /// <summary>Puts the carcasses a settler carries into this Butcher's Hut's storage. Returns how many went in.</summary>
    public int DeliverCarcasses(Colony colony, CitizenRecord citizen, Building butcherHut)
    {
        int n = 0;
        var storage = HutStorage(colony, butcherHut).ToList();
        while (TakeFromPack(colony, citizen, IsCarcass, 1) is { } carcass)
        {
            foreach (var chest in storage)
            {
                if (carcass.StackSize <= 0) break;
                PutItems(chest, carcass);
            }
            if (carcass.StackSize > 0)
            {
                AddToPack(colony, citizen, carcass, false);
                break;
            }
            n++;
        }
        return n;
    }

    /// <summary>The Butcher's Hut window lines.</summary>
    List<string> ButcherInfo(Colony colony, Building hut)
    {
        var lines = new List<string>
        {
            Lang.Get("createacolony:butcher-progress", hut.StatTotals.Where(s => s.Key.StartsWith("butchered:")).Sum(s => s.Value)),
            Lang.Get("createacolony:butcher-waiting", HutStorage(colony, hut).Sum(c => c.Inventory.Where(s => !s.Empty && IsCarcass(s.Itemstack)).Sum(s => s.StackSize)))
        };
        if (ButcheringMod)
        {
            lines.Add(Lang.Get(ButcheringStation(colony, hut) != null ? "createacolony:butchering-mod-station" : "createacolony:butchering-mod-nostation"));
        }
        return lines;
    }
}
