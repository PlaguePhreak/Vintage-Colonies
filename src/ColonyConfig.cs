using System.Collections.Generic;

namespace CreateAColony;

/// <summary>Server config, stored in ModConfig/createacolony.json.</summary>
public class ColonyConfig
{
    public const string FileName = "createacolony.json";
    /// <summary>Bumped when a default changes in a way old config files should pick up; see ColonyManager.UpgradeConfig.</summary>
    public const int CurrentConfigVersion = 7;

    /// <summary>Which defaults this file has been brought up to. Files from before 0.33.0 have none, so read as 0.</summary>
    public int ConfigVersion;

    /// <summary>The mod is designed around permadeath. When false, a killed settler is
    /// knocked out instead and returns to the Town Hall after DownedRecoveryDays.
    /// This is the default for new colonies; each colony's owner can switch it in the Town Hall window.</summary>
    public bool Permadeath = true;
    public double DownedRecoveryDays = 1.0;

    /// <summary>Settlers that arrive when a colony is founded (rolled between min and max).</summary>
    public int StartingSettlersMin = 3;
    public int StartingSettlersMax = 4;

    public int MaxConcurrentPathRequestsPerColony = 4;

    // Needs
    /// <summary>Full stomach. A settler starts here and eats when below EatBelowFraction of it.</summary>
    public float SaturationMax = 1200f;
    /// <summary>Saturation lost per in-game day; 600 means a full settler gets hungry in about a day and a half.</summary>
    public float HungerPerDay = 600f;
    public float EatBelowFraction = 0.4f;
    /// <summary>Raw grain, meat, fish and meal vegetables are left for the cooks until a settler is below this share of a full stomach.</summary>
    public float EatRawBelowFraction = 0.2f;
    /// <summary>Health lost per in-game hour while saturation is zero. Settlers have 20 health.</summary>
    public float StarvationDamagePerHour = 0.5f;

    // Sleep
    public float SleepStartHour = 21f;
    public float SleepEndHour = 6f;
    /// <summary>A settler that cannot find a path to its bed but is this close (blocks) gets into it anyway, instead of sleeping on the floor.</summary>
    public float BedFindWayRange = 16f;

    // Storms
    /// <summary>Settlers head home this many in-game hours before a temporal storm starts.</summary>
    public float StormWarningHours = 2f;

    // Respawn
    /// <summary>A settler not seen for this long while the Town Hall area is loaded is treated as lost and respawned.</summary>
    public double RespawnMissingAfterDays = 0.5;

    // Construction
    /// <summary>When false, builders conjure blocks without materials. Handy for creative testing.</summary>
    public bool BuildersNeedMaterials = true;
    /// <summary>
    /// Builders give each wattle and daub block a stage of their own (wattle, cracked or smooth) instead of the
    /// blueprint's, picked from where the block stands, so the same building looks a little different each time.
    /// </summary>
    public bool RandomDaubStages = true;
    /// <summary>Seconds a builder takes per block placed or cleared.</summary>
    public float BuilderSecondsPerBlock = 0.75f;
    /// <summary>
    /// How far a builder can reach to place or clear a block when it cannot walk any closer, such as a roof
    /// block high above the ground or one over a wall.
    /// </summary>
    public float BuilderReach = 12f;
    /// <summary>Builders need a shovel, axe or pickaxe of the right tier to clear blocks, and wear it out.</summary>
    public bool BuildersNeedTools = true;
    /// <summary>
    /// Blocks whose saved details builders copy from a blueprint, such as a VS Roofing roof's frame, shape
    /// and materials. Builders pay for what those details hold. Leave chests out: their contents would come free.
    /// </summary>
    public string[] BlueprintBlockDetails = { "vsroofing:*" };

    /// <summary>
    /// Bindings that VS Roofing takes in the other hand for stick, bamboo and firewood frames. When a roof in a
    /// blueprint was tied with one of these, a builder may use any of them.
    /// </summary>
    public string[] RoofBindings = { "game:flaxfibers", "game:cattailtops", "game:papyrustops", "game:wildvine-*" };

    /// <summary>
    /// Blocks a builder places the way a player does, from the item in hand, so the block sets itself up:
    /// W4RD0's Furniture (wood and cloth colours, shelves of several cells, beds), Heraldry : Banners poles and
    /// Food Shelves (shelves, baskets and cabinets in their wood). Builders pay for the furniture item in the same
    /// wood and colours as the blueprint, and for a banner with the same design when a pole holds one. Nothing
    /// inside furniture comes along. The mods are optional.
    /// </summary>
    public string[] BlueprintPlaceAsItem = { "w4rd0sfurniture:*", "heraldrybanners:*", "foodshelves:*" };
    /// <summary>
    /// Extra blocks (code patterns) that count as storage for huts and the Warehouse, for mods whose storage the
    /// mod does not recognise by itself. Chests, trunks, crates and clay storage vessels always count.
    /// </summary>
    public string[] HutStorageBlocks = System.Array.Empty<string>();

    /// <summary>
    /// World-generated land claims (no player owner) whose name or description contains one of these words count as
    /// trader claims: they do not shrink the colony's land, and go when their trader joins.
    /// </summary>
    public string[] ClearedWorldgenClaims = { "trader" };

    // Traders join the colony (see Colonies/ColonyManager.Traders.cs)
    /// <summary>
    /// Every trader's land claim in the world is taken away, near a colony or not. Off: traders keep their claims
    /// (given back to houses that lost them) until they join a colony.
    /// </summary>
    public bool RemoveAllTraderClaims = false;
    /// <summary>A trader a settler finds joins the colony as a new settler, skilled for its trade.</summary>
    public bool TradersJoin = true;
    /// <summary>How close, in blocks, a settler must come to a trader (and see it) to find it.</summary>
    public int TraderFindRange = 16;
    /// <summary>The chance each stack a trader was selling, and its money, goes to the warehouse when it joins. The rest is lost.</summary>
    public double TraderGoodsChance = 0.25;
    /// <summary>Level of a former trader's main skill (rolled between the two), with two passions.</summary>
    public int TraderMainSkillMin = 6;
    public int TraderMainSkillMax = 10;
    /// <summary>Level of a former trader's second skill (rolled between the two), with one passion.</summary>
    public int TraderSecondSkillMin = 3;
    public int TraderSecondSkillMax = 4;
    /// <summary>A builder takes down the house of a trader who joined, without being asked.</summary>
    public bool TraderHouseTeardown = true;
    /// <summary>Trader houses further than this from the Town Hall, in blocks, are left standing: too far for a builder to walk.</summary>
    public int TraderTeardownMaxDistance = 400;

    // Forester
    /// <summary>How far from the Forester's Hut, in blocks, a forester looks for trees.</summary>
    public int ForesterRadius = 16;
    /// <summary>Seconds a forester chops before a tree comes down.</summary>
    public float ForesterSecondsPerTree = 6f;
    /// <summary>Tree seeds or saplings a forester keeps on hand for replanting; the rest go to the warehouse.</summary>
    public int ForesterSeedReserve = 8;
    /// <summary>
    /// The forester also clears wild bushes and leaves standing on the ground near its hut (the leaves give sticks).
    /// The Allowed to collect tab still counts: a wood turned off there is left alone.
    /// </summary>
    public bool ForesterClearsBushes = true;
    /// <summary>Seconds a forester takes to clear one bush.</summary>
    public float ForesterSecondsPerBush = 3f;
    /// <summary>Bigger clumps of leaves than this many blocks are left alone (they are likely part of a tree).</summary>
    public int ForesterBushMaxBlocks = 48;

    // Farmer
    /// <summary>How far a field reaches from its Field marker: 4 makes a 9 by 9 field.</summary>
    public int FieldRadius = 4;
    /// <summary>How many fields one farmer looks after.</summary>
    public int FieldsPerFarmer = 4;
    /// <summary>Seconds a farmer takes to till, sow or harvest one block.</summary>
    public float FarmerSecondsPerBlock = 1.5f;
    /// <summary>Seeds a farmer keeps on hand for sowing; the rest go to the warehouse.</summary>
    public int FarmerSeedReserve = 32;
    /// <summary>How far from the Farmer's Hut a farmer picks berries and fruit from bushes and fruit trees.</summary>
    public int FarmerOrchardRadius = 16;
    /// <summary>A farmer with a watering can waters growing crops whose farmland is drier than this (0 dry to 1 soaked), filling it to 100%.</summary>
    public float FarmerWaterBelow = 0.5f;
    /// <summary>A farmer with fertilizer spreads it before sowing when the crop's nutrient is below this (soil shows 0 to 100).</summary>
    public float FarmerFertilizeBelow = 40f;
    /// <summary>Farmers only sow a crop that will live until it is ripe, working out the cold and heat it would meet from the region's climate and the seasons ahead.</summary>
    public bool FarmerChecksSeasons = true;
    /// <summary>Nothing is sown in winter.</summary>
    public bool FarmerSkipsWinter = true;
    /// <summary>How much of the cold or heat damage that kills a crop (48 hours of it in the game) a sown crop may take: 0.5 is half.</summary>
    public float FarmerCropDamageAllowed = 0.5f;
    /// <summary>Degrees of safety: a crop counts as cold this much above its cold limit, and as hot this much below its heat limit.</summary>
    public float FarmerSeasonMarginDegrees = 1f;
    /// <summary>Farmers go after animals that eat the crops on their fields: they kill wild ones and chase livestock off.</summary>
    public bool FarmerGuardsCrops = true;
    /// <summary>Farmers swap poor soil on bare field spots for better soil from the Farmer's Hut storage (or asked for from the warehouse).</summary>
    public bool FarmerUpgradesSoil = true;
    /// <summary>When the colony has a hunter, the farmer calls it and only goes after the animal itself if it is still at the crops this many seconds later.</summary>
    public float FarmerWaitsForHunterSeconds = 45f;
    /// <summary>How far from its fields, in blocks, a farmer goes after a crop eater before it counts as chased off.</summary>
    public int FarmerChaseDistance = 16;

    // Prospector
    /// <summary>How far from the Prospector's Hut, in blocks, a prospector takes readings (change it on the Work area tab).</summary>
    public int ProspectorRadius = 64;
    /// <summary>A prospector walks on this many blocks at least from its last reading (or a good one) before the next...</summary>
    public int ProspectorMinStep = 25;
    /// <summary>...and this many at most; each step is a random distance in between.</summary>
    public int ProspectorMaxStep = 50;
    /// <summary>No new reading within this many blocks of one the colony already has.</summary>
    public int ProspectorNearReading = 16;
    /// <summary>How strongly a prospector heads where nearby readings were good: 0 is purely random; at 15 an area that reads "decent" all round is picked about 25 times as often as one that reads "very poor".</summary>
    public double ProspectorPreference = 15;
    /// <summary>What a prospector expects of ground with no reading near it (0 to 1), so new ground is worth a look beside poor readings but not beside good ones.</summary>
    public double ProspectorUnknownQuality = 0.15;
    /// <summary>Seconds a prospector spends tapping the ground for one reading (a player takes three samples).</summary>
    public float ProspectorSecondsPerReading = 9f;
    /// <summary>Wear on the prospecting pick per reading; a player's three samples wear it 3.</summary>
    public int ProspectorPickWear = 3;
    /// <summary>Each reading also shows on the "Colony prospecting" layer of every colony member's world map (kept apart from their own prospecting layer).</summary>
    public bool ProspectorSharesOreMap = true;

    // Explorer
    /// <summary>How far from the Explorer's Hut, in blocks, the explorer goes (change it on the Work area tab).</summary>
    public int ExplorerRadius = 300;
    /// <summary>The explorer looks over the land in squares this many blocks wide, one stop in the middle of each.</summary>
    public int ExplorerStep = 32;
    /// <summary>How far around each stop, in blocks, the explorer looks for things to mark.</summary>
    public int ExplorerLookRadius = 20;
    /// <summary>Seconds the explorer spends looking around at each stop.</summary>
    public float ExplorerSecondsPerLook = 4f;
    /// <summary>Once the explorer has seen all of its area, it starts over after this many days, to find traders and animals that moved.</summary>
    public double ExplorerRevisitDays = 7;
    /// <summary>Ground this many blocks above sea level or more is marked as a mountain.</summary>
    public int ExplorerMountainHeight = 60;

    // Hunter
    /// <summary>How far from the Hunter's Hut, in blocks, the hunter hunts (change it on the Work area tab).</summary>
    public int HunterRadius = 48;
    /// <summary>Seconds between a hunter's strikes.</summary>
    public float HunterSecondsPerHit = 1.2f;
    /// <summary>Seconds a hunter chases one animal before it lets it go for a while.</summary>
    public float HunterChaseSeconds = 45f;
    /// <summary>Animals the hunter leaves alone: born in a pen (bred by players), or a baby.</summary>
    public bool HunterSparesLivestock = true;

    // Gatherer
    /// <summary>How far from the Gatherer's Hut, in blocks, the gatherer forages (change it on the Work area tab).</summary>
    public int GathererRadius = 48;
    /// <summary>How far around where it works, in blocks, the gatherer looks for things to mark.</summary>
    public int GathererLookRadius = 10;
    /// <summary>Seconds the gatherer takes to pick one bush, mushroom or loose stone.</summary>
    public float GathererSecondsPerPick = 2f;
    /// <summary>
    /// The gatherer picks up loose stones, flints, sticks, ore bits and seashells, but not in or beside a
    /// colony building, so it does not tidy away stones placed on purpose.
    /// </summary>
    public bool GathererPicksUpStones = true;
    /// <summary>The gatherer cuts the tops off cattails, papyrus and tule, the way a knife does, leaving the plant.</summary>
    public bool GathererCutsReeds = true;
    /// <summary>When the colony runs low on food the gatherer digs up reeds for their roots and roasts them on a fire pit by its hut.</summary>
    public bool GathererDigsRoots = true;
    /// <summary>A gatherer with a knife takes cuttings from berry bushes (one per bush a year, as a player can).</summary>
    public bool GathererTakesCuttings = true;
    /// <summary>How many cuttings of each bush the colony keeps (hut and warehouse) before the gatherer stops taking them.</summary>
    public int GathererCuttingsKeep = 4;
    /// <summary>Food counts as low below this many full bellies (SaturationMax) per settler in the colony's chests.</summary>
    public double GathererRootsFoodDays = 2;
    /// <summary>The gatherer leaves a reed alone unless this many others grow within 4 blocks of it.</summary>
    public int GathererRootsLeave = 2;
    /// <summary>
    /// The gatherer digs blue, fire and red clay and peat where it shows on the surface. Only the top block the
    /// world made is taken, so it never digs a pit.
    /// </summary>
    public bool GathererDigsClay = true;
    /// <summary>
    /// When huts wait for dry grass the colony does not have, the gatherer cuts tall grass for it first, the way a
    /// knife does, so the grass grows back.
    /// </summary>
    public bool GathererCutsGrass = true;
    /// <summary>How far around itself (and around its hut) the gatherer looks for tall grass to cut.</summary>
    public int GrassSearchRadius = 16;
    /// <summary>
    /// When the gatherer picks up ore bits, the spot is marked as a likely deposit and the prospectors read
    /// there first; at most one such spot per ore within this many blocks.
    /// </summary>
    public int OreHintSpacing = 16;
    /// <summary>The colour of every colony marker on the world map, e.g. "#000000" (solid black); "" gives each kind its own colour.</summary>
    public string MapMarkerColor = "#000000";

    // Map markers
    /// <summary>
    /// No new marker of a kind within this many blocks of one the colony already has, by marker group. Animals
    /// and danger move about, so they are spaced out more.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, int> MarkerSpacing = new()
    {
        ["ore"] = 32, ["clay"] = 24, ["soil"] = 32, ["traders"] = 48, ["animals"] = 96, ["danger"] = 96, ["landmarks"] = 64,
        ["berries"] = 16, ["fruit"] = 16, ["mushrooms"] = 16, ["crops"] = 16, ["reeds"] = 24, ["resin"] = 16, ["bees"] = 16,
        ["herbs"] = 16, ["shells"] = 24, ["flint"] = 24, ["graves"] = 0
    };
    /// <summary>The most markers Explorers and Gatherers put down for one colony; graves do not count.</summary>
    public int MaxColonyMarkers = 600;
    /// <summary>A grave marker shows where a settler died or was knocked out (only their latest spot).</summary>
    public bool MarkSettlerDeaths = true;

    // Miner
    /// <summary>How far from the Miner's Hut, in blocks, the readings a miner may go and dig at can be.</summary>
    public int MinerRadius = 64;
    /// <summary>Readings weaker than this (the pick's density factor, 0 to 1; "poor" starts at 0.13) are not worth a mine.</summary>
    public double MinerMinFactor = 0.05;
    /// <summary>How far around a reading, across the ground, a miner looks for the ore's blocks.</summary>
    public int MinerOreSearchRadius = 12;
    /// <summary>Seconds a miner takes to dig one block of rock; ore takes half as long again.</summary>
    public float MinerSecondsPerBlock = 2.5f;
    /// <summary>How far, in blocks, a miner reaches from where it stands to dig ore around it.</summary>
    public int MinerReach = 3;
    /// <summary>With "Go to prospected ore" on, the miner only leaves its shaft for readings at least this strong ("decent" starts at 0.267), unless an ore is chosen on the Ore tab.</summary>
    public double MinerTripMinFactor = 0.267;
    /// <summary>Levels of the Miner's Hut's shaft per hut level. Each level is one turn of the spiral staircase, 8 blocks deeper, with a tunnel out from each side.</summary>
    public int MinerLevelsPerHutLevel = 2;
    /// <summary>How long, in blocks, each tunnel out from a level of the shaft is.</summary>
    public int MinerTunnelLength = 12;
    /// <summary>A miner puts a torch down where it works when the light there is below this (0 to 24; monsters can spawn at 7 and below). 0 turns torches off.</summary>
    public int MinerTorchLight = 8;
    /// <summary>How many torches a miner takes from the hut's storage, and asks couriers for when there are none.</summary>
    public int MinerTorchesCarried = 8;
    /// <summary>When a mine at prospected ore is finished, the miner fills its staircase back in with dirt this many blocks down from the surface.</summary>
    public int MinerFillDepth = 6;
    /// <summary>Mineshaft sections (cross, tee, straight) a Miner's Hut's mineshaft may grow per hut level. 0 (since 0.76.0): no limit, only levels are.</summary>
    public int MinerSectionsPerHutLevel = 0;
    /// <summary>Levels of a mineshaft made of pieces that sections may go on, per Miner's Hut level (a level 0 hut counts as 1). With a level piece: levels each hut upgrade adds below the entrance (a level 0 hut has level 1; the top hut level has no limit).</summary>
    public int MinerMineLevelsPerHutLevel = 1;
    /// <summary>
    /// How many times a mineshaft entrance's stairs repeat further down for each level of the Miner's Hut. 0 (since
    /// 0.73.0): the entrance is used as drawn, as Aaron's current one already has tunnel starts on two levels.
    /// </summary>
    public int MinerEntranceRepeatsPerHutLevel = 0;
    /// <summary>
    /// How far, in blocks, a miner's node search with its prospecting pick reaches, as the pick's node search
    /// mode does for a player. 0 goes by the world's own setting for the pick, or 8 when it has none.
    /// </summary>
    public int MinerNodeSearchRadius = 0;
    /// <summary>Seconds a miner takes over a node search.</summary>
    public float MinerNodeSearchSeconds = 2f;
    /// <summary>How much a node search wears the prospecting pick down.</summary>
    public int MinerNodeSearchWear = 1;

    // Quarry
    /// <summary>The pit beside the Quarry goes this many blocks out from its middle at hut level 1 (2 makes a 5 by 5 pit)...</summary>
    public int QuarryRadius = 2;
    /// <summary>...and this many more for each level above that...</summary>
    public int QuarryRadiusPerLevel = 1;
    /// <summary>...up to this many (6 makes a 13 by 13 pit).</summary>
    public int QuarryMaxRadius = 6;
    /// <summary>How many layers deep the pit goes for each hut level. A staircase runs around the pit's edge, so a small pit cannot go very deep.</summary>
    public int QuarryDepthPerLevel = 6;
    /// <summary>Solid blocks up to this high above the pit's top layer are cleared too, so a pit on a slope is open to the sky.</summary>
    public int QuarryClearAbove = 4;
    /// <summary>Seconds a quarrier takes to dig one block.</summary>
    public float QuarrySecondsPerBlock = 2f;

    // Land claim
    /// <summary>
    /// The square of land, in blocks along each side, the Town Hall claims around itself when the colony is founded
    /// and while it is at level 1. 250 claims a 250 by 250 square.
    /// </summary>
    public int TownHallClaimSize = 250;
    /// <summary>How many blocks each side of the Town Hall's square grows for every Town Hall level above 1.</summary>
    public int TownHallClaimPerLevel = 50;
    /// <summary>The Town Hall's square never grows past this, whatever its level. ClaimSizeMax does not apply to it.</summary>
    public int TownHallClaimSizeMax = 500;
    /// <summary>
    /// The square, in blocks along each side, each finished hut adds to the colony's claim around its hut block,
    /// by building type. Types not listed claim ClaimSizeMin, or MilitaryClaimSize when they are on the Builder's
    /// Wand Military tab. Every size is kept between ClaimSizeMin and ClaimSizeMax.
    /// </summary>
    public Dictionary<string, int> HutClaimSizes = new()
    {
        ["builder"] = 40,
        ["warehouse"] = 40,
        ["tavern"] = 50,
        ["farmer"] = 60,
        ["forester"] = 60,
        ["potter"] = 30,
        ["residence"] = 30,
        ["cook"] = 30,
        ["prospector"] = 30,
        ["miner"] = 30,
        ["quarry"] = 40,
        ["smelter"] = 30,
        ["blacksmith"] = 30,
        ["armourer"] = 30,
        ["weaponsmith"] = 30,
        ["sawmill"] = 30,
        ["craftsman"] = 30,
        ["tailor"] = 30,
        ["jeweller"] = 30,
        ["healer"] = 30,
        ["mint"] = 30,
        ["archive"] = 30,
        ["barracks"] = 40,
        ["explorer"] = 30,
        ["gatherer"] = 30,
        ["hunter"] = 30,
    };
    /// <summary>
    /// How many blocks each side of a hut's square grows for every hut level above 1, so a hut near the edge of the
    /// colony pushes the border further out as it is upgraded. Still kept under ClaimSizeMax.
    /// </summary>
    public int HutClaimPerLevel = 10;
    /// <summary>The claim of a military building (Builder's Wand Military tab) that has no entry of its own in HutClaimSizes.</summary>
    public int MilitaryClaimSize = 200;
    public int ClaimSizeMin = 30;
    public int ClaimSizeMax = 200;
    /// <summary>How far below and above each building's hut block its claim reaches.</summary>
    public int ClaimDepthBelow = 32;
    public int ClaimHeightAbove = 64;

    // Work area (Forester's and Farmer's Hut windows)
    /// <summary>The ranges, in blocks, a hut's Work area tab offers for how far its worker goes out.</summary>
    public int[] GatherRadiusChoices = { 16, 32, 64, 128, 200, 300, 500, 1000, 2000 };
    /// <summary>
    /// A forester, farmer or guard further than this from every player keeps the land around it loaded
    /// while it works, so it does not freeze out there.
    /// </summary>
    public int FarWorkPlayerDistance = 112;

    // Guard
    /// <summary>How far around the settler it protects (or the Town Hall) a guard looks for monsters.</summary>
    public float GuardThreatRadius = 16f;
    /// <summary>How close a monster must come to wake a sleeping guard (or keep it from going to bed).</summary>
    public float GuardWakeRadius = 8f;
    /// <summary>Damage a guard does with bare hands; with a weapon it does the weapon's damage.</summary>
    public float GuardFistDamage = 1.5f;
    /// <summary>Seconds between a guard's blows.</summary>
    public float GuardSecondsPerHit = 1.2f;
    /// <summary>The creatures in GuardTargets hunt settlers as they hunt players.</summary>
    public bool HostilesAttackSettlers = true;
    /// <summary>Creatures guards fight, and that hunt settlers, as entity codes; * matches anything.</summary>
    public string[] GuardTargets = { "drifter-*", "locust-*", "bell-*", "bowtorn-*", "shiver-*", "wolf-*", "hyena-*", "bear-*" };

    // Guard Tower
    /// <summary>How far from its guard spot (or the tower) a tower guard on the Guard task goes after monsters.</summary>
    public float GuardTowerRadius = 20f;
    /// <summary>The same for a Barracks, whose guards watch farther.</summary>
    public float BarracksRadius = 32f;
    /// <summary>Huts this close to a Guard Tower are on its guards' automatic patrol.</summary>
    public int GuardPatrolRange = 48;
    /// <summary>The same for a Barracks, whose guards walk a wider round.</summary>
    public int BarracksPatrolRange = 96;
    /// <summary>Seconds a patrolling guard looks around at each patrol point.</summary>
    public float GuardPatrolPauseSeconds = 4f;
    /// <summary>Most patrol points one tower keeps.</summary>
    public int GuardMaxPatrolPoints = 20;
    /// <summary>How close a guard keeps to the player it follows, loose and tight grouping.</summary>
    public float GuardFollowLoose = 7f;
    public float GuardFollowTight = 3f;
    /// <summary>A following guard further than this from its player is brought to them, as tamed animals are.</summary>
    public float GuardFollowTeleport = 48f;
    /// <summary>A guard below this share of its health walks back to its tower (when Retreat is on)...</summary>
    public float GuardRetreatHealth = 0.33f;
    /// <summary>...and goes back to work once healed to this share.</summary>
    public float GuardReturnHealth = 0.75f;
    /// <summary>Guards at a tower stay up at night and sleep by day, from GuardSleepStartHour to GuardSleepEndHour.</summary>
    public bool GuardNightWatch = true;
    public float GuardSleepStartHour = 10f;
    public float GuardSleepEndHour = 16f;
    /// <summary>Tower guards stay out in temporal storms to fight, instead of sheltering with everyone else.</summary>
    public bool GuardsFightInStorms = true;
    /// <summary>When a monster hurts a settler inside the colony, the nearest guard comes running; this far around the claim it still counts.</summary>
    public int GuardAlarmMargin = 16;
    /// <summary>An alarm is forgotten after this many seconds without the monster striking again.</summary>
    public float GuardAlarmSeconds = 30f;
    /// <summary>Guards put on the best armor from the tower storage and the warehouse.</summary>
    public bool GuardsWearArmor = true;
    /// <summary>Guards carry the best shield from the tower storage and the warehouse in their offhand.</summary>
    public bool GuardsUseShields = true;
    /// <summary>Guards only swap a weapon, armor piece or shield for one that is this much better (0.15 = 15%).</summary>
    public float GearUpgradeMargin = 0.15f;

    // Running away
    /// <summary>Settlers who are not guards run home, or to a guard, when a creature hurts them.</summary>
    public bool FleeWhenAttacked = true;
    /// <summary>A hurt settler keeps running (or hiding) this many seconds after the last blow, unless the attacker dies first.</summary>
    public float FleeSeconds = 30f;
    /// <summary>A hurt settler runs to a guard within this many blocks, if that is better than going home; a guard this close also answers the alarm outside the colony.</summary>
    public float FleeGuardRadius = 48f;
    /// <summary>Once safe, a settler stops running when its attacker is further away than this.</summary>
    public float FleeSafeDistance = 24f;
    /// <summary>A settler with a weapon fights back against a creature that hurts it, instead of running, when no guard is near.</summary>
    public bool FightBackWhenAttacked = true;
    /// <summary>A guard this close to a hurt settler deals with the attacker, so the settler runs to it instead of fighting.</summary>
    public float FightBackGuardRadius = 24f;
    /// <summary>A settler below this share of its health stops fighting and runs.</summary>
    public float FightBackMinHealth = 0.3f;
    /// <summary>A settler fighting back stops once its attacker is further away than this; it does not give chase.</summary>
    public float FightBackChaseDistance = 6f;
    /// <summary>Default for new colonies' Support allies switch (Colony militia tab): armed settlers help a settler being attacked nearby.</summary>
    public bool SupportAllies = true;
    /// <summary>Armed settlers this close to a settler being attacked come to help, when Support allies is on.</summary>
    public float SupportAllyRadius = 16f;

    // Potter
    /// <summary>Seconds a potter takes to shape one layer of clay on the clay forming block.</summary>
    public float PotterSecondsPerLayer = 2.5f;
    /// <summary>Seconds a potter takes to dig the kiln pit, load it, add one layer of fuel or light it.</summary>
    public float PotterSecondsPerKilnStep = 3f;
    /// <summary>Clay a potter keeps on hand; more goes back to the hut storage.</summary>
    public int PotterClayReserve = 24;
    /// <summary>Clay a potter asks couriers to bring from the warehouse at a time.</summary>
    public int PotterClayRequest = 16;
    /// <summary>How far from the Potter's Hut, in blocks, the potter digs its kiln pit and shapes clay.</summary>
    public int PotterSpotRadius = 8;
    /// <summary>Most pit kilns a potter uses at once. It may use one more than the hut's level, up to this.</summary>
    public int PotterMaxKilns = 4;
    /// <summary>The "Keep in stock" amounts the Pottery tab offers.</summary>
    public int[] PotteryKeepChoices = { 0, 1, 2, 4, 8, 16, 32 };
    /// <summary>
    /// What a new Potter's Hut keeps in stock until an officer changes it: a crucible and ingot molds,
    /// what it takes to smelt copper. Keys are item codes, * matches anything.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, int> PotteryDefaultKeep = new()
    {
        ["*crucible*"] = 1,
        ["*ingotmold*"] = 2
    };

    // Cook
    /// <summary>Seconds a cook takes to load, fuel or empty the fire pit.</summary>
    public float CookSecondsPerStep = 2f;
    /// <summary>Raw food a cook keeps on hand, and asks couriers to bring at a time.</summary>
    public int CookRawReserve = 8;
    /// <summary>Raw food a cook puts in the fire pit at once.</summary>
    public int CookBatch = 4;
    /// <summary>Firewood a cook puts in the fire pit at once.</summary>
    public int CookFirewood = 4;
    /// <summary>How far from the Cook's Hut, in blocks, the cook looks for a fire pit.</summary>
    public int CookFirepitRadius = 8;
    /// <summary>Raw food that needs more heat than this to cook is left to players (a fire pit with firewood reaches about 700).</summary>
    public int CookMaxTemperature = 500;
    /// <summary>
    /// Until an officer picks dishes on the Kitchen tab, the cook makes what it can from the food on hand and keeps
    /// this many of each such cooked food. Dishes it has nothing for are not kept.
    /// </summary>
    public int KitchenAutoKeep = 4;
    /// <summary>How many baked pies a Cook's Hut with a Stone Bake Oven keeps until dishes are picked (bread uses KitchenAutoKeep).</summary>
    public int OvenDefaultPieKeep = 2;
    /// <summary>How many servings of each pot meal the cook keeps until dishes are picked, for meals it has the ingredients for.</summary>
    public int KitchenAutoMealKeep = 4;
    /// <summary>The cook pours pot meals into crocks. It keeps this many empty crocks on hand and orders more from the Potter's Hut.</summary>
    public int CookSpareCrocks = 2;
    /// <summary>How far, in blocks, a settler or cook walks to water to wash out a bowl or crock of rotten food.</summary>
    public int WashWaterRadius = 32;
    /// <summary>The "Keep in stock" amounts the Kitchen tab offers.</summary>
    public int[] KitchenKeepChoices = { 0, 2, 4, 8, 16, 32, 64 };

    // Crafter huts (Smelter, Blacksmith, Sawmill, Primitive Craftsman)
    /// <summary>The "Keep in stock" amounts the Smelting, Smithing and Sawmill tabs offer.</summary>
    public int[] CraftKeepChoices = { 0, 1, 2, 4, 8, 16, 32, 64 };

    // Smelter
    /// <summary>Seconds a smelter takes to set out a mold, load the crucible, light the fire, pour or take ingots out.</summary>
    public float SmelterSecondsPerStep = 3f;
    /// <summary>How far from the Smelter's Hut, in blocks, the smelter looks for a fire pit.</summary>
    public int SmelterRadius = 8;
    /// <summary>How far from the fire pit, in blocks, the smelter sets out ingot molds and pours into them.</summary>
    public int SmelterMoldRadius = 2;
    /// <summary>How many ingot molds the smelter keeps set out beside the fire pit, when it has them.</summary>
    public int SmelterMolds = 4;
    /// <summary>The most ingots the smelter melts in one crucible (the crucible holds about 5 ingots of nuggets).</summary>
    public int SmelterIngotsPerBatch = 4;
    /// <summary>Fuel pieces the smelter puts in the fire pit at once.</summary>
    public int SmelterFuel = 6;
    /// <summary>What the smelter asks couriers for when the warehouse has no fuel hot enough.</summary>
    public string SmelterFuelCode = "game:charcoal";
    /// <summary>Fuel at least this hot stays at the Smelter's and Blacksmith's Huts (charcoal and coal).</summary>
    public int SmelterKeepFuelTemperature = 1000;
    /// <summary>Nuggets and metal bits a smelter keeps on hand; more goes back to the hut storage.</summary>
    public int SmelterOreReserve = 100;
    /// <summary>
    /// How many ingots of each metal a new Smelter's Hut keeps in stock until an officer changes it, by ingot
    /// code (wildcards allowed). Every other metal is 0.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, int> SmelterDefaultKeep = new()
    {
        ["game:ingot-copper"] = 4
    };

    // Blacksmith
    /// <summary>Seconds a blacksmith takes to load or fuel the forge, or take the hot ingots out.</summary>
    public float BlacksmithSecondsPerStep = 2f;
    /// <summary>Seconds between the blacksmith's hammer strikes on the anvil.</summary>
    public float BlacksmithSecondsPerStrike = 1.5f;
    /// <summary>Voxels of the piece each hammer strike stands for; bigger pieces take more strikes.</summary>
    public int BlacksmithVoxelsPerStrike = 8;
    /// <summary>How far from the Blacksmith's Hut, in blocks, the blacksmith looks for a forge and an anvil.</summary>
    public int BlacksmithRadius = 8;
    /// <summary>Charcoal the blacksmith keeps in the forge.</summary>
    public int BlacksmithFuel = 4;
    /// <summary>Pieces that take more ingots than this are left to players.</summary>
    public int BlacksmithMaxIngots = 8;
    /// <summary>What a new Blacksmith's Hut keeps in stock until an officer changes it, by code (wildcards allowed). Everything else is 0.</summary>
    public System.Collections.Generic.Dictionary<string, int> BlacksmithDefaultKeep = new();

    // Armourer and Weaponsmith (Blacksmith split, ColonyManager.Smithing.cs and ColonyManager.Armourer.cs)
    /// <summary>What the anvil makes for the Armourer, by the first part of the code. Armor itself is the Armourer's too.</summary>
    public string[] ArmourerParts = { "metalchain", "metalscale", "metalplate", "metallamellae" };
    /// <summary>What the anvil makes for the Weaponsmith, by the first part of the code, besides anything the game calls a weapon (WeaponTools).</summary>
    public string[] WeaponsmithParts = { "blade", "arrowhead", "spearhead" };
    public string[] WeaponTools = { "Sword", "Spear", "Bow", "Sling", "Firearm", "Crossbow", "Javelin", "Pike", "Shield", "Club", "Mace", "Warhammer", "Poleaxe", "Halberd", "Polearm" };
    /// <summary>Armor recipes can take many chains or plates; the Armourer may use ones with up to this many.</summary>
    public int ArmourerMaxInputs = 32;
    /// <summary>The Armourer mends metal armor below this share of its durability.</summary>
    public float ArmourerMendBelow = 0.75f;
    /// <summary>What mends metal armor, by how it is made (the part of its code after the body part); {metal} is the armor's metal.</summary>
    public System.Collections.Generic.Dictionary<string, string> ArmourerMendWith = new()
    {
        ["chain"] = "game:metalchain-{metal}",
        ["scale"] = "game:metalscale-{metal}",
        ["plate"] = "game:metalplate-{metal}",
        ["brigandine"] = "game:metalplate-{metal}",
        ["lamellar"] = "game:metallamellae-{metal}"
    };

    // Sawmill
    /// <summary>Seconds a sawyer takes to make one thing at the Sawmill (one crafting recipe, such as a log into planks).</summary>
    public float SawmillSecondsPerCraft = 5f;
    /// <summary>
    /// What counts as wood for the sawyer, by the first part of a code (plank in plank-oak). The sawyer only
    /// uses crafting recipes whose ingredients are all wood.
    /// </summary>
    public string[] SawmillWoodParts = { "log", "logsection", "debarkedlog", "plank", "planks", "plankslab", "firewood", "stick", "supportbeam" };
    /// <summary>The tools a sawyer's recipe may take, by the first part of the code. Recipes with any other tool are left to players.</summary>
    public string[] SawmillToolParts = { "saw" };
    /// <summary>Recipes that take more pieces of wood than this are left to players.</summary>
    public int SawmillMaxInputs = 16;
    /// <summary>The most times over the sawyer fetches or asks couriers for the wood of one recipe at once.</summary>
    public int SawmillBatch = 4;
    /// <summary>What a new Sawmill keeps in stock until an officer changes it, by code (wildcards allowed). Everything else is 0.</summary>
    public System.Collections.Generic.Dictionary<string, int> SawmillDefaultKeep = new()
    {
        ["game:plank-*"] = 16
    };

    // Primitive Craftsman
    /// <summary>Seconds between the craftsman's strikes when knapping a stone.</summary>
    public float CraftsmanSecondsPerStrike = 1.5f;
    /// <summary>Strikes it takes to knap one stone into a tool head.</summary>
    public int CraftsmanStrikes = 8;
    /// <summary>Seconds the craftsman takes to put a head on its handle.</summary>
    public float CraftsmanSecondsPerHaft = 4f;
    /// <summary>Haftings that take more than this many other things (sticks and the like) are left to players.</summary>
    public int CraftsmanMaxHandleParts = 4;
    /// <summary>The most stones (or handles) the craftsman fetches or asks couriers for at once.</summary>
    public int CraftsmanBatch = 4;
    /// <summary>What a new Primitive Craftsman keeps in stock until an officer changes it, by code (wildcards allowed). Everything else is 0.</summary>
    public System.Collections.Generic.Dictionary<string, int> CraftsmanDefaultKeep = new()
    {
        ["game:axe-*flint"] = 2,
        ["game:knife-*flint"] = 2,
        ["game:shovel-*flint"] = 2,
        ["game:hoe-*flint"] = 2,
        ["game:spear-*flint"] = 2
    };

    // Foundations and upkeep
    /// <summary>Builders fill gaps under a building's floor with soil, down to the ground or this many blocks at most. 0 turns it off.</summary>
    public int FoundationDepth = 8;
    /// <summary>
    /// How often, in seconds, each finished building is looked over for ground that got inside it or gaps under it,
    /// which gives it an upkeep work order. 0 turns it off.
    /// </summary>
    public int BuildingUpkeepSeconds = 300;

    // Lights
    /// <summary>When the colony has no lantern, builders put up a torch where a blueprint has one.</summary>
    public bool LanternTorchFallback = true;
    /// <summary>Couriers put a lit torch from the warehouse up wherever a torch in a finished building has gone out.</summary>
    public bool RelightTorches = true;
    /// <summary>How often, in seconds, the colony's buildings are checked for torches that went out.</summary>
    public int TorchCheckSeconds = 60;

    // Huts make things for other huts
    /// <summary>
    /// When a hut is waiting for something the warehouse does not have, another hut that can make it makes it (the
    /// Sawmill saws planks for the mine), and couriers bring it straight from that hut.
    /// </summary>
    public bool HutsMakeForOthers = true;
    /// <summary>
    /// Huts ask couriers for this many percent more than they need, at least 1 and at most RequestSpareMax, in case
    /// (a potter who needs 4 firewood asks for 5). 0 turns it off. Not for tools, Minimum stock or players' requests.
    /// </summary>
    public int RequestSparePercent = 25;
    public int RequestSpareMax = 8;
    /// <summary>
    /// Couriers carry full loads for requests: things from every warehouse chest, other requests for the same hut on the
    /// same trip, and for a worker's own hut up to a full stack of what it asked for (not for builders' work orders).
    /// </summary>
    public bool CouriersCarryFullLoads = true;
    /// <summary>Pit kilns use the best fuel the colony has: coal, then charcoal, then peat, then firewood.</summary>
    public bool KilnPrefersBestFuel = true;
    /// <summary>What huts gather by themselves, by hut type, so the Required resources tab can say which hut brings it in.</summary>
    public System.Collections.Generic.Dictionary<string, string[]> HutGathers = new()
    {
        ["forester"] = new[] { "game:log-*" },
        ["gatherer"] = new[] { "game:drygrass" },
        ["quarry"] = new[] { "game:rock-*", "game:stone-*" },
        ["miner"] = new[] { "game:ore-*", "game:stone-*" }
    };

    // Crafting recipes tab (huts that are not crafter huts)
    /// <summary>
    /// What each hut's worker may craft on its Crafting recipes tab, by hut type: the first part of the made
    /// thing's code (firewood in firewood, stick in stick). Recipes come from the game's crafting grid recipes.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string[]> HutRecipeOutputs = new()
    {
        ["forester"] = new[] { "firewood", "stick", "debarkedlog" },
        ["farmer"] = new[] { "flaxtwine", "rope", "linen", "hay" },
        // Poultices from the horsetail and reed tops it picks (ColonyManager.Healing.cs).
        ["gatherer"] = new[] { "torch", "basket", "flaxtwine", "poultice" },
        ["miner"] = new[] { "cobblestone", "stonebricks", "torch" },
        ["quarry"] = new[] { "cobblestone", "stonebricks", "stonepath" },
        // Every piece of clothing but jewellery, the sewing kit, and armor of leather or linen (never metal).
        // The rest are Tailor's Delight's buttons, threads, leather bundles and strips, needles and awls.
        ["tailor"] = new[] { "clothes", "sewingkit", "armor-*-leather", "armor-*-linen", "buttons", "twine", "leatherbundle", "leatherstrips", "needle", "awl" },
        // Only jewellery (config JewelleryWords): our own recipes and any the game or other mods have.
        ["jeweller"] = new[] { "clothes" },
        // Metal armor only (leather and linen armor is the Tailor's), put together from the chains, scales and plates it smiths.
        ["armourer"] = new[] { "armor" },
        // Bandages, and poultices too (any settler can make a poultice, config HomemadeRemedies).
        ["healer"] = new[] { "bandage", "poultice" },
        // The colony's coins (ColonyManager.Economy.cs).
        ["mint"] = new[] { "coin" }
    };
    /// <summary>The mods whose things a hut may make, by hut type; "game" alone where a hut is not listed.</summary>
    public System.Collections.Generic.Dictionary<string, string[]> HutRecipeDomains = new()
    {
        ["tailor"] = new[] { "game", "tailorsdelight" },
        ["jeweller"] = new[] { "game", "canjewelry", "canjewelryadornments" },
        ["mint"] = new[] { "createacolony" }
    };
    /// <summary>
    /// Recipes only a player class can make that a hut's worker may make too, by hut type: the Tailor counts as
    /// a Clothier. A hut not listed makes none of them.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string[]> HutRecipeTraits = new()
    {
        ["tailor"] = new[] { "clothier" },
        ["jeweller"] = new[] { "tinkerer" },
        // "*": the Healer may make healing recipes whatever class they are for.
        ["healer"] = new[] { "*" }
    };
    /// <summary>The tools a hut's recipes may use, by hut type (first part of the tool's code). Recipes needing another tool are left out.</summary>
    public System.Collections.Generic.Dictionary<string, string[]> HutRecipeTools = new()
    {
        ["forester"] = new[] { "axe" },
        ["gatherer"] = new[] { "knife" },
        ["farmer"] = new[] { "hoe" },
        ["miner"] = new[] { "pickaxe" },
        ["quarry"] = new[] { "pickaxe" },
        // Tailor's Delight adds needles and awls to many clothing recipes.
        ["tailor"] = new[] { "knife", "shears", "scissors", "needle", "awl" },
        ["jeweller"] = new[] { "hammer", "chisel" },
        ["armourer"] = new[] { "hammer" },
        ["healer"] = new[] { "knife", "shears", "scissors" },
        ["mint"] = new[] { "hammer" }
    };
    /// <summary>Seconds a worker takes to craft one recipe at its hut.</summary>
    public float HutRecipeSecondsPerCraft = 4f;
    /// <summary>Recipes that take more items than this are left to players.</summary>
    public int HutRecipeMaxInputs = 9;
    /// <summary>The most times over a worker fetches or asks couriers for the things of one recipe at once.</summary>
    public int HutRecipeBatch = 4;
    /// <summary>Officers may teach huts their own recipes on the Crafting recipes tab (Teach recipe). Off: only the game's recipes.</summary>
    public bool CustomRecipes = true;
    /// <summary>The most recipes players can teach one kind of hut in a colony.</summary>
    public int CustomRecipesPerHut = 30;

    // Healing: going home to recover, first aid, the Healer and herbs (ColonyManager.Healing.cs)
    /// <summary>Hurt settlers (not guards) go home to their bed to recover.</summary>
    public bool RecoverAtHome = true;
    /// <summary>A settler goes home to recover below this share of its health, and gets up again at RecoverUntilHealth.</summary>
    public float RecoverBelowHealth = 0.4f;
    public float RecoverUntilHealth = 0.9f;
    /// <summary>Health a settler resting in bed gets back each in-game hour, on top of what it heals anyway.</summary>
    public float RestHealPerHour = 2f;
    /// <summary>Settlers carry one bandage or poultice, taken from the warehouse, a Healer's Hut or a chest at home, and use it when badly hurt.</summary>
    public bool SettlersCarryFirstAid = true;
    /// <summary>A settler below this share of its health uses a bandage or poultice it carries, wherever it is.</summary>
    public bool UseHealingWhenHurt = true;
    public float UseHealingBelow = 0.75f;
    /// <summary>Settlers with no first aid to take make their own from these (first part of the code, as in poultice-reed-horsetail).</summary>
    public bool SettlersMakeRemedies = true;
    public string[] HomemadeRemedies = { "poultice" };
    /// <summary>The tools a settler may use to make one (it must carry the tool already).</summary>
    public string[] HomemadeRemedyTools = { "knife" };
    /// <summary>The Healer walks to settlers recovering in bed and treats them with what its hut has, healing this much more.</summary>
    public bool HealerTreatsPatients = true;
    public float HealerHealBonus = 1.5f;
    /// <summary>Seconds the Healer takes to treat someone.</summary>
    public float HealerTreatSeconds = 4f;
    /// <summary>How many of each bandage a new Healer's Hut keeps on its Crafting recipes tab.</summary>
    public int HealerStartKeep = 4;
    /// <summary>
    /// The Gatherer picks herbs: plants that go into the game's bandage and poultice recipes, and blocks matching
    /// GathererHerbs. It stops taking a herb once the colony has GathererHerbKeep of it.
    /// </summary>
    public bool GathererPicksHerbs = true;
    public string[] GathererHerbs = { "flower-horsetail-*" };
    public int GathererHerbKeep = 16;
    /// <summary>How many of each poultice a new Gatherer's Hut keeps on its Crafting recipes tab.</summary>
    public int GathererStartPoultices = 4;

    // Clothes, the Tailor and the Jeweller (ColonyManager.Clothing.cs)
    /// <summary>How much of its condition (0 to 1) a worn piece of clothing loses each in-game day. 0.01 wears it out in 100 days.</summary>
    public float ClothesWearPerDay = 0.01f;
    /// <summary>Clothes below this condition are in rags: the settler is unhappy and swaps them for better ones from the warehouse.</summary>
    public float ClothesRaggedBelow = 0.2f;
    /// <summary>Mood points for clothes in rags and for being cold (the air plus what the settler wears is below the world's body temperature limit).</summary>
    public int RaggedMood = -3;
    public int ColdMood = -4;
    /// <summary>Degrees a settler inside an enclosed room counts as warmer, for being cold.</summary>
    public float RoomWarmth = 5f;
    /// <summary>Mood points for each piece of jewellery worn, up to JewelleryMoodMax pieces.</summary>
    public int JewelleryMood = 3;
    public int JewelleryMoodMax = 2;
    /// <summary>Settlers take clothes from the warehouse when theirs are in rags, a slot is empty, or they are cold.</summary>
    public bool SettlersChangeClothes = true;
    /// <summary>Settlers take jewellery from the warehouse to wear, up to JewelleryMoodMax pieces.</summary>
    public bool SettlersWearJewellery = true;
    /// <summary>
    /// A piece of clothing is jewellery when a part of its code starts with one of these (necklace in
    /// clothes-neck-golden-necklace), or when it comes from a jewellery mod (JewelleryDomains).
    /// </summary>
    public string[] JewelleryWords = { "necklace", "amulet", "bracelet", "armlet", "brooch", "pin", "torc", "collar", "pendant", "chain", "ring",
        "earring", "circlet", "coronet", "diadem", "crown", "tiara", "talisman", "cross", "nazar", "gorget", "lunula", "japamala", "medallion", "locket", "choker" };
    public string[] JewelleryDomains = { "canjewelry", "canjewelryadornments" };
    /// <summary>The Tailor mends clothes below this condition, and leather or linen armor below this share of its durability.</summary>
    public float TailorMendBelow = 0.75f;
    /// <summary>Seconds the Tailor takes for one stitch of mending.</summary>
    public float TailorSecondsPerMend = 5f;
    /// <summary>What mends leather and linen armor, by the armor's material (the end of its code), and how much of its durability one gives back.</summary>
    public System.Collections.Generic.Dictionary<string, string> ArmorMendWith = new()
    {
        ["linen"] = "game:linen-*",
        ["leather"] = "game:leather-*"
    };
    public float ArmorMendShare = 0.25f;

    // Recruiting
    /// <summary>How many settlers a built Tavern offers for hire each day.</summary>
    public int TavernRecruits = 3;
    /// <summary>What hiring a settler at the Tavern costs, rolled between min and max for each one.</summary>
    public int RecruitCostMin = 4;
    public int RecruitCostMax = 12;
    /// <summary>The item recruits are paid in.</summary>
    public string RecruitCurrency = "game:gear-rusty";
    /// <summary>Wanderers now and then walk up to the Town Hall and ask to join the colony.</summary>
    public bool Wanderers = true;
    /// <summary>About how many in-game days pass between wanderers, without and with a built Tavern.</summary>
    public double WandererEveryDays = 4;
    public double WandererEveryDaysWithTavern = 2;
    /// <summary>How long a wanderer waits to be welcomed before it moves on, in in-game days.</summary>
    public double WandererWaitDays = 2;
    /// <summary>Wanderers only come while the colony has a bed nobody sleeps in.</summary>
    public bool WanderersNeedFreeBed = true;

    // Bailiff (ColonyManager.Bailiff.cs)
    /// <summary>How long a wanderer waits at the Town Hall before the bailiff answers, in in-game hours.</summary>
    public double BailiffAnswerHours = 1;
    /// <summary>Multiply farmers' work speed by this while a bailiff is on duty, and everyone else's by BailiffWorkBoost.</summary>
    public float BailiffFarmBoost = 1.15f;
    public float BailiffWorkBoost = 1.05f;
    /// <summary>How long a worker must be stuck (no tool, nothing left to do) before the bailiff lends them to another hut, in in-game hours.</summary>
    public double BailiffStuckHours = 2;
    /// <summary>How long a lent worker stays at the other hut before going back to their own, in in-game days.</summary>
    public double BailiffLendDays = 1;
    /// <summary>Mood points every other settler has while a bailiff watches over them (they are not liked).</summary>
    public int BailiffMood = -3;

    // Economy (ColonyManager.Economy.cs)
    /// <summary>The coins a settler is paid each in-game month, by job; jobs not listed get WageDefault. Settlers with no job get nothing.</summary>
    public System.Collections.Generic.Dictionary<string, int> Wages = new()
    {
        ["courier"] = 4,
        ["gatherer"] = 4,
        ["builder"] = 7,
        ["guard"] = 7,
        ["healer"] = 7,
        ["blacksmith"] = 7,
        ["armourer"] = 8,
        ["weaponsmith"] = 8,
        ["jeweller"] = 8,
        ["minter"] = 6,
        ["researcher"] = 7,
        ["priest"] = 5,
        ["bailiff"] = 10
    };
    public int WageDefault = 5;
    /// <summary>Coins a new Royal Mint keeps in stock, set on its Crafting recipes tab.</summary>
    public int MintStartKeep = 100;
    /// <summary>Coins the minter strikes from one ingot when the colony uses its own coin (Coinage).</summary>
    public int MintCoinsPerIngot = 10;
    /// <summary>No taxes or wages, and no moods about them, until the Town Hall is this level.</summary>
    public int EconomyTownHallLevel = 3;

    // Research (ColonyManager.Research.cs; the nodes are in config/research.json)
    /// <summary>The Town Hall level needed to place an Archive.</summary>
    public int ArchiveTownHallLevel = 2;
    /// <summary>Seconds of study for one piece of a researcher's work (skill and mood shorten it), and the Research Points it gives.</summary>
    public float ResearchSecondsPerWork = 20f;
    public float ResearchPointsPerWork = 1f;
    /// <summary>Most researchers on one research node at a time.</summary>
    public int ResearchMaxPerNode = 3;
    /// <summary>Each Archive tier above 1 makes research this much faster (0.25 = 25%).</summary>
    public float ArchiveSpeedPerTier = 0.25f;
    /// <summary>How far round the Archive's hut block it looks for a table, a shelf with books and a light.</summary>
    public int ArchiveRoomRadius = 6;
    /// <summary>Research is this much slower for each of the table, shelf and light that is missing.</summary>
    public float ArchiveMissingPenalty = 0.15f;
    /// <summary>Huts this close make the Archive noisy: research is ArchiveNoisePenalty slower for each.</summary>
    public int ArchiveNoiseRadius = 16;
    public float ArchiveNoisePenalty = 0.1f;
    public string[] ArchiveNoisyHuts = { "smelter", "blacksmith", "armourer", "weaponsmith", "sawmill", "quarry" };
    /// <summary>Other mods' coins, as item codes with wildcards. Any of them can be picked as a colony's coin on the Bailiff tab.</summary>
    public string[] OtherCoins = { "coinage:coin", "coinage:coin-*" };
    /// <summary>Mood points a settler loses per percent of tax while the bailiff collects taxes (10% is -2).</summary>
    public float TaxMoodPerPercent = 0.2f;
    /// <summary>Mood points a settler has until payday when their last wage was not paid in full.</summary>
    public int UnpaidMood = -4;

    // The church and religion (ColonyManager.Church.cs)
    /// <summary>A church service is held every this many in-game days, starting at ChurchServiceHour for ChurchServiceHours.</summary>
    public int ChurchServiceEveryDays = 7;
    public float ChurchServiceHour = 9f;
    public float ChurchServiceHours = 2f;
    /// <summary>Settlers with less Faith than this stay away from services, and are not sad to miss them.</summary>
    public float FaithAttendsAt = 15f;
    /// <summary>Settlers with at least this much Faith are devout: they are unhappy when they miss a service.</summary>
    public float FaithDevoutAt = 70f;
    /// <summary>Faith a service gives; the clergy's Theology adds up to half again, a bishop leading it half again more.</summary>
    public float ServiceFaithGain = 3f;
    public float BishopServiceFactor = 1.5f;
    /// <summary>Faith a devout settler loses for each service missed.</summary>
    public float MissedServiceFaithLoss = 1f;
    /// <summary>For this many days after a service, a settler learns skills this much faster.</summary>
    public double ServiceLearnDays = 1;
    public float ServiceLearnBoost = 0.15f;
    /// <summary>The chance of a level in a random skill from a service: the first with no Faith, the second with full Faith.</summary>
    public double ServiceLevelChanceMin = 0.001;
    public double ServiceLevelChanceMax = 0.005;
    /// <summary>The priest holds a funeral for settlers who died within this many days; grief then fades this much faster.</summary>
    public double FuneralWithinDays = 10;
    public float FuneralGriefFactor = 0.5f;
    /// <summary>Seconds the priest takes over a funeral, and over writing a chronicle.</summary>
    public float FuneralSeconds = 20f;
    public float ChronicleSeconds = 30f;
    /// <summary>The priest writes the colony's events into a book once at least this many have happened since the last one.</summary>
    public int ChronicleMinEvents = 8;
    /// <summary>The share of a wage a settler gives the church for its Faith: devout and faithful, doubting, faithless.</summary>
    public float TitheShareFaithful = 1f;
    public float TitheShareDoubting = 0.5f;
    public float TitheShareFaithless = 0.25f;
    /// <summary>Mood points a settler with little Faith loses while the tithe is this high or higher.</summary>
    public int TitheGrumbleAtPercent = 10;
    public int TitheGrumbleMood = -2;
    /// <summary>Charity: what a meal (two of CharityFoods) and a bandage cost the church, in coins.</summary>
    public int CharityMealCost = 1;
    public int CharityBandageCost = 2;
    /// <summary>How many bandages the church buys at once when its Healer's Station has none.</summary>
    public int CharityBandages = 2;
    /// <summary>The food the church hands out when the colony has none, the first one the game has.</summary>
    public string[] CharityFoods = { "game:bread-spelt-perfect", "game:bread-rye-perfect", "game:bread-flax-perfect", "game:fruit-redapple", "game:vegetable-carrot" };
    /// <summary>Coins a week the church spends on each orphan and on each settler sleeping in its beds (the poorhouse).</summary>
    public int CharityCarePerWeek = 1;
    /// <summary>Mood points for an orphan the church looks after, and for a settler sheltered in the church's beds.</summary>
    public int OrphanCareMood = 6;
    public int PoorhouseMood = 2;

    // Families
    /// <summary>Settlers pair up, share a home and have children who grow up into new settlers.</summary>
    public bool Families = true;
    /// <summary>How many in-game days a settler lives in the colony before it may pair up with another.</summary>
    public double FamilyCoupleAfterDays = 2;
    /// <summary>At most this many new couples a colony gets each in-game day.</summary>
    public int FamilyCouplesPerDay = 1;
    /// <summary>Two settlers of the same sex may also pair up. Only a man and a woman have children.</summary>
    public bool FamilyCouplesAnySex = false;
    /// <summary>The chance each in-game day that a couple who can have a child has one.</summary>
    public double FamilyChildChancePerDay = 0.35;
    /// <summary>In-game days a couple waits after a child before the next one.</summary>
    public double FamilyChildCooldownDays = 3;
    /// <summary>The most children one couple has.</summary>
    public int FamilyMaxChildren = 4;
    /// <summary>In-game days a child takes to grow up into an adult settler who can work.</summary>
    public double FamilyChildGrowDays = 6;
    /// <summary>Children are only born while the colony has a bed nobody sleeps in, so housing limits growth.</summary>
    public bool FamilyChildNeedsFreeBed = true;
    /// <summary>No children are born once the colony has this many living settlers (children count); 0 means no limit.</summary>
    public int FamilyMaxColonySize = 0;

    // Paths (Pathblocks)
    /// <summary>
    /// Blocks settlers treat as roads, as block codes; * matches anything. Settlers walk faster on them and
    /// the pathfinder prefers routes along them. Stairs listed here count as roads up and down; ladders listed
    /// here are the ones settlers climb when there is no other way up (see LadderClimbing).
    /// Use /colony findblock to look up codes, e.g. /colony findblock *path*.
    /// </summary>
    public string[] PathBlocks = { "game:stonepath*", "game:ladder-*" };
    /// <summary>How much faster a settler walks while standing on a Pathblock: 1.4 is 40% faster.</summary>
    public float PathSpeedMultiplier = 1.4f;
    /// <summary>
    /// What one step along a Pathblock costs the pathfinder, compared to a step on anything else. 0.65 means a
    /// settler will take a road up to about one and a half times as long as the direct way. 1 turns the
    /// preference off; keep it above 0.3 or the pathfinder slows down a lot.
    /// </summary>
    public float PathStepCost = 0.65f;
    /// <summary>
    /// When the pathfinder finds no way up or down to a goal (the game's pathfinder only walks, steps up one
    /// block and drops down), settlers look for a ladder nearby and climb it.
    /// </summary>
    public bool LadderClimbing = true;
    /// <summary>How far around itself, in blocks, a settler looks for a ladder to climb.</summary>
    public int LadderSearchRadius = 8;
    /// <summary>
    /// Settlers carry ladders and, when there is no way up and no ladder nearby, put a column of them up
    /// against the cliff and climb it. The ladders stay for the next trip. Workers take them from their hut's storage.
    /// </summary>
    public bool ScaffoldClimbing = true;
    /// <summary>How many ladders a worker takes from its hut's storage to carry.</summary>
    public int ScaffoldCarry = 16;
    /// <summary>The highest cliff, in blocks, a settler puts ladders up.</summary>
    public int ScaffoldMaxHeight = 16;
    /// <summary>
    /// The deepest drop, in blocks, a settler's path may walk down. Anything deeper (a hole, a cliff edge, a pit
    /// kiln shaft) is planned around, or climbed by ladder. The game's own limit for creatures is 4.
    /// </summary>
    public int MaxDropHeight = 2;

    // Getting stuck: Willpower
    /// <summary>
    /// A settler that cannot get where it is going slowly builds Willpower and, once it has enough, moves
    /// itself to a safe spot nearby. Turn off to leave stuck settlers for the player to help.
    /// </summary>
    public bool Willpower = true;
    /// <summary>A walking settler that has got no closer to its goal (and moved less than 3 blocks) for this many seconds counts as stuck.</summary>
    public float StuckNoProgressSeconds = 10f;
    /// <summary>Seconds of being stuck it takes to build full Willpower and relocate.</summary>
    public float WillpowerSeconds = 20f;
    /// <summary>Each settler's threshold is rolled up to this fraction above or below WillpowerSeconds each time, so not everyone pops at once.</summary>
    public float WillpowerJitter = 0.2f;
    /// <summary>Willpower builds this many times faster while the settler is boxed in (walls all around, a hole with no way out).</summary>
    public float WillpowerTrappedRate = 2f;
    /// <summary>Willpower lost per second while the settler makes real progress again.</summary>
    public float WillpowerDecayPerSecond = 1f;
    /// <summary>Willpower lost per second while the settler is not trying to walk anywhere.</summary>
    public float WillpowerIdleDecayPerSecond = 0.1f;
    /// <summary>How far, in blocks, a relocation may move a settler.</summary>
    public int WillpowerRadius = 10;
    /// <summary>
    /// Chance that a relocating settler does not look before it leaps: it only makes sure it is not inside a
    /// block and has floor under it, and may land next to lava, in deep water or among monsters.
    /// Guards never check for monsters.
    /// </summary>
    public float WillpowerRecklessChance = 0.05f;
    /// <summary>At most this many settlers relocate each second across the server, so a pile-up does not cause lag.</summary>
    public int WillpowerMaxRelocationsPerSecond = 2;
    /// <summary>Show Willpower in the game: sparks over stuck settlers (blue to red as it fills) and lines in the server log. /colony pathdebug switches it until restart.</summary>
    public bool PathDebug = false;
    /// <summary>
    /// For making blueprints: the Town Hall, huts and the Warehouse go down by hand like any block, anywhere,
    /// without the Builder's Wand or a colony. A block placed this way is only a block: it founds no colony and
    /// adds no building. /colony handplace switches it and saves it here.
    /// </summary>
    public bool PlaceHutsByHand = false;
    /// <summary>
    /// How the Builder's Wand shows a building before it is built: "ghost" draws the blueprint's real blocks
    /// see-through where the building would stand, as the WorldEdit import tool does, with blocks in the way
    /// marked red; "outline" is the old blue and red outline of the walls; "both" shows the two together. If
    /// the ghost cannot be made, the outline is shown instead.
    /// </summary>
    public string PlacementPreview = "ghost";
    /// <summary>
    /// While a building is being placed, the colonies' buildings within this many blocks of it show box
    /// outlines (purple, orange where the new one would overlap), so buildings can go right next to each
    /// other. 0 turns this off.
    /// </summary>
    public int NearbyOutlineRange = 32;
    /// <summary>At most this many nearby buildings get an outline, the nearest first.</summary>
    public int NearbyOutlineMax = 30;

    // Skills (config/skills.json lists them; see ColonyManager.Skills.cs)
    /// <summary>Settlers learn skills by doing their job and get faster at it. Off: no XP, no effects, no Skills panel.</summary>
    public bool Skills = true;
    /// <summary>The highest level a skill can reach (RimWorld style, 0 to 20).</summary>
    public int SkillLevelMax = 20;
    /// <summary>XP step: level n costs this x n XP on top of the level below, up to SkillSteepFrom.</summary>
    public int SkillXpPerLevel = 75;
    /// <summary>Above this level each step grows SkillSteepFactor times as fast: level n costs SkillXpPerLevel x (from + factor x (n - from)).</summary>
    public int SkillSteepFrom = 10;
    public int SkillSteepFactor = 2;
    /// <summary>Speeds up or slows down all skill XP, like Project Zomboid's sandbox XP multiplier.</summary>
    public float SkillXpMultiplier = 1f;
    /// <summary>
    /// How much of a skill a settler can use at a building of level 1, 2, 3, 4, 5 and up (the last entry covers
    /// higher levels). A job skill is limited by the hut they work at, Strength and Fitness by the Residence
    /// they sleep in. A building at the top level its blueprint style has always allows everything.
    /// </summary>
    public int[] SkillCapByBuildingLevel = { 4, 8, 12, 16, 20 };
    /// <summary>No skill effect multiplies a number by more than this (or less than one over it).</summary>
    public float SkillEffectMax = 2.5f;
    /// <summary>The levels at which skills.json "perks" kick in: the first value at the first level here, and so on.</summary>
    public int[] SkillPerkLevels = { 5, 10, 15, 20 };
    /// <summary>Settlers have passions (one or two flames) for some skills: those grow faster and lift their mood while they work.</summary>
    public bool SkillPassions = true;
    /// <summary>XP multiplier for no passion, one flame (Interested) and two flames (Burning).</summary>
    public float[] SkillPassionXp = { 1f, 2f, 3f };
    /// <summary>Weights for a new settler having 0, 1, 2 or 3 passions.</summary>
    public int[] SkillPassionCountWeights = { 15, 40, 35, 10 };
    /// <summary>The chance a rolled passion is Burning (two flames) instead of Interested.</summary>
    public double SkillPassionBurningChance = 0.25;
    /// <summary>The chance a grown-up child gets each of its parents' passions.</summary>
    public double SkillPassionInherit = 0.5;
    /// <summary>Some traits (Pacifist, Bad Back, Ham-fisted) make a settler incapable of a skill: they cannot take its jobs.</summary>
    public bool SkillIncapable = true;
    /// <summary>Skills above SkillSteepFrom lose XP every day, used or not, as in RimWorld; never below SkillSteepFrom.</summary>
    public bool SkillDecay = true;
    /// <summary>XP lost per day at level SkillSteepFrom + 1, + 2 and so on up to the top level (the last entry covers the rest).</summary>
    public int[] SkillDecayPerDay = { 5, 9, 14, 23, 40, 63, 90, 135, 180, 270 };
    /// <summary>Each skill gains full XP up to this much per in-game day; XP past it counts for SkillOverCapFactor. 0 turns the cap off.</summary>
    public int SkillDailyCap = 600;
    public float SkillOverCapFactor = 0.2f;

    // Traits (config/colonytraits.json lists them; see ColonyManager.Traits.cs)
    /// <summary>Settlers are born with traits: strengths and flaws that change how they work, eat, fight and live.</summary>
    public bool Traits = true;
    /// <summary>How many traits a settler from outside rolls (children inherit theirs and can have more than the max).</summary>
    public int TraitsMin = 2;
    public int TraitsMax = 10;
    /// <summary>Every settler has at least this many good and this many bad traits.</summary>
    public int TraitsMinGood = 1;
    public int TraitsMinBad = 1;
    /// <summary>Safety cap on a child's inherited traits, so the window stays readable (0 = none).</summary>
    public int TraitsChildMax = 20;
    /// <summary>The Tavern asks this many more rusty gears per point of trait score (and one more per 10 levels of good traits).</summary>
    public int TraitGearsPerPoint = 1;
    /// <summary>Chance a trait both parents have passes to a child, and a trait only one parent has.</summary>
    public double TraitInheritShared = 0.75;
    public double TraitInheritSingle = 0.35;
    /// <summary>Fresh random traits every child gets on top of what it inherits.</summary>
    public int TraitNewPerChild = 1;
    /// <summary>The highest trait level. A good trait gets 5% stronger a level; a bad one loses 4% of its penalty a level.</summary>
    public int TraitLevelMax = 20;
    /// <summary>Trait XP cost per level step: level L to L+1 costs (L+1) times this. Every day gives 1 XP, 2 more on a day the trait was used.</summary>
    public int TraitXpPerLevel = 3;
    /// <summary>A guard becomes a Veteran after this many kills.</summary>
    public int TraitVeteranKills = 25;
    /// <summary>Knocked out this many times within TraitShakenDays makes a settler Shaken for TraitShakenDays.</summary>
    public int TraitShakenCount = 3;
    public double TraitShakenDays = 10;

    // Population
    /// <summary>Settlers the colony can hold: PopulationBase plus PopulationPerTownHallLevel for each Town Hall level (a freshly placed Town Hall is level 0).</summary>
    public int PopulationBase = 20;
    public int PopulationPerTownHallLevel = 5;
    // Settler gear and inventory (see ColonyManager.Gear.cs and ColonyManager.Lunch.cs)
    /// <summary>The character class whose starting clothes new settlers wear ("commoner" is the game's default class).</summary>
    public string SettlerClothesClass = "commoner";
    /// <summary>Settlers pick up items lying near them and take them to their hut (or the warehouse).</summary>
    public bool SettlersPickUpItems = true;
    /// <summary>Settlers leave items alone for this long after they land, so they do not grab what a player just dropped.</summary>
    public float PickUpDelaySeconds = 5f;
    /// <summary>An idle settler walks to items on the ground within this many blocks. 0: only what it walks over.</summary>
    public int PickUpRange = 10;
    /// <summary>
    /// A settler with no job hut keeps what it picks up until its pack is full or it has carried it this many
    /// seconds, then takes it all to the warehouse in one trip (if the colony has one).
    /// </summary>
    public float LooseItemsDepositSeconds = 120f;
    /// <summary>Settlers carry food to eat on the go and snack while they work, so their stomach stays nearly full.</summary>
    public bool CarryFood = true;
    /// <summary>How much food a settler packs when it gets some, in saturation: 1200 is one full stomach.</summary>
    public float CarryFoodSaturation = 1200f;
    /// <summary>
    /// Settlers eat, pack and cook with the food that will spoil soonest first. Food about to spoil is up to
    /// 1 + this many times as wanted as food that keeps; 0 turns it off.
    /// </summary>
    public float EatSpoilingFirst = 2f;
    /// <summary>Food with this many in-game hours left before it is rot gets about a third of the EatSpoilingFirst pull.</summary>
    public float SpoilingSoonHours = 48f;
    /// <summary>Food that can spoil goes in the coolest storage there is room in, and couriers and cooks carry it from warm storage down to cellars.</summary>
    public bool KeepFoodCool = true;
    /// <summary>Food is only carried to storage where it spoils at most this share as fast as where it is now (0.75 = a quarter slower or better).</summary>
    public float CoolStoreMaxRateShare = 0.75f;
    /// <summary>Each adult settler carries a bowl in its food slots, to take a serving of a meal from a crock or pot along. Settlers without one take one, and the Potter's Hut makes more.</summary>
    public bool CarryBowl = true;
    /// <summary>
    /// Every grown settler carries a light in its offhand and holds it up in the dark, lighting the ground around it.
    /// It fetches one from its hut or the warehouse: a lantern when one is free (no build needs it), else a torch.
    /// </summary>
    public bool CarryTorches = true;
    /// <summary>A settler only walks to a chest this many blocks away or closer for its light.</summary>
    public int LightFetchRange = 64;
    /// <summary>Workers within this many blocks of their hut drop off what they do not need on their way past.</summary>
    public int DepositNearHut = 8;

    // Jobless settlers (see ColonyManager.Zones.cs and AiTaskSettlerChores.cs)
    /// <summary>
    /// Settlers without a job look after their home: the building their bed is in. They tend the farmland inside
    /// its footprint (harvest, pull weeds, sow, water, till), using seeds, hoes and watering cans from chests in
    /// the home, else from the warehouse.
    /// </summary>
    public bool JoblessTendHome = true;
    /// <summary>Settlers without a job help out with hauling: deliveries to huts and builders, drop-off chests, clearing huts.</summary>
    public bool JoblessHelpHauling = true;
    /// <summary>Seeds a settler takes along for its home plot at a time.</summary>
    public int JoblessSeedTake = 8;
    /// <summary>Settlers without a job skin dead animals on the colony's land with a knife and take the meat, hide and the rest home.</summary>
    public bool JoblessSkinAnimals = true;
    /// <summary>How far from itself, in blocks, a settler looks for a dead animal to skin.</summary>
    public int JoblessSkinRadius = 64;
    /// <summary>Settlers without a job cook raw food from home (meat, fish) on a fire pit in or by their home.</summary>
    public bool JoblessCookAtHome = true;
    /// <summary>How far outside its home, in blocks, a household's fire pit may stand.</summary>
    public int JoblessFirepitReach = 4;

    // Home decorations (see ColonyManager.Decor.cs)
    /// <summary>
    /// Settlers put up things the household owns inside their home: paintings, antlers and other trophies on an
    /// antler mount, weapons on a tool rack, books in a bookshelf, bottles in a bottle rack (A Culinary Artillery)
    /// and banners on a wall pole (Heraldry : Banners). The things come from chests in the home; the empty rack,
    /// shelf, mount or pole from a chest at home or the warehouse.
    /// </summary>
    public bool SettlersDecorate = true;
    /// <summary>How many of each kind of rack, shelf, mount or pole a home gets at most (paintings have no limit but wall space).</summary>
    public int DecorPerKind = 2;
    /// <summary>How often, in real seconds, a settler looks for something to put up at home.</summary>
    public float DecorCheckSeconds = 90;
    /// <summary>How near its home, in blocks, a settler must be to go and decorate it.</summary>
    public int DecorRange = 64;

    // Happiness (see ColonyManager.Happiness.cs)
    /// <summary>Settlers have a mood from 0 to 100 made of thoughts (meals, home, family, passions); it changes how well they work and learn.</summary>
    public bool Happiness = true;
    /// <summary>Points an hour that happiness moves towards the sum of the settler's thoughts.</summary>
    public float HappinessPerHour = 2f;
    /// <summary>A settler at or below this happiness downs tools and sulks for a few hours.</summary>
    public float HappinessBreakAt = 15f;
    /// <summary>In-game hours a breakdown lasts (rolled between the two).</summary>
    public double HappinessBreakHoursMin = 2;
    public double HappinessBreakHoursMax = 4;

    // Settler animations (see Settlers/SettlerBody.cs and Settlers/SettlerSeats.cs)
    /// <summary>Settlers hold a torch or lantern up the way a player does.</summary>
    public bool HoldLightsLikePlayers = true;
    /// <summary>Settlers with nothing to do now and then look around, scratch their head, cough, stretch or yawn, like an idle player.</summary>
    public bool IdleAnimations = true;
    /// <summary>Settlers sit down to eat: on a chair, stool or bench nearby, else on the floor.</summary>
    public bool SitToEat = true;
    /// <summary>Settlers taking a break sometimes sit down: on a chair, stool or bench near home, else on the floor.</summary>
    public bool SitWhenIdle = true;
    /// <summary>Settlers swim in water and climb ladders with the player's swimming and climbing animations.</summary>
    public bool SwimAndClimbLikePlayers = true;
    /// <summary>Settlers with nothing to do wave, nod or bow to a player who comes near.</summary>
    public bool GreetPlayers = true;
    /// <summary>Settlers with nothing to do out in the cold hug themselves to keep warm.</summary>
    public bool ShiverWhenCold = true;
    /// <summary>Settlers hold their food in their hand while they eat it.</summary>
    public bool HoldFoodToEat = true;

    // Town map (see Map/ColonyLandMap.cs)
    /// <summary>Blocks around the colony's land that the Town map shows, once a player or a settler has loaded them.</summary>
    public int TownMapRange = 1024;

    // Fires (see Colonies/ColonyManager.Fires.cs and Settlers/AiTaskSettlerFirefight.cs)
    /// <summary>Settlers put out fires on the colony's land with watering cans.</summary>
    public bool Firefighting = true;
    /// <summary>Also put out burning piles (firewood and the like); a lit pit kiln is never put out.</summary>
    public bool FirefightingPiles = true;
    /// <summary>Settlers this many blocks or closer to a fire come to put it out.</summary>
    public int FireAlarmRadius = 64;
    /// <summary>How many settlers go to each fire at most.</summary>
    public int FirefightersPerFire = 3;
    /// <summary>How far around a fire (and around the settler) it looks for water to fill its watering can.</summary>
    public int FireWaterSearchRadius = 32;
    /// <summary>Seconds of a watering can's water (a full can holds 32) a settler pours on one burning block, and how long it takes.</summary>
    public float FirePourSeconds = 1.5f;
    /// <summary>Before lighting its pit kiln the potter cuts the grass and plants this many blocks around it, so the fire cannot spread. 0 turns it off.</summary>
    public int KilnClearRadius = 3;
}
