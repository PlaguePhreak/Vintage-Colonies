using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Raids (0.181.0, design doc https://claude.ai/artifact/82qtsGCQ2M6wJN4ce3duZH with Aaron's answers): once a
/// colony has RaidMinSettlers grown settlers, a raid comes every RaidMinDays to an in-game month (never more than
/// RaidMaxDays apart), while a member is online. The members are warned in chat and on the Town Hall; settlers who
/// aren't guards go home (AiTaskSettlerShelter). After RaidWarningHours the raiders appear past the colony's border
/// and march on the Town Hall: outlaws (bandits, EntityBandit with AiTaskBanditRaid), a wolf pack, or drifters
/// (night only), the monsters walking with AiTaskRaidMarch. Raiders never break blocks; bandits steal and set dry
/// grass on fire, and the fire burns what it reaches. The raid is won when every raider is dead, and ends when the
/// rest run off or give up after RaidGiveUpHours. A raid in progress is not saved: after a restart its raiders go.
/// </summary>
public partial class ColonyManager
{
    public const string RaidAttr = "createacolony:raid";
    const int RaidTickMs = 2000;

    public static readonly string[] RaidSides = { "north", "east", "south", "west" };

    public class ActiveRaid
    {
        public string ColonyId = "";
        public string Faction = "";
        public string Side = "";
        public Vec3d Gather = new();
        public int Size;
        public double AttackAtHours;
        public bool Attacking;
        public bool Fleeing;
        public int Spawned;
        public int Killed;
        public int Escaped;
        public readonly List<long> Raiders = new();
        /// <summary>What each bandit stole, by entity id; dropped where it dies.</summary>
        public readonly Dictionary<long, List<ItemStack>> Loot = new();
        public bool StoleLogged;
        public bool FireLogged;
    }

    readonly Dictionary<string, ActiveRaid> raids = new();
    bool raidTickStarted;

    void StartRaids()
    {
        if (raidTickStarted) return;
        raidTickStarted = true;
        sapi.Event.RegisterGameTickListener(OnRaidTick, RaidTickMs, 1500);
        sapi.Event.OnEntityLoaded += OnRaiderLoaded;
        sapi.Event.OnEntityDeath += OnRaiderDeath;
    }

    public ActiveRaid? RaidOn(Colony colony) => raids.TryGetValue(colony.ColonyId, out var raid) ? raid : null;

    /// <summary>The raid this creature is part of, or null.</summary>
    public ActiveRaid? RaidOf(Entity e)
    {
        string id = e.WatchedAttributes.GetString(RaidAttr, "");
        return id.Length > 0 && raids.TryGetValue(id, out var raid) && raid.Raiders.Contains(e.EntityId) ? raid : null;
    }

    /// <summary>Settlers who aren't guards stay home while a raid is on its way or under way.</summary>
    public bool HidesFromRaid(Colony colony, CitizenRecord citizen) => citizen.JobCode != Jobs.Guard && RaidOn(colony) is { Fleeing: false };

    int GrownSettlers(Colony colony) => colony.Citizens.Count(c => c.Alive && !c.IsChild && !c.Visitor);

    bool MemberOnline(Colony colony) => colony.Members.Any(m => sapi.World.PlayerByUid(m.PlayerUid) is IServerPlayer { ConnectionState: EnumClientState.Playing });

    IEnumerable<IServerPlayer> OnlineMembers(Colony colony) => colony.Members
        .Select(m => sapi.World.PlayerByUid(m.PlayerUid)).OfType<IServerPlayer>().Where(p => p.ConnectionState == EnumClientState.Playing);

    /// <summary>Days to the next raid: at least RaidMinDays, at most an in-game month (or RaidMaxDays, if less).</summary>
    double RaidGap()
    {
        double low = Math.Max(1, Config.RaidMinDays);
        double high = Math.Clamp(sapi.World.Calendar.DaysPerMonth, low, Math.Max(low, Config.RaidMaxDays));
        return low + sapi.World.Rand.NextDouble() * (high - low);
    }

    void OnRaidTick(float dt)
    {
        double day = sapi.World.Calendar.TotalDays;
        foreach (var colony in colonies.Values)
        {
            if (RaidOn(colony) is { } raid)
            {
                UpdateRaid(colony, raid);
                continue;
            }
            if (!Config.Raids || colony.RaidMode != 0 || GrownSettlers(colony) < Config.RaidMinSettlers) continue;
            if (sapi.World.BlockAccessor.GetChunkAtBlockPos(colony.TownHallPos) == null || !MemberOnline(colony)) continue;
            if (colony.NextRaidDay <= 0)
            {
                colony.NextRaidDay = day + RaidGap();
                MarkDirty(colony);
                continue;
            }
            if (day >= colony.NextRaidDay) StartRaid(colony, null);
        }
    }

    static bool IsNightHour(float hour) => hour >= 20 || hour < 5;

    /// <summary>
    /// Sends a raid: picks a faction (drifters only at night, outlaws only by day), its size and side, finds where
    /// the raiders gather, and warns the members. Returns a lang key when no raid could be sent.
    /// </summary>
    public string? StartRaid(Colony colony, string? faction)
    {
        StartRaids();
        if (RaidOn(colony) != null) return "createacolony:raid-already";
        bool night = IsNightHour(sapi.World.Calendar.HourOfDay);
        var choices = Config.RaidFactions.Where(f => f switch
        {
            "outlaws" => !night,
            "drifters" => night && RaiderTypes(Config.RaidDrifters).Count > 0,
            "wolves" => RaiderTypes(Config.RaidWolves).Count > 0,
            _ => false
        }).ToList();
        if (faction != null) choices = choices.Contains(faction) ? new List<string> { faction } : new List<string>();
        if (choices.Count == 0)
        {
            colony.NextRaidDay = sapi.World.Calendar.TotalDays + 0.25; // try again in a few hours
            return "createacolony:raid-no-faction";
        }

        var raid = new ActiveRaid { ColonyId = colony.ColonyId, Faction = choices[sapi.World.Rand.Next(choices.Count)] };
        raid.Size = RaidSizeFor(colony);
        var sides = RaidSides.OrderBy(_ => sapi.World.Rand.Next()).ToList();
        Vec3d? gather = null;
        foreach (var side in sides)
        {
            gather = GatherSpot(colony, side);
            if (gather == null) continue;
            raid.Side = side;
            break;
        }
        if (gather == null)
        {
            colony.NextRaidDay = sapi.World.Calendar.TotalDays + 0.25;
            return "createacolony:raid-no-spot";
        }
        raid.Gather = gather;
        raid.AttackAtHours = sapi.World.Calendar.TotalHours + Config.RaidWarningHours;
        raids[colony.ColonyId] = raid;

        colony.LastRaidDay = sapi.World.Calendar.TotalDays;
        colony.NextRaidDay = colony.LastRaidDay + RaidGap();
        colony.RaidsTotal++;
        MarkDirty(colony);
        TellMembers(colony, "createacolony:raid-warning-" + raid.Faction, "createacolony:side-" + raid.Side, raid.Size);
        LogEvent(colony, "event-raid-warning-" + raid.Faction, Lang.Get("createacolony:side-" + raid.Side));
        sapi.World.PlaySoundAt(new AssetLocation("sounds/effect/bonebreak"), colony.TownHallPos.X + 0.5, colony.TownHallPos.Y + 1, colony.TownHallPos.Z + 0.5, null, false, 64f, 1f);
        return null;
    }

    /// <summary>One raider per RaidSettlersPerRaider grown settlers plus one per Town Hall level, times the colony's raid size, growing a little with every three raids won.</summary>
    int RaidSizeFor(Colony colony)
    {
        int townHall = GetBuilding(colony, colony.TownHallPos)?.Level ?? 1;
        double size = Math.Ceiling(GrownSettlers(colony) / Math.Max(1, Config.RaidSettlersPerRaider)) + townHall + colony.RaidsWon / 3;
        size *= colony.RaidSize switch { 0 => 0.6, 2 => 1.5, _ => 1.0 };
        return Math.Clamp((int)Math.Round(size), 1, Math.Max(1, Config.RaidMaxRaiders));
    }

    /// <summary>A place to stand past the colony's border on that side, in loaded land, out of the water; null when there is none.</summary>
    Vec3d? GatherSpot(Colony colony, string side)
    {
        var areas = ClaimAreas(colony);
        int x1 = colony.TownHallPos.X - 16, x2 = colony.TownHallPos.X + 16, z1 = colony.TownHallPos.Z - 16, z2 = colony.TownHallPos.Z + 16;
        foreach (var a in areas)
        {
            x1 = Math.Min(x1, a.X1); x2 = Math.Max(x2, a.X2); z1 = Math.Min(z1, a.Z1); z2 = Math.Max(z2, a.Z2);
        }
        var ba = sapi.World.BlockAccessor;
        var rand = sapi.World.Rand;
        int far = rand.Next(Config.RaidSpawnDistanceMin, Math.Max(Config.RaidSpawnDistanceMin, Config.RaidSpawnDistanceMax) + 1);
        for (int distance = far; distance >= 16; distance -= 8)
        {
            int along = rand.Next(-16, 17);
            int cx = (x1 + x2) / 2 + along, cz = (z1 + z2) / 2 + along;
            var column = side switch
            {
                "north" => new BlockPos(cx, 0, z1 - distance),
                "south" => new BlockPos(cx, 0, z2 + distance),
                "east" => new BlockPos(x2 + distance, 0, cz),
                _ => new BlockPos(x1 - distance, 0, cz)
            };
            if (ba.GetChunkAtBlockPos(column.X, 1, column.Z) == null) continue;
            column.Y = ba.GetTerrainMapheightAt(column) + 1;
            if (ba.GetBlock(column, BlockLayersAccess.Fluid).IsLiquid() || ba.GetBlock(column.DownCopy(), BlockLayersAccess.Fluid).IsLiquid()) continue;
            return new Vec3d(column.X + 0.5, column.Y, column.Z + 0.5);
        }
        return null;
    }

    List<EntityProperties> RaiderTypes(string[] patterns) => sapi.World.EntityTypes
        .Where(t => t.Code != null && patterns.Any(p => WildcardUtil.Match(new AssetLocation(p), t.Code))).ToList();

    void UpdateRaid(Colony colony, ActiveRaid raid)
    {
        double hours = sapi.World.Calendar.TotalHours;
        if (!raid.Attacking)
        {
            if (hours < raid.AttackAtHours) return;
            if (sapi.World.BlockAccessor.GetChunkAtBlockPos(raid.Gather.AsBlockPos) == null)
            {
                // Nobody is near enough for the land to be loaded: the raid comes to nothing.
                raids.Remove(colony.ColonyId);
                return;
            }
            SpawnRaiders(colony, raid);
            raid.Attacking = true;
            TellMembers(colony, "createacolony:raid-attack-" + raid.Faction);
            return;
        }

        foreach (long id in raid.Raiders.ToList())
        {
            var e = sapi.World.GetEntityById(id);
            if (e == null || !e.Alive)
            {
                raid.Raiders.Remove(id);
                if (e == null) raid.Escaped++; // unloaded or walked off
            }
        }

        // Outlaws lose heart once enough of them are down; everyone gives up after a while.
        if (!raid.Fleeing && raid.Faction == "outlaws" && raid.Killed >= Math.Ceiling(raid.Spawned * Config.BanditFleeShare))
        {
            raid.Fleeing = true;
            TellMembers(colony, "createacolony:raid-flee-outlaws");
        }
        if (hours >= raid.AttackAtHours + Config.RaidGiveUpHours && !raid.Fleeing)
        {
            raid.Fleeing = true;
            TellMembers(colony, "createacolony:raid-giveup");
        }
        // Monsters don't walk home: once the raid is over for them they go, out of sight or not. Bandits get a while to run.
        if (raid.Fleeing && raid.Raiders.Count > 0)
        {
            bool late = hours >= raid.AttackAtHours + Config.RaidGiveUpHours + 0.5;
            foreach (long id in raid.Raiders.ToList())
            {
                if (sapi.World.GetEntityById(id) is not { } e) continue;
                bool gone = e is EntityBandit ? late || OffColonyLand(colony, e, 24) : raid.Faction != "outlaws";
                if (!gone) continue;
                raid.Loot.Remove(id);
                e.Die(EnumDespawnReason.Removed);
                raid.Raiders.Remove(id);
                raid.Escaped++;
            }
        }

        if (raid.Raiders.Count == 0) EndRaid(colony, raid);
    }

    bool OffColonyLand(Colony colony, Entity e, int margin)
    {
        var pos = e.Pos;
        return !ClaimAreas(colony).Any(a => pos.X >= a.X1 - margin && pos.X <= a.X2 + margin && pos.Z >= a.Z1 - margin && pos.Z <= a.Z2 + margin);
    }

    void EndRaid(Colony colony, ActiveRaid raid)
    {
        raids.Remove(colony.ColonyId);
        bool won = raid.Killed > 0 && raid.Killed >= raid.Escaped;
        if (won)
        {
            colony.RaidsWon++;
            TellMembers(colony, "createacolony:raid-won-" + raid.Faction);
            LogEvent(colony, "event-raid-won", raid.Killed.ToString());
            foreach (var c in colony.Citizens)
            {
                if (c.Alive && HasMood(c)) AddThought(c, "raidwon");
            }
        }
        else
        {
            TellMembers(colony, "createacolony:raid-ended");
            LogEvent(colony, "event-raid-ended");
        }
        MarkDirty(colony);
    }

    // ---- Spawning -----------------------------------------------------------------------------

    void SpawnRaiders(Colony colony, ActiveRaid raid)
    {
        var rand = sapi.World.Rand;
        var types = raid.Faction switch
        {
            "wolves" => RaiderTypes(Config.RaidWolves),
            "drifters" => RaiderTypes(Config.RaidDrifters),
            _ => new[] { "male", "female" }.Select(sex => sapi.World.GetEntityType(new AssetLocation(CreateAColonyModSystem.ModId, "bandit-" + sex))).OfType<EntityProperties>().ToList()
        };
        if (types.Count == 0) return;
        int archers = raid.Faction == "outlaws" ? (int)Math.Round(raid.Size * Config.BanditArcherShare) : 0;
        int torches = raid.Faction == "outlaws" ? Math.Max(1, (int)Math.Round(raid.Size * Config.BanditTorchShare)) : 0;
        var around = raid.Gather.AsBlockPos;
        for (int i = 0; i < raid.Size; i++)
        {
            var entity = sapi.World.ClassRegistry.CreateEntity(types[rand.Next(types.Count)]);
            var spot = new Vec3d(raid.Gather.X + rand.Next(-4, 5), raid.Gather.Y, raid.Gather.Z + rand.Next(-4, 5));
            var column = spot.AsBlockPos;
            column.Y = sapi.World.BlockAccessor.GetTerrainMapheightAt(column) + 1;
            entity.Pos.SetPos(column.X + 0.5, column.Y, column.Z + 0.5);
            entity.Pos.Yaw = (float)(rand.NextDouble() * Math.PI * 2);
            entity.WatchedAttributes.SetString(RaidAttr, colony.ColonyId);
            sapi.World.SpawnEntity(entity);
            raid.Raiders.Add(entity.EntityId);
            raid.Spawned++;
            if (entity is EntityBandit bandit) EquipBandit(bandit, archer: i < archers, torch: i >= archers && i < archers + torches);
            else if (entity is EntityAgent agent) AddRaidMarch(agent);
        }
        _ = around;
    }

    /// <summary>Gives a monster raider the march on the Town Hall, below its own hunting and fighting.</summary>
    void AddRaidMarch(EntityAgent agent)
    {
        var manager = agent.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
        if (manager == null) return;
        var config = new JsonObject(JObject.Parse("{\"code\":\"raidmarch\",\"priority\":1.3,\"movespeed\":0.03,\"animation\":\"walk\"}"));
        var task = new AiTaskRaidMarch(agent, config, new JsonObject(new JObject()));
        manager.AddTask(task);
        task.AfterInitialize();
    }

    /// <summary>Where a raid marches: the Town Hall.</summary>
    public Vec3d RaidTarget(ActiveRaid raid) =>
        GetById(raid.ColonyId) is { } colony ? colony.TownHallPos.ToVec3d().Add(0.5, 1, 0.5) : raid.Gather;

    /// <summary>A raider loaded from the save, or left over from a raid that is over: it goes.</summary>
    void OnRaiderLoaded(Entity entity)
    {
        if (entity.WatchedAttributes.GetString(RaidAttr, "").Length == 0 || RaidOf(entity) != null) return;
        sapi.Event.EnqueueMainThreadTask(() => { if (entity.Alive) entity.Die(EnumDespawnReason.Removed); }, "createacolony-raider");
    }

    void OnRaiderDeath(Entity entity, DamageSource? source)
    {
        if (RaidOf(entity) is not { } raid) return;
        raid.Raiders.Remove(entity.EntityId);
        raid.Killed++;
        var at = entity.Pos.XYZ.Add(0, 0.5, 0);
        if (raid.Loot.Remove(entity.EntityId, out var stolen))
        {
            foreach (var stack in stolen) sapi.World.SpawnItemEntity(stack, at);
        }
        if (entity is EntityBandit bandit) DropBanditGear(bandit, at);
    }

    // ---- Bandits ------------------------------------------------------------------------------

    static readonly string[] BanditClothes = { "upperbody", "lowerbody", "foot", "head" };
    /// <summary>Words in the game's clothes codes that fit an outlaw: plain, rough, worn country clothes in browns and greens.</summary>
    static readonly string[] OutlawWords = { "peasant", "rough", "hunter", "forest", "farmer", "woodsman", "shepherd", "hood", "leather", "linen", "brown", "green", "tattered", "simple", "plain", "worn" };

    List<ItemStack>? outlawClothes;
    List<Item>? outlawMelee;
    List<Item>? outlawBows;

    List<ItemStack> OutlawClothes() => outlawClothes ??= sapi.World.Items
        .Where(i => i?.Code != null && i.Code.Path.StartsWith("clothes-") && OutlawWords.Any(w => i.Code.Path.Contains(w)))
        .Select(i => new ItemStack(i)).ToList();

    List<Item> OutlawMelee() => outlawMelee ??= sapi.World.Items
        .Where(i => i?.Code != null && i.Tool is EnumTool.Spear or EnumTool.Club or EnumTool.Knife
            && (i.Code.Path.Contains("flint") || i.Code.Path.Contains("copper") || i.Code.Path.Contains("wood") || i.Code.Path.Contains("stone")))
        .Cast<Item>().ToList();

    List<Item> OutlawBows() => outlawBows ??= sapi.World.Items
        .Where(i => i is ItemBow && (i.Code.Path.Contains("crude") || i.Code.Path.Contains("simple"))).Cast<Item>().ToList();

    /// <summary>Dresses and arms a new outlaw: country clothes, a spear, club or knife (spears most often), or a simple bow for an archer, and a torch for some.</summary>
    void EquipBandit(EntityBandit bandit, bool archer, bool torch)
    {
        var rand = sapi.World.Rand;
        if (bandit.GetBehavior<EntityBehaviorHealth>() is { } health)
        {
            health.BaseMaxHealth = Config.BanditHealth;
            health.Health = Config.BanditHealth;
        }
        if (bandit.Code.Path.EndsWith("female") && bandit.GetBehavior<EntityBehaviorExtraSkinnable>() is { } skin)
        {
            foreach (var part in new[] { "beard", "mustache" })
            {
                if (skin.AvailableSkinPartsByCode.TryGetValue(part, out var available) && available.VariantsByCode.ContainsKey("none")) skin.selectSkinPart(part, "none", false, false);
            }
            bandit.WatchedAttributes.MarkPathDirty("skinConfig");
        }
        if (bandit.GetBehavior<EntityBehaviorSeraphInventory>() is { Inventory: { } gear } dress)
        {
            var clothes = OutlawClothes();
            foreach (var category in BanditClothes)
            {
                var fits = clothes.Where(c => ClothesSlotOf(c) is var slot && slot >= 0 && slot < gear.Count
                    && string.Equals(c.ItemAttributes?["clothescategory"].AsString(), category, StringComparison.OrdinalIgnoreCase)).ToList();
                if (fits.Count == 0 || category == "head" && rand.NextDouble() < 0.4) continue;
                var pick = fits[rand.Next(fits.Count)];
                gear[ClothesSlotOf(pick)].Itemstack = pick.Clone();
            }
            dress.storeInv();
        }

        Item? weapon = null;
        if (archer && OutlawBows() is { Count: > 0 } bows) weapon = bows[rand.Next(bows.Count)];
        else
        {
            var melee = OutlawMelee();
            var spears = melee.Where(i => i.Tool == EnumTool.Spear).ToList();
            var pool = spears.Count > 0 && rand.NextDouble() < 0.6 ? spears : melee;
            if (pool.Count > 0) weapon = pool[rand.Next(pool.Count)];
        }
        if (weapon != null && bandit.RightHandItemSlot is { } hand) hand.Itemstack = new ItemStack(weapon);
        bandit.Archer = weapon is ItemBow;
        bandit.ArrowsLeft = bandit.Archer ? Config.BanditArrows : 0;
        if (torch && bandit.LeftHandItemSlot is { } left && sapi.World.GetBlock(new AssetLocation("game:torch-basic-lit-up")) is { } lit)
        {
            left.Itemstack = new ItemStack(lit);
            bandit.CarriesTorch = true;
        }
        bandit.GetBehavior<EntityBehaviorVillagerInv>()?.storeInv();
        bandit.GetBehavior<EntityBehaviorNameTag>()?.SetName(Lang.Get("createacolony:bandit-outlaw"));
    }

    /// <summary>A bandit got away (with its loot, if it had any): it goes, and its loot with it.</summary>
    public void BanditEscaped(EntityBandit bandit)
    {
        if (RaidOf(bandit) is not { } raid) return;
        raid.Loot.Remove(bandit.EntityId);
        raid.Raiders.Remove(bandit.EntityId);
        raid.Escaped++;
        bandit.Die(EnumDespawnReason.Removed);
    }

    /// <summary>A dead outlaw drops a few rusty gears now and then, its weapon sometimes, and its arrows.</summary>
    void DropBanditGear(EntityBandit bandit, Vec3d at)
    {
        var rand = sapi.World.Rand;
        if (Config.BanditMaxGears > 0 && sapi.World.GetItem(new AssetLocation(ColonyMoney.GearCode)) is { } gear)
        {
            int gears = rand.Next(Config.BanditMaxGears + 1);
            if (gears > 0) sapi.World.SpawnItemEntity(new ItemStack(gear, gears), at);
        }
        if (bandit.RightHandItemSlot?.Itemstack is { } weapon && rand.NextDouble() < 0.35) sapi.World.SpawnItemEntity(weapon.Clone(), at);
        if (bandit.ArrowsLeft > 0 && BanditArrow() is { } arrow)
        {
            arrow.StackSize = Math.Min(bandit.ArrowsLeft, rand.Next(2, 7));
            sapi.World.SpawnItemEntity(arrow, at);
        }
        // Its own clothes and torch go with it, so a fight doesn't leave heaps of rags behind.
        if (bandit.RightHandItemSlot is { } hand) hand.Itemstack = null;
    }

    Item? banditArrow;

    /// <summary>The arrow outlaws shoot: a flint arrow, or any arrow the game has.</summary>
    public ItemStack? BanditArrow()
    {
        banditArrow ??= sapi.World.GetItem(new AssetLocation("game:arrow-flint"))
            ?? sapi.World.Items.FirstOrDefault(i => i?.Code != null && i.Code.Path.StartsWith("arrow-")) as Item;
        return banditArrow == null ? null : new ItemStack(banditArrow);
    }

    /// <summary>
    /// A bandit at a chest on the colony's land takes one stack, the biggest it finds, and carries it off. True
    /// when it took something.
    /// </summary>
    public bool StealFrom(EntityBandit bandit, BlockEntityContainer chest)
    {
        if (RaidOf(bandit) is not { } raid || GetById(raid.ColonyId) is not { } colony) return false;
        var slot = chest.Inventory.Where(s => !s.Empty).OrderByDescending(s => s.StackSize).FirstOrDefault();
        if (slot == null) return false;
        var stolen = slot.TakeOutWhole();
        slot.MarkDirty();
        chest.MarkDirty(true);
        if (!raid.Loot.TryGetValue(bandit.EntityId, out var list)) raid.Loot[bandit.EntityId] = list = new List<ItemStack>();
        list.Add(stolen);
        bandit.HasLoot = true;
        if (!raid.StoleLogged)
        {
            raid.StoleLogged = true;
            TellMembers(colony, "createacolony:raid-stolen", stolen.GetName());
            LogEvent(colony, "event-raid-stolen", stolen.GetName());
        }
        return true;
    }

    /// <summary>A chest on the colony's land within the radius that has something in it, for a bandit to rob; null when none.</summary>
    public BlockEntityContainer? ChestToRob(EntityBandit bandit, int radius, HashSet<BlockPos> tried)
    {
        if (RaidOf(bandit) is not { } raid || GetById(raid.ColonyId) is not { } colony) return null;
        BlockEntityContainer? best = null;
        double bestDistance = double.MaxValue;
        var center = bandit.Pos.AsBlockPos;
        sapi.World.BlockAccessor.WalkBlocks(center.AddCopy(-radius, -3, -radius), center.AddCopy(radius, 3, radius), (block, x, y, z) =>
        {
            if (block.EntityClass == null) return;
            var pos = new BlockPos(x, y, z, center.dimension);
            if (tried.Contains(pos) || sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityContainer chest || chest.Inventory.Empty) return;
            if (chest is not BlockEntityGenericTypedContainer) return;
            if (GetAt(pos) != colony) return;
            double d = pos.DistanceSqTo(bandit.Pos.X, bandit.Pos.Y, bandit.Pos.Z);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = chest;
            }
        }, true);
        return best;
    }

    /// <summary>
    /// Sets the dry grass or hay next to a torch-carrying bandit on fire (Aaron: if it spreads to buildings, it
    /// does). Only on the colony's land. True when a fire was lit.
    /// </summary>
    public bool SetFireNear(EntityBandit bandit, int radius)
    {
        if (RaidOf(bandit) is not { } raid || GetById(raid.ColonyId) is not { } colony) return false;
        var ba = sapi.World.BlockAccessor;
        var fire = sapi.World.GetBlock(new AssetLocation("game:fire"));
        if (fire == null) return false;
        var center = bandit.Pos.AsBlockPos;
        BlockPos? fuel = null;
        ba.WalkBlocks(center.AddCopy(-radius, -2, -radius), center.AddCopy(radius, 2, radius), (block, x, y, z) =>
        {
            if (fuel != null || block.CombustibleProps == null || block.BlockMaterial is not (EnumBlockMaterial.Plant or EnumBlockMaterial.Wood or EnumBlockMaterial.Leaves)) return;
            var pos = new BlockPos(x, y, z, center.dimension);
            if (block.BlockMaterial != EnumBlockMaterial.Plant || ba.GetBlock(pos.UpCopy()).Replaceable < 6000 && ba.GetBlock(pos.UpCopy()).Id != 0) return;
            if (GetAt(pos) != colony) return;
            fuel = pos;
        }, true);
        if (fuel == null) return false;
        var firePos = fuel.UpCopy();
        ba.SetBlock(fire.Id, firePos);
        if (ba.GetBlockEntity(firePos) is { } be && be.GetBehavior<BEBehaviorBurning>() is { } burning) burning.OnFirePlaced(firePos, fuel, "", false);
        if (!raid.FireLogged)
        {
            raid.FireLogged = true;
            TellMembers(colony, "createacolony:raid-fire");
            LogEvent(colony, "event-raid-fire");
        }
        return true;
    }

    /// <summary>Town Hall line while a raid is coming or under way; empty when none.</summary>
    public string RaidStatus(Colony colony)
    {
        if (RaidOn(colony) is not { } raid) return "";
        string side = Lang.Get("createacolony:side-" + raid.Side);
        return raid.Attacking
            ? Lang.Get("createacolony:raid-status-attack-" + raid.Faction, side, raid.Raiders.Count)
            : Lang.Get("createacolony:raid-status-warning-" + raid.Faction, side, raid.Size);
    }

    /// <summary>Days until the next raid can come, for the Town Hall Settings tab; -1 while raids are off or the colony is too small.</summary>
    public double DaysToNextRaid(Colony colony)
    {
        if (!Config.Raids || colony.RaidMode != 0 || GrownSettlers(colony) < Config.RaidMinSettlers || colony.NextRaidDay <= 0) return -1;
        return Math.Max(0, colony.NextRaidDay - sapi.World.Calendar.TotalDays);
    }
}
