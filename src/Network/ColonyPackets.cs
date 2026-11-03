using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;
using CreateAColony.Colonies;

namespace CreateAColony.Network;

/// <summary>Client asks to open the Town Hall window for the Town Hall at Pos.</summary>
[ProtoContract]
public class TownHallOpenRequest
{
    [ProtoMember(1)] public BlockPos Pos = new BlockPos(0);
}

[ProtoContract]
public class CitizenView
{
    [ProtoMember(1)] public int CitizenId;
    [ProtoMember(2)] public string Name = "";
    [ProtoMember(3)] public string Job = "";
    [ProtoMember(4)] public string State = "";
    [ProtoMember(5)] public int FoodPercent;
    [ProtoMember(6)] public bool HasBed;
    /// <summary>A wanderer asking to join, and how many in-game hours it still waits.</summary>
    [ProtoMember(7)] public bool Visitor;
    [ProtoMember(8)] public int VisitorHoursLeft;
    /// <summary>Where the settler was last seen, for the Town map.</summary>
    [ProtoMember(9)] public int X;
    [ProtoMember(10)] public int Z;
    /// <summary>A child, who cannot be given a job, and how many in-game hours until it grows up.</summary>
    [ProtoMember(11)] public bool Child;
    [ProtoMember(12)] public int GrowsUpHours;
    /// <summary>For the dead: when they died (0 if unknown) and whether they have been buried.</summary>
    [ProtoMember(13)] public double DiedTotalDays;
    /// <summary>CitizenRecord.DeathCause, named on the client.</summary>
    [ProtoMember(31)] public string DeathCause = "";
    [ProtoMember(14)] public bool Buried;
    /// <summary>In a hut's worker lists: the settler's level in the hut's job skill, and the skill's code.</summary>
    [ProtoMember(15)] public int SkillLevel;
    [ProtoMember(16)] public string Skill = "";
    /// <summary>Their traits in a line, "Hard Worker 3, Glutton 1", for the Town Hall and hut lists.</summary>
    [ProtoMember(17)] public string TraitsText = "";
    /// <summary>In a hut's worker lists: they have the job trait for this hut's job.</summary>
    [ProtoMember(18)] public bool JobTrait;
    /// <summary>In a hut's worker lists: their passion for the hut's job skill (0-2), and the trait that rules the job out, if any.</summary>
    [ProtoMember(19)] public int Passion;
    [ProtoMember(20)] public string Incapable = "";
    /// <summary>What the worker does at a hut whose workers each pick their work (a prospector's ProspectorRoles).</summary>
    [ProtoMember(21)] public int WorkRole;
    /// <summary>Colony militia tab: the weapon the settler would fight with, empty for none.</summary>
    [ProtoMember(30)] public string Weapon = "";
}

/// <summary>One person in a family list: who, how they are related, and what they do.</summary>
[ProtoContract]
public class FamilyMemberView
{
    [ProtoMember(1)] public int CitizenId;
    [ProtoMember(2)] public string Name = "";
    /// <summary>Already in words: "Partner", "Child", "Mother" and so on, or empty.</summary>
    [ProtoMember(3)] public string Relation = "";
    [ProtoMember(4)] public string Job = "";
    [ProtoMember(5)] public bool Child;
    [ProtoMember(6)] public int GrowsUpHours;
    [ProtoMember(7, IsRequired = true)] public bool Alive = true;
}

/// <summary>A couple and their children, or one settler on their own, living in a Residence.</summary>
[ProtoContract]
public class HouseholdView
{
    [ProtoMember(1)] public string Surname = "";
    [ProtoMember(2)] public List<FamilyMemberView> Members = new();
    /// <summary>Why this couple is not having a child right now, already in words; empty when they can.</summary>
    [ProtoMember(3)] public string Note = "";
}

/// <summary>A building on the Town Hall's Town map: what it is and the ground it stands on (block columns, inclusive).</summary>
[ProtoContract]
public class MapBuildingView
{
    [ProtoMember(1)] public string Type = "";
    [ProtoMember(2)] public string Name = "";
    [ProtoMember(3)] public int Level;
    [ProtoMember(4)] public bool UnderConstruction;
    [ProtoMember(5)] public int X1;
    [ProtoMember(6)] public int Z1;
    [ProtoMember(7)] public int X2;
    [ProtoMember(8)] public int Z2;
}

[ProtoContract]
public class BuildingView
{
    [ProtoMember(1)] public int BuildingId;
    [ProtoMember(2)] public string Type = "";
    [ProtoMember(3)] public int Level;
    /// <summary>Highest level with a blueprint; 0 means no blueprint is installed for this type.</summary>
    [ProtoMember(4)] public int MaxLevel;
    [ProtoMember(5)] public int TargetLevel;
    [ProtoMember(6)] public int ProgressPercent;
    /// <summary>What the work order waits on, a lang key suffix ("building", "waiting-materials", ...).</summary>
    [ProtoMember(7)] public string Status = "";
    [ProtoMember(9)] public int Rotation;
    /// <summary>The blueprint style (see BlueprintStyles).</summary>
    [ProtoMember(10)] public string Style = "";
    /// <summary>The name players gave it, or its type's name.</summary>
    [ProtoMember(11)] public string Name = "";
    /// <summary>Which of its type's variants it is; "" when the type has only one.</summary>
    [ProtoMember(12)] public string Variant = "";
    /// <summary>Remove hut was pressed: a builder is taking it down (ProgressPercent and Status are for that).</summary>
    [ProtoMember(13)] public bool Removing;
    /// <summary>Not a building but a trader's old house a builder is taking down (TeardownOrder.Id); Name is the trader's.</summary>
    [ProtoMember(240)] public int TeardownId;
    /// <summary>The work order is a repair (ColonyManager.Repair.cs); TargetLevel is then the level it is at.</summary>
    [ProtoMember(250)] public bool Repairing;
}

/// <summary>One line of the Town Hall's event log; the window fills the lang key in.</summary>
[ProtoContract]
public class EventView
{
    [ProtoMember(1)] public double TotalDays;
    [ProtoMember(2)] public string Key = "";
    [ProtoMember(3)] public List<string> Args = new();
}

/// <summary>One job on the Town Hall's Statistics tab: settlers with it, and places in huts (-1 when it needs no hut).</summary>
[ProtoContract]
public class JobView
{
    [ProtoMember(1)] public string Job = "";
    [ProtoMember(2)] public int Workers;
    [ProtoMember(3)] public int Places;
}

/// <summary>Everything the settler window shows: who they are, what they do, and what they carry.</summary>
[ProtoContract]
public class CitizenSnapshot
{
    [ProtoMember(1)] public string ColonyId = "";
    [ProtoMember(2)] public int CitizenId;
    [ProtoMember(3)] public string Name = "";
    [ProtoMember(4)] public string Job = "";
    [ProtoMember(5)] public string State = "";
    [ProtoMember(6)] public int FoodPercent;
    [ProtoMember(7)] public bool HasBed;
    /// <summary>What they are doing right now, already in words.</summary>
    [ProtoMember(8)] public string Activity = "";
    [ProtoMember(9)] public List<byte[]> Pack = new();
    [ProtoMember(10)] public int Slots;
    [ProtoMember(11)] public List<byte[]> Bags = new();
    [ProtoMember(12)] public byte[]? Hand;
    [ProtoMember(13)] public bool OpenWindow;
    /// <summary>The settler's entity, so the window can draw its model; 0 when it is not loaded.</summary>
    [ProtoMember(14)] public long EntityId;
    [ProtoMember(15)] public float Health;
    [ProtoMember(16)] public float MaxHealth;
    /// <summary>Walking speed as a fraction of normal (1 = 100%).</summary>
    [ProtoMember(17)] public float WalkSpeed = 1;
    /// <summary>Armor worn, by slot (head, body, legs); an empty array where the slot is empty.</summary>
    [ProtoMember(18)] public List<byte[]> Armor = new();
    /// <summary>The settler's family: partner, parents, children, brothers and sisters.</summary>
    [ProtoMember(19)] public List<FamilyMemberView> Family = new();
    [ProtoMember(20)] public bool Child;
    [ProtoMember(21)] public int GrowsUpHours;
    /// <summary>The Residence the settler lives in, or empty.</summary>
    [ProtoMember(22)] public string Home = "";
    /// <summary>For a couple: whether a child may come, already in words.</summary>
    [ProtoMember(23)] public string FamilyNote = "";
    [ProtoMember(24)] public string Surname = "";
    /// <summary>Every skill with its level and how much of it counts here; empty when skills are off.</summary>
    [ProtoMember(25)] public List<SkillView> Skills = new();
    /// <summary>Their traits with levels and effects, good first; empty when traits are off. (26-29 traits; 40+ the settler inventory thread.)</summary>
    [ProtoMember(26)] public List<TraitView> Traits = new();
    /// <summary>The sum of the traits' points.</summary>
    [ProtoMember(27)] public int TraitScore;
    // 26-39 are left for the traits and quest work.
    /// <summary>The tool bar by slot: weapon, three tools, offhand; an empty array where a slot is empty.</summary>
    [ProtoMember(40)] public List<byte[]> ToolBar = new();
    /// <summary>Clothes by the game's dress slot (0-11); an empty array where a slot is empty.</summary>
    [ProtoMember(41)] public List<byte[]> Clothes = new();
    /// <summary>Packed food, carried to eat on the go.</summary>
    [ProtoMember(42)] public List<byte[]> Lunch = new();
    /// <summary>True when the viewer may take and give the settler's things (colony members).</summary>
    [ProtoMember(43)] public bool CanEdit;
    /// <summary>The settler's character class, for the Character tab ("commoner").</summary>
    [ProtoMember(44)] public string ClothesClass = "";
    /// <summary>Happiness 0-100 and its mood word ("content"); -1 when happiness is off or for children. 50-59 are skills and happiness.</summary>
    [ProtoMember(50, IsRequired = true)] public int Happiness = -1;
    [ProtoMember(51)] public string Mood = "";
    /// <summary>Every thought, already in words ("Ate a cooked meal +4 (9 h)"), good ones first.</summary>
    [ProtoMember(52)] public List<string> Thoughts = new();
    /// <summary>Hours left of a breakdown, 0 when working normally.</summary>
    [ProtoMember(53)] public int BreakdownHours;
    /// <summary>Full XP a skill gets per day before the soft cap; 0 = no cap.</summary>
    [ProtoMember(54)] public int SkillDailyCap;
    /// <summary>The food groups (fruit, vegetable, protein, grain, dairy) as 0-1 of full; empty for an old save. 60-69 are the detailed stats.</summary>
    [ProtoMember(60)] public List<float> Nutrition = new();
    [ProtoMember(61)] public float Saturation;
    [ProtoMember(62)] public float SaturationMax;
    /// <summary>The air temperature where the settler stands, in °C; NaN when unknown.</summary>
    [ProtoMember(63, IsRequired = true)] public float Temperature = float.NaN;
    /// <summary>0 (dry) to 1 (soaked).</summary>
    [ProtoMember(64)] public float Wetness;
    /// <summary>How fast they heal and grow hungry, 1 = normal (traits and perks).</summary>
    [ProtoMember(65, IsRequired = true)] public float HealingRate = 1;
    [ProtoMember(66, IsRequired = true)] public float HungerRate = 1;
    /// <summary>Coins in their purse, their monthly wage, and whether the last one went unpaid (ColonyManager.Economy.cs).</summary>
    [ProtoMember(120)] public int Purse;
    [ProtoMember(121)] public int Wage;
    [ProtoMember(122)] public bool Unpaid;
    /// <summary>Their Faith (0 to 100, -1 unknown) and the word for it (ColonyManager.Church.cs).</summary>
    [ProtoMember(300, IsRequired = true)] public int Faith = -1;
    [ProtoMember(301)] public string FaithWord = "";
}

/// <summary>A click on a slot of the settler window: which settler, which area and slot, and how.</summary>
[ProtoContract]
public class SettlerSlotPacket
{
    [ProtoMember(1)] public string ColonyId = "";
    [ProtoMember(2)] public int CitizenId;
    /// <summary>ColonyManager.AreaPack and the like.</summary>
    [ProtoMember(3)] public string Area = "";
    [ProtoMember(4)] public int Index;
    [ProtoMember(5)] public bool Right;
    [ProtoMember(6)] public bool Shift;
}
/// <summary>The server's PlaceHutsByHand setting, sent on joining and whenever it changes.</summary>
[ProtoContract]
public class HandPlacePacket
{
    [ProtoMember(1)] public bool On;
}
/// <summary>A settler put something on a rack or shelf whose block entity does not redraw by itself when the server changes it (A Culinary Artillery's bottle rack).</summary>
[ProtoContract]
public class DisplayRedrawPacket
{
    [ProtoMember(1)] public int X;
    [ProtoMember(2)] public int Y;
    [ProtoMember(3)] public int Z;
}
/// <summary>One trait of a settler for the windows.</summary>
[ProtoContract]
public class TraitView
{
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public int Level;
    /// <summary>Plus for a good trait, minus for a bad one, 0 for a quirk.</summary>
    [ProtoMember(3)] public int Points;
    /// <summary>The job a job trait helps, empty for the rest.</summary>
    [ProtoMember(4)] public string Job = "";
    /// <summary>False for a job trait that does nothing in the settler's current job, and for a Brave guard while Shaken.</summary>
    [ProtoMember(5, IsRequired = true)] public bool Active = true;
    /// <summary>What it does at this level, already in words, one line per effect.</summary>
    [ProtoMember(6)] public string Effects = "";
    /// <summary>An earned trait (Veteran, Shaken) rather than one they were born with.</summary>
    [ProtoMember(7)] public bool Earned;
}

/// <summary>One skill of a settler for the windows: real level, usable level and what limits it.</summary>
[ProtoContract]
public class SkillView
{
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public int Level;
    /// <summary>The level that counts where the settler works or lives now (never above Level).</summary>
    [ProtoMember(3)] public int Usable;
    [ProtoMember(4)] public int Xp;
    /// <summary>XP needed for the next level, 0 at the top.</summary>
    [ProtoMember(5)] public int NextXp;
    /// <summary>The building whose level limits the skill when Usable is below Level; "@key" is a lang key.</summary>
    [ProtoMember(6)] public string CapNote = "";
    /// <summary>XP at which the current level began, so the bar runs from it to NextXp.</summary>
    [ProtoMember(7)] public int LevelXp;
    /// <summary>Passion: 0 none, 1 Interested, 2 Burning.</summary>
    [ProtoMember(8)] public int Passion;
    /// <summary>The trait that rules this skill out, empty if none.</summary>
    [ProtoMember(9)] public string Incapable = "";
    /// <summary>True when the skill is above 10 and loses XP every day.</summary>
    [ProtoMember(10)] public bool Decaying;
    [ProtoMember(11)] public int XpToday;
    /// <summary>Already in words: the next milestone perk and its level, empty past the last.</summary>
    [ProtoMember(12)] public string NextPerk = "";
}

/// <summary>Client asks to open the window of the hut block at Pos.</summary>
[ProtoContract]
public class HutOpenRequest
{
    [ProtoMember(1)] public BlockPos Pos = new BlockPos(0);
}

/// <summary>One line of the "Items needed" list: what, how many are needed and how many the builder has.</summary>
[ProtoContract]
public class ResourceView
{
    /// <summary>The item, serialized, so the window can draw its icon.</summary>
    [ProtoMember(1)] public byte[] Stack = System.Array.Empty<byte>();
    [ProtoMember(2)] public int Needed;
    [ProtoMember(3)] public int Have;
    [ProtoMember(4)] public bool IsTool;
    /// <summary>"wood" or "rock" when any type of it will do, else empty.</summary>
    [ProtoMember(5)] public string AnyOf = "";
    /// <summary>The item code the colony asks for (with * where any will do), for the give button.</summary>
    [ProtoMember(6)] public string Code = "";
    /// <summary>How many of it the viewer carries.</summary>
    [ProtoMember(7)] public int PlayerHas;
    /// <summary>The hut that makes it for this one when the colony has none ("Sawmill"), or empty.</summary>
    [ProtoMember(8)] public string MadeBy = "";
    /// <summary>How many are in the warehouse or other huts, ready for a courier to bring.</summary>
    [ProtoMember(9)] public int InColony;
}

/// <summary>One level of a Miner's Hut's mineshaft, for the Levels tab.</summary>
[ProtoContract]
public class MineLevelView
{
    [ProtoMember(1)] public int Level;
    [ProtoMember(2)] public int Y;
    [ProtoMember(3)] public int Sections;
    /// <summary>Blocks still to dig out or build on this level.</summary>
    [ProtoMember(4)] public int Left;
    /// <summary>The miner is working on this level now.</summary>
    [ProtoMember(5)] public bool Working;
}

/// <summary>One research node on the Archive's Research tab.</summary>
[ProtoContract]
public class ResearchView
{
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public string Branch = "";
    [ProtoMember(3)] public int Tier;
    /// <summary>0 locked, 1 open, 2 paused, 3 running, 4 done.</summary>
    [ProtoMember(4)] public int State;
    [ProtoMember(5)] public string Cost = "";
    /// <summary>What it still waits for (locked), or why it is not moving (running).</summary>
    [ProtoMember(6)] public string Note = "";
    [ProtoMember(7)] public int Points;
    [ProtoMember(8)] public int PointsNeeded;
    /// <summary>Running at another Archive of the colony.</summary>
    [ProtoMember(9)] public bool Elsewhere;
}

/// <summary>Everything a hut window shows.</summary>
[ProtoContract]
public class HutSnapshot
{
    [ProtoMember(1)] public string ColonyId = "";
    [ProtoMember(2)] public BuildingView Building = new();
    [ProtoMember(3)] public int ViewerRole;
    [ProtoMember(4)] public bool IsBuilderHut;
    /// <summary>The builder working from this hut, empty if none.</summary>
    [ProtoMember(5)] public string BuilderName = "";
    /// <summary>All work orders in the colony, oldest first (Builder's Hut only).</summary>
    [ProtoMember(6)] public List<BuildingView> Orders = new();
    /// <summary>What this hut's builder still needs for its current work order.</summary>
    [ProtoMember(7)] public List<ResourceView> Resources = new();
    /// <summary>Building name the resources are for.</summary>
    [ProtoMember(8)] public string ResourcesFor = "";
    /// <summary>Extra lines for the building tab, e.g. the warehouse summary.</summary>
    [ProtoMember(9)] public string Info = "";
    [ProtoMember(10)] public bool OpenWindow;
    [ProtoMember(11)] public bool Periodic;
    [ProtoMember(12)] public bool AnyWoodOrStone;
    /// <summary>The hut has its own storage, so the window offers a Storage button.</summary>
    [ProtoMember(13)] public bool HasStorage;
    /// <summary>A Forester's or Farmer's Hut, whose window has a Work area tab.</summary>
    [ProtoMember(14)] public bool IsGatherHut;
    [ProtoMember(15)] public int WorkRadius;
    [ProtoMember(16)] public List<int> RadiusChoices = new();
    [ProtoMember(17)] public bool Escort;
    [ProtoMember(18)] public bool CollectBerries;
    /// <summary>The guard escorting this hut's worker, empty if none.</summary>
    [ProtoMember(19)] public string GuardName = "";
    [ProtoMember(20)] public int GuardCount;
    /// <summary>A Potter's Hut, whose window has a Pottery tab.</summary>
    [ProtoMember(21)] public bool IsPotterHut;
    [ProtoMember(22)] public List<PotteryView> Pottery = new();
    [ProtoMember(23)] public List<int> KeepChoices = new();
    /// <summary>A Tavern, whose window has a Recruits tab.</summary>
    [ProtoMember(24)] public bool IsTavern;
    [ProtoMember(25)] public List<CreateAColony.Colonies.RecruitOffer> Recruits = new();
    /// <summary>Rusty gears the viewer carries plus those in the Tavern's storage.</summary>
    [ProtoMember(26)] public int Gears;
    /// <summary>One of the currency item, serialized, for its icon.</summary>
    [ProtoMember(27)] public byte[]? Currency;
    /// <summary>The viewer is in creative mode, where hiring is free.</summary>
    [ProtoMember(28)] public bool Creative;
    /// <summary>A Cook's Hut, whose window has a Kitchen tab. Its rows reuse PotteryView (Clay unused).</summary>
    [ProtoMember(29)] public bool IsCookHut;
    [ProtoMember(30)] public List<PotteryView> Kitchen = new();
    /// <summary>Kitchen: the food the cook has to cook with (its inventory, the hut, the warehouse), most first.</summary>
    [ProtoMember(190)] public List<HutItemView> KitchenOnHand = new();
    /// <summary>Kitchen: an officer has picked dishes; else the cook makes what it can.</summary>
    [ProtoMember(191)] public bool KitchenChosen;

    // The Main tab every hut has.
    /// <summary>The building's name (its own or its type's), without the level.</summary>
    [ProtoMember(31)] public string DisplayName = "";
    /// <summary>The job of the hut's workers, empty for buildings nobody works at.</summary>
    [ProtoMember(32)] public string Job = "";
    [ProtoMember(33)] public List<CitizenView> Workers = new();
    [ProtoMember(34)] public int WorkerSlots;
    /// <summary>Settlers who could be put to work here (Manage workers): everyone alive who is not a visitor and not already here.</summary>
    [ProtoMember(35)] public List<CitizenView> Candidates = new();
    [ProtoMember(36)] public int PickupPriority;
    [ProtoMember(37)] public bool PickupNow;
    /// <summary>Everything in the hut's storage, for the chest list.</summary>
    [ProtoMember(38)] public List<HutItemView> Items = new();

    // A Guard Tower's tabs.
    [ProtoMember(39)] public bool IsGuardTower;
    [ProtoMember(40)] public int GuardTask;
    [ProtoMember(41)] public BlockPos? GuardSpot;
    [ProtoMember(42)] public bool PatrolManual;
    [ProtoMember(43)] public List<BlockPos> PatrolPoints = new();
    /// <summary>The player the guards follow on the Follow task, empty if none.</summary>
    [ProtoMember(44)] public string FollowName = "";
    [ProtoMember(45)] public bool FollowTight;
    [ProtoMember(46)] public bool Retreat;
    /// <summary>Every creature type guards fight (entity code paths), and which of them this tower leaves alone.</summary>
    [ProtoMember(47)] public List<string> Hostiles = new();
    [ProtoMember(48)] public List<string> HostilesOff = new();
    [ProtoMember(49)] public List<CreateAColony.Colonies.MinStockEntry> MinStock = new();
    [ProtoMember(50)] public List<StatView> Stats = new();
    /// <summary>A Residence, whose window has a Residents tab: the households living there and its beds.</summary>
    [ProtoMember(52)] public bool IsResidence;
    [ProtoMember(53)] public List<HouseholdView> Households = new();
    [ProtoMember(54)] public int ResidenceBeds;
    [ProtoMember(55)] public int ResidenceFreeBeds;
    /// <summary>The hut block's position, so the window can show patrol points relative to it.</summary>
    [ProtoMember(51)] public BlockPos HutPos = new BlockPos(0);
    /// <summary>A Prospector's or Miner's Hut, whose window has an Ore tab: what the colony's prospectors found.</summary>
    [ProtoMember(56)] public bool IsOreHut;
    [ProtoMember(57)] public List<OreView> Ores = new();
    /// <summary>A Miner's Hut: the ore its miner digs for, empty for the best found.</summary>
    [ProtoMember(58)] public string MineOre = "";
    [ProtoMember(59)] public int OreReadings;
    /// <summary>A Miner's Hut: its miner goes to ore the prospectors found before working its shaft.</summary>
    [ProtoMember(61)] public bool MineProspected;
    /// <summary>A Quarry, whose window has a Quarry tab.</summary>
    [ProtoMember(60)] public bool IsQuarry;
    [ProtoMember(62)] public string PitInfo = "";
    /// <summary>For the Recruits tab: each offer's two best skills as "{0} {1}" lines, in the order of Recruits (empty when skills are off).</summary>
    [ProtoMember(63)] public List<string> RecruitSkills = new();
    /// <summary>For the Recruits tab: each offer's traits as a line, in the order of Recruits (empty when traits are off).</summary>
    [ProtoMember(64)] public List<string> RecruitTraits = new();
    /// <summary>For a Tavern: how many settlers the colony holds and how many it has (the Town Hall level sets the cap).</summary>
    [ProtoMember(65)] public int PopulationCap;
    [ProtoMember(66)] public int Population;
    /// <summary>A crafter hut (Smelter, Blacksmith, Sawmill): the name of its "keep in stock" tab, also the start of its lang keys; empty for other huts.</summary>
    [ProtoMember(80)] public string CraftTab = "";
    /// <summary>The rows of that tab. Clay is how many of the input each one takes (nuggets per ingot, ingots per piece).</summary>
    [ProtoMember(81)] public List<PotteryView> Craft = new();
    /// <summary>A hut with a Crafting recipes tab, and its rows (Clay is how many items one craft takes; Name says what it is made from).</summary>
    [ProtoMember(170)] public bool HasRecipes;
    [ProtoMember(171)] public List<PotteryView> Recipes = new();
    /// <summary>The recipes players taught this kind of hut (Key is the recipe's Id), and whether officers may teach more.</summary>
    [ProtoMember(172)] public List<PotteryView> Taught = new();
    [ProtoMember(173)] public bool CanTeach;
    /// <summary>Priorities tab: the keys of what the hut keeps (rows of its other tabs), in the order its worker makes them.</summary>
    [ProtoMember(240)] public List<string> Priorities = new();
    [ProtoMember(241)] public bool HasPriorities;
    /// <summary>Priorities rows for a hut's own work, not something it keeps in stock: a Tailor's mending.</summary>
    [ProtoMember(242)] public List<PotteryView> WorkRows = new();

    // The Miner's Hut's Levels, Settings and Guards tabs (82-99).
    [ProtoMember(82)] public bool IsMiner;
    /// <summary>The mineshaft's levels, top first; empty when the hut digs the old spiral shaft.</summary>
    [ProtoMember(83)] public List<MineLevelView> MineLevels = new();
    [ProtoMember(84)] public int MineLevel;
    [ProtoMember(85)] public int RepairLevel;
    [ProtoMember(86)] public int MaxMineLevels;
    [ProtoMember(87)] public int MineLevelsAllowed;
    [ProtoMember(88)] public string FillBlock = "";
    [ProtoMember(89)] public List<CitizenView> MineGuards = new();
    /// <summary>Guards of towers on Patrol mine not with this mine, and the mine they are with ("" for none) in CitizenView.Job.</summary>
    [ProtoMember(90)] public List<CitizenView> MineGuardChoices = new();
    [ProtoMember(91)] public int MineGuardSlots;
    /// <summary>Required resources: "N of M spots done" for a mineshaft piece, the used share for a builder.</summary>
    [ProtoMember(92)] public int UsedPercent = -1;
    [ProtoMember(93)] public string ResourcesStep = "";
    /// <summary>A Residence: the colony's other grown settlers, with where they live now (already in words, empty for nowhere) in Relation.</summary>
    [ProtoMember(94)] public List<FamilyMemberView> ResidentChoices = new();
    // 130-139 for the Explorer's and Gatherer's huts.
    /// <summary>An Explorer's or Gatherer's Hut: the marker groups its worker can put on the map, for the Markers tab; empty for other huts.</summary>
    [ProtoMember(130)] public List<string> MarkerGroups = new();
    /// <summary>The groups it is set not to mark.</summary>
    [ProtoMember(131)] public List<string> MarkersOff = new();
    /// <summary>How many markers its worker has put down.</summary>
    [ProtoMember(132)] public int MarkerCount;
    /// <summary>How many spots of its marker groups a player took markers off, so it leaves them alone.</summary>
    [ProtoMember(133)] public int RemovedMarkerCount;
    /// <summary>A Gatherer's Hut: what its gatherer does (GatherModes); empty for other huts.</summary>
    [ProtoMember(134)] public string GatherMode = "";
    /// <summary>A Gatherer's Hut: its collecting order (ColonyManager.GatherCategories), for the Priorities tab; empty for other huts.</summary>
    [ProtoMember(135)] public List<string> GatherOrder = new();
    /// <summary>A Mineshaft Entrance: the colony's Miner's Huts, those working its mine first.</summary>
    [ProtoMember(150)] public bool IsMineEntrance;
    [ProtoMember(151)] public List<MineHutView> MineHuts = new();
    /// <summary>A Miner's Hut working a Mineshaft Entrance's mine: that entrance's name; empty for its own mine.</summary>
    [ProtoMember(152)] public string MineEntranceName = "";
    /// <summary>The mine's size and what is left, or that it is derelict (Mineshaft Entrance).</summary>
    [ProtoMember(153)] public string MineInfo = "";
    /// <summary>A gathering hut's Allowed to collect tab: every key with its picture, and the keys turned off.</summary>
    [ProtoMember(160)] public List<string> CollectKeys = new();
    [ProtoMember(161)] public List<byte[]> CollectStacks = new();
    [ProtoMember(162)] public List<string> CollectOff = new();
    // 300-319: the Church and work stations (ColonyManager.Church.cs, ColonyManager.Stations.cs).
    [ProtoMember(300)] public bool IsChurch;
    /// <summary>The share of wages given to the church (0, 5 or 10), and the coins in the Church's storage.</summary>
    [ProtoMember(301)] public int TithePercent;
    [ProtoMember(302)] public int ChurchCoins;
    /// <summary>The colony's average Faith (-1 with nobody), and how many settlers are devout, faithful, doubting and faithless.</summary>
    [ProtoMember(303, IsRequired = true)] public int AverageFaith = -1;
    [ProtoMember(304)] public List<int> FaithCounts = new();
    /// <summary>Days until the next service (0 today), and whether one is under way now.</summary>
    [ProtoMember(305)] public int ServiceInDays;
    [ProtoMember(306)] public bool ServiceNow;
    [ProtoMember(307)] public List<ChurchDay> ChurchLedger = new();
    /// <summary>The names of the work stations in this building.</summary>
    [ProtoMember(308)] public List<string> Stations = new();
    /// <summary>This is a work station; HostName is the building it stands in ("" when that is gone).</summary>
    [ProtoMember(309)] public bool IsStation;
    [ProtoMember(310)] public string HostName = "";
    /// <summary>Orphans the church looks after and settlers sleeping in its beds.</summary>
    [ProtoMember(311)] public int Orphans;
    [ProtoMember(312)] public int Sheltered;
    /// <summary>Settlers who died lately and still wait for their funeral.</summary>
    [ProtoMember(313)] public int AwaitingFuneral;
    /// <summary>Events written down since the last chronicle, and how many make a new one.</summary>
    [ProtoMember(314)] public int ChronicleEvents;
    [ProtoMember(315)] public int ChronicleNeeds;
    /// <summary>The Requests tab (0.142.0): what the hut asked couriers for and how each is getting on.</summary>
    [ProtoMember(340)] public bool HasRequestsTab;
    [ProtoMember(341)] public List<RequestView> Requests = new();
    /// <summary>A Courier's Station: every courier and settler helping them, what they do and where they move things.</summary>
    [ProtoMember(342)] public bool IsCourierStation;
    [ProtoMember(343)] public List<CourierView> Couriers = new();
    [ProtoMember(344)] public int CourierJobs;
    // 400-419: the Archive's Research tab (ColonyManager.Research.cs).
    [ProtoMember(400)] public bool IsArchive;
    [ProtoMember(401)] public List<ResearchView> Research = new();
    [ProtoMember(402)] public int KnowledgeTokens;
    /// <summary>Nodes this Archive runs at once, and how many run now.</summary>
    [ProtoMember(403)] public int ResearchSlots;
    [ProtoMember(404)] public int ResearchRunning;
    /// <summary>Research speed at this Archive in percent (100 = normal).</summary>
    [ProtoMember(405)] public int ResearchSpeed;
    /// <summary>What slows research here: a missing table, shelf or light, noisy huts nearby.</summary>
    [ProtoMember(406)] public List<string> ResearchNotes = new();
    [ProtoMember(407)] public int ArchiveTier;
}

/// <summary>One row of a hut's Requests tab.</summary>
[ProtoContract]
public class RequestView
{
    [ProtoMember(1)] public byte[] Stack = System.Array.Empty<byte>();
    /// <summary>The item's name when there is no icon (a tool any material will do).</summary>
    [ProtoMember(2)] public string Name = "";
    [ProtoMember(3)] public int Quantity;
    [ProtoMember(4)] public int Delivered;
    /// <summary>How many of Quantity are a spare.</summary>
    [ProtoMember(5)] public int Extra;
    [ProtoMember(6)] public int InColony;
    [ProtoMember(7)] public string Status = "";
    /// <summary>0: on its way. 1: waiting. 2: nobody has it.</summary>
    [ProtoMember(8)] public int Color;
    /// <summary>"Builder's Hut: Smelter 2" when it is for a building job, else empty.</summary>
    [ProtoMember(9)] public string For = "";
}

/// <summary>One courier on a Courier's Station.</summary>
[ProtoContract]
public class CourierView
{
    [ProtoMember(1)] public string Name = "";
    [ProtoMember(2)] public string Doing = "";
    [ProtoMember(3)] public string From = "";
    [ProtoMember(4)] public string To = "";
    [ProtoMember(5)] public string Carrying = "";
    /// <summary>Not a courier: a settler without a job helping out.</summary>
    [ProtoMember(6)] public bool Helping;
}

/// <summary>One line of a hut's chest list: an item, how many there are, and which containers hold it.</summary>
/// <summary>One ore the colony's prospectors found, for the Ore tab.</summary>
[ProtoContract]
public class OreView
{
    /// <summary>The deposit code, such as "nativecopper".</summary>
    [ProtoMember(1)] public string Code = "";
    /// <summary>The strongest reading's density factor, 0 to 1.</summary>
    [ProtoMember(2)] public double Best;
    [ProtoMember(3)] public int Sites;
    [ProtoMember(4)] public int MinedOut;
    /// <summary>"40 blocks north-east" of the hut, to the strongest reading.</summary>
    [ProtoMember(5)] public string Where = "";
}

[ProtoContract]
public class HutItemView
{
    [ProtoMember(1)] public byte[] Stack = System.Array.Empty<byte>();
    [ProtoMember(2)] public int Count;
    [ProtoMember(3)] public List<BlockPos> Where = new();
}

/// <summary>One line of the Statistics tab: a key such as "kill:drifter-normal" and its counts for today, yesterday, 7 days, 100 days and all time.</summary>
[ProtoContract]
public class StatView
{
    [ProtoMember(1)] public string Key = "";
    [ProtoMember(2)] public int[] Counts = System.Array.Empty<int>();
}

/// <summary>One line of a Potter's Hut's Pottery tab: a kind of pottery, how many to keep and how many there are.</summary>
[ProtoContract]
public class PotteryView
{
    [ProtoMember(1)] public string Key = "";
    /// <summary>A fired piece, serialized, for its icon and name.</summary>
    [ProtoMember(2)] public byte[] Stack = System.Array.Empty<byte>();
    [ProtoMember(3)] public int Keep;
    [ProtoMember(4)] public int Have;
    [ProtoMember(5)] public int Clay;
    /// <summary>The name to show, where the stack's own name would not say it (a pot meal shows a cooking pot).</summary>
    [ProtoMember(6)] public string? Name;
    /// <summary>Kitchen: the Cooking level this meal takes, and the cook's usable level now (so a locked meal can say what is missing).</summary>
    [ProtoMember(7)] public int NeedsLevel;
    [ProtoMember(8)] public int CookLevel;
    [ProtoMember(9)] public bool Locked;
    /// <summary>A second, greyed line under the name (a recipe row: what it is made from).</summary>
    [ProtoMember(10)] public string? Detail;
    /// <summary>Priorities: a line to show instead of "have of keep" (a Tailor's "3 to mend").</summary>
    [ProtoMember(240)] public string? PriorityText;
}

[ProtoContract]
public class MemberView
{
    [ProtoMember(1)] public string PlayerUid = "";
    [ProtoMember(2)] public string Name = "";
    [ProtoMember(3)] public int Role;
}

/// <summary>Everything the Town Hall window shows. Sent when the window opens and after every change.</summary>
[ProtoContract]
public class ColonySnapshot
{
    [ProtoMember(1)] public string ColonyId = "";
    [ProtoMember(2)] public string Name = "";
    [ProtoMember(3)] public int Level;
    [ProtoMember(4)] public List<CitizenView> Citizens = new();
    [ProtoMember(5)] public List<MemberView> Members = new();
    /// <summary>The receiving player's own role, so the window can hide buttons they may not use.</summary>
    [ProtoMember(6)] public int ViewerRole;
    [ProtoMember(7)] public int BedCount;
    /// <summary>True when this snapshot should open the window, false when it only refreshes an open one.</summary>
    [ProtoMember(9)] public bool OpenWindow;
    [ProtoMember(10)] public bool HasWarehouse;
    [ProtoMember(11)] public int WarehouseChests;
    [ProtoMember(12)] public int OpenRequests;
    [ProtoMember(13)] public int DropOffs;
    [ProtoMember(14)] public List<BuildingView> Buildings = new();
    /// <summary>A timed refresh for construction progress; the window only redraws it on the Buildings tab.</summary>
    [ProtoMember(15)] public bool Periodic;
    /// <summary>Killed settlers are gone for good (else only knocked out); the owner switches it.</summary>
    [ProtoMember(16)] public bool Permadeath;
    [ProtoMember(17)] public int FreeBeds;
    /// <summary>About how many days pass between wanderers; 0 when wanderers are off.</summary>
    [ProtoMember(18)] public double WandererDays;
    [ProtoMember(19)] public bool WanderersNeedFreeBed;
    /// <summary>Columns of land the colony's claim covers, its number of squares, and the Town Hall's square size.</summary>
    [ProtoMember(20)] public long ClaimColumns;
    [ProtoMember(21)] public int ClaimSquares;
    [ProtoMember(22)] public int TownHallClaimSize;
    /// <summary>The event log for the Information tab, oldest first.</summary>
    [ProtoMember(23)] public List<EventView> Events = new();
    /// <summary>Every building with a work order, in the order builders take them, for the Work orders page.</summary>
    [ProtoMember(24)] public List<BuildingView> WorkOrders = new();
    [ProtoMember(25)] public List<JobView> Jobs = new();
    /// <summary>All huts' Statistics added together.</summary>
    [ProtoMember(26)] public List<StatView> Stats = new();
    [ProtoMember(27)] public double FoundedTotalDays;
    [ProtoMember(28)] public int BuildingCount;
    /// <summary>For the Town map: every building, and the colony's land as x1, z1, x2, z2 (inclusive) per square.</summary>
    [ProtoMember(29)] public List<MapBuildingView> MapBuildings = new();
    [ProtoMember(30)] public List<int> ClaimRects = new();
    /// <summary>The colony banner for the Banners tab (serialized), or null; whether it goes on new buildings; whether Heraldry: Banners is installed.</summary>
    [ProtoMember(31)] public byte[]? BannerStack;
    [ProtoMember(32)] public bool BannerOnNew;
    [ProtoMember(33)] public bool HeraldryInstalled;
    /// <summary>For the Prospecting tab: every ore the colony's prospectors found (where is from the Town Hall), how many readings, and how many Prospector's Huts.</summary>
    [ProtoMember(34)] public List<OreView> Ores = new();
    [ProtoMember(35)] public int OreReadings;
    [ProtoMember(36)] public int ProspectorHuts;
    /// <summary>How many settlers the colony can hold (PopulationBase + PopulationPerTownHallLevel x Town Hall level).</summary>
    [ProtoMember(45)] public int PopulationCap; // 37 to 44 are left to the quests branch
    /// <summary>The settlers' average happiness, -1 when happiness is off or nobody has a mood. 50-59 are skills and happiness.</summary>
    [ProtoMember(50, IsRequired = true)] public int AverageHappiness = -1;
    /// <summary>Where every quest stands for the viewer, for the Quests tab.</summary>
    [ProtoMember(37)] public List<QuestStateView> Quests = new();
    /// <summary>Colony militia switches.</summary>
    [ProtoMember(60)] public bool SelfDefence;
    [ProtoMember(61)] public bool SupportAllies;
    /// <summary>The Bailiff tab (110-119): the bailiff's name ("" for none) and whether they are on duty (BailiffState), the rules, the ledger and the estate report.</summary>
    [ProtoMember(110)] public string BailiffName = "";
    [ProtoMember(111)] public int BailiffState;
    [ProtoMember(112)] public int WandererRule;
    [ProtoMember(113)] public bool BailiffGivesJobs;
    [ProtoMember(114)] public bool BailiffCollectsRent;
    [ProtoMember(115)] public int TaxPercent;
    /// <summary>Coins in the treasury.</summary>
    [ProtoMember(116)] public int Treasury;
    [ProtoMember(117)] public List<LedgerDay> Ledger = new();
    [ProtoMember(118)] public List<string> EstateReport = new();
    [ProtoMember(119)] public bool BailiffLendsWorkers;
    [ProtoMember(120)] public bool BailiffPaysWages;
    /// <summary>The in-game day of the next payday, and whether the colony has a Royal Mint yet.</summary>
    [ProtoMember(121)] public int NextPayday;
    [ProtoMember(122)] public bool HasMint;
    /// <summary>What wages cost a month, for everyone with a job now.</summary>
    [ProtoMember(123)] public int WageBill;
    /// <summary>The colony's coin, whether it is one of its own choosing, and the ingot the minter strikes it from.</summary>
    [ProtoMember(124)] public string CurrencyName = "";
    [ProtoMember(125)] public bool OwnCoin;
    [ProtoMember(126)] public string CoinMetalName = "";
    /// <summary>The Town Hall level taxes and wages start at, and whether the colony has reached it.</summary>
    [ProtoMember(127)] public int EconomyLevel;
    [ProtoMember(128)] public bool EconomyOpen;
}

/// <summary>ColonySnapshot.BailiffState: none, on duty, knocked out, or too unhappy to work.</summary>
public static class BailiffState
{
    public const int None = 0, OnDuty = 1, Downed = 2, Sulking = 3;
}

/// <summary>Client asks for the quest book: from the hotkey, the Colony Ledger, the inventory button or the Town Hall's Quests tab.</summary>
[ProtoContract]
public class QuestBookOpenRequest
{
    [ProtoMember(1)] public int Source;
}

/// <summary>Where one quest stands for the viewer: its status (QuestStatus) and how far along each objective is.</summary>
[ProtoContract]
public class QuestStateView
{
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public int Status;
    [ProtoMember(3)] public List<int> Progress = new();
    [ProtoMember(4)] public List<int> Needed = new();
    [ProtoMember(5)] public bool Tracked;
}

/// <summary>Everything the quest book shows: the viewer's colony and where every quest stands for them.</summary>
[ProtoContract]
public class QuestBookSnapshot
{
    [ProtoMember(1)] public string ColonyId = "";
    [ProtoMember(2)] public string ColonyName = "";
    [ProtoMember(3)] public List<QuestStateView> Quests = new();
    [ProtoMember(4)] public bool OpenWindow;
    [ProtoMember(5)] public bool Periodic;
    /// <summary>The viewer's role in the colony, so the book knows whether they may claim colony quests.</summary>
    [ProtoMember(6)] public int ViewerRole;
}

public enum QuestAction
{
    Accept = 1,
    Claim = 2,
    Drop = 3,
    Track = 4,
    CloseWindow = 5,
    Refresh = 6
}

/// <summary>A change asked from the quest book; the server checks it before applying it.</summary>
[ProtoContract]
public class QuestActionPacket
{
    [ProtoMember(1)] public string Code = "";
    [ProtoMember(2)] public QuestAction Action;
}

public enum ColonyAction
{
    Rename = 1,
    SetJob = 2,
    SetRole = 3,
    RemoveMember = 4,
    CloseWindow = 5,
    Build = 6,
    CancelBuild = 7,
    Rotate = 8,
    /// <summary>Text "1" lets builders use any wood or stone type, "0" holds them to the blueprint's.</summary>
    SetAnyWoodOrStone = 9,
    /// <summary>Opens the storage of the hut with BuildingId.</summary>
    OpenStorage = 10,
    /// <summary>Text is how far, in blocks, the worker of the hut with BuildingId goes out.</summary>
    SetWorkRadius = 11,
    /// <summary>Text "1" sends a guard along with the worker of the hut with BuildingId.</summary>
    SetEscort = 12,
    /// <summary>Text "1" has the farmer of the hut with BuildingId pick berry bushes and fruit trees.</summary>
    SetCollectBerries = 13,
    /// <summary>Shows the outline and placement window of the building with BuildingId (the Builder's Wand).</summary>
    OpenPlacement = 14,
    /// <summary>Text "1" turns the building a quarter right, "-1" left, before it is built.</summary>
    PlacementTurn = 15,
    /// <summary>Text "1" raises the building one block, "-1" lowers it, before it is built.</summary>
    PlacementHeight = 16,
    /// <summary>Keeps the building where it is and orders level 1.</summary>
    PlacementConfirm = 17,
    /// <summary>Takes the hut back: the block goes back into the player's inventory.</summary>
    PlacementCancel = 18,
    /// <summary>Hides the outline.</summary>
    PlacementClose = 19,
    /// <summary>Text "forward", "back", "left" or "right" moves the building one block that way, as the player faces.</summary>
    PlacementNudge = 20,
    /// <summary>Opens the placement window again for the outline still showing (the wand used in the air).</summary>
    ReopenPlacement = 21,
    /// <summary>The wand used on the ground with nothing planned: Text "x,y,z" is the spot; opens the building list.</summary>
    WandStart = 22,
    /// <summary>Text is the building type picked from the list; its outline appears at the spot.</summary>
    WandChoose = 23,
    /// <summary>Back to the building list for the planned spot.</summary>
    WandChange = 24,
    /// <summary>Text "kind|amount": how many of a kind of pottery the Potter's Hut with BuildingId keeps in stock.</summary>
    SetPotteryKeep = 25,
    /// <summary>Lets the visitor with CitizenId join the colony.</summary>
    WelcomeVisitor = 26,
    /// <summary>Sends the visitor with CitizenId away.</summary>
    TurnAwayVisitor = 27,
    /// <summary>Text "1" turns permadeath on for the colony, "0" off. Owner only.</summary>
    SetPermadeath = 28,
    /// <summary>Hires the recruit named Text at the Tavern with BuildingId.</summary>
    HireRecruit = 29,
    /// <summary>Text "food|amount": how much of a cooked food the Cook's Hut with BuildingId keeps in stock.</summary>
    SetKitchenKeep = 30,
    /// <summary>Text is the new name of the building with BuildingId; empty goes back to its type's name.</summary>
    RenameBuilding = 31,
    /// <summary>Puts the settler with CitizenId to work at the hut with BuildingId.</summary>
    AssignWorker = 32,
    /// <summary>Takes the settler with CitizenId off the job at the hut with BuildingId.</summary>
    UnassignWorker = 33,
    /// <summary>Brings the workers of the hut with BuildingId to it.</summary>
    RecallWorkers = 34,
    /// <summary>Text 0 to 10: how soon couriers come for the hut's things; 0 is never.</summary>
    SetPickupPriority = 35,
    /// <summary>The next free courier takes everything in the hut's storage.</summary>
    PickupNow = 36,
    /// <summary>Gives the player a Guard Scepter for the Guard Tower with BuildingId.</summary>
    GetGuardScepter = 37,
    /// <summary>Clears the Guard Tower's patrol points and guard spot.</summary>
    ClearGuardPoints = 38,
    /// <summary>Text "code|1" has the tower's guards fight that creature, "code|0" leave it alone; "*" sets all back on.</summary>
    SetHostile = 39,
    /// <summary>Text is the tower's task (GuardTasks); Follow follows the player who chose it.</summary>
    SetGuardTask = 40,
    /// <summary>Text "1" makes badly hurt guards fall back to the tower.</summary>
    SetRetreat = 41,
    /// <summary>Text "1" makes patrols walk the Guard Scepter's points, "0" hut to hut.</summary>
    SetPatrolManual = 42,
    /// <summary>Text "1" keeps following guards close, "0" a little further off.</summary>
    SetFollowTight = 43,
    /// <summary>Adds the item in the player's hand to the hut's Minimum stock list.</summary>
    AddMinStock = 44,
    /// <summary>Text "code|count": how many of an item the hut keeps; 0 takes it off the list.</summary>
    SetMinStock = 45,
    /// <summary>Brings the settler with CitizenId to the Town Hall at once.</summary>
    RecallCitizen = 46,
    /// <summary>Makes the Heraldry banner in the player's hand the colony banner.</summary>
    SetColonyBanner = 47,
    /// <summary>Copies the colony banner onto the banners of the Town Hall ("townhall"), the Guard Towers ("guardtower") or every building ("all").</summary>
    ReplaceBanners = 48,
    /// <summary>Text "1" puts the colony banner on each building once a builder finishes it.</summary>
    SetBannerOnNew = 49,
    /// <summary>Text "-1" or "1" shows the level below or above in the placement preview.</summary>
    PlacementLevel = 50,
    /// <summary>Text is the ore (deposit code) the Miner's Hut with BuildingId digs for; empty for the best found.</summary>
    SetMineOre = 51,
    /// <summary>Turns "Go to prospected ore" on (Text "1") or off for the Miner's Hut with BuildingId.</summary>
    SetMineProspected = 52,
    /// <summary>Forgets the Miner's Hut's site and tunnel, so the miner starts a new mine.</summary>
    NewMine = 53,
    /// <summary>Text is the asset pack (blueprint style) the player builds with from now on; opens the building list again.</summary>
    WandPack = 54,
    /// <summary>Text "-1" or "1" switches the building being placed to the type's previous or next variant (a small or a medium house).</summary>
    PlacementVariant = 55,
    /// <summary>Text "key|amount": how many of a thing the crafter hut (Smelter, Blacksmith, Sawmill) with BuildingId keeps in stock.</summary>
    SetCraftKeep = 70,
    /// <summary>Text is what the settler with CitizenId does at the Prospector's Hut with BuildingId (ProspectorRoles: 0 finds ore, 1 mines it).</summary>
    SetWorkRole = 90,
    /// <summary>Text is the level (1 at the top) new mineshaft sections of the Miner's Hut with BuildingId go on first; 0 lets the miner pick.</summary>
    SetMineLevel = 91,
    /// <summary>Text is the level the miner puts back as it was first.</summary>
    RepairMineLevel = 92,
    /// <summary>Text is how many levels deep the mineshaft goes at most; 0 as deep as the hut allows.</summary>
    SetMaxMineLevels = 93,
    /// <summary>Text is what holes in the mineshaft floor are filled with (MineFillBlocks).</summary>
    SetFillBlock = 94,
    /// <summary>Puts the guard with CitizenId on the Miner's Hut with BuildingId.</summary>
    AssignMineGuard = 95,
    /// <summary>Takes the guard with CitizenId off the Miner's Hut with BuildingId.</summary>
    UnassignMineGuard = 96,
    /// <summary>Moves the item with code Text from the player's inventory into the hut's storage, as many as are still needed.</summary>
    GiveResource = 97,
    /// <summary>Fulfil all: Text holds one "code|count" line per item; each moves from the player into the hut's storage, as many as it still needs.</summary>
    GiveAllResources = 98,
    /// <summary>A Residence's Choose residents page: move a settler and their household in, or out to a bed elsewhere.</summary>
    MoveInResidence = 99,
    MoveOutResidence = 100,
    /// <summary>Text "group|1" or "group|0": whether the Explorer's or Gatherer's Hut with BuildingId puts that marker group on the map.</summary>
    SetMarkerGroup = 120,
    /// <summary>Takes away every map marker the worker of the hut with BuildingId put down.</summary>
    RemoveMarkers = 121,
    /// <summary>Takes the map marker with MarkerId Text off the map (right-click on the world map); settlers leave that spot alone after.</summary>
    RemoveMarker = 122,
    /// <summary>Lets the worker of the hut with BuildingId mark the spots players took markers off again.</summary>
    MarkRemovedAgain = 123,
    SetGatherMode = 124,
    /// <summary>Colony militia switches (officers); Text is "1" or "0".</summary>
    SetSelfDefence = 140,
    SetSupportAllies = 141,
    /// <summary>Remove hut: the hut's workers lose their job, a builder takes the building down and the hut block comes back.</summary>
    RemoveHut = 150,
    /// <summary>Stops taking a trader's old house down; BuildingId is the TeardownOrder id.</summary>
    CancelTeardown = 240,
    /// <summary>Priorities tab: moves Text (a key) by Role (-1 up, 1 down, -1000 to the top). BuildingId 0 or a Builder's Hut: the builders' queue.</summary>
    MovePriority = 241,
    /// <summary>Has the Miner's Hut with Text (its BuildingId) work the mine of the Mineshaft Entrance with BuildingId.</summary>
    LinkMineHut = 151,
    UnlinkMineHut = 152,
    /// <summary>Text "key|1" or "key|0": whether the gathering hut with BuildingId collects that; "*" turns all on, "-" all off.</summary>
    SetCollect = 160,
    /// <summary>How many of a thing a hut's worker keeps crafted (Crafting recipes tab): Text = "code|amount".</summary>
    SetRecipeKeep = 170,
    /// <summary>Teach recipe (officers): every hut of this kind learns a recipe. Text = "any|inputs|output" (ColonyManager.TeachRecipe).</summary>
    TeachRecipe = 171,
    /// <summary>Forgets the taught recipe with Id Text.</summary>
    ForgetRecipe = 172,
    /// <summary>The Bailiff tab's rules (officers): Text "wanderers|n" (BailiffWanderers), "jobs|1", "lend|1" or "rent|0".</summary>
    SetBailiffRule = 180,
    /// <summary>Repair button on a hut's Main tab: a builder puts the building back as its blueprint has it.</summary>
    Repair = 250,
    /// <summary>A Church's Church tab (officers): Text is the tithe in percent (0, 5 or 10).</summary>
    SetTithe = 300,
    /// <summary>The Archive's Research tab (officers): starts or resumes the node with code Text at the Archive BuildingId.</summary>
    StartResearch = 400,
    /// <summary>Pauses the node with code Text; it keeps its points.</summary>
    PauseResearch = 401
}

/// <summary>One Miner's Hut on a Mineshaft Entrance's Main tab.</summary>
[ProtoContract]
public class MineHutView
{
    [ProtoMember(1)] public int BuildingId;
    [ProtoMember(2)] public string Name = "";
    [ProtoMember(3)] public int Level;
    /// <summary>Its miner's name, or empty when nobody works there.</summary>
    [ProtoMember(4)] public string Worker = "";
    [ProtoMember(5)] public bool Linked;
    /// <summary>The other Mineshaft Entrance it works under, if any.</summary>
    [ProtoMember(6)] public string Elsewhere = "";
}

/// <summary>The Builder's Wand building list: each building type with how many of its blocks the player carries.</summary>
[ProtoContract]
public class WandChooserSnapshot
{
    [ProtoMember(1)] public List<string> Types = new();
    [ProtoMember(2)] public List<int> Have = new();
    /// <summary>"type|style" for each building type that has blueprints in that style.</summary>
    [ProtoMember(3)] public List<string> WithBlueprints = new();
    /// <summary>The style of the building being planned, when the list was reopened with Change building; empty otherwise.</summary>
    [ProtoMember(4)] public string Style = "";
    /// <summary>The asset pack the player builds with; empty until they pick one, which the list asks for first.</summary>
    [ProtoMember(5)] public string Pack = "";
    /// <summary>"type|style|count" for each building type with more than one variant in that style.</summary>
    [ProtoMember(6)] public List<string> VariantCounts = new();
}

/// <summary>
/// Where the ghost preview of a building stands. Its blocks travel to the client in the player's preview
/// mini-dimension, like the WorldEdit import preview; this only tells the client which one it is and where
/// to draw it. DimId -1 hides it.
/// </summary>
[ProtoContract]
public class GhostPreviewPacket
{
    [ProtoMember(1)] public int DimId = -1;
    [ProtoMember(2)] public int X;
    [ProtoMember(3)] public int Y;
    [ProtoMember(4)] public int Z;
}

/// <summary>What the placement window shows for one building while its outline is on screen.</summary>
[ProtoContract]
public class PlacementSnapshot
{
    [ProtoMember(1)] public string ColonyId = "";
    [ProtoMember(2)] public int BuildingId;
    [ProtoMember(3)] public string Type = "";
    [ProtoMember(4)] public int Rotation;
    [ProtoMember(5)] public int YOffset;
    /// <summary>The level whose outline is shown, the highest there is; 0 when the type has no blueprints.</summary>
    [ProtoMember(6)] public int ShownLevel;
    [ProtoMember(7)] public int Blocks;
    /// <summary>Outline blocks where something already stands that the builder will clear.</summary>
    [ProtoMember(8)] public int Blocked;
    /// <summary>Not built yet, so it can still be turned, moved up or down, or taken back.</summary>
    [ProtoMember(9)] public bool CanAdjust;
    [ProtoMember(10)] public bool CanOrder;
    [ProtoMember(11)] public bool OpenWindow;
    [ProtoMember(12)] public int XOffset;
    [ProtoMember(13)] public int ZOffset;
    /// <summary>A building planned with the wand and not placed yet (BuildingId is 0).</summary>
    [ProtoMember(14)] public bool Planned;
    /// <summary>How many of the building's blocks the player carries.</summary>
    [ProtoMember(15)] public int Have;
    /// <summary>The blueprint style shown (see BlueprintStyles).</summary>
    [ProtoMember(16)] public string Style = "";
    /// <summary>The highest level the building's blueprints go to; ShownLevel is picked from 1 to this.</summary>
    [ProtoMember(17)] public int MaxLevel;
    /// <summary>The names of the buildings nearby that the shown level would overlap.</summary>
    [ProtoMember(18)] public List<string> Overlaps = new();
    /// <summary>The names of the buildings nearby it would overlap once they are upgraded to their top level.</summary>
    [ProtoMember(19)] public List<string> OverlapsLater = new();
    /// <summary>How many buildings nearby have their outlines shown.</summary>
    [ProtoMember(20)] public int Nearby;
    /// <summary>The variant shown (see BlueprintLibrary.Variants), and all the type's variants in this style, in order.</summary>
    [ProtoMember(21)] public string Variant = "";
    [ProtoMember(22)] public List<string> Variants = new();
}

/// <summary>A change requested from the Town Hall window. The server checks permissions before applying it.</summary>
[ProtoContract]
public class ColonyActionPacket
{
    [ProtoMember(1)] public string ColonyId = "";
    [ProtoMember(2)] public ColonyAction Action;
    [ProtoMember(3)] public int CitizenId;
    [ProtoMember(4)] public string Text = "";
    [ProtoMember(5)] public string PlayerUid = "";
    [ProtoMember(6)] public int Role;
    [ProtoMember(7)] public int BuildingId;
}
