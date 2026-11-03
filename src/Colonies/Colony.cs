using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

public enum ColonyRole
{
    Visitor = 0,
    Member = 1,
    Officer = 2,
    Owner = 3
}

[ProtoContract]
public class ColonyMember
{
    [ProtoMember(1)] public string PlayerUid = "";
    [ProtoMember(2)] public string LastKnownName = "";
    [ProtoMember(3)] public ColonyRole Role = ColonyRole.Member;
}

/// <summary>
/// Authoritative server-side record for one settler. The entity is only a view of this:
/// if the entity is lost, the colony respawns it from here.
/// </summary>
[ProtoContract]
public class CitizenRecord
{
    [ProtoMember(1)] public int CitizenId;
    [ProtoMember(2)] public long EntityId;
    [ProtoMember(3)] public string Name = "";
    [ProtoMember(4)] public string Sex = "male";
    [ProtoMember(5)] public string? JobCode;
    [ProtoMember(6)] public BlockPos? HomeBedPos;
    [ProtoMember(7)] public BlockPos? WorkplacePos;
    // IsRequired: protobuf-net skips zero values, which would reload as the non-zero initializer.
    [ProtoMember(8, IsRequired = true)] public float Saturation = -1f; // -1 = not set yet, filled to max on first tick
    [ProtoMember(9)] public Vec3d? LastSeenPos;
    [ProtoMember(10, IsRequired = true)] public bool Alive = true;
    /// <summary>Set when Permadeath is off and the settler was knocked out instead of killed.</summary>
    [ProtoMember(11)] public double DownedUntilTotalDays;
    [ProtoMember(12)] public double LastSeenTotalDays;
    /// <summary>Old single carried stack; moved into Pack when first read.</summary>
    [ProtoMember(13)] public byte[]? CarriedStack;
    /// <summary>The settler's own inventory, serialized stacks, so items survive a save mid-trip.</summary>
    [ProtoMember(14)] public List<byte[]> Pack = new();
    /// <summary>Bags the settler wears; each adds its slots to the inventory.</summary>
    [ProtoMember(15)] public List<byte[]> Bags = new();
    /// <summary>A wanderer asking to join: it has no job until an officer welcomes it, and leaves if nobody does.</summary>
    [ProtoMember(16)] public bool Visitor;
    /// <summary>When a visitor nobody welcomed gives up and leaves.</summary>
    [ProtoMember(17)] public double VisitorUntilTotalDays;
    /// <summary>Food groups (EnumFoodCategory) of the settler's last few meals, oldest first.</summary>
    [ProtoMember(18)] public List<int> RecentFood = new();
    /// <summary>Armor the settler wears (helmet, body, legs), serialized stacks; it takes the blows first.</summary>
    [ProtoMember(19)] public List<byte[]> Armor = new();
    /// <summary>The family name, also the last word of Name; children take one of their parents'.</summary>
    [ProtoMember(20)] public string Surname = "";
    /// <summary>The settler this one shares a home with, 0 if single. Stays set after the partner dies.</summary>
    [ProtoMember(21)] public int PartnerId;
    /// <summary>A child's mother and father (citizen ids); empty for settlers who came from outside.</summary>
    [ProtoMember(22)] public List<int> ParentIds = new();
    /// <summary>When the settler joined the colony or was born; 0 in older saves, which counts as long ago.</summary>
    [ProtoMember(23)] public double JoinedTotalDays;
    /// <summary>A child grows up at this time; 0 for adults.</summary>
    [ProtoMember(24)] public double GrowsUpTotalDays;
    /// <summary>When this couple last had a child, so the next one waits a while.</summary>
    [ProtoMember(25)] public double LastChildTotalDays;
    /// <summary>When a settler died for good (permadeath); 0 if alive, or dead before 0.33.0 recorded it.</summary>
    [ProtoMember(26)] public double DiedTotalDays;
    /// <summary>What killed them: "entity:game:wolf-male", "player:Name" or "type:Hunger"; empty when not known.</summary>
    [ProtoMember(63)] public string DeathCause = "";
    /// <summary>Whether a dead settler has been buried. Nothing sets it yet: burial and mourning are coming later.</summary>
    [ProtoMember(27)] public bool Buried;
    /// <summary>XP in each skill, by skill code (config/skills.json); the level follows from it. See ColonyManager.Skills.cs.</summary>
    [ProtoMember(28)] public Dictionary<string, int> SkillXp = new();
    /// <summary>Whether starting skills were rolled, so settlers from saves before skills get theirs once.</summary>
    [ProtoMember(29)] public bool SkillsRolled;
    /// <summary>Traits they were born with or earned, each with its level (config/colonytraits.json; ColonyManager.Traits.cs). 30-34 are traits; the settler inventory thread uses 40+.</summary>
    [ProtoMember(30)] public List<TraitRecord> Traits = new();
    /// <summary>Whether traits were rolled, so settlers from saves before traits get theirs once.</summary>
    [ProtoMember(31)] public bool TraitsRolled;
    /// <summary>Kills as a guard, for the earned Veteran trait.</summary>
    [ProtoMember(32)] public int Kills;
    /// <summary>When the settler was knocked out lately (total days), for the earned Shaken trait.</summary>
    [ProtoMember(33)] public List<double> DownedDays = new();
    /// <summary>While Shaken: when it fades, if they are not knocked out again before then.</summary>
    [ProtoMember(34)] public double ShakenUntilTotalDays;
    // 30-39 are left for the traits and quest work.
    /// <summary>
    /// The tool bar, kept apart from the inventory: weapon, three tools, offhand (see ColonyManager.Gear.cs).
    /// Serialized stacks, an empty array where a slot is empty.
    /// </summary>
    [ProtoMember(40)] public List<byte[]> ToolBar = new();
    /// <summary>Clothes worn, by the game's dress slot (EnumCharacterDressType 0-11); an empty array where the slot is empty.</summary>
    [ProtoMember(41)] public List<byte[]> Clothes = new();
    /// <summary>Set once the settler got its starting (Commoner) clothes, so clothes a player takes off stay off.</summary>
    [ProtoMember(42)] public bool Dressed;
    /// <summary>The settler's look from the player character creator: skin part code to chosen variant.</summary>
    [ProtoMember(43)] public Dictionary<string, string> Skin = new();
    /// <summary>Food the settler carries to eat on the go, kept apart from the inventory so it is never unloaded with the harvest.</summary>
    [ProtoMember(44)] public List<byte[]> Lunch = new();
    /// <summary>Passions by skill code: 1 = Interested (one flame), 2 = Burning (two flames). 50-59 are skills and happiness.</summary>
    [ProtoMember(50)] public Dictionary<string, int> Passions = new();
    /// <summary>Whether passions were rolled, so settlers from saves before passions get theirs once.</summary>
    [ProtoMember(51)] public bool PassionsRolled;
    /// <summary>XP gained in each skill on SkillXpDay, for the daily soft cap.</summary>
    [ProtoMember(52)] public Dictionary<string, int> SkillXpToday = new();
    [ProtoMember(53)] public int SkillXpDay;
    /// <summary>Mood from 0 to 100; -1 until first set (starts at 50). See ColonyManager.Happiness.cs.</summary>
    [ProtoMember(54, IsRequired = true)] public float Happiness = -1f;
    /// <summary>Thoughts that last a while: a good meal, a death in the family, being knocked out.</summary>
    [ProtoMember(55)] public List<ThoughtRecord> Thoughts = new();
    /// <summary>While above the current time: too unhappy to work, sulking.</summary>
    [ProtoMember(56)] public double BreakdownUntilTotalDays;
    // 60-69 are job settings.
    /// <summary>What the settler does at a hut that has more than one kind of work: a Prospector's Hut's ProspectorRoles.</summary>
    [ProtoMember(60)] public int WorkRole;
    /// <summary>A prospector on Mine ore: its own trip to prospected ore, kept apart from its hut mate's.</summary>
    [ProtoMember(61)] public MineTrip Trip = new();
    /// <summary>
    /// How well fed the settler is in each food group (EnumFoodCategory: fruit, vegetable, protein, grain, dairy),
    /// 0 to SaturationMax, as the player's nutrition bars: eating fills a group, and it drains with hunger.
    /// </summary>
    [ProtoMember(62)] public List<float> Nutrition = new();
    /// <summary>A former trader who joined while the colony was full: sits at the Tavern doing nothing until there is room.</summary>
    [ProtoMember(240)] public bool WaitingForRoom;
    /// <summary>The trade of a trader who joined the colony ("foods", "survivalgoods" ...), empty for everyone else.</summary>
    [ProtoMember(241)] public string FormerTrade = "";
    /// <summary>The job a former trader takes once there is room and a hut for it.</summary>
    [ProtoMember(242)] public string PlannedJob = "";
    // 110-119: the bailiff (ColonyManager.Bailiff.cs).
    /// <summary>A worker the bailiff lent to another hut while its own could not work: its own job and hut, and when it goes back.</summary>
    [ProtoMember(110)] public string? HomeJob;
    [ProtoMember(111)] public BlockPos? HomeWorkplacePos;
    [ProtoMember(112)] public double LentUntilTotalDays;
    /// <summary>The settler's coins: wages in, taxes out (ColonyManager.Economy.cs).</summary>
    [ProtoMember(113)] public int Purse;
    /// <summary>True when their last wage could not be paid in full; cleared on the next payday they are paid.</summary>
    [ProtoMember(114)] public bool Unpaid;

    // 280-289: healing (ColonyManager.Healing.cs).
    /// <summary>The bandage or poultice the settler carries for when it gets hurt, kept apart from its inventory so it is never hauled off.</summary>
    [ProtoMember(280)] public byte[]? FirstAid;

    // 300-319: the church and religion (ColonyManager.Church.cs).
    /// <summary>How strongly the settler believes, 0 to 100; -1 until rolled.</summary>
    [ProtoMember(300, IsRequired = true)] public float Faith = -1f;
    /// <summary>The in-game day of the last church service they went to; -1 for never.</summary>
    [ProtoMember(301, IsRequired = true)] public int LastServiceDay = -1;
    /// <summary>After a service they learn faster until this time (total days).</summary>
    [ProtoMember(302)] public double LearnBoostUntilTotalDays;

    public bool IsDowned => DownedUntilTotalDays > 0;
    public bool IsChild => GrowsUpTotalDays > 0;
}

/// <summary>
/// "Bring N of this item to that container." Builders will create these in a later milestone; for now
/// officers create them with /colony request. A courier leases a request while carrying it so two
/// couriers never fetch the same thing; a lease that runs out (courier died, got stuck) frees it again.
/// </summary>
[ProtoContract]
public class DeliveryRequest
{
    [ProtoMember(1)] public int RequestId;
    /// <summary>Item or block code, wildcards allowed, e.g. "game:plank-*".</summary>
    [ProtoMember(2)] public string ItemCode = "";
    [ProtoMember(3)] public int Quantity;
    [ProtoMember(4)] public int Delivered;
    [ProtoMember(5)] public BlockPos DestPos = new BlockPos(0);
    [ProtoMember(6)] public string RequestedBy = "";
    [ProtoMember(7)] public int LeasedByCitizenId;
    [ProtoMember(8)] public double LeaseUntilTotalHours;
    /// <summary>How many of Quantity are a spare on top of what the hut needs (ColonyManager.AddRequest, 0.142.0).</summary>
    [ProtoMember(20)] public int Extra;

    public int Remaining => Quantity - Delivered;
}

/// <summary>
/// A hut block and the building grown around it from blueprints. Level 0 means only the hut block
/// stands; a work order (TargetLevel above Level) asks a builder to build the next blueprint level.
/// </summary>
[ProtoContract]
public class Building
{
    /// <summary>The order a crafting hut makes things in (keys of what it keeps), set on its Priorities tab. Empty: most wanted first.</summary>
    [ProtoMember(240)] public List<string> Priorities = new();
    [ProtoMember(1)] public int BuildingId;
    /// <summary>Building type, also the blueprint folder name: "builder", "townhall", "warehouse".</summary>
    [ProtoMember(2)] public string Type = "";
    [ProtoMember(3)] public BlockPos HutPos = new BlockPos(0);
    [ProtoMember(4)] public int Level;
    /// <summary>Blueprint rotation in degrees (0, 90, 180, 270), from the direction the player faced when placing the hut.</summary>
    [ProtoMember(5)] public int Rotation;
    /// <summary>0 = no work order.</summary>
    [ProtoMember(6)] public int TargetLevel;
    /// <summary>Index of the next build step to check, so a builder does not rescan finished work.</summary>
    [ProtoMember(7)] public int Cursor;
    [ProtoMember(8)] public int BuilderCitizenId;
    /// <summary>How far, in blocks, the worker of a Forester's or Farmer's Hut goes out; 0 uses the config default.</summary>
    [ProtoMember(9)] public int WorkRadius;
    /// <summary>A guard goes along with this hut's worker.</summary>
    [ProtoMember(10)] public bool Escort;
    /// <summary>A Farmer's Hut's farmer leaves berry bushes and fruit trees alone.</summary>
    [ProtoMember(11)] public bool NoBerries;
    /// <summary>Blocks the blueprint is raised (or lowered, below zero) from the hut, set with the Builder's Wand.</summary>
    [ProtoMember(12)] public int YOffset;
    /// <summary>Blocks the blueprint is shifted east (or west, below zero) from the hut.</summary>
    [ProtoMember(13)] public int XOffset;
    /// <summary>Blocks the blueprint is shifted south (or north, below zero) from the hut.</summary>
    [ProtoMember(14)] public int ZOffset;
    /// <summary>A Potter's Hut: how many of each kind of pottery to keep in stock, by kind (see ColonyManager.PotteryKind).</summary>
    [ProtoMember(15)] public Dictionary<string, int> PotteryKeep = new();
    /// <summary>An officer has set PotteryKeep; until then the config defaults apply.</summary>
    [ProtoMember(16)] public bool PotteryChosen;
    /// <summary>The pit a potter dug for its pit kiln.</summary>
    [ProtoMember(17)] public BlockPos? KilnPos;
    /// <summary>More pit kilns the potter dug beside the first (see ColonyManager.KilnsAllowed).</summary>
    [ProtoMember(175)] public List<BlockPos> MoreKilns = new();
    /// <summary>Where a potter shapes clay.</summary>
    [ProtoMember(18)] public BlockPos? FormPos;
    /// <summary>A Tavern: the settlers who can be hired there today.</summary>
    [ProtoMember(19)] public List<RecruitOffer> Recruits = new();
    /// <summary>The in-game day the Tavern's recruits arrived; a new day brings new ones.</summary>
    [ProtoMember(20, IsRequired = true)] public int RecruitDay = -1;
    /// <summary>A Cook's Hut: how many of each cooked food to keep in stock, by the cooked food's code.</summary>
    [ProtoMember(21)] public Dictionary<string, int> KitchenKeep = new();
    /// <summary>An officer has set KitchenKeep; until then the config default applies to every food.</summary>
    [ProtoMember(22)] public bool KitchenChosen;
    /// <summary>The blueprint style the building is built in (see BlueprintStyles); empty in older saves means Testing.</summary>
    [ProtoMember(23)] public string Style = "";
    /// <summary>A name the players gave the building; empty uses the type's name.</summary>
    [ProtoMember(24)] public string CustomName = "";
    /// <summary>How soon couriers come to take things away from this hut, 1 to 10 (10 first); 0 means never.</summary>
    [ProtoMember(25, IsRequired = true)] public int PickupPriority = 5;
    /// <summary>An officer pressed Request pickup now: the next courier free takes everything in the hut's storage.</summary>
    [ProtoMember(26)] public bool PickupNow;
    /// <summary>A Guard Tower: what its guards do (see GuardTasks).</summary>
    [ProtoMember(27)] public int GuardTask;
    /// <summary>A Guard Tower: where its guards stand on the Guard task; null means at the tower.</summary>
    [ProtoMember(28)] public BlockPos? GuardSpot;
    /// <summary>A Guard Tower: guards on the Patrol task walk PatrolPoints instead of from hut to hut.</summary>
    [ProtoMember(29)] public bool PatrolManual;
    [ProtoMember(30)] public List<BlockPos> PatrolPoints = new();
    /// <summary>A Guard Tower: the player its guards follow on the Follow task.</summary>
    [ProtoMember(31)] public string FollowPlayerUid = "";
    [ProtoMember(32)] public bool FollowTight;
    /// <summary>A Guard Tower: badly hurt guards walk back to the tower until they have healed.</summary>
    [ProtoMember(33, IsRequired = true)] public bool Retreat = true;
    /// <summary>A Guard Tower: creatures (entity code paths) its guards leave alone; all others are fought.</summary>
    [ProtoMember(34)] public List<string> HostilesOff = new();
    /// <summary>Items couriers keep at least this many of in the hut's storage.</summary>
    [ProtoMember(35)] public List<MinStockEntry> MinStock = new();
    /// <summary>Counts per in-game day for the Statistics tab, for the last 100 days.</summary>
    [ProtoMember(36)] public List<StatEntry> Stats = new();
    /// <summary>All-time totals for the Statistics tab.</summary>
    [ProtoMember(37)] public Dictionary<string, int> StatTotals = new();
    /// <summary>A Miner's Hut: the ore its miner digs for (a deposit code such as "nativecopper"); empty means the best the prospectors found.</summary>
    [ProtoMember(38)] public string MineOre = "";
    // 39-43 held the Miner's Hut's trip to prospected ore before 0.62.0; it is in Trip now (see AfterLoad).
    [ProtoMember(39)] BlockPos? oldMineSite;
    [ProtoMember(40)] string oldMineSiteOre = "";
    [ProtoMember(41)] BlockPos? oldMineEntrance;
    [ProtoMember(42)] BlockPos? oldMineEnd;
    [ProtoMember(43)] BlockPos? oldMineTarget;
    /// <summary>A Quarry: the middle of its pit beside the hut, in the air just above the ground; the top layer of the pit is the ground under it.</summary>
    [ProtoMember(44)] public BlockPos? QuarryCenter;
    /// <summary>A Miner's Hut: the miner goes off to dig ore the colony's prospectors found before working its own shaft.</summary>
    [ProtoMember(45, IsRequired = true)] public bool MineProspected = true;
    /// <summary>A Miner's Hut: the middle of its shaft beside the hut, at the height a settler stands on the ground there.</summary>
    [ProtoMember(46)] public BlockPos? ShaftPos;
    /// <summary>Which of its type's variants the building is (a small or a medium house; see BlueprintLibrary.Variants); "" is the first.</summary>
    [ProtoMember(47)] public string Variant = "";
    /// <summary>A Miner's Hut: its miner's trip to ore the colony's prospectors found. (Prospectors on Mine ore keep their own, on CitizenRecord.)</summary>
    [ProtoMember(48)] public MineTrip Trip = new();
    /// <summary>A Miner's Hut: the sections its mineshaft has grown so far, in the order they were dug, after its entrance.</summary>
    [ProtoMember(49)] public List<MineSection> Sections = new();
    /// <summary>A Miner's Hut: where its mineshaft's entrance comes up beside the hut, at the height a settler stands on the ground there.</summary>
    [ProtoMember(50)] public BlockPos? MineshaftPos;
    /// <summary>A Miner's Hut: the level of the mineshaft (1 is the top stair loop) new sections go on first; 0 lets the miner pick.</summary>
    [ProtoMember(51)] public int MineLevel;
    /// <summary>A Miner's Hut: a level the miner puts back as it was before anything else; 0 when none was asked for.</summary>
    [ProtoMember(52)] public int RepairLevel;
    /// <summary>A Miner's Hut: how many levels deep the mineshaft goes at most; 0 goes as deep as the hut's level allows.</summary>
    [ProtoMember(53)] public int MaxMineLevels;
    /// <summary>A Miner's Hut: what the miner fills holes in the mineshaft floor with (see MineFillBlocks); "none" leaves them.</summary>
    [ProtoMember(54, IsRequired = true)] public string FillBlock = MineFillBlocks.Soil;
    /// <summary>A Miner's Hut: the guards (citizen ids) that patrol its mine, from Guard Towers set to Patrol mine.</summary>
    [ProtoMember(55)] public List<int> MineGuards = new();
    /// <summary>A Miner's Hut: the Mineshaft Entrance whose mine it works (its BuildingId); 0 works its own mine.</summary>
    [ProtoMember(56)] public int MineEntranceId;
    /// <summary>An officer pressed Remove hut: a builder takes the building down, then the hut block goes back to RemovedBy.</summary>
    [ProtoMember(57)] public bool Removing;
    /// <summary>The player who pressed Remove hut, who gets the hut block back.</summary>
    [ProtoMember(58)] public string RemovedBy = "";
    /// <summary>The turn and asset pack the mine's pieces use: the hut's when the mine was started, kept when the mine moves to another hut. -1 / empty use the building's own.</summary>
    [ProtoMember(59, IsRequired = true)] public int MineRotation = -1;
    [ProtoMember(62)] public string MineStyle = "";
    // 60 and 61 held the trip's entrances and the mines to fill in before 0.62.0.
    [ProtoMember(60)] List<BlockPos> oldSiteEntrances = new();
    [ProtoMember(61)] List<BlockPos> oldMineSeals = new();
    // 70-79 for the crafter huts (Smelter, Blacksmith, Sawmill, Primitive Craftsman and the ones to come).
    /// <summary>A crafter hut: how many of each thing to keep in stock, by key (see ColonyManager.Crafting.cs).</summary>
    [ProtoMember(70)] public Dictionary<string, int> CraftKeep = new();
    /// <summary>An officer has set CraftKeep; until then the config defaults apply.</summary>
    [ProtoMember(71)] public bool CraftChosen;
    // 110-119 for the Explorer's and Gatherer's huts.
    /// <summary>An Explorer's or Gatherer's Hut: the marker groups (ColonyManager.MarkerGroups) its worker does not put on the map.</summary>
    [ProtoMember(110)] public List<string> MarkersOff = new();
    /// <summary>An Explorer's Hut: the squares of land its explorer has looked over (ColonyManager.ExploreCell keys).</summary>
    [ProtoMember(111)] public List<long> Explored = new();
    /// <summary>When the explorer last started over, in total days, so it goes round again now and then.</summary>
    [ProtoMember(112)] public double ExploreStartedTotalDays;
    /// <summary>A Gatherer's Hut: whether its gatherer picks things, only looks around and marks the map, or both (GatherModes).</summary>
    [ProtoMember(113, IsRequired = true)] public string GatherMode = GatherModes.Both;
    /// <summary>A Gatherer's Hut: the kinds of thing its gatherer goes for first (ColonyManager.GatherCategories); empty is the usual order.</summary>
    [ProtoMember(114)] public List<string> GatherOrder = new();
    /// <summary>A gathering hut: what its worker is set not to collect (ColonyManager.CollectKinds keys). Empty is everything.</summary>
    [ProtoMember(160)] public List<string> CollectOff = new();
    /// <summary>The things that start off (fruit trees, ColonyManager.OffByDefault) which an officer turned on.</summary>
    [ProtoMember(161)] public List<string> CollectOn = new();
    /// <summary>A hut's Crafting recipes tab: how many of each thing its worker keeps crafted, by item code (ColonyManager.HutRecipes.cs). Empty is nothing.</summary>
    [ProtoMember(170)] public Dictionary<string, int> RecipeKeep = new();
    /// <summary>
    /// A work station (ColonyManager.Stations.cs): the building whose zone it stands in, by BuildingId. 0 for an
    /// ordinary hut. A station has no blueprint or zone of its own; its worker uses the host's storage.
    /// </summary>
    [ProtoMember(300)] public int HostId;
    public bool IsStation => HostId != 0;

    public bool HasWorkOrder => TargetLevel > Level || ((Upkeep || Repairing) && Level > 0 && TargetLevel == Level);
    /// <summary>The work order is upkeep of the finished building: digging out ground that got in and filling gaps under it (ColonyManager.Foundations.cs).</summary>
    [ProtoMember(270)] public bool Upkeep;
    /// <summary>The work order is a repair players asked for: the building is put back as its blueprint has it (ColonyManager.Repair.cs).</summary>
    [ProtoMember(271)] public bool Repairing;

    /// <summary>A Miner's Hut working a Mineshaft Entrance's mine: that entrance. Not saved; see Colony.LinkMines.</summary>
    public Building? Entrance;
    /// <summary>A Mineshaft Entrance: the Miner's Huts that work its mine. Not saved; see Colony.LinkMines.</summary>
    public List<Building> LinkedHuts = new();
    /// <summary>Where this building's mine is kept: its Mineshaft Entrance's, or its own.</summary>
    public Building Mine => Entrance ?? this;
    public int MineTurn => MineRotation >= 0 ? MineRotation : Rotation;
    public string MinePack => MineStyle.Length > 0 ? MineStyle : Style;
    /// <summary>True when this building holds a mine made of pieces.</summary>
    public bool HasMine => MineshaftPos != null;

    /// <summary>Moves a Miner's Hut's trip from where saves before 0.62.0 kept it into Trip.</summary>
    [ProtoAfterDeserialization]
    void AfterLoad()
    {
        Trip ??= new MineTrip();
        if (oldMineSite != null || oldMineSeals.Count > 0 || oldSiteEntrances.Count > 0)
        {
            Trip.Site = oldMineSite;
            Trip.SiteOre = oldMineSiteOre;
            Trip.Entrance = oldMineEntrance;
            Trip.End = oldMineEnd;
            Trip.Target = oldMineTarget;
            Trip.Entrances.AddRange(oldSiteEntrances);
            Trip.Seals.AddRange(oldMineSeals);
        }
        oldMineSite = oldMineEntrance = oldMineEnd = oldMineTarget = null;
        oldMineSiteOre = "";
        oldSiteEntrances.Clear();
        oldMineSeals.Clear();
    }
}

/// <summary>
/// A miner's trip to ore the colony's prospectors found: the reading it works and the ore it digs there,
/// the staircase down to it, and the finished mines it still has to fill back in. A Miner's Hut keeps one
/// for its miner, and every prospector on Mine ore keeps its own.
/// </summary>
[ProtoContract]
public class MineTrip
{
    /// <summary>The prospecting reading (OreSite position) being worked, and the ore dug there.</summary>
    [ProtoMember(1)] public BlockPos? Site;
    [ProtoMember(2)] public string SiteOre = "";
    /// <summary>Where the staircase starts at the surface (the spot to stand on).</summary>
    [ProtoMember(3)] public BlockPos? Entrance;
    /// <summary>The far end of the tunnel dug so far (the spot to stand on); new tunnels start from here.</summary>
    [ProtoMember(4)] public BlockPos? End;
    /// <summary>The ore block being tunnelled towards.</summary>
    [ProtoMember(5)] public BlockPos? Target;
    /// <summary>The surface entrances of the staircases dug at the site.</summary>
    [ProtoMember(6)] public List<BlockPos> Entrances = new();
    /// <summary>Entrances of finished mines still to be filled back in.</summary>
    [ProtoMember(7)] public List<BlockPos> Seals = new();
}

/// <summary>One section of a Miner's Hut's mineshaft: which piece (cross, tee, straight), turned how far, and where its bottom middle is.</summary>
[ProtoContract]
public class MineSection
{
    [ProtoMember(1)] public string Name = "";
    [ProtoMember(2)] public int Rotation;
    [ProtoMember(3)] public BlockPos Origin = new BlockPos(0);
}

/// <summary>What each of a Prospector's Hut's prospectors does (CitizenRecord.WorkRole).</summary>
public static class ProspectorRoles
{
    /// <summary>Walks the land taking readings with the prospecting pick.</summary>
    public const int Find = 0;
    /// <summary>Goes to the best readings with a pickaxe and digs the ore out, as a Miner's Hut's miner does.</summary>
    public const int Mine = 1;
}

/// <summary>What each of a Church's clergy is (CitizenRecord.WorkRole, job Priest).</summary>
public static class ChurchRoles
{
    public const int Priest = 0;
    /// <summary>One per colony, at a Church of level ChurchBishopLevel or more: stronger services.</summary>
    public const int Bishop = 1;
}

/// <summary>What a Guard Tower's guards do.</summary>
public static class GuardTasks
{
    /// <summary>Stand at the guard spot (or the tower) and fight what comes near.</summary>
    public const int Guard = 0;
    /// <summary>Walk from patrol point to patrol point, or from hut to hut, fighting on the way.</summary>
    public const int Patrol = 1;
    /// <summary>Follow a player as a bodyguard, even outside the colony.</summary>
    public const int Follow = 2;
    /// <summary>Go with the miner of the Miner's Hut that took them on its Guards tab (Guard Towers only, as in MineColonies).</summary>
    public const int PatrolMine = 3;
}

/// <summary>What a miner fills holes in its mineshaft floor with (Building.FillBlock), and the block placed for each.</summary>
/// <summary>What a Gatherer's Hut's gatherer does: picks and looks around, only looks around (marking the map), or only picks.</summary>
public static class GatherModes
{
    public const string Both = "both";
    public const string Look = "look";
    public const string Gather = "gather";
    public static readonly string[] All = { Both, Look, Gather };
}

public static class MineFillBlocks
{
    public const string None = "none";
    public const string Soil = "soil";
    public const string Cobblestone = "cobblestone";
    public const string Gravel = "gravel";
    public static readonly string[] All = { Soil, Cobblestone, Gravel, None };

    /// <summary>The block code placed; null for None. Any rock will do when "any wood or stone" is on.</summary>
    public static string? BlockCode(string fill) => fill switch
    {
        Soil => "game:soil-medium-none",
        Cobblestone => "game:cobblestone-granite",
        Gravel => "game:gravel-granite",
        _ => null
    };
}

/// <summary>"Keep at least Count of this item here."</summary>
[ProtoContract]
public class MinStockEntry
{
    /// <summary>One of the item, serialized, so the window can show it and couriers can match it.</summary>
    [ProtoMember(1)] public byte[] Stack = System.Array.Empty<byte>();
    [ProtoMember(2)] public string Code = "";
    [ProtoMember(3)] public int Count;
}

/// <summary>How many times something happened at a building on one in-game day.</summary>
[ProtoContract]
public class StatEntry
{
    [ProtoMember(1)] public string Key = "";
    [ProtoMember(2)] public int Day;
    [ProtoMember(3)] public int Count;
}

/// <summary>What the bailiff does when a wanderer asks to join.</summary>
public static class BailiffWanderers
{
    /// <summary>Leaves it to the players, as without a bailiff.</summary>
    public const int Ask = 0;
    public const int Welcome = 1;
    /// <summary>Welcomes them while a bed is free, else turns them away.</summary>
    public const int IfBed = 2;
    public const int TurnAway = 3;
    public const int Last = TurnAway;
}

/// <summary>One day of the bailiff's ledger: rent collected and what they did.</summary>
[ProtoContract]
public class LedgerDay
{
    [ProtoMember(1)] public int Day;
    /// <summary>Taxes collected that day, in coins (rusty gears of rent before 0.136.0).</summary>
    [ProtoMember(2)] public int Rent;
    [ProtoMember(3)] public int Welcomed;
    [ProtoMember(4)] public int TurnedAway;
    [ProtoMember(5)] public int JobsGiven;
    /// <summary>How many settlers paid taxes (before 0.136.0, rent) that day.</summary>
    [ProtoMember(6)] public int Payers;
    /// <summary>Workers lent to another hut that day because they could not work at their own.</summary>
    [ProtoMember(7)] public int Lent;
    /// <summary>Coins paid out in wages that day, and how many settlers went without their full wage.</summary>
    [ProtoMember(8)] public int Wages;
    [ProtoMember(9)] public int Unpaid;
}

/// <summary>One day of the church's ledger: tithes in, and what the church spent on charity (in coins).</summary>
[ProtoContract]
public class ChurchDay
{
    [ProtoMember(1)] public int Day;
    [ProtoMember(2)] public int Tithes;
    /// <summary>Coins spent on food for the hungry, and how many meals that bought.</summary>
    [ProtoMember(3)] public int FoodCoins;
    [ProtoMember(4)] public int Meals;
    /// <summary>Coins spent on bandages for the Healer's Station, and how many.</summary>
    [ProtoMember(5)] public int BandageCoins;
    [ProtoMember(6)] public int Bandages;
    /// <summary>Coins spent caring for orphans and on the poorhouse beds.</summary>
    [ProtoMember(7)] public int CareCoins;
    /// <summary>Settlers at that day's service, and funerals held.</summary>
    [ProtoMember(8)] public int Attended;
    [ProtoMember(9)] public int Funerals;
    /// <summary>Chronicle books written.</summary>
    [ProtoMember(10)] public int Books;
}

/// <summary>One line of the Town Hall's event log: a lang key and its arguments, filled in by the reader's own language.</summary>
[ProtoContract]
public class ColonyEvent
{
    [ProtoMember(1)] public double TotalDays;
    [ProtoMember(2)] public string Key = "";
    /// <summary>An argument starting with "@" is itself a lang key, such as "@building-farmer".</summary>
    [ProtoMember(3)] public List<string> Args = new();
}

/// <summary>A trader's old house for a builder to take down: everything in the box but natural ground and plants.</summary>
[ProtoContract]
public class TeardownOrder
{
    [ProtoMember(1)] public int Id;
    /// <summary>The trader's name, for "Take down Hilda's old house".</summary>
    [ProtoMember(2)] public string Owner = "";
    [ProtoMember(3)] public int X1;
    [ProtoMember(4)] public int Y1;
    [ProtoMember(5)] public int Z1;
    [ProtoMember(6)] public int X2;
    [ProtoMember(7)] public int Y2;
    [ProtoMember(8)] public int Z2;
    [ProtoMember(9)] public int BuilderCitizenId;
    /// <summary>Blocks there were to take down when the order was made, for the progress bar.</summary>
    [ProtoMember(10)] public int Total;
    [ProtoMember(11)] public int Done;

    public Cuboidi Area => new(X1, Y1, Z1, X2, Y2, Z2);
    public BlockPos Center => new((X1 + X2) / 2, Y1, (Z1 + Z2) / 2);
}

/// <summary>Someone at the Tavern who joins the colony for a price in rusty gears.</summary>
[ProtoContract]
public class RecruitOffer
{
    [ProtoMember(1)] public string Name = "";
    [ProtoMember(2)] public string Sex = "male";
    [ProtoMember(3)] public int Cost;
    /// <summary>The skills they would bring, rolled when the offer is made and shown before hiring.</summary>
    [ProtoMember(4)] public Dictionary<string, int> SkillXp = new();
    /// <summary>Their traits, rolled with the offer; the price follows the trait score.</summary>
    [ProtoMember(5)] public List<TraitRecord> Traits = new();
    /// <summary>Their passions (skill code to 1 or 2 flames), shown before hiring.</summary>
    [ProtoMember(6)] public Dictionary<string, int> Passions = new();
}

/// <summary>A thought that lasts a while and adds to a settler's mood (ColonyManager.Happiness.cs).</summary>
[ProtoContract]
public class ThoughtRecord
{
    /// <summary>The thought's code: "meal", "knockedout", "death", "passion" and so on.</summary>
    [ProtoMember(1)] public string Code = "";
    /// <summary>Mood points it adds (negative to take away).</summary>
    [ProtoMember(2)] public int Mood;
    [ProtoMember(3)] public double UntilTotalDays;
}

/// <summary>One trait of a settler: which, and how far it has grown (0 to TraitLevelMax).</summary>
[ProtoContract]
public class TraitRecord
{
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public int Level;
    /// <summary>Trait XP towards the next level; level L to L+1 costs (L+1) x TraitXpPerLevel.</summary>
    [ProtoMember(3)] public int Xp;
}

/// <summary>
/// A field marked out by a Field marker (a scarecrow): the square of ground around it that farmers
/// till, sow and harvest.
/// </summary>
[ProtoContract]
public class FieldRecord
{
    [ProtoMember(1)] public BlockPos Pos = new BlockPos(0);
    /// <summary>Seed code to sow here, e.g. "game:seeds-carrot"; empty means any seed the farmer has.</summary>
    [ProtoMember(2)] public string Seed = "";
    /// <summary>The farmer looking after this field, 0 if none yet.</summary>
    [ProtoMember(3)] public int FarmerCitizenId;
}

/// <summary>
/// One reading a prospector took with the game's prospecting pick (density mode): the spot, and how much
/// of each ore lies in the ground around it, exactly as the pick would tell a player.
/// </summary>
[ProtoContract]
public class OreSite
{
    /// <summary>The ground surface where the reading was taken.</summary>
    [ProtoMember(1)] public BlockPos Pos = new BlockPos(0);
    [ProtoMember(2)] public double TotalDays;
    [ProtoMember(3)] public List<OreFind> Ores = new();
    /// <summary>Ores a miner has dug out here, or found none of close enough to reach.</summary>
    [ProtoMember(4)] public List<string> MinedOut = new();
    /// <summary>The ore whose bits a gatherer found here ("nativecopper"), so miners dig for it whatever the reading says.</summary>
    [ProtoMember(5)] public string HintOre = "";
    /// <summary>A gatherer's find that a miner went to before any prospector read it: it holds no reading.</summary>
    public bool Unread => Ores.Count == 0 && HintOre.Length > 0;
}

/// <summary>A spot where ore bits lay on the ground ("nativecopper"), so a deposit is likely right below.</summary>
[ProtoContract]
public class OreHint
{
    [ProtoMember(1)] public BlockPos Pos = new BlockPos(0);
    [ProtoMember(2)] public string Ore = "";
    [ProtoMember(3)] public double TotalDays;
}

/// <summary>One ore in a reading: the deposit code, the pick's density factor (0 to 1) and parts per thousand.</summary>
[ProtoContract]
public class OreFind
{
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public double Factor;
    [ProtoMember(3)] public double Ppt;
}

/// <summary>
/// What is saved about one quest for a player or a colony: whether it was accepted and when it was
/// claimed. Everything else (locked, counting, complete) is worked out from the world each time.
/// </summary>
[ProtoContract]
public class QuestRecord
{
    [ProtoMember(1)] public bool Accepted;
    [ProtoMember(2)] public double AcceptedTotalDays;
    [ProtoMember(3)] public bool Claimed;
    [ProtoMember(4)] public double ClaimedTotalDays;
    /// <summary>The player was told the quest is ready to claim, so they are not told again.</summary>
    [ProtoMember(5)] public bool Notified;
}

/// <summary>One player's own quests (the base game groups and the Story prologue), saved on the world under the player's uid.</summary>
[ProtoContract]
public class PlayerQuests
{
    [ProtoMember(1)] public Dictionary<string, QuestRecord> Quests = new();
    /// <summary>Quest codes the player chose to track in the quest book.</summary>
    [ProtoMember(2)] public List<string> Tracked = new();
}

/// <summary>
/// A map marker the colony's settlers put down: what was found (Kind, see ColonyManager.MarkerKinds) and where.
/// Every member sees it on the "Colony markers" layer of their world map.
/// </summary>
[ProtoContract]
public class ColonyMarker
{
    [ProtoMember(1)] public int MarkerId;
    [ProtoMember(2)] public string Kind = "";
    [ProtoMember(3)] public double X;
    [ProtoMember(4)] public double Y;
    [ProtoMember(5)] public double Z;
    [ProtoMember(6)] public string Title = "";
    /// <summary>The hut whose worker put it down; 0 for markers of the colony itself (a settler's grave).</summary>
    [ProtoMember(7)] public int BuildingId;
    [ProtoMember(9)] public double TotalDays;
    /// <summary>For a grave: the settler who fell there, so only their latest spot is kept.</summary>
    [ProtoMember(10)] public int CitizenId;
    /// <summary>The waypoint icon of what it marks (Map.MarkerSubjects), or "" for its kind's icon.</summary>
    [ProtoMember(11)] public string Icon = "";
}

/// <summary>Where a player took a colony marker off the map, and what it marked.</summary>
[ProtoContract]
public class RemovedMarker
{
    [ProtoMember(1)] public string Kind = "";
    [ProtoMember(2)] public string Icon = "";
    [ProtoMember(3)] public double X;
    [ProtoMember(4)] public double Z;
}

/// <summary>
/// A recipe a player taught a kind of hut on its Crafting recipes tab (ColonyManager.CustomRecipes.cs): what goes in,
/// what comes out. Every hut of that kind in the colony knows it.
/// </summary>
[ProtoContract]
public class CustomRecipe
{
    [ProtoMember(1)] public int Id;
    [ProtoMember(2)] public string HutType = "";
    [ProtoMember(3)] public List<CustomRecipePart> Inputs = new();
    [ProtoMember(4)] public CustomRecipePart Output = new();
    /// <summary>Any type of the inputs will do (any wood for oak planks), not just the one taught.</summary>
    [ProtoMember(5)] public bool AnyType;
    [ProtoMember(6)] public string TaughtBy = "";
}

[ProtoContract]
public class CustomRecipePart
{
    /// <summary>The item or block code, such as game:stick.</summary>
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public bool IsBlock;
    [ProtoMember(3, IsRequired = true)] public int Quantity = 1;
    /// <summary>A tool (it has durability): it wears down instead of being used up.</summary>
    [ProtoMember(4)] public bool Tool;
}

/// <summary>One research node started at an Archive (ColonyManager.Research.cs).</summary>
[ProtoContract]
public class ResearchProgress
{
    [ProtoMember(1)] public string Code = "";
    /// <summary>The Archive it runs at.</summary>
    [ProtoMember(2)] public int ArchiveId;
    [ProtoMember(3)] public double Points;
    /// <summary>True once its items were taken from the Archive's storage.</summary>
    [ProtoMember(4)] public bool Paid;
    /// <summary>False while paused: it keeps its points and items but takes no slot.</summary>
    [ProtoMember(5, IsRequired = true)] public bool Running = true;
}

[ProtoContract]
public class Colony
{
    public const int CurrentSchemaVersion = 1;

    [ProtoMember(1, IsRequired = true)] public int SchemaVersion = CurrentSchemaVersion;
    [ProtoMember(2)] public string ColonyId = "";
    [ProtoMember(3)] public string Name = "";
    [ProtoMember(4)] public BlockPos TownHallPos = new BlockPos(0);
    [ProtoMember(5, IsRequired = true)] public int Level = 1;
    [ProtoMember(6)] public int ClaimRadius;
    [ProtoMember(7)] public List<ColonyMember> Members = new();
    [ProtoMember(8)] public List<CitizenRecord> Citizens = new();
    [ProtoMember(9, IsRequired = true)] public int NextCitizenId = 1;
    [ProtoMember(10)] public double FoundedTotalDays;
    // 11 to 13 held the old supply camp's daily deliveries (up to 0.31). The camp no longer belongs to a
    // colony; the numbers stay taken so old saves still load.

    /// <summary>Position of the colony's warehouse marker; chests around it form the warehouse.</summary>
    [ProtoMember(14)] public BlockPos? WarehousePos;
    /// <summary>Drop-off markers: couriers empty the chests touching them into the warehouse.</summary>
    [ProtoMember(15)] public List<BlockPos> DropOffs = new();
    [ProtoMember(16)] public List<DeliveryRequest> Requests = new();
    [ProtoMember(17, IsRequired = true)] public int NextRequestId = 1;
    [ProtoMember(18)] public List<Building> Buildings = new();
    [ProtoMember(19, IsRequired = true)] public int NextBuildingId = 1;
    /// <summary>Builders may use any wood or stone type in place of the one a blueprint asks for.</summary>
    [ProtoMember(20, IsRequired = true)] public bool AnyWoodOrStone = true;
    /// <summary>Fields marked out with Field markers, for farmers.</summary>
    [ProtoMember(21)] public List<FieldRecord> Fields = new();
    /// <summary>The owner's Permadeath switch; null until changed, so the server config decides.</summary>
    [ProtoMember(22)] public bool? Permadeath;
    /// <summary>When the next wanderer may come asking to join; 0 until first worked out.</summary>
    [ProtoMember(23)] public double NextWandererTotalDays;
    /// <summary>The Town Hall's event log, oldest first, the newest ColonyManager.MaxEvents kept.</summary>
    [ProtoMember(24)] public List<ColonyEvent> Events = new();
    /// <summary>The colony banner set on the Town Hall's Banners tab (one banner, serialized), or null.</summary>
    [ProtoMember(25)] public byte[]? BannerStack;
    /// <summary>Puts the colony banner on each building as soon as a builder finishes it.</summary>
    [ProtoMember(26)] public bool BannerOnNew;
    /// <summary>The last in-game day couples and births were looked at.</summary>
    [ProtoMember(27, IsRequired = true)] public int LastFamilyDay = -1;
    /// <summary>Readings the colony's prospectors took with the game's prospecting pick, oldest first.</summary>
    [ProtoMember(28)] public List<OreSite> OreSites = new();
    /// <summary>The last in-game day traits earned their daily XP; -1 until the first tick.</summary>
    [ProtoMember(35, IsRequired = true)] public int LastTraitDay = -1; // 29 to 34 are left to the quests branch
    /// <summary>The last in-game day skills decayed. 50-59 are skills and happiness.</summary>
    [ProtoMember(50, IsRequired = true)] public int LastSkillDay = -1;
    /// <summary>When moods last moved (total hours); 0 until the first happiness tick.</summary>
    [ProtoMember(51)] public double LastMoodTotalHours;
    /// <summary>The colony's quests (the Story and The Colony groups), by quest code: accepted and claimed ones only.</summary>
    [ProtoMember(29)] public Dictionary<string, QuestRecord> Quests = new();
    /// <summary>Map markers the colony's settlers put down (ColonyManager.Markers.cs).</summary>
    [ProtoMember(70)] public List<ColonyMarker> Markers = new();
    [ProtoMember(71, IsRequired = true)] public int NextMarkerId = 1;
    /// <summary>Spots a player took a marker off the map at, so settlers do not mark them again.</summary>
    [ProtoMember(72)] public List<RemovedMarker> RemovedMarkers = new();
    /// <summary>Colony militia: settlers with a weapon fight a creature that hurts them. Null until changed, so the server config decides.</summary>
    [ProtoMember(80)] public bool? SelfDefence;
    /// <summary>Colony militia: settlers with a weapon come to help a nearby settler who is attacked. Null until changed, so the server config decides.</summary>
    [ProtoMember(81)] public bool? SupportAllies;
    /// <summary>Mines whose Miner's Hut or Mineshaft Entrance was removed. Their blocks stay; the next Miner's Hut or entrance near one takes it on.</summary>
    [ProtoMember(90)] public List<Building> DerelictMines = new();
    /// <summary>Where the gatherer picked up ore bits: likely a deposit below, for the prospectors to check.</summary>
    [ProtoMember(91)] public List<OreHint> OreHints = new();
    /// <summary>The Bailiff tab (ColonyManager.Bailiff.cs): what the bailiff does with wanderers (BailiffWanderers), whether
    /// they give jobless settlers work, and whether they collect rent. 110-119 are the bailiff's.</summary>
    [ProtoMember(110, IsRequired = true)] public int WandererRule = BailiffWanderers.IfBed;
    [ProtoMember(111, IsRequired = true)] public bool BailiffGivesJobs = true;
    [ProtoMember(112, IsRequired = true)] public bool BailiffCollectsRent = true;
    /// <summary>The bailiff's ledger, one line per in-game day, oldest first; the newest ColonyManager.LedgerDays kept.</summary>
    [ProtoMember(113)] public List<LedgerDay> Ledger = new();
    /// <summary>The last in-game day rent was collected; -1 until the first.</summary>
    [ProtoMember(114, IsRequired = true)] public int LastRentDay = -1;
    /// <summary>Rusty gears from the old daily rent that found no room; no longer added to (taxes are coins now).</summary>
    [ProtoMember(115)] public int RentOwed;
    /// <summary>The bailiff lends workers who can't work at their own hut to another one for a while.</summary>
    [ProtoMember(116, IsRequired = true)] public bool BailiffLendsWorkers = true;
    /// <summary>The economy (ColonyManager.Economy.cs): whether the bailiff pays wages, the tax on settlers' purses, and the last month paid.
    /// BailiffCollectsRent is the "Collect taxes" switch.</summary>
    [ProtoMember(117, IsRequired = true)] public bool BailiffPaysWages = true;
    [ProtoMember(118, IsRequired = true)] public int TaxPercent = 10;
    [ProtoMember(119, IsRequired = true)] public int LastPayMonth = -1;
    /// <summary>The colony's own coin, picked on the Bailiff tab (a Coinage coin a player designed), as stack bytes; null for the Royal Mint's copper coins.</summary>
    [ProtoMember(120)] public byte[]? Currency;
    // 300-319: the church (ColonyManager.Church.cs).
    /// <summary>The share of every wage settlers give the church, in percent (0, 5 or 10).</summary>
    [ProtoMember(300, IsRequired = true)] public int TithePercent = 10;
    /// <summary>The church's ledger, one line per in-game day, oldest first; the newest ColonyManager.ChurchLedgerDays kept.</summary>
    [ProtoMember(301)] public List<ChurchDay> ChurchLedger = new();
    /// <summary>The last service day whose service was settled (mood, faith, learning); -1 until the first.</summary>
    [ProtoMember(302, IsRequired = true)] public int LastServiceDay = -1;
    /// <summary>When the priest last wrote the colony's events into a chronicle (total days); events after it go in the next book.</summary>
    [ProtoMember(303)] public double LastChronicleTotalDays;
    /// <summary>When the church last paid for the poorhouse and the orphans (in-game day); -1 until the first.</summary>
    [ProtoMember(304, IsRequired = true)] public int LastAlmsDay = -1;
    // 320-329: recipes players taught the huts (ColonyManager.CustomRecipes.cs).
    [ProtoMember(320)] public List<CustomRecipe> CustomRecipes = new();
    [ProtoMember(321, IsRequired = true)] public int NextCustomRecipeId = 1;
    // 400-409: research (ColonyManager.Research.cs).
    /// <summary>The research nodes the colony has finished, by code (config/research.json).</summary>
    [ProtoMember(400)] public List<string> ResearchDone = new();
    /// <summary>Research started at an Archive: running, or paused with its progress kept.</summary>
    [ProtoMember(401)] public List<ResearchProgress> Research = new();
    /// <summary>Knowledge Tokens: a second research currency for high-tier nodes.</summary>
    [ProtoMember(402)] public int KnowledgeTokens;
    /// <summary>Houses of traders who joined the colony, for a builder to take down (ColonyManager.Traders.cs).</summary>
    [ProtoMember(240)] public List<TeardownOrder> Teardowns = new();
    [ProtoMember(241, IsRequired = true)] public int NextTeardownId = 1;
    /// <summary>The order builders take work orders and trader houses in ("b:12", "t:3"), set on the Priorities tab; new ones come after.</summary>
    [ProtoMember(242)] public List<string> OrderQueue = new();

    [ProtoAfterDeserialization]
    void AfterLoad() => LinkMines();

    /// <summary>Joins each Miner's Hut to its Mineshaft Entrance (Building.Entrance and LinkedHuts); links to entrances that are gone are dropped.</summary>
    public void LinkMines()
    {
        foreach (var b in Buildings)
        {
            b.Entrance = null;
            b.LinkedHuts.Clear();
        }
        foreach (var hut in Buildings)
        {
            if (hut.MineEntranceId == 0) continue;
            var entrance = Buildings.Find(b => b.BuildingId == hut.MineEntranceId && b.Type == BuildingTypes.MineshaftEntrance);
            if (entrance == null || hut.Type != BuildingTypes.Miner)
            {
                hut.MineEntranceId = 0;
                continue;
            }
            hut.Entrance = entrance;
            entrance.LinkedHuts.Add(hut);
        }
    }

    public string OwnerUid => Members.Find(m => m.Role == ColonyRole.Owner)?.PlayerUid ?? "";

    public ColonyRole RoleOf(string playerUid)
    {
        return Members.Find(m => m.PlayerUid == playerUid)?.Role ?? ColonyRole.Visitor;
    }

    public bool CanManage(string playerUid) => RoleOf(playerUid) >= ColonyRole.Officer;
}
