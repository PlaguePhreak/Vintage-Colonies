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
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The herder huts: Chicken Coop, Piggery, Shepherd's Hut, Goatherd's Hut and the Herdsman's Hut, whose herdsman
/// looks after any farm animal (other mods' too). A herder keeps the animals near its hut (its pen, the Work area
/// range): it fills the troughs with feed they eat, so they breed the game's way, collects the eggs from nest boxes,
/// milks sheep and goats into empty buckets, and once the pen holds more than the hut's herd size it culls the
/// spare grown animals with its knife, keeping a breeding pair or more. It skins the culls and brings the meat and
/// hides to the hut, where couriers pick them up. A cull is tagged like a hunter's kill (HuntedByAttr), so a
/// Butcher can take it instead (config HerderLeavesCulls). Settlers without a job keep a few hens at home the
/// same way (AiTaskSettlerChores).
/// </summary>
public partial class ColonyManager
{
    /// <summary>The herder huts and the job that works at each.</summary>
    public static readonly Dictionary<string, string> HerderHuts = new()
    {
        [BuildingTypes.ChickenCoop] = Jobs.ChickenHerder,
        [BuildingTypes.Piggery] = Jobs.Swineherd,
        [BuildingTypes.Shepherd] = Jobs.Shepherd,
        [BuildingTypes.Goatherd] = Jobs.Goatherd,
        [BuildingTypes.Herdsman] = Jobs.Herdsman,
    };

    public static bool IsHerderHut(string type) => HerderHuts.ContainsKey(type);
    public static bool IsHerderJob(string? job) => job != null && HerderHuts.ContainsValue(job);

    readonly Dictionary<string, List<EntityProperties>> herdTypes = new();

    /// <summary>The animal code patterns a herder hut looks after (config HerdAnimals).</summary>
    public string[] HerdPatterns(string hutType) => Config.HerdAnimals.GetValueOrDefault(hutType) ?? Array.Empty<string>();

    /// <summary>True for an animal that breeds or grows up in the game: a farm animal, not a monster.</summary>
    static bool Breeds(EntityProperties type) =>
        type.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() is "multiply" or "multiplybase" or "grow") == true;

    /// <summary>The kinds of animal that match the patterns and breed, worked out once per hut type.</summary>
    public List<EntityProperties> HerdTypes(string hutType, string[] patterns)
    {
        if (herdTypes.TryGetValue(hutType, out var known)) return known;
        var list = new List<EntityProperties>();
        foreach (var type in sapi.World.EntityTypes)
        {
            if (type?.Code == null || type.Code.Domain == "createacolony" || type.Code.Path.StartsWith("player")) continue;
            string path = type.Code.Path;
            if (!patterns.Any(p => WildcardUtil.Match(p, path))) continue;
            if (Config.GuardTargets.Any(p => WildcardUtil.Match(p, path)) || !Breeds(type)) continue;
            list.Add(type);
        }
        return herdTypes[hutType] = list;
    }

    /// <summary>True for a living animal of one of these kinds.</summary>
    public bool IsHerdAnimal(Entity e, List<EntityProperties> types) =>
        e.Alive && e is EntityAgent && e is not EntityPlayer && e is not Settlers.EntitySettler && types.Any(t => t.Code.Equals(e.Code));

    /// <summary>The animals of these kinds within radius blocks of a spot.</summary>
    public List<Entity> AnimalsNear(BlockPos center, int radius, List<EntityProperties> types)
    {
        if (types.Count == 0) return new List<Entity>();
        var c = center.ToVec3d().Add(0.5, 0, 0.5);
        return sapi.World.GetEntitiesAround(c, radius, 8, e => IsHerdAnimal(e, types))
            .Where(e => e.Pos.HorDistanceTo(c) <= radius)
            .ToList();
    }

    /// <summary>How far, in blocks, a herder's pen reaches from its hut (the Work area range).</summary>
    public int PenRadius(Building hut) => hut.WorkRadius > 0 ? hut.WorkRadius : Config.HerdRadius;

    /// <summary>The animals a herder hut looks after: its kinds within its pen.</summary>
    public List<Entity> Herd(Building hut)
    {
        var herd = AnimalsNear(hut.HutPos, PenRadius(hut), HerdTypes(hut.Type, HerdPatterns(hut.Type)));
        // The Herdsman keeps any animal, but not wild game that wanders past.
        if (hut.Type == BuildingTypes.Herdsman) herd.RemoveAll(e => Config.HerdsmanSkips.Any(p => WildcardUtil.Match(p, e.Code.Path)));
        return herd;
    }

    /// <summary>An animal no herder culls: named, tamed for riding, or of a kind in config HerdNeverCull.</summary>
    public bool NeverCull(Entity e) =>
        !string.IsNullOrEmpty(e.GetBehavior<EntityBehaviorNameTag>()?.DisplayName)
        || e.Properties.Attributes?["tamed"].AsBool() == true
        || Config.HerdNeverCull.Any(p => WildcardUtil.Match(p, e.Code.Path));

    /// <summary>How many animals a herder keeps before it culls: config HerdSize, more for each hut level.</summary>
    public int HerdSize(Building hut) => Config.HerdSize + Config.HerdSizePerLevel * Math.Max(0, hut.Level - 1);

    /// <summary>A young animal that still grows (chicks, poults, piglets, lambs, kids).</summary>
    public static bool IsYoungAnimal(Entity e) => e.GetBehavior("grow") != null || e.Code.Path.Contains("-baby") || e.Code.Path.Contains("poult");

    /// <summary>A male animal: a rooster, boar, ram or billy.</summary>
    public static bool IsMaleAnimal(Entity e)
    {
        var parts = e.Code.Path.Split('-');
        return parts.Contains("male") || parts.Contains("rooster") || parts.Contains("roosterpoult");
    }

    /// <summary>The kind of animal for counting breeding pairs: the first part of its code (chicken, pig, sheep, goat).</summary>
    static string Species(Entity e) => e.Code.Path.Split('-')[0];

    /// <summary>
    /// The spare grown animal to cull, or null while the pen holds no more than the herd size. It keeps at
    /// least config HerdKeepMales males and HerdKeepFemales females of each kind, never culls the young, and
    /// takes spare males first, from the kind there are most of.
    /// </summary>
    public Entity? CullPick(Building hut, List<Entity> herd, Vec3d from)
    {
        if (herd.Count <= HerdSize(hut)) return null;
        foreach (var kind in herd.GroupBy(Species).OrderByDescending(g => g.Count()))
        {
            var grown = kind.Where(e => !IsYoungAnimal(e) && !NeverCull(e)).ToList();
            var males = grown.Where(IsMaleAnimal).ToList();
            var females = grown.Where(e => !IsMaleAnimal(e)).ToList();
            var pick = males.Count > Config.HerdKeepMales ? males : females.Count > Config.HerdKeepFemales ? females : null;
            if (pick == null) continue;
            return pick.OrderBy(e => e.Pos.SquareDistanceTo(from)).First();
        }
        return null;
    }

    /// <summary>A herder strikes the animal it culls; returns true when it died, and tags the carcass as the colony's.</summary>
    public bool HerderStrike(Colony colony, CitizenRecord citizen, Building hut, EntityAgent herder, Entity animal)
    {
        var hand = herder.RightHandItemSlot;
        var weapon = hand?.Itemstack is { } held && IsWeapon(held) ? held : null;
        float damage = (weapon != null ? WeaponDamage(weapon) : Config.GuardFistDamage) * Config.HerderCullDamageMult;
        damage *= ClampEffect(1f + 0.03f * UsableLevel(colony, citizen, "husbandry") + 0.03f * UsableLevel(colony, citizen, Strength));
        animal.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = herder,
            CauseEntity = herder,
            Type = weapon != null ? EnumDamageType.SlashingAttack : EnumDamageType.BluntAttack,
            DamageTier = weapon?.Collectible.ToolTier ?? 0
        }, damage);
        if (weapon != null && hand != null) WearTool(herder, hand);
        if (animal.Alive) return false;
        animal.WatchedAttributes.SetString(HuntedByAttr, colony.ColonyId);
        AddStat(hut, "cull:" + animal.Code.Path);
        return true;
    }

    /// <summary>
    /// True when the herder leaves its culls on the ground for a Butcher (config HerderLeavesCulls). The Butcher's
    /// Hut work can make this depend on a butcher being at work.
    /// </summary>
    public bool HerderLeavesCulls(Colony colony) => Config.HerderLeavesCulls;

    // ---- Troughs ----------------------------------------------------------------------------

    /// <summary>The troughs within radius blocks of a spot.</summary>
    public List<BlockEntityTrough> TroughsNear(BlockPos at, int radius) => AllNear<BlockEntityTrough>(at, radius);

    /// <summary>The trough's fill setting for a stack, or null when the trough does not take it.</summary>
    ContentConfig? TroughConfig(BlockEntityTrough trough, ItemStack stack) =>
        trough.contentConfigs == null ? null : ItemSlotTrough.getContentConfig(sapi.World, trough.contentConfigs, new DummySlot(stack));

    /// <summary>True when the stack can go in the trough (it is empty or holds the same) and one of these kinds eats it.</summary>
    public bool FeedFits(BlockEntityTrough trough, ItemStack stack, List<EntityProperties> eaters)
    {
        if (TroughConfig(trough, stack) is not { } cfg) return false;
        var inside = trough.Inventory[0].Itemstack;
        if (inside != null && !inside.Equals(sapi.World, stack, GlobalConstants.IgnoredStackAttributes)) return false;
        var block = trough.Block as BlockTroughBase;
        foreach (var type in eaters)
        {
            if (block != null && block.UnsuitableForEntity(type.Code.Path)) continue;
            if (type.Attributes?["creatureDiet"].AsObject<CreatureDiet>()?.Matches(stack) == true) return true;
        }
        return false;
    }

    /// <summary>How much more of its current feed the trough takes; 0 when full. An empty trough counts as wanting feed.</summary>
    public bool TroughWantsFeed(BlockEntityTrough trough)
    {
        var inside = trough.Inventory[0].Itemstack;
        if (inside == null) return true;
        if (TroughConfig(trough, inside) is not { } cfg) return false;
        // Topped up once it is down to half, so a herder is not back every few minutes.
        return inside.StackSize <= cfg.QuantityPerFillLevel * cfg.MaxFillLevels / 2;
    }

    /// <summary>Puts feed the settler carries into the trough, the way a player fills it; returns how much went in.</summary>
    public int FillTrough(Colony colony, CitizenRecord citizen, BlockEntityTrough trough, List<EntityProperties> eaters)
    {
        int moved = 0;
        for (int i = 0; i < 64; i++)
        {
            var feed = PackItems(citizen).FirstOrDefault(s => FeedFits(trough, s, eaters));
            if (feed == null || TroughConfig(trough, feed) is not { } cfg) break;
            var slot = trough.Inventory[0];
            int room = cfg.QuantityPerFillLevel * cfg.MaxFillLevels - (slot.Itemstack?.StackSize ?? 0);
            int portions = Math.Min(room, PackCount(citizen, s => s.Equals(sapi.World, feed, GlobalConstants.IgnoredStackAttributes))) / Math.Max(1, cfg.QuantityPerFillLevel);
            if (portions <= 0) break;
            var taken = TakeFromPack(colony, citizen, s => s.Equals(sapi.World, feed, GlobalConstants.IgnoredStackAttributes), portions * cfg.QuantityPerFillLevel);
            if (taken == null) break;
            if (slot.Itemstack == null) slot.Itemstack = taken;
            else slot.Itemstack.StackSize += taken.StackSize;
            moved += taken.StackSize;
            slot.MarkDirty();
            trough.MarkDirty(true);
        }
        return moved;
    }

    /// <summary>Feed for the trough in these containers, the nearest first; null when there is none.</summary>
    public BlockEntityContainer? FeedIn(IEnumerable<BlockEntityContainer> containers, BlockEntityTrough trough, List<EntityProperties> eaters) =>
        containers.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && FeedFits(trough, s.Itemstack, eaters)));

    // ---- Nest boxes -------------------------------------------------------------------------

    /// <summary>The nest boxes within radius blocks of a spot.</summary>
    public List<BlockEntityHenBox> NestsNear(BlockPos at, int radius) => AllNear<BlockEntityHenBox>(at, radius);

    static bool IsFertile(ItemStack egg) => (egg.Attributes["chick"] as TreeAttribute)?.GetString("code") is { Length: > 0 };

    /// <summary>True when the nest has eggs to take: any egg, or only plain ones while the flock should grow (fertileToo false).</summary>
    public static bool HasEggsToTake(BlockEntityHenBox nest, bool fertileToo) =>
        nest.Inventory.Any(s => !s.Empty && (fertileToo || !IsFertile(s.Itemstack)));

    /// <summary>Takes the eggs from a nest box into the settler's inventory, leaving fertile ones to hatch unless fertileToo.</summary>
    public int TakeEggs(Colony colony, CitizenRecord citizen, BlockEntityHenBox nest, bool fertileToo)
    {
        int taken = 0;
        foreach (var slot in nest.Inventory)
        {
            if (slot.Empty || (!fertileToo && IsFertile(slot.Itemstack))) continue;
            var egg = slot.Itemstack.Clone();
            // An egg taken out is just food: it no longer hatches.
            egg.Attributes.RemoveAttribute("chick");
            int n = AddToPack(colony, citizen, egg);
            if (n <= 0) break;
            taken += n;
            slot.TakeOut(n);
            nest.Inventory.DidModifyItemSlot(slot);
        }
        if (taken > 0)
        {
            nest.MarkDirty(true);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/player/collect"), nest.Pos.X + 0.5, nest.Pos.Y + 0.5, nest.Pos.Z + 0.5, null);
        }
        return taken;
    }

    // ---- Milking ----------------------------------------------------------------------------

    /// <summary>A wooden bucket (or another mod's bucket).</summary>
    public static bool IsBucket(ItemStack? s) => s?.Block is BlockBucket;

    /// <summary>An empty bucket, ready for milk or water.</summary>
    public static bool IsEmptyBucket(ItemStack? s) => s?.Block is BlockBucket b && b.GetContent(s) == null;

    /// <summary>A milkable animal in the herd that has milk today.</summary>
    public Entity? MilkReady(List<Entity> herd, Vec3d from, System.Func<long, bool> skip) => herd
        .Where(e => !skip(e.EntityId) && e.GetBehavior<EntityBehaviorMilkable>() is { } milk && CanMilkNow(e, milk))
        .OrderBy(e => e.Pos.SquareDistanceTo(from))
        .FirstOrDefault();

    /// <summary>The game's own test, without starting to milk: lactating, calm, and not milked today.</summary>
    bool CanMilkNow(Entity e, EntityBehaviorMilkable milk)
    {
        if (e.WatchedAttributes.GetFloat("stressLevel") > 0.1f) return false;
        var cal = e.World.Calendar;
        if (cal.TotalHours - e.WatchedAttributes.GetFloat("lastMilkedTotalHours") < cal.HoursPerDay) return false;
        var mult = e.GetBehavior<EntityBehaviorMultiply>();
        double lastBirth = mult?.TotalDaysLastBirth ?? -9999;
        return cal.TotalDays - lastBirth < Config.HerderLactatingDays;
    }

    /// <summary>Milks the animal into an empty bucket the herder carries; the full bucket goes back into its inventory.</summary>
    public bool Milk(Colony colony, CitizenRecord citizen, EntityAgent herder, Building hut, Entity animal)
    {
        if (animal.GetBehavior<EntityBehaviorMilkable>() is not { } milk || !milk.TryBeginMilking()) return false;
        var bucket = TakeFromPack(colony, citizen, IsEmptyBucket, 1);
        if (bucket == null) return false;
        var slot = new DummySlot(bucket);
        milk.MilkingComplete(slot, herder);
        if (slot.Itemstack != null) StoreHarvest(colony, hut, citizen, new[] { slot.Itemstack }, hut.HutPos);
        AddStat(hut, "milked");
        return true;
    }

    // ---- Garden hens ---------------------------------------------------------------------------

    /// <summary>The chickens a household keeps (the Chicken Coop's kinds).</summary>
    public List<EntityProperties> HenKinds() => HerdTypes(BuildingTypes.ChickenCoop, HerdPatterns(BuildingTypes.ChickenCoop));

    /// <summary>The home's yard: its zone and config JoblessHenReach blocks around it.</summary>
    Cuboidi HomeYard(Building home)
    {
        var (min, max) = ZoneOf(home);
        int r = Math.Max(0, Config.JoblessHenReach);
        return new Cuboidi(min.X - r, min.Y - 2, min.Z - r, max.X + r, max.Y + 3, max.Z + r);
    }

    public List<BlockEntityTrough> HomeTroughs(Building home) => FindBlockEntities<BlockEntityTrough>(HomeYard(home)).ToList();
    public List<BlockEntityHenBox> HomeNests(Building home) => FindBlockEntities<BlockEntityHenBox>(HomeYard(home)).ToList();

    /// <summary>The chickens in the home's yard.</summary>
    public List<Entity> HomeHens(Building home)
    {
        var yard = HomeYard(home);
        var kinds = HenKinds();
        var center = new Vec3d((yard.X1 + yard.X2) / 2.0, (yard.Y1 + yard.Y2) / 2.0, (yard.Z1 + yard.Z2) / 2.0);
        float range = Math.Max(yard.X2 - yard.X1, yard.Z2 - yard.Z1) / 2f + 1;
        return sapi.World.GetEntitiesAround(center, range, 6, e => IsHerdAnimal(e, kinds))
            .Where(e => yard.ContainsOrTouches(e.Pos.AsBlockPos))
            .ToList();
    }

    // ---- Hut window ---------------------------------------------------------------------------

    /// <summary>The herder hut's window lines: the pen, then what it gave.</summary>
    string HerderInfo(Building hut)
    {
        var types = HerdTypes(hut.Type, HerdPatterns(hut.Type));
        if (types.Count == 0) return Lang.Get("createacolony:herder-no-kinds");
        var herd = Herd(hut);
        int r = PenRadius(hut);
        string pen = Lang.Get("createacolony:herder-pen", herd.Count, HerdSize(hut), TroughsNear(hut.HutPos, r).Count, NestsNear(hut.HutPos, r).Count);
        int culled = hut.StatTotals.Where(s => s.Key.StartsWith("cull:")).Sum(s => s.Value);
        string done = Lang.Get("createacolony:herder-done", hut.StatTotals.GetValueOrDefault("eggs"), hut.StatTotals.GetValueOrDefault("milked"), culled);
        return pen + "<br>" + done;
    }

    /// <summary>At a herder hut, everything goes to the warehouse but its knife, buckets and the feed its troughs take.</summary>
    ItemMatch HerderHutJunk(Colony colony, Building hut)
    {
        var types = HerdTypes(hut.Type, HerdPatterns(hut.Type));
        var troughs = TroughsNear(hut.HutPos, PenRadius(hut));
        var recipeInputs = HutRecipeInputs(colony, hut);
        return s => !IsKnife(s) && !IsEmptyBucket(s) && !troughs.Any(t => FeedFits(t, s, types)) && !recipeInputs(s);
    }
}
