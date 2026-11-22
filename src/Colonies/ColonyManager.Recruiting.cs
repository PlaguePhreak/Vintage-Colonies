using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// New settlers, MineColonies style. A built Tavern offers a few people for hire each day, paid in
/// rusty gears. Now and then a wanderer walks up to the Town Hall and asks to join; an officer
/// welcomes it or turns it away in the Town Hall window, and one nobody answers moves on.
/// </summary>
public partial class ColonyManager
{
    const string NamesAsset = "config/settlernames.json";
    /// <summary>A wanderer starts this far inside the colony border and walks in from there.</summary>
    const int WandererStartInset = 4;

    Dictionary<string, string[]>? names;

    /// <summary>Whether a killed settler of this colony is gone for good, or only knocked out.</summary>
    public bool Permadeath(Colony colony) => colony.Permadeath ?? Config.Permadeath;

    // ---- Names ----------------------------------------------------------------------------

    /// <summary>A first name from config/settlernames.json and a new family name from config/settlersurnames.json.</summary>
    public string RandomName(Colony colony, string sex)
    {
        string first = RandomFirstName(colony, sex);
        string surname = RandomSurname(colony);
        return surname.Length > 0 ? $"{first} {surname}" : first;
    }

    /// <summary>A first name from config/settlernames.json nobody in the colony has yet, if there is one.</summary>
    string RandomFirstName(Colony colony, string sex)
    {
        names ??= sapi.Assets.TryGet(new AssetLocation(CreateAColonyModSystem.ModId, NamesAsset))?.ToObject<Dictionary<string, string[]>>()
            ?? new Dictionary<string, string[]>();
        if (!names.TryGetValue(sex, out var list) || list.Length == 0)
        {
            return Lang.Get("createacolony:settler-default-name", colony.NextCitizenId);
        }
        var used = new HashSet<string>(colony.Citizens.Where(c => c.Alive).Select(c => FirstNameOf(c)));
        foreach (var b in colony.Buildings) used.UnionWith(b.Recruits.Select(r => FirstNameOf(r.Name)));
        var free = list.Where(n => !used.Contains(n)).ToArray();
        var pick = free.Length > 0 ? free : list;
        return pick[sapi.World.Rand.Next(pick.Length)];
    }

    string RandomSex() => sapi.World.Rand.Next(2) == 0 ? "male" : "female";

    // ---- Tavern ---------------------------------------------------------------------------

    /// <summary>
    /// The people a Tavern offers today. Nobody comes until its first level is built; each new day
    /// brings a fresh lot.
    /// </summary>
    public List<RecruitOffer> TavernRecruits(Colony colony, Building tavern)
    {
        if (tavern.Level < 1)
        {
            tavern.Recruits.Clear();
            return tavern.Recruits;
        }
        int today = (int)sapi.World.Calendar.TotalDays;
        if (tavern.RecruitDay == today) return tavern.Recruits;

        tavern.RecruitDay = today;
        tavern.Recruits.Clear();
        int min = Math.Max(0, Config.RecruitCostMin);
        int max = Math.Max(min, Config.RecruitCostMax);
        for (int i = 0; i < Math.Max(0, Config.TavernRecruits); i++)
        {
            string sex = RandomSex();
            var skills = RollStartingSkills();
            var rolled = RollTraits();
            ApplyTraitSkillStart(skills, rolled);
            tavern.Recruits.Add(new RecruitOffer { Name = RandomName(colony, sex), Sex = sex, Cost = RecruitPrice(sapi.World.Rand.Next(min, max + 1), rolled), SkillXp = skills, Traits = rolled, Passions = RollPassions(skills, rolled) });
        }
        MarkDirty(colony);
        return tavern.Recruits;
    }

    /// <summary>Lines for the Tavern window's Building tab.</summary>
    public string TavernInfo(Colony colony, Building tavern)
    {
        var lines = new List<string>
        {
            Lang.Get(tavern.Level >= 1 ? "createacolony:tavern-open" : "createacolony:tavern-closed"),
            Config.Wanderers ? Lang.Get("createacolony:tavern-wanderers", WandererEveryDays(colony).ToString("0.#")) : Lang.Get("createacolony:tavern-no-wanderers"),
            Lang.Get("createacolony:tavern-beds", FreeBeds(colony))
        };
        if (PopulationCap(colony) > 0) lines.Add(Lang.Get(IsFull(colony) ? "createacolony:tavern-full" : "createacolony:tavern-population", Population(colony), PopulationCap(colony)));
        lines.AddRange(TavernDrinkInfo(colony, tavern));
        return string.Join("<br>", lines);
    }

    /// <summary>How many of the recruit currency the player carries, plus what is in the Tavern's storage.</summary>
    public int GearsFor(IPlayer player, Colony colony, Building tavern)
    {
        var match = Matcher(Config.RecruitCurrency);
        return PlayerSlots(player).Where(s => match(s.Itemstack!)).Sum(s => s.StackSize)
            + CountItems(HutStorage(colony, tavern), match);
    }

    /// <summary>
    /// Hires the recruit with this name at the Tavern: takes the gears from the player, then from the
    /// Tavern's storage, and the new settler steps out of the Tavern. Returns a lang key on failure.
    /// </summary>
    public string? Hire(IServerPlayer player, Colony colony, Building tavern, string name)
    {
        var offer = TavernRecruits(colony, tavern).Find(r => r.Name == name);
        if (offer == null) return "createacolony:recruit-gone";
        if (IsFull(colony)) return "createacolony:colony-full";

        bool free = player.WorldData.CurrentGameMode == EnumGameMode.Creative;
        if (!free && GearsFor(player, colony, tavern) < offer.Cost) return "createacolony:recruit-cant-afford";

        if (!free)
        {
            var match = Matcher(Config.RecruitCurrency);
            int owed = offer.Cost;
            foreach (var slot in PlayerSlots(player).Where(s => match(s.Itemstack!)))
            {
                int n = Math.Min(owed, slot.StackSize);
                slot.TakeOut(n);
                slot.MarkDirty();
                owed -= n;
                if (owed == 0) break;
            }
            while (owed > 0 && TakeItems(HutStorage(colony, tavern), match, owed) is { } taken) owed -= taken.StackSize;
        }

        tavern.Recruits.Remove(offer);
        var record = SpawnSettler(colony, offer.Name, offer.Sex, Beside(tavern.HutPos));
        if (record == null) return "createacolony:recruit-failed";
        // The skills the offer showed are the ones they bring.
        if (offer.SkillXp.Count > 0) record.SkillXp = new Dictionary<string, int>(offer.SkillXp);
        if (offer.Traits.Count > 0) record.Traits = offer.Traits.Select(t => new TraitRecord { Code = t.Code, Level = t.Level }).ToList();
        // Offers from before passions get theirs now, from the traits they really have.
        record.Passions = offer.Passions.Count > 0 ? new Dictionary<string, int>(offer.Passions) : RollPassions(record.SkillXp, record.Traits);
        TellMembers(colony, "createacolony:recruit-hired", record.Name, colony.Name, player.PlayerName);
        LogEvent(colony, "event-hired", record.Name, player.PlayerName);
        return null;
    }

    /// <summary>The player's hotbar and backpack slots that hold something.</summary>
    static IEnumerable<ItemSlot> PlayerSlots(IPlayer player)
    {
        foreach (var name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inv = player.InventoryManager.GetOwnInventory(name);
            if (inv == null) continue;
            foreach (var slot in inv)
            {
                if (!slot.Empty) yield return slot;
            }
        }
    }

    // ---- Wanderers ------------------------------------------------------------------------

    /// <summary>Beds in the colony nobody has claimed.</summary>
    public int FreeBeds(Colony colony)
    {
        var taken = BedsTaken(colony);
        return FindBlockEntities<BlockEntity>(colony).Sum(b => Math.Max(0, BedPlaces(b) - taken.GetValueOrDefault(b.Pos)));
    }

    /// <summary>The average days between wanderers right now: sooner with a built Tavern.</summary>
    public double WandererEveryDays(Colony colony) =>
        colony.Buildings.Any(b => b.Type == BuildingTypes.Tavern && b.Level >= 1) ? Config.WandererEveryDaysWithTavern : Config.WandererEveryDays;

    /// <summary>
    /// Part of the colony tick: sends off visitors who waited too long, and when it is time, has a
    /// wanderer turn up at the colony border and walk to the Town Hall.
    /// </summary>
    void TickWanderers(Colony colony, double now)
    {
        foreach (var visitor in colony.Citizens.Where(c => c.Visitor && c.Alive && now >= c.VisitorUntilTotalDays).ToList())
        {
            ForgetCitizen(colony, visitor);
            TellMembers(colony, "createacolony:visitor-left", visitor.Name, colony.Name);
            LogEvent(colony, "event-visitor-left", visitor.Name);
        }

        if (!Config.Wanderers) return;
        if (colony.NextWandererTotalDays <= 0)
        {
            ScheduleWanderer(colony, now);
            return;
        }
        if (now < colony.NextWandererTotalDays) return;
        ScheduleWanderer(colony, now);

        // One at a time, and only while there is a bed for them.
        if (colony.Citizens.Any(c => c.Visitor && c.Alive)) return;
        if (Config.WanderersNeedFreeBed && FreeBeds(colony) == 0) return;
        if (IsFull(colony)) return;
        SpawnWanderer(colony);
    }

    void ScheduleWanderer(Colony colony, double now)
    {
        // Between half and one and a half times the average, so they do not arrive like clockwork.
        double every = Math.Max(0.1, WandererEveryDays(colony));
        colony.NextWandererTotalDays = now + every * (0.5 + sapi.World.Rand.NextDouble());
        MarkDirty(colony);
    }

    /// <summary>Brings a wanderer in at once, for testing. Returns null when it could not be placed.</summary>
    public CitizenRecord? SpawnWanderer(Colony colony)
    {
        var start = WandererStart(colony);
        if (start == null) return null;
        string sex = RandomSex();
        var record = SpawnSettler(colony, RandomName(colony, sex), sex, start.ToVec3d().Add(0.5, 0, 0.5), visitor: true);
        if (record == null) return null;
        TellMembers(colony, "createacolony:visitor-arrived", record.Name, colony.Name);
        LogEvent(colony, "event-visitor-arrived", record.Name);
        return record;
    }

    /// <summary>A spot on the ground near the colony border, in loaded land, or null.</summary>
    BlockPos? WandererStart(Colony colony)
    {
        var ba = sapi.World.BlockAccessor;
        var center = colony.TownHallPos;
        int distance = Math.Max(8, colony.ClaimRadius - WandererStartInset);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            // Later attempts come closer in, in case the border is all water or unloaded.
            int d = attempt < 8 ? distance : distance / 2;
            double angle = sapi.World.Rand.NextDouble() * GameMath.TWOPI;
            var column = new BlockPos(center.X + (int)(Math.Cos(angle) * d), 0, center.Z + (int)(Math.Sin(angle) * d));
            if (ba.GetChunkAtBlockPos(column.X, 1, column.Z) == null) continue;
            column.Y = ba.GetTerrainMapheightAt(column);
            var ground = ba.GetBlock(column);
            if (ground.IsLiquid() || ba.GetBlock(column, BlockLayersAccess.Fluid).IsLiquid()) continue;
            var feet = column.UpCopy();
            if (IsOpen(feet) && IsOpen(feet.UpCopy())) return feet;
        }
        return null;
    }

    bool IsOpen(BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var boxes = ba.GetBlock(pos).GetCollisionBoxes(ba, pos);
        return (boxes == null || boxes.Length == 0) && !ba.GetBlock(pos, BlockLayersAccess.Fluid).IsLiquid();
    }

    /// <summary>An officer lets a visitor stay: from now on it is a settler like any other.</summary>
    public string? Welcome(Colony colony, CitizenRecord visitor)
    {
        if (IsFull(colony)) return "createacolony:colony-full";
        visitor.Visitor = false;
        visitor.VisitorUntilTotalDays = 0;
        MarkDirty(colony);
        TellMembers(colony, "createacolony:visitor-welcomed", visitor.Name, colony.Name);
        LogEvent(colony, "event-welcomed", visitor.Name);
        return null;
    }

    /// <summary>An officer turns a visitor away: it leaves at once.</summary>
    public void TurnAway(Colony colony, CitizenRecord visitor)
    {
        ForgetCitizen(colony, visitor);
        TellMembers(colony, "createacolony:visitor-turned-away", visitor.Name, colony.Name);
        LogEvent(colony, "event-turned-away", visitor.Name);
    }

    /// <summary>Removes a citizen from the colony entirely; its entity leaves and what it carried drops.</summary>
    public void ForgetCitizen(Colony colony, CitizenRecord citizen)
    {
        var entity = sapi.World.GetEntityById(citizen.EntityId);
        var drops = EmptyPackAndBags(colony, citizen);
        if (entity != null && entity.Alive)
        {
            foreach (var stack in drops) sapi.World.SpawnItemEntity(stack, entity.Pos.XYZ.Add(0, 0.5, 0));
            entity.Die(EnumDespawnReason.Removed);
        }
        colony.Citizens.Remove(citizen);
        MarkDirty(colony);
    }

    /// <summary>A chat message to every colony member online.</summary>
    void TellMembers(Colony colony, string langKey, params object[] args)
    {
        foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (colony.RoleOf(player.PlayerUID) < ColonyRole.Member) continue;
            // Arguments that are lang keys of this mod are put in the player's language too (raid sides).
            var translated = args.Select(a => a is string s && s.StartsWith("createacolony:") ? Lang.GetL(player.LanguageCode, s) : a).ToArray();
            player.SendMessage(0, Lang.GetL(player.LanguageCode, langKey, translated), EnumChatType.Notification);
        }
    }

    /// <summary>
    /// Makes the spot a settler without a workplace hangs about (AiTaskSettlerIdle reads it) the given
    /// position, so a wanderer settles in at the Town Hall rather than at the border. The game's wander task,
    /// which older settler files used, reads the same spot.
    /// </summary>
    public static void SetWanderHome(EntityAgent entity, Vec3d pos)
    {
        entity.Attributes.SetDouble("spawnX", pos.X);
        entity.Attributes.SetDouble("spawnY", pos.Y);
        entity.Attributes.SetDouble("spawnZ", pos.Z);
        var wander = entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.GetTask<AiTaskWander>();
        if (wander != null) wander.SpawnPosition = pos.Clone();
    }
}
