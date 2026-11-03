# Vintage Colonies

Vintage Story 1.22.x code mod, .NET 10 (mod id `createacolony`). It brings the ideas of the Minecraft mod
MineColonies to Vintage Story: found a colony with a Town Hall, recruit settlers, and place huts that your
settlers build, staff and level up: builders, farmers, foresters, miners, cooks, couriers, guards, crafters
and more. See [ROADMAP.md](ROADMAP.md) for what is done and what is planned.

## The name and the internal id
The mod is called **Vintage Colonies** (it used to be called Create a Colony). Only the name changed.
The internal mod id stays `createacolony`, and so do the code namespace (`CreateAColony`), the asset
domain (`assets/createacolony/`, block and item codes like `createacolony:townhall`), the config files
(`ModConfig/createacolony.json`), the release zips (`createacolony_<version>.zip`) and all save data keys.
Changing any of those would stop old worlds from loading their colonies, so leave them as they are.

## Build
1. Install the .NET 10 SDK and set `VINTAGE_STORY` to your game folder.
2. Run `dotnet build CreateAColony.csproj -c Release`.
3. The ready-to-install mod is written to `Releases/createacolony_<version>.zip`. Copy that zip into the
   game's `Mods` folder.

## Try it
- Start with a **Supply Camp** (3 firewood over 2 sticks): place it anywhere, no colony needed. Your first camp in a world gives you a Town Hall, a Builder's Hut, a Builder's Wand and a **Colony Ledger**, and right-clicking the camp opens its storage with one set of starting materials. See "The supply camp and the Colony Ledger".
- Or craft a **Builder's Wand** (flint on a stick), then a **Town Hall** (8 planks of any wood around the Builder's Wand; craft a second wand to keep one), or take them from the creative inventory. See "Crafting recipes" for every recipe. Right-click the ground with the wand, pick Town Hall and press Accept: this founds the colony, claims a 250x250 area, and 3 to 4 settlers arrive. Finished huts grow the claim (see "Growing the colony's land").
- `/colony info` inside the claim shows the colony.
- `/colony invite <player>`, then that player types `/colony join`. `/colony leave` leaves the colony you stand in.
- `/colony recruit [name]` (needs controlserver) spawns one more settler, for testing.
- Settlers get hungry and take food from any chest, shelf or crock inside the colony claim. Keep bread, fruit,
  bowls of stew, pots of meals or pies near the Town Hall. With no food they slowly starve. See "Food and meals".
- At night (21:00 to 06:00) each settler claims a free bed inside the claim and sleeps there. Without a bed they
  sleep on the floor by the Town Hall.
- About two in-game hours before a temporal storm, settlers head to their bed and stay put until it passes.
- `/colony citizens` lists every settler with their food level and whether they have a bed.
- Lost settlers (missing for half a day while the Town Hall area is loaded) respawn at the Town Hall. With
  Permadeath off, a killed settler returns after `DownedRecoveryDays`.
- Breaking the Town Hall disbands the colony and removes the claim. Only the owner (or a server admin) can, and
  only while sneaking. Officers can break huts; a hut that is built or has a work order also needs sneaking.
  Settlers of a disbanded colony leave with it.
- The mod refuses to start if VS Village is installed.

## Crafting recipes (0.32.0)
Every building needs planks, and planks need a saw, so a colony is a mid game goal (copper age). Huts all use
the same shape: two of the hut's own item in the middle column (top middle and center), the other 7 slots
planks of any wood.

| Block | Recipe |
| --- | --- |
| Builder's Wand | flint on a stick (1x2) |
| Town Hall | 8 planks around a Builder's Wand (the wand is used up) |
| Builder's Hut | 2 rope + 7 planks |
| Farmer's Hut | 2 seeds (any) + 7 planks |
| Forester's Hut | 2 firewood + 7 planks |
| Potter's Hut | 2 clay (any) + 7 planks |
| Cook's Hut | 2 bread (any) + 7 planks |
| Residence | 2 dry grass + 7 planks |
| Guard Tower | 2 spears (any) + 7 planks |
| Barracks | 2 bows (any) + 7 planks |
| Tavern | 2 rusty gears + 7 planks |
| Warehouse | 2 chests + 7 planks |
| Prospector's Hut | 2 stones (any rock) + 7 planks |
| Miner's Hut | 2 torches + 7 planks |
| Quarry | 2 rock blocks (any) + 7 planks |
| Smelter's Hut | 2 charcoal + 7 planks |
| Blacksmith's Hut | 2 ingots (any metal) + 7 planks |
| Sawmill | 2 logs (any) + 7 planks |
| Primitive Craftsman | 2 flint + 7 planks |
| Supply Camp | 3 firewood over 2 sticks (unchanged) |
| Colony Ledger | 4 paper (any), 2 leather, 2 charcoal: paper, leather, paper / charcoal, paper, charcoal / paper, leather, paper |
| Drop-off | stick over a plank (unchanged) |
| Field Marker | dry grass over 2 sticks (unchanged) |

## The supply camp and the Colony Ledger (0.32.0)
- The **Supply Camp** goes down anywhere, inside a colony or not. The first camp each player places in a world hands them the colony charter: a Town Hall, a Builder's Hut, a Builder's Wand and a Colony Ledger (anything that does not fit drops on the camp). The camp itself starts with one set of materials in its own storage; right-click it to open it like a chest.
- This happens **once per player per world**, saved on the world, so it holds before you have a colony and also for players who join someone else's colony. Breaking the camp and placing it again, or crafting another, only gives an empty camp (still useful as storage). Breaking a camp drops what is inside.
- The daily deliveries are gone, and so is the "Supply bundles left" line in the Town Hall window.
- What the camp gives is in `assets/createacolony/config/supplycamp.json`: `playerItems` go to the player, `items` (up to 16 stacks) go into the camp.
- **Camp buildings**: put a WorldEdit export at `ModConfig/createacolony/blueprints/supplycamp/level1.json` (or `assets/createacolony/config/blueprints/supplycamp/level1.json`). The first camp then puts that building up around itself at once, turned to face you. Put a Supply Camp block in the export where the camp should sit; without one the camp sits at the bottom centre. If any of it would land on land you may not build on, none of it is built.
- The **Colony Ledger** opens the quest book when you right-click with it, and your colony's Town Hall window when you sneak and right-click. The colony's history pages will live in it later. A replacement is expensive on purpose (4 paper, 2 leather, 2 charcoal); the first one is free.

## The quest book (0.42.0)
An FTB-Quests-style quest book that doubles as a guide to the mod and to the base game. Nothing is ever locked behind a quest; quests only hand out item rewards.
- **Open it** with the **L** key (changeable in the game's controls), by right-clicking the **Colony Ledger** (sneak and right-click opens the Town Hall window instead), with the **Quests** button that appears at the top of the screen while your inventory is open, or from the **Quests** tab of the Town Hall window.
- **Groups** are the tabs down the left: Story, Ages, The Colony, Home and Food, The Land, Crafting, Travel, Dangers, Lore and Tavern. Each group has **chapters** (the list at the top, with "done / total"), and each chapter is a **map**: drag to move it, click a quest to read it. Lines show which quest leads to which; grey means "do the earlier quest first", white open, blue counting, gold ready to claim, green done. A star marks a chapter's last quest, a square a main story quest, a hexagon an optional one.
- **Story** and **The Colony** are shared by the whole colony (any member can claim; the Town Hall's Quests tab shows them). The **Prologue** and every other group are each player's own. Colony quests stay grey until you have a colony.
- **Most quests complete on their own** from what you carry (hotbar and bags), what is in the colony's storage, its buildings, settlers, jobs, families, the Town Hall's statistics and event log. A chat line tells you when one is ready; open the book and press **Claim**. A few story quests (the chapter 4 choice) must be **Accepted** first; **Drop** gives one up with no cost.
- **Track** up to three quests and they show in the top right of the screen with their progress.
- **Quest files** live in `assets/createacolony/config/quests/*.json` (one per group) and their text in `lang/en.json` as `quest-<code>` / `quest-<code>-desc`, `chapter-<code>`, `questgroup-<code>`. Objective types: `obtain` / `deliver` (carried items, wildcards like `game:ore-*copper*` allowed), `stock` (colony storage), `building` (type and optional `level`), `buildings`, `settlers`, `job`, `couples`, `children`, `members`, `stat` (Town Hall statistics key, `kill:*` allowed), `event` (event log key), `age`, `land`, `camp`, `colony`. `x`/`y` place a quest on the map, `requires` draws the lines, `shape` is round, square, star or hexagon, `icon` an item code, `accept: true` asks for Accept, `secret: true` hides it until it opens.
- **Commands** (admin): `/colony quest list`, `/colony quest complete <code>` (marks it done, no rewards), `/colony quest reset` (forgets your quests and your own colony's), `/colony quest reload` (re-reads the quest files; players re-join to see changes).
- Chapters with no quests yet say so in the book (Maps and waypoints, Riding, The wider world, Temporal storms, Raids, Deep places, and the whole Tavern group). Dialogue windows, ledger journal pages and the accepted-and-ignored downside are not in yet.

## Settler gear, clothes and inventory (0.43.0)
- **Settlers look like players.** They use the player's own model and get a random look from the player character creator (skin, eyes, hair, beard; women never get a beard). The look is kept, so a settler who is brought back looks the same. New settlers, and settlers from older saves once, start in the clothes of the game's **Commoner** class (config `SettlerClothesClass`).
- **The settler window has tabs**: Character & Armour, Stats, Inventory, Traits & Skills. Traits are not in yet; the tab says so above the skills.
- **Take and give items.** Colony members click the settler's slots the same way as their own: left-click takes a stack or puts the one on your cursor (swapping if different), right-click takes half or puts one, shift-click sends it to your inventory. Clothing, armor and bag slots only take what fits there; visitors to the colony can only look.
- **Tool bar**: a line of its own on the Inventory tab with one **weapon** slot, three **tool** slots and an **offhand** slot. Tools and weapons a settler gets go there first, so they never get dropped off with the harvest; workers take their tool from it into their hand and put it back after. A shield in the offhand shows in the left hand.
- **Packed food**: three slots of food carried to eat on the go. A settler eats from it whenever a whole bite fits in its stomach, so it stays nearly full while it keeps working. Once the packed food is gone it works on until it is hungry (40%), walks to the nearest food, eats, and packs up to a stomach's worth for later (config `CarryFood`, `CarryFoodSaturation`). Food that rots is moved to the inventory.
- **Dropping things off**: a settler keeps only what its job needs. Workers drop the rest off at their hut whenever they pass within 8 blocks of it (`DepositNearHut`), as well as when their pack is full or work runs out; guards take theirs to the tower; others take theirs to their hut, or the warehouse if they have none.
- **Picking things up**: settlers pick up items they walk past, and an idle settler walks to items lying within 10 blocks inside the colony (`SettlersPickUpItems`, `PickUpRange`). Items are left alone for 5 seconds after they land (`PickUpDelaySeconds`), so a settler does not grab what you just dropped. Settlers without a job pick things up too: they save them up and take them to the Warehouse in one trip once their pack is full or after about two minutes (`LooseItemsDepositSeconds`); with no Warehouse yet, they keep them.
- **Torches**: at night or in a dark place a settler holds a lit torch in its left hand when it carries one (tool bar offhand or inventory). A settler without one fetches a lit torch from its hut's storage or the warehouse when it has nothing else to do (`CarryTorches`).
- If the player's model cannot be read (a mod that changes the player a lot), settlers keep the older villager model and the log says why.

## Mining: Prospector, Miner and Quarry (0.36.0)
All three are on the new Mining tab of the Builder's Wand and use placeholder blueprints for now.
- **Prospector's Hut**: put a prospecting pick in its storage. The prospector wanders its work area (`ProspectorRadius` 64, changeable on the Work area tab), walking a random `ProspectorMinStep` 25 to `ProspectorMaxStep` 50 blocks from its last reading (or from a good earlier one) and never reading within `ProspectorNearReading` 16 of an existing reading. Each candidate spot is weighted by the ore the nearby readings promise (inverse-distance average of best density plus a quarter of the others; `ProspectorUnknownQuality` 0.15 for unread ground) to the power of `ProspectorPreference` 15, so it searches more near good readings and less near poor ones. It takes a density reading with the game's own prospecting pick code, wearing the pick 3 per reading. Readings appear on the hut's Ore tab, on the Town Hall's **Prospecting** tab (top of its tab list) and on a separate **Colony prospecting** layer of every colony member's world map (`ProspectorSharesOreMap`), kept apart from the player's own prospecting layer. Colony readings can't be deleted from the map. Good finds go in the Town Hall event log.
- **Miner's Hut**: put a pickaxe in its storage. Like MineColonies, the miner digs its own shaft beside the hut: a spiral staircase round a rock pillar (3x3, steps 3 high so settlers can walk it). The first turn is the way in; every turn below it is a level, 8 blocks deeper, with a 2-high tunnel `MinerTunnelLength` 12 out from each side. Each hut level allows `MinerLevelsPerHutLevel` 2 levels. It digs out any ore it can see from where it stands (`MinerReach` 3), but never the floors of the shaft or tunnels. A tunnel that runs into water, lava, a building or someone else's land stops there; a blocked shaft waits.
- **Go to prospected ore** (switch on the Ore tab, on by default): the miner first goes to the best reading in range (`MinerRadius` 64) that is at least `MinerTripMinFactor` 0.267 ("decent"), or any reading of the ore chosen on the Ore tab. It finds that ore's blocks within `MinerOreSearchRadius` 12, digs a staircase down to them from the surface and digs them out, then comes back to its shaft. Ore too hard for its pick waits for a better pickaxe in the hut storage.
- **Quarry**: put a pickaxe in its storage. The quarrier picks a side of the Quarry facing away from the Town Hall and digs a square pit there, clearing up to 4 blocks above the ground first, then one layer at a time, leaving a staircase round the edge. Level 1 is 5x5 and 6 deep (`QuarryRadius` 2, `QuarryDepthPerLevel` 6); each level adds `QuarryRadiusPerLevel` 1 (2 blocks wider) up to `QuarryMaxRadius` 6 (13x13), and 6 more layers.
- **The right tool for each block** (0.38.0): miners and quarriers carry a pickaxe and, when the hut's storage has one, a shovel. For each block they hold whichever carried tool is fastest on its material (the game's own `MiningSpeed`, so modded tools work too): the shovel for soil, sand and gravel, the pickaxe for rock and ore. The right tool digs in the normal time, a slow one (a pickaxe in soil) takes up to twice as long. A block too hard for every carried tool still waits for a better pickaxe.
- **Filling finished mines in** (0.41.0): when the ore at a prospected spot is dug out (or the miner leaves it for New mine or a different ore), every staircase it dug there is filled back in, `MinerFillDepth` 6 blocks down from the surface: all the open space joined to the steps beside the entrance, lowest first, never along the surface and never inside buildings. It uses dirt first, then gravel, sand or stone it carries, then fetches from the hut's storage, then asks couriers for `game:soil-*` (going on with other work meanwhile). Save data: `Building` 60 `SiteEntrances`, 61 `MineSeals`.
- **Node search** (0.44.0): the miner carries a prospecting pick (from the hut's storage, or asked of couriers; it digs on without one). Wherever it starts digging somewhere new (more than half a search's reach from its last search), in the shaft or at prospected ore, it does a node search like the pick's node search mode: every ore block within `MinerNodeSearchRadius` (0 = the world's `propickNodeSearchRadius`, or 8 when the world has none) is noted, and it digs a side tunnel to each (nearest first, from the search spot or the end of the last side tunnel), never digging away a floor of the mine or the shaft's pillar. Ore straight above or below a search spot, or behind water or buildings, is let go. Each search takes `MinerNodeSearchSeconds` and wears the pick by `MinerNodeSearchWear`. Finds are kept in memory only; after a restart the miner searches again.
- **Torches in the mine** (0.40.0): the miner keeps up to `MinerTorchesCarried` 8 torches from the hut's storage (topped up when it is down to 2, and asked of couriers as `game:torch-*` when there are none). After each block it digs, if the light where it stands is below `MinerTorchLight` 8 (0 turns this off), it puts a lit torch on the floor there. Miners and quarriers never dig torches out, and treat them as open space.
- **Waypoints for blueprints** (creative inventory only): put a **Mine waypoint** in a Miner's Hut blueprint, on the ground where the middle of the shaft goes, or a **Quarry waypoint** in a Quarry blueprint, on the ground in the middle of the pit. The hut then digs there instead of picking a spot beside itself. The builder never places the waypoint (its spot is cleared), and it leaves out the blueprint's own blocks wherever the miner or quarrier will dig: the shaft's steps (the 3x3 round the waypoint, starting at its north-west corner at the waypoint's height and going down clockwise, with the pillar under the waypoint) and its tunnels, or the whole pit area below the Quarry waypoint (up to 13x13). Everything else the blueprint builds is left alone by the digging. The current level's blueprint is used first, then the other levels.

## Two prospectors and a mineshaft maze (0.62.0)
- **Prospector's Hut** takes two prospectors. In **Manage workers** each one has a button, **Finds ore** or **Mines ore**; click it to switch. Finding ore works as before (prospecting pick). A prospector on Mine ore needs a pickaxe (and torches help): it goes to the best reading in the hut's work area, or of the ore chosen on the hut's Ore tab, digs a staircase down to the ore, digs it out and fills the staircase back in, like the Miner's Hut's trips. Each prospector on Mine ore works its own reading, and its work trains Mining. The hut window shows each prospector's work and status.
- **Miner's Hut mineshaft from pieces**: the miner builds a mineshaft out of schematic pieces in `blueprints/<pack>/mineshaft/`: `entrance.json` (the main opening, the way down) and the sections `cross.json` (+), `tee.json` (T), `straight.json` and `corner.json` (L). It digs and builds the entrance under the hut's Mine waypoint (or beside the hut), then grows a maze: at an open side with nothing beyond it, it picks a section at random, turned so it joins, and digs and builds that, then the next. Each hut level allows `MinerSectionsPerHutLevel` 4 sections. The pieces' posts, beams, ladders and torches come from the hut's storage (couriers are asked for what is missing). When the mineshaft is finished or waiting for materials, it goes to prospected ore with Go to prospected ore on. Without any entrance piece it digs the old spiral shaft. Simple placeholder pieces ship in `medievaloak/mineshaft/` (made by `tools/blueprints/mineshaft_pieces.py`); yours in `ModConfig/createacolony/blueprints/<pack>/mineshaft/` replace them one by one. Save data: `Building` 48 `Trip`, 49 `Sections`; `CitizenRecord` 60 `WorkRole`, 61 `Trip`.

### Making mineshaft pieces
1. In a creative world, build each piece inside solid rock: dig out the way through and put in your posts, beams, ladders and torches. Natural rock, soil, gravel and sand in the export are left as the miner finds them; every empty spot is dug out; everything else is built.
2. Sections (cross, tee, straight, corner) can be any size; their floor is the bottom layer of the export. Pieces line up by their ways through, not their middles, so a way off-centre still meets the next piece's way. Mine waypoints standing in a doorway mark where the next section joins: they count as open and are dug out (0.88.0).
3. An open side is an edge with a gap (empty spots) in the bottom four layers; the middle of its widest gap is where the next piece joins.
4. The entrance (0.63.0, 0.73.0): built down from the surface. Its ground layer is the highest layer whose edge is mostly filled in (natural soil or rock, or a deck of planks); that layer goes at ground height on the land beside the Miner's Hut, with anything above it (a fence) built on top. Tunnels start at **rows of two or more Mine waypoints** on an edge of the entrance: the section's floor goes level with the landing (the waypoints' layer when they replace the landing's floor, the layer above when they sit under a built floor). Sections never come up through the ground layer. Aaron's 0.73.0 entrance winds down 22 blocks with tunnel starts at four landings and one at the bottom, so its stairs no longer repeat (`MinerEntranceRepeatsPerHutLevel` now 0; config version 4 changes a saved 1 to 0). `tools/blueprints/mineshaft_preview/render.py` draws a possible mine from the shipped pieces.
5. Settlers walk steps one block high, and climb ladders.
6. Select each piece with WorldEdit and `/we export entrance` (or `cross`, `tee`, `straight`, `corner`), copy the files to `VintagestoryData/ModConfig/createacolony/blueprints/medievaloak/mineshaft/` (or your pack's folder) and run `/colony reloadblueprints`.

## Aaron's mineshaft pieces and a relieving Quarry (0.63.0)
- **Mineshaft**: Aaron's surface entrance, T section, straight section and corner (0.88.0) ship as the Medieval oak defaults (`assets/createacolony/config/blueprints/medievaloak/mineshaft/`); the cross is still a placeholder (`tools/blueprints/mineshaft_pieces.py`). The entrance goes on the ground beside the hut (saved as the hut's `MineshaftPos`), not under the hut's Mine waypoint, and goes deeper with each hut level. Sections join by their gaps, so their floors line up; tunnels leave the entrance at its rows of Mine waypoints on every loop of the stairs (0.63.1, from Aaron's connected example: loops 7 blocks apart). Support beams keep their saved beams when built.
- **Quarry**: its main work is now whole rock blocks, the game's relieving (a block broken with all six sides open drops itself). Inside the pit's staircase, rock in a 3D checkerboard is left standing (`IsReliefCell`), the rest is dug as before, and each rock that ends up free on all sides is broken out whole before more digging. A 5x5 pit at level 1 gives about 19 rock blocks; any settler digging rock that stands free gets the whole block too. Stat "Whole rock blocks quarried".

## Storage, doors, trader claims and lag fixes (0.72.0)
- **Hut storage**: every chest, trunk, crate, clay storage vessel and modded storage block inside a hut's
  blueprint counts as that hut's storage. A hut without a blueprint uses storage within 2 blocks of the hut
  block. `HutStorageBlocks` in the config adds block codes the mod does not recognise by itself. The
  Warehouse only looks around itself (and inside its building), which was a big source of lag.
- **Doors**: settlers close a door or gate once they have walked through it (after 8 seconds at most), unless
  someone is standing in the doorway.
- **Trader claims**: a land claim the world made for a trader (no player owner, "trader" in its name) does
  not shrink the colony's land. Since 0.164.0 it stays until its trader joins. `ClearedWorldgenClaims` lists
  the words to look for.
- **Placement**: only officers and the owner can move, turn, confirm or take back placed huts.
- **Lag**: colonies are saved together every 30 seconds (and whenever the world saves) instead of on every
  change; open windows reuse build progress and materials for a few seconds; the mineshaft checks its
  hut's storage once per scan.

## Paintings (0.105.0, dyes 0.105.1)
- 17 framed paintings to hang in builds: 6 small (1 block), 5 wide (2 x 1), 3 tall (1 x 2) and 3 large (2 x 2). All the art is original, drawn by `tools/paintings/paintings.py` (no outside images).
- **Blank canvas** (Aaron's layout): 8 boards around 4 linen.
- **Painting a picture**: put the blank canvas in the middle of the crafting grid and a bucket of each of the picture's dyes around it: the first dye above, then below, left, right, then the corners. Each bucket needs 2 litres of that dye; only the dye is used. Simple pictures take 2 dyes (Candlelight: yellow, black), the Map of the Realm takes 8. Each painting's tooltip lists its dyes, and the handbook shows the recipes. The list is `DYES` in the script; `src/Block/PaintingRecipes.cs` makes the recipes at load from the game's `dyeportion-*` items and any bucket, and logs a warning for a picture whose dye is missing.
- Paintings hang on a wall like vanilla ones (`HorizontalAttachable`) and drop themselves when broken. A painting bigger than one block sits in the block you click and spreads up and to the right; only that first block is solid to the game, so keep the spaces it covers free.
- **Blueprints**: save a building with paintings on its walls and builders hang them. The Builder's list asks for the exact picture (for example "Painting: Mountain Lake").
- **Adding pictures**: add a function, an entry to `PAINTINGS` and its dyes to `DYES` in `tools/paintings/paintings.py`, run it (it rewrites the textures, the four shapes and `blocktypes/painting.json`), and add the name to `lang/en.json`. `--sheet out.png` makes a preview sheet.

## Settlers decorate their homes (0.106.0)
- Now and then a settler puts up things its household owns inside its home. "Owns" means it is in a chest in that home.
- **Paintings**: any painting in a home chest gets hung on a wall at eye height, near the settler's bed, with room for its size.
- **Trophies**: antlers (anything the game's antler mount takes) go on an antler mount. A Hunter also takes trophies from its hut or the warehouse.
- **Weapons**: swords, spears, bows and other weapons in a home chest go on a tool rack. Never the weapons the settler carries.
- **Books**: go in a bookshelf. **Bottles** (A Culinary Artillery): go in a bottle rack. **Banners** (Heraldry : Banners): go on a wall banner pole.
- The empty rack, shelf, mount or pole comes from a home chest, else the warehouse. Players make or buy those; settlers do not craft them.
- Wall pieces hang about eye height (banners one higher, so they hang down). Shelves and racks stand on the floor with their back to a wall and room to walk in front. Nothing goes within 2 blocks of a door, next to a chest or bed, or outside under the sky.
- A rack or shelf gets filled before a new one goes up. At most `DecorPerKind` (2) of each per home.
- Any grown-up settler near home (`DecorRange`, 64 blocks) does it, jobless or not, checking every `DecorCheckSeconds` (90). It is a short break from work. Afterwards it feels "Made their home nicer" (+5 mood, 2 days).
- Turn it off with `SettlersDecorate: false`.

## Traders join the colony (0.108.0)
- **Trader claims (0.164.0).** Traders keep their land claims, as in the base game. A trader's claim goes only when that trader joins a colony. Trader houses that lost their claim in older versions get it back while their trader is there. (0.108.0 to 0.163.0 took every trader claim away, which broke the game's balance. `RemoveAllTraderClaims: true` still does that.)
- **The house is emptied (0.164.0).** When a trader joins, everything in its house is destroyed: what is in chests, shelves and racks, things set down on the ground or piled up, clutter, and items lying about. Only the building blocks stay, so trader houses can't be used to skip ahead (no free steel tools).
- **Finding a trader.** When a grown-up settler comes within 16 blocks of a trader (`TraderFindRange`) and can see it, the trader leaves in a puff of smoke and a new settler takes its place, with the trader's name. Walls don't stop it when the trader is on colony land or within 6 blocks of the settler (0.135.0: before, a trader inside its house was never found). Nobody may be trading with it at the time. An Explorer far away sends them home to the Town Hall.
- **Skills for the trade.** The new settler gets a job that fits the trader's goods (`assets/createacolony/config/traderjobs.json`): its skill at level 6 to 10 with ++, a second skill at 3 to 4 with +, the job's trait, and never a trait that rules the job out. They start work at once when a hut for that job has room.
  - Agriculture: Farmer, Cook or Gatherer. Survival goods: Guard or Hunter. Building materials: Builder or Quarrier. Commodities: Courier. Artisan: Potter or Blacksmith. Furniture: Sawyer. Treasure hunter: Explorer or Prospector. Clothing: Tailor. Luxuries: Jeweller (0.113.0). Traders from other mods: a random job. (Before 0.113.0 every trader got a random job, because the trade was read wrongly from the trader's code.)
- **Goods.** Each stack the trader was selling, and its money, has a 25% chance (`TraderGoodsChance`) to go to the warehouse. The rest is lost.
- **A full colony.** They join anyway, but sit at the Tavern (or the Town Hall) and do nothing. Their window says "waiting at the Tavern for room in the colony". When the population cap goes up they start work. They don't count towards the cap while they wait.
- **The house comes down.** A builder takes the trader's house down without being asked, after any building work orders. It shows in the Builder's Hut's Work orders tab and the Town Hall as "Hilda's old house", with Cancel. The box is the structure the world placed (or the claim it had). Everything comes down from the top except natural ground, soil, plants, standing trees, snow and colony buildings. The blocks go to the builder's hut (the house was already emptied when the trader joined). Houses more than 400 blocks from the Town Hall are left (`TraderTeardownMaxDistance`). Turn it off with `TraderHouseTeardown: false`.

## Priorities tab (0.109.0)
- **Builders.** The Builder's Hut's work order tab is now called **Priorities**. Each order has a number, ^ and v to move it, **Do next** to put it at the top, and Cancel. The Town Hall's Work orders list has the same buttons, and both show the same order. Builders always take the highest order nobody else is on; "Do next" makes a builder switch at once. Trader houses are in the same list.
- **Crafting huts.** Every hut that makes more than one kind of thing has a **Priorities** tab: Cook, Potter, Smelter, Blacksmith, Sawmill, Primitive Craftsman, and the huts with a Crafting recipes tab. It lists what the hut keeps in stock (set on its other tabs), how many there are, and the order the worker makes what is short. Until you move something, the most wanted comes first (as before). Once you move a row, the worker follows your order.
- Couriers, guards and miners have no Priorities tab: they each do one kind of work.

## Gatherer: clay, seashells and reeds (0.110.0)
- The gatherer now also takes:
  - **Clay** (blue, fire and red) and **peat** where it shows on the surface. It only digs the top block the world made, so it never digs a pit, and never under water or under something solid. It leaves a hole one block deep. Turn it off with `GathererDigsClay: false`.
  - **Seashells** on beaches (not under water) and **ore bits**.
  - **Cattail, papyrus and tule tops**, cut like with a knife, so the plant stays. Turn it off with `GathererCutsReeds: false`.
- Like loose stones, none of it is taken in or within two blocks of a colony building.
- Each one has its own row on the Gatherer's Allowed to collect tab, all on at first.

## Huts make for each other, lights, foundations (0.111.0)
- **Crafting recipes tab.** Each row shows the thing on one line and what it is made from on a grey line under it. Rows grow to fit their text and never run into each other.
- **Huts make what others need.** When a hut is waiting for something the warehouse does not have, another hut that can make it makes it, on top of what its own tab keeps. The Sawmill saws planks and boards for the mine, a hut with firewood on its Crafting recipes tab splits firewood, and so on. Couriers take it straight from that hut to the one that asked. The Required resources tab says where each missing thing will come from: "Warehouse: 12", "Sawmill makes it" or "Forester's Hut makes it" (orange), or "You have none" (red) when no hut makes it. Turn it off with `HutsMakeForOthers: false`.
- **Lanterns and torches.** When the colony has no lantern, builders put up a lit torch where a blueprint has a lantern. Couriers bring a lit torch from the warehouse to every torch in a finished building that has gone out or burned out. Turn them off with `LanternTorchFallback: false` and `RelightTorches: false`.
- **Foundations.** Builders fill gaps under a building's floor with soil, down to the ground (8 blocks at most, `FoundationDepth`).
- **Upkeep.** Every 5 minutes (`BuildingUpkeepSeconds`) each finished building is looked over. If ground is inside it (it was built before 0.102.0, or the land changed) or there is a gap under it, it gets a work order and a builder digs the ground out and fills the gaps. Upkeep touches nothing else in the building. Soil or stone a player put inside a building's empty space is dug out too.

## Tailor, Jeweller and clothes (0.113.0)
- **Clothes wear out.** Worn clothes lose condition, about 1% a day (`ClothesWearPerDay`). Armor and jewellery don't.
- **Mood.** A settler with a piece below 20% (`ClothesRaggedBelow`) is in rags (-3). One who is cold is -4: the air plus the warmth of their clothes is below the world's body temperature limit, as for players. Inside an enclosed room counts 5 degrees warmer (`RoomWarmth`). Each piece of jewellery worn is +3, for up to two pieces.
- **Changing clothes.** An idle settler in rags or cold walks to the warehouse and puts on better clothes there (warmer ones when cold), leaving the old ones in the chest. One with no jewellery takes a piece to wear. Turn these off with `SettlersChangeClothes: false` and `SettlersWearJewellery: false`.
- **Tailor's Hut** (new job: Tailor, skill Tailoring, trait Fine Stitcher). Needs a knife. On its Crafting recipes tab you set the clothes, sewing kits and leather or linen armor to keep; it makes them from the game's own recipes, including the Clothier ones. It fetches worn clothes from its hut and the warehouse and mends them with twine, cloth or a sewing kit, the way players do. Leather and linen armor (gambesons, jerkins, leather armor) are mended with leather or linen. **Metal armor is never mended here.** Mending comes first; move it down on the Priorities tab to make things first.
- **Jeweller's Workshop** (new job: Jeweller, skill Jewelcrafting, trait Keen Eye). Needs a hammer. Makes the jewellery set on its Crafting recipes tab from metal bits, chains and gems. Mended and new pieces go to the warehouse.
- **Our own jewellery recipes.** 28 base game necklaces, bracelets, brooches and pins that had no recipe can now be crafted, by the Jeweller or by you, with a hammer (`recipes/grid/jewellery.json`). Each costs at least as much metal as chiselling it back gives. They were written from scratch, not taken from another mod.
- Both huts start with no building: place the block and build around it yourself, like other huts without a design.
- **Tailor's Delight** works with it: the Tailor also uses its needles and awls (it fetches them from its hut), its threads, buttons and leather bundles, and can make them. Its needles and awls count as hammers to the game, so no settler mistakes one for a hammer.

## Blacksmith, Armourer and Weaponsmith (0.114.0)
- **Three smiths.** Two new huts work like the Blacksmith's Hut, with a forge and an anvil within 8 blocks:
  - **Armourer's Hut** (job Armourer): smiths chains, scales, plates and lamellae, and puts metal armor together from them on its Crafting recipes tab (recipes with up to 32 parts, `ArmourerMaxInputs`). It mends worn metal armor from its hut and the warehouse at the anvil, with one more part of the same kind and metal (a chain for chain mail), a quarter of the armor's durability each time (`ArmorMendShare`). It never mends leather or linen armor; that is the Tailor's.
  - **Weaponsmith's Hut** (job Weaponsmith): smiths swords, spears, maces, shields, blades and arrowheads (`WeaponsmithParts`, `WeaponTools`).
  - **Blacksmith's Hut**: tools and everything else.
- **Moving over.** Until the colony has built an Armourer's or Weaponsmith's Hut, the Blacksmith still makes armor parts and weapons, so nothing changes for colonies without them. When one is built, the Blacksmith's keep-in-stock amounts for its things move to it.
- **Worn armor.** A worn piece now counts for less when guards pick armor, so a guard swaps it for a better piece in the tower or warehouse and leaves the worn one there to be mended.
- All three use the Smithing skill. A survival goods trader can now also become a Weaponsmith.
- Both huts start with no building: place the block and build around it yourself.

## Healing, the Healer and poultices (0.117.0)
- **Going home to recover.** A settler below 40% health (`RecoverBelowHealth`) goes home and lies in its bed until it is back to 90% (`RecoverUntilHealth`). In bed it heals 2 health an in-game hour faster (`RestHealPerHour`). Running from an attacker comes first, and a very hungry settler gets up to eat. Guards don't: they pull back to their tower as before. Turn it off with `RecoverAtHome: false`.
- **First aid.** Each settler carries one bandage or poultice. An idle settler with none takes the best one from a chest at home, a Healer's Hut or the warehouse. When it lies down to recover, it uses it. It sits in its own slot, so couriers never haul it off, and it drops with the settler's things if it dies.
- **Horsetail poultice.** A settler with no first aid to take makes a poultice from the game's own recipe, from what is in its home chests or the warehouse. It keeps one and leaves the rest in the chest. Turn it off with `SettlersMakeRemedies: false`.
- **Healer's Hut** (new job: Healer, skill Medicine). Its Crafting recipes tab lists the game's bandage and poultice recipes, and a new hut keeps 4 of each bandage. Bandages need linen, which the Farmer can make on its own tab. When a settler lies in bed recovering, the healer takes a bandage from the hut, walks to the bed and treats them. That heals half as much again as a bandage used on oneself (`HealerHealBonus`). Treating always comes before making things.
- **Herbs for the Gatherer.** The Gatherer picks horsetail and any other plant the game's bandage and poultice recipes take, and brings it to its hut. Each herb has a row on its Allowed to collect tab. It leaves a herb alone once the colony has 16 of it (`GathererHerbKeep`), so it does not strip the land. Turn it off with `GathererPicksHerbs: false`.
- The hut starts with no building: place the block (planks around linen) and build around it yourself.
- **Using what they carry (0.157.0).** A settler below 75% health (`UseHealingBelow`) uses a bandage or poultice it carries, wherever it is: its first aid first, then any in its inventory. It picks the smallest one that covers what it is missing, and waits for one to finish before using the next. Turn it off with `UseHealingWhenHurt: false`.
- **Gatherer makes poultices (0.125.0).** The Gatherer's Crafting recipes tab now lists the game's poultices, and a new Gatherer's Hut keeps 4 of each. It makes them from the horsetail and reed tops it picks; they go to the warehouse for settlers to take. A knife in its hut storage is used if a recipe needs one. Herbs are also found by what a plant drops now, not just the plant block.

## Repair button (0.118.0)
- **Repair** sits under Build options on every hut's Main tab, and on the Town Hall. Press it and a builder puts the building back as its blueprint has it: missing or broken blocks go back, blocks that should not be there come out, ground that got in is dug out and holes under the floor are filled.
- Chests, beds, racks, shelves, signs and paintings stay where they are. The builder asks couriers for the blocks it needs, as for any build.
- **Stop repair** cancels it. The colony gets a message when it is done. A Mineshaft Entrance has no Repair; use the Levels tab's Repair for mines.

## Recommended mods
These work well with Vintage Colonies. Install them separately.
- **Tailor's Delight** (Tels and Phiwa): more tools and materials for the Tailor.
- **Food Shelves**: settlers eat from and stock its shelves.
- **Stone Bake Oven**: the Cook bakes in it.
- **C&N Jewelry** (KenigVovan): gems to cut and set. The Jeweller makes its crafting recipes.
- **C&N Jewelry Adornments** (KenigVovan): more jewellery for the Jeweller to make and settlers to wear for Happiness.
- **Smithing Plus** (jayu): smith with metal bits, repair tool heads on the anvil and other smithing tweaks. Our smiths still use the game's anvil and recipes beside it; not tested together in game yet.
- **Coinage** (Xel): design your own coins. One can be the colony's money (Bailiff tab, Taxes & wages).

## Research and the Archive (0.166.0)

Research belongs to the whole colony. Once the Town Hall is level 2, place an **Archive** (Education tab of the Builder's Wand; recipe: planks around 2 parchment) and give settlers the new **Researcher** job. The Archive has 3 tiers. Each tier runs one more research at once (1, 2, 3) and takes two more researchers (2, 4, 6). Its placeholder build is a plain box until Aaron's own builds replace it.

On the Archive's **Research** tab, officers pick a branch (Settlement, Production, Population, Defence, Exploration) and press **Start**. Couriers bring the research's items to the Archive, then researchers study there and make Research Points. Up to 3 researchers work on one research. Research is faster with a table, a shelf or books and a light within 6 blocks of the hut block, and slower with smelters, forges, saws or a quarry within 16 blocks. **Pause** keeps the points and items. Finished research is kept even if the Archive is removed. Nothing you could build before is locked by research.

| Research | Branch | Tier | Needs | Cost | Does |
| --- | --- | --- | --- | --- | --- |
| Scribe Training | Population | 1 | - | 200 points, 8 parchment | Researchers work 20% faster |
| Organized Storage | Settlement | 1 | Warehouse | 300 points, 4 chests | Couriers walk 20% faster with a load |
| Crop Rotation | Production | 1 | Farmer's Hut | 300 points, 32 seeds | Farmers harvest 10% more |
| Basic Fortifications | Defence | 1 | Guard Tower | 300 points, 32 logs | Guards get 2 more health |
| Herbal Remedies | Population | 1 | Healer's Station | 300 points, 16 horsetail | Settlers heal twice as fast in bed |
| Field Notes | Exploration | 1 | Explorer's Hut | 300 points, 8 parchment | Explorers go 25% further |
| Irrigation Systems | Production | 2 | Crop Rotation | 600 points, 32 planks | Watering cans last twice as long |
| Specialized Craftsmen | Production | 2 | Town Hall 3 | 600 points, 8 copper ingots | Crafting jobs work 15% faster |
| Guard Training Regimen | Defence | 2 | Basic Fortifications | 600 points, 8 spears | Guards hit 15% harder |
| Cartographer's Guild | Exploration | 2 | Field Notes | 600 points, 16 parchment | Explorers walk 15% faster, gives 1 Knowledge Token |
| Bigger Village | Population | 2 | 15 settlers | 800 points, 16 linen | 2 more settlers per Town Hall level |
| Colony Expansion Protocols | Settlement | 2 | Town Hall 3 | 800 points, 1 Knowledge Token | Town Hall claim grows 16 blocks each way |

One researcher makes about 100 points a day. The nodes live in `assets/createacolony/config/research.json`; speeds and the Archive's rules are in the config (`ArchiveTownHallLevel`, `ResearchSecondsPerWork`, `ResearchPointsPerWork`, `ResearchMaxPerNode`, `ArchiveSpeedPerTier`, `ArchiveRoomRadius`, `ArchiveMissingPenalty`, `ArchiveNoiseRadius`, `ArchiveNoisePenalty`, `ArchiveNoisyHuts`).

## Farmers lay better soil (0.156.0)
- **Poor field soil gets swapped for better soil.** Put better soil in the Farmer's Hut storage (very low, low, medium, compost, high, as in the game). The farmer digs out the poor soil on bare spots of its fields and lays the better soil as fresh farmland. The worst spots go first.
- Only spots with nothing growing are swapped. After a harvest, a farmer carrying better soil lays it before sowing again. It works in winter too.
- The old soil goes back to the hut, so it can be used elsewhere.
- If better soil is only in the warehouse, the farmer asks couriers to bring some.
- Also in this version: the **farmer holds the watering can** while watering and filling it, with the player's watering animation (first made as 0.152.0; 0.153.0 to 0.155.0 did not have it).
- Config: `FarmerUpgradesSoil` (on/off).
- Not tested in game.

## Taxes and wages start at Town Hall level 3 (0.153.0)
- No taxes are collected and no wages are paid until the Town Hall is level 3 (`EconomyTownHallLevel`). Until then settlers have no "Paying taxes" or "Wasn't paid" moods, and the Church gets no tithes. The Taxes & wages page says so.

## Gatherers take bush cuttings and need a knife for grass (0.151.0)
- Gatherers now need a knife to cut dry grass. Without one, the hut asks for a knife and the gatherer picks other things meanwhile. Put a knife in the hut storage or the warehouse.
- A gatherer with a knife takes cuttings from berry bushes, the way you do (sneak and right-click with a knife). That's one cutting per bush a year, and it doesn't hurt the bush. The colony keeps 4 of each kind (`GathererCuttingsKeep`) in the hut storage for you to plant. Switch it off with `GathererTakesCuttings`.
- **Bush cuttings** is a new row on the gatherer's Priorities list, after Herbs.

## Gatherers roast cattail roots when food is low (0.150.0)
- When the colony runs low on food, the gatherer digs up cattails, papyrus and tule for their roots. It never digs out a whole patch: it leaves at least 2 nearby (`GathererRootsLeave`). Food counts as low below 2 days of food per settler (`GathererRootsFoodDays`).
- It roasts the roots on a fire pit next to the Gatherer's Hut, with firewood from the hut or the warehouse, then puts them in the hut storage. **Place a fire pit by the hut** for this.
- Roots count as **Food** on the gatherer's Priorities tab. Switch it off with `GathererDigsRoots`.
- On the gatherer's Markers tab, ore bits now have their own switch, separate from flint.
## Guards upgrade their gear (0.149.0)
- **Guards look for better gear.** When nothing needs fighting, a guard checks its tower's storage and the warehouse for a better weapon, better armor or a better shield.
- **It swaps, and puts the old one back.** The guard takes the better one and leaves what it had in the same chest, so the Weaponsmith or Armourer can mend it.
- **Better means clearly better.** A guard only swaps for something at least 15% better (`GearUpgradeMargin`), so it does not walk back for every scratch. A worn item counts for less, so a badly worn sword gets swapped for a fresh one.
- **Shields work.** A shield blocks blows from the front, like yours does. While a guard fights it holds the shield up, which blocks more often.
- **Shields and lights.** A guard with a shield keeps a torch in its bag and holds it in the dark. When it fights, the torch goes away and the shield goes up.
- Couriers leave shields at the Guard Tower, like weapons and armor.
- Config: `GuardsUseShields` (on), `GearUpgradeMargin` (0.15).
- Not tested in game.

## Farmers guard their crops and plant by season (0.147.0)
- **Animals that eat the crops get chased off.** When an animal goes for the crops on the colony's farmland, the Town Hall log says so.
- **The hunter comes first.** If the colony has a hunter, the farmer calls it, and the hunter goes after that animal before anything else, from anywhere. If the animal is still there 45 seconds later (`FarmerWaitsForHunterSeconds`), the farmer goes too.
- **No hunter?** The farmer goes after the animal with its hoe and kills it. The body is the colony's, so the hunter or settlers without a job skin it.
- **Livestock is never killed.** Animals you bred or tamed, and babies, are only pushed away from the crops.
- An animal that runs well away from the fields counts as chased off.
- **Dead crops are replaced.** The farmer clears a dead crop and sows a new seed there, one that fits the season.
- **Only crops that will live get planted.** Before sowing, the farmer looks at the weather ahead for that spot: the season, how warm the region gets, day and night. If a crop would freeze or overheat before it is ripe, it picks another seed. If no seed it has would live, it waits. A greenhouse counts as 5 degrees warmer, like in the game.
- **No planting in winter.** The farmer still harvests, weeds, waters and tills. Settlers without a job follow the same rules on their home plots.
- Config: `FarmerGuardsCrops`, `FarmerWaitsForHunterSeconds` (45), `FarmerChaseDistance` (16), `FarmerChecksSeasons`, `FarmerSkipsWinter`, `FarmerCropDamageAllowed` (0.5: a crop may take half the cold or heat that kills it), `FarmerSeasonMarginDegrees` (1).
- Not tested in game.

## Foresters clear bushes, and sticks from firewood (0.146.0)
- **Foresters clear wild bushes.** Besides felling trees, the forester breaks the wild bushes and clumps of leaves standing on the ground around its hut, nearest first. The leaves give sticks (and sometimes tree seeds), the same as when you break them.
- It leaves alone berry bushes, fruit trees, leaves you placed, and anything in or beside a colony building.
- **Allowed to collect still counts.** Turn a tree off there and the forester leaves that wood's bushes alone too.
- **New recipe: axe + firewood = 2 sticks.** Put both in the crafting grid. The axe wears down a little each time.
- The Forester's **Crafting recipes** tab now has this recipe too, so set how many sticks to keep and the forester splits firewood for them.
- Config: `ForesterClearsBushes` (on/off), `ForesterSecondsPerBush` (3), `ForesterBushMaxBlocks` (48: bigger clumps are left as part of a tree).
- Not tested in game.

## Gatherer priorities (0.145.0)
- The Gatherer's Hut has a **Priorities** tab. At the top, **What to collect first** lists the kinds of thing the gatherer collects. Use ^ and v to change the order.
- The usual order is: Colony requests, Ore bits, Food, Herbs, Clay, Peat, Reeds, then Stones, flint, sticks and shells.
- **Colony requests** are things a hut is waiting for, like dry grass, clay or flint for the builder.
- The gatherer takes the nearest thing of the highest kind it can find. Crafting (like poultices) is listed below, as before.

## Lights for every worker (0.144.0)
- **Every grown settler carries a light.** When it gets dark, a settler without one walks to its hut storage or the warehouse and takes one into its offhand. It holds it up like a player and it **lights up the ground around it** as it walks, the same as a torch in your hand.
- **Lanterns first.** A settler takes a lantern if there is a free one, and swaps its torch for one when a lantern turns up later.
- **Builds come first.** A lantern is only free when no build still needs it. While your work orders need more lanterns than the warehouse and the Builder's Huts hold, settlers leave lanterns alone and use torches, and anyone already carrying a lantern brings it back and takes a torch.
- Builders always carry torches, so their lantern is never mixed up with the build's.
- A shield takes the offhand; that settler goes without a light.
- Config: `CarryTorches` (on/off) and `LightFetchRange` (64 blocks: how far a settler walks for its light).
- Not tested in game.

## Requests tab, Courier's Station and better fuel (0.142.0)
- **Requests tab on every hut.** It lists what the hut and its worker asked the couriers for (the potter's firewood, dry grass and sticks, a builder's planks, a tool). Each row shows how many were brought, how many are in the warehouse, and what is happening: a courier bringing it, waiting for a courier, the Gatherer cutting grass for it, another hut making it, or nobody has it.
- **Huts ask for a few spare.** A hut asks for about a quarter more than it needs, at least 1 and at most 8 (a potter needing 4 firewood asks for 5). Once what it needs is there, it does not wait for the spare. Not for tools, Minimum stock or your own requests. Config: `RequestSparePercent` (25, 0 turns it off) and `RequestSpareMax` (8).
- **Courier's Station.** A new work station for the Warehouse. Craft it from a chest and 4 planks: plank, chest, plank on top, and a plank under each end. Put it inside the Warehouse building, or near the Warehouse block. Its **Couriers** tab shows how many couriers there are, what each is doing, where they take things from and to, and what they carry. Settlers without a job show up while they help.
- **Pit kilns use the best fuel there is**: coal, then charcoal, then peat, then firewood. Config: `KilnPrefersBestFuel`.
- **Smiths prefer charcoal** for the forge, then coke, black coal and brown coal, depending on what there is.
- Not tested in game.

## Penny coins and Coinage coins (0.141.0)
- The copper coin now looks like a 1p coin: round, with a rim, a portcullis on one side and a "1" on the other.
- **Coinage support**: design your own coin with the Coinage mod (Xel), hold it, and press **Use the coin in my hand** on the Bailiff tab's **Taxes & wages** page. From then on only that coin is the colony's money. Taxes, wages and the Church's tithes use it, and the minter strikes copies of it from ingots of its metal: 10 per ingot (`MintCoinsPerIngot`), up to 100 in the Mint (`MintStartKeep`). **Use the Mint's coins** switches back.
- Which other mods' items count as coins is set in `OtherCoins` (default `coinage:coin` and `coinage:coin-*`). Coinage is not bundled; it is a separate mod.
- Not tested in game. Coinage doesn't publish its item codes, so if its coins aren't accepted, add their code to `OtherCoins`.

## Crafting recipes on every hut, and your own recipes (0.140.0)
- **Every hut with a worker now has a Crafting recipes tab**, the crafter huts too (Sawmill, Smelter, Blacksmith and the rest). Builders, guards and priests don't, since they have their own work.
- **Teach recipe.** On the tab, press **Teach recipe** (officers). Put what it takes in the 3x3 grid and what it makes in the slot on the right, then press **Teach**. Every hut of that kind in the colony learns it.
  - Open your bag with E and click your items into the slots. Your items stay in your bag; the slots only show copies.
  - Click adds one more, right click puts the whole stack, an empty hand takes one off.
  - Or hold an item and press **Add held item** (for the grid) or **Makes held item** (for the result).
  - Tools (anything with durability) wear down instead of being used up. Any tool of that kind will do (any axe). Up to 2 tools.
  - Turn on **Any type will do** to let any wood or any stone count, not just the one you put in.
- Your recipes show on the tab with "(your recipe)". Set how many to keep, like any other row. **Forget** on the Teach recipe page removes one.
- The worker fetches the things and tools from its hut storage, or asks couriers for them from the warehouse.
- Server owners can turn teaching off with `CustomRecipes: false`. A kind of hut can learn up to 30 (`CustomRecipesPerHut`).

## Craftsmanship tabs (0.139.0)
- The Builder's Wand's **Craftsmanship** tab now has its own tabs along the top:
  - **Wood working**: Sawmill
  - **Food stuffs**: Cook's Hut
  - **Metallurgy**: Smelter, Blacksmith, Armourer, Weaponsmith, Royal Mint
  - **Primitive**: Potter's Hut, Primitive Craftsman
  - **Luxury**: Tailor, Jeweller
- The list remembers the last one you looked at.

## Gatherers mark what they see on the way (0.138.0)
- Gatherers now look around the whole time they walk, not only where they stop. Every few blocks they mark the berries, fruit trees, mushrooms, wild crops, herbs, reeds, clay, peat, seashells and flint they can see, for picking later.
- Set to **Only look around**, they see twice as far.

## Church, religion and work stations (0.137.0)
- With no Church in the colony there is no religion at all: no services or missed services, no tithe, no charity, no church thoughts or learning boost, and Faith is hidden on the Stats tab (0.154.0). Removing the Church takes away the church thoughts it left.
- A new hut, the **Church** (Education), and a new job, the **Priest**. At Church level 3 one priest can be made the **Bishop** (Manage workers, click their role). Level 4 takes a second priest. The Church has no buildings yet, so build your own around the block.
- Every settler has **Faith**, shown on their Stats tab: devout, faithful, doubting or faithless.
- **Services** every 7 days at 9:00 for 2 hours (`ChurchServiceEveryDays`, `ChurchServiceHour`, `ChurchServiceHours`). Settlers with some Faith walk to the Church and stand facing the block while the priest leads. Going gives a "Went to church" thought, raises Faith, and gives +15% skill XP for a day (`ServiceLearnBoost`). There is a 0.1% to 0.5% chance (more with more Faith) of a free level in a random skill. Devout settlers who miss one are unhappy.
- **Tithe**: 0, 5 or 10% of each wage goes to the church on payday (Church tab). Doubting settlers give half, faithless a quarter. At 10% settlers with little Faith grumble.
- **Charity**: if the colony has no food, the church spends coins on bread and apples for the hungry. It buys bandages for the Healer's Station, pays weekly for orphans and for settlers sleeping in beds inside the Church (beds there are given out last), and they feel looked after.
- **Funerals**: the priest holds one at the grave of anyone who died in the last 10 days. Family and friends grieve less.
- **Records**: put blank books and an ink and quill in the Church's storage. After 8 new events the priest writes them into a book, signed, and puts it in the Church's storage.
- **Church paintings**: 5 new original paintings (Cross at Sunset, Walking on the Water, The Crucifixion, Christ in Glory, The Heavens Opened), made from a blank canvas and dyes like the others. Put them in the Church or the warehouse and the priest hangs them on the Church walls. Settlers do not take them home.
- **Work stations**: a new kind of block with no building of its own. It goes inside another building's build zone and adds a job there. The first is the **Healer's Station**, which replaces the Healer's Hut and only goes inside a Church. It uses the Church's storage and the Church's level. Old Healer's Huts still work but can no longer be made.
- The Church tab shows Faith, the next service, the tithe, coins, orphans, funerals waiting and the last 30 days of spending.

## Royal Mint, taxes and wages (0.136.0)
- A new hut, the **Royal Mint** (Craftsmanship), and a new job, the **Minter**. The minter strikes copper coins with a hammer: 1 copper ingot makes 10 coins. Set how many to keep on its Crafting recipes tab; a new Mint keeps 100 (`MintStartKeep`). You can strike coins by hand too.
- The **treasury** is the coins in the Mint, the Town Hall's chests and the warehouse.
- **Payday** is the first day of each in-game month. The bailiff first collects taxes, then pays wages. Nothing happens until you have a Royal Mint or coins in the treasury, and only while a bailiff is on duty.
- **Taxes** replace the old daily rent in rusty gears. Pick the rate on the Bailiff tab's new **Taxes & wages** page, 0% to 50% (10% to start). The bailiff takes that share of every settler's purse into the Town Hall's chests. Every settler has a "Paying taxes" thought: -1 mood for each 5% (`TaxMoodPerPercent`).
- **Wages**: each settler with a job gets a wage from the treasury: 5 coins by default, 4 for couriers and gatherers, 7 to 8 for builders, guards, healers and smiths, 10 for the bailiff (`Wages`, `WageDefault`). A settler who gets paid has a "Got paid" thought for a day. One who doesn't get their full wage has "Wasn't paid", -4 mood (`UnpaidMood`), until a payday pays them.
- Settlers keep their coins in their purse, shown on the settler's Stats tab with their wage. Spending them comes later.
- The page shows the treasury, what wages cost a month and the next payday. The **Ledger** now has columns for taxes and wages.

## Generated packs removed (0.163.0)
The Oak & Shale pack (0.162.0) and the stretched Dev's Builds levels (0.161.0) are gone: Aaron is designing
every building himself. The pack list is Medieval Oak and Dev's Builds again; players and buildings saved on
Oak & Shale are moved as for any removed pack.

## New Dev's Builds huts (0.134.0)
Aaron's new buildings in the Dev's Builds pack: Smelter level 1, Blacksmith level 1, Sawmill levels 1 and 2,
Gatherer level 1, and two Forester (Lumberjack) level 1 types, Small and Large (pick with Previous/Next type).
Blocks from mods you don't have (Food Shelves, w4rd0s furniture) are skipped. Not tested in game.

## Kitchen: cook from what's on hand, crocks and bowls (0.124.0)
- The Kitchen tab now shows the food the cook has on hand (cook, Cook's Hut and warehouse), and only the dishes it can make from it right now: fire pit food, pot meals and oven bakes. Pot meals are named from the ingredients the cook would use.
- The cook no longer tries to keep 8 of every food. Until you pick dishes, it makes a few of each dish it can (`KitchenAutoKeep`, 4, and `KitchenAutoMealKeep`, 4 servings).
- Change an amount to pick dishes yourself. The dishes shown keep their amounts, and from then on the cook only makes your picks. A picked dish it can't make right now stays on the list as "Not enough food to make it now". **Let the cook choose** goes back to the cook choosing. Old huts that kept 8 of everything go back to the cook choosing.
- The cook pours each pot meal into crocks (4 servings each) and uses the pot again. It keeps `CookSpareCrocks` (2) empty crocks on hand. When the colony runs short, the Potter's Hut makes more on top of its Pottery tab, which says how many are wanted.
- Every adult settler carries a bowl in its food slots (`CarryBowl`). After a meal at a crock or pot it fills the bowl with a serving and eats it later, then keeps the empty bowl. Settlers without one take one from home, their hut, the warehouse or a Cook's Hut. The Potter's Hut makes bowls when there are not enough.
- Rotten food in a bowl, crock or pot gets washed out in water, as you do by dropping it in water. Settlers wash the bowls they carry, and the cook washes the crocks and pots in its hut. Water must be within `WashWaterRadius` (32) blocks. The rot is kept for compost.


## Settlers swim, climb, greet you and hold their food (0.131.0)
- In water, settlers now swim like a player instead of walking upright. Standing still in deep water, they tread water.
- On ladders, settlers use the player's climbing animation.
- Settlers walk and stand like a player now. When running away, fighting back or rushing to a fire, they sprint.
- A settler with nothing to do waves, nods or bows when you come near. Each settler greets each player at most once every 5 minutes.
- Happy settlers sometimes cheer or laugh. Unhappy ones cry, shrug or facepalm. One having a breakdown rages.
- Outdoors in the cold (below 2 °C), a settler with nothing to do hugs itself to keep warm.
- When eating, settlers hold the food in their hand. Their tool goes back in their hand after.
- Sheltering from a storm, they sit on the floor like a player.
- Config switches: `SwimAndClimbLikePlayers`, `GreetPlayers`, `ShiverWhenCold`, `HoldFoodToEat`.

## Gatherers mark what they find, and look-only mode (0.130.0)
- While picking or looking around, the gatherer now also marks herbs like horsetail, cattails and other reeds, clay, peat, seashells and flint on the Colony markers map. It marks them even when it isn't allowed to pick them.
- Each has a switch on the Gatherer's Hut's **Markers** tab, all on to start.
- New on the same tab: **What the gatherer does**. **Gather and look** is how it worked before. **Only look around** picks nothing and walks around marking the map more often. **Only gather** never walks off just to look, but still marks what it sees while picking.

## The Bailiff moves stuck workers (0.129.0)
- When a worker can't do their job for 2 in-game hours (`BailiffStuckHours`), the bailiff sends them to help at another hut with a free place for a day (`BailiffLendDays`), then sends them back. Stuck means things like no pickaxe, nothing left to pick, no trees left, or everything in stock.
- They go to the hut whose job they are best at. If no hut has room, they help carry things like a settler without a job.
- Their own place is kept for them, so the bailiff won't give it to someone else. If you change their job yourself, the loan ends.
- Guards, the healer, builders and couriers always stay put.
- Switch it off on the Bailiff tab: **Move workers who are stuck**. The Ledger counts the workers lent each day, and the Estate report shows who is away and when they come back.

## Settlers get into their beds (0.128.0)
- A settler that can't find a way to its bed, but is close to it (`BedFindWayRange`, 16 blocks), now gets into it anyway. Before, it lay down on the floor wherever it got stuck, often under an upstairs bed.
- Settlers no longer share a double bed with someone who isn't family when a bed is free. They take an empty bed first, and move to one in the same home once it's free (say its owner died).
- A settler working far away keeps its bed. Before, it could lose it when the bed's land wasn't loaded.
- A settler that slept on the floor somewhere no longer pops onto its bed in the morning.

## The potter uses several pit kilns (0.155.0)
- The potter digs and uses more than one pit kiln: 2 at hut level 1, one more each level, up to 4 (`PotterMaxKilns`). It fires one load while it shapes the next.
- Each kiln takes as many pieces as the game allows: 4 bowls, crocks or other small pieces (they can be mixed), 2 ingot molds, or a stack of bricks or shingles up to the game's limit.
- The hut window shows every kiln, for example "Kilns (3): 1 firing, 1 being filled, 1 empty".

## The Bailiff (0.127.0)
- A new job, the **Bailiff**. On the Town Hall's Citizens tab, give one settler the Bailiff job (only one per colony). They work from the Town Hall and walk around checking on the huts and fields.
- The Town Hall has a new **Bailiff** tab with three pages: the bailiff and their rules, the **Ledger**, and the **Estate report**.
- **Wanderers**: pick what the bailiff does when a wanderer reaches the Town Hall: Ask me (you decide, as before), Welcome everyone, Welcome them if a bed is free (the default), or Turn everyone away. The bailiff answers after about an hour (`BailiffAnswerHours`).
- **Jobs**: the bailiff gives settlers without a job work at huts with a free place, picking whoever is best at that job (skill level and passions). Switch it off on the Bailiff tab.
- **Fields and work**: while the bailiff is on duty, farmers work 15% faster (`BailiffFarmBoost`) and everyone else 5% faster (`BailiffWorkBoost`).
- **Rent**: replaced by monthly taxes in coins in 0.136.0 (see Royal Mint, taxes and wages).
- **Ledger**: rent, wanderers welcomed or sent away, and jobs given, for each of the last 30 days.
- **Estate report**: every hut, who works there and what they are doing or waiting on (for example "nothing ready to pick"), the fields and who has no job.
- Nobody likes being watched: every other settler has a "The bailiff is watching" thought, -3 mood (`BailiffMood`).
- The bailiff stops while knocked out or too unhappy to work.

## Remove a colony marker (0.126.0)
- On the world map, right-click a Colony marker and press **Remove**. Officers and the owner can do this.
- Settlers won't mark that spot again with the same thing. Other markers nearby stay.
- Changed your mind? An Explorer's or Gatherer's Hut Markers tab has **Mark removed spots again**.
- Grave markers can be removed the same way.
- Since 0.132.0 the Remove window shows on top of the open map.
## Settlers put out fires (0.123.0)
- Fire spreads through grass and wood. If something catches fire on your colony's land, the Town Hall log says "Fire near ...!" and up to 3 settlers within 64 blocks come running. They even get out of bed.
- Each one needs a watering can. A settler without one takes it from a chest at home, the warehouse or a hut (the Farmer's Hut keeps them). An empty can gets filled at the nearest water first.
- They pour water on each burning block until the fire is out, then keep the can for next time.
- No watering can anywhere? The log tells you. Make one from clay and fire it in a pit kiln, then put it in the warehouse.
- They put out loose fire and burning piles. They never put out a lit pit kiln, fire pit, forge, bloomery or charcoal pit.
- The potter now cuts the grass and plants 3 blocks around its pit kiln before lighting it, so the fire can't spread. Crops are left alone.
- Config: `Firefighting`, `FirefightingPiles`, `FireAlarmRadius`, `FirefightersPerFire`, `FireWaterSearchRadius`, `FirePourSeconds`, `KilnClearRadius` (0 turns the grass cutting off).

## Gatherer cuts grass for dry grass (0.121.0)
- When huts are waiting for dry grass and the colony doesn't have enough (warehouse, Gatherer's Huts and gatherers' packs together), the gatherer cuts tall grass first, before it forages.
- It cuts like a knife does, so short grass is left and grows back. It looks within 16 blocks of itself (`GrassSearchRadius`), then near its hut, and stays inside the hut's range. Never in or beside a colony building.
- It stops once there is enough. The Required resources tab says "Gatherer's Hut makes it" for dry grass.
- The Allowed to collect tab has a "Grass for dry grass" row. Turn it off everywhere with `GathererCutsGrass: false`.

## Config (`ModConfig/createacolony.json`)
- `Permadeath` (default `true`): settlers who die are gone. Set `false` to have them knocked out and recover after `DownedRecoveryDays`.
- `StartingSettlersMin` / `StartingSettlersMax` (default 3 / 4).
- Hunger: `SaturationMax`, `HungerPerDay`, `EatBelowFraction`, `StarvationDamagePerHour`.
- Sleep hours: `SleepStartHour`, `SleepEndHour`. Storm warning: `StormWarningHours`.

## Layout
- `src/CreateAColonyModSystem.cs` registration, VS Village guard, commands
- `src/ColonyConfig.cs` server config
- `src/Colonies/` Colony data (protobuf) and `ColonyManager` (save/load, claims, members)
- `src/Settlers/` `EntitySettler` and the `colonycitizen` behavior
- `src/Block/` Town Hall block and block entity
- `assets/createacolony/` entity, block and lang JSON

## Known TODOs
- Most hut blocks have simple placeholder models (0.57.0); only the Town Hall and the Farmer's Hut have finished ones.
- The Town Hall window, the warehouse and courier, and jobs come in milestones 3 to 6.

## Blueprints

Buildings grow from blueprints, which are ordinary WorldEdit exports. Build the structure in a
creative world with its hut block where the hut should stand, select it with WorldEdit, and run
`/we export <name>`. Copy the file from `VintagestoryData/WorldEdit/` to
`VintagestoryData/ModConfig/createacolony/blueprints/<pack>/<type>/level<N>.json`, where `<pack>` is
`medievaloak` or `devsbuilds`, `<type>` is the building (`townhall`, `builder`, `farmer`, `forester`,
`cook`, `potter`, `residence`, `guardtower`, `tavern`, `warehouse`, `prospector`, `miner`, `quarry`) and
`<N>` is 1 to 5. `/colony reloadblueprints` picks up changes without a restart. Blueprints in ModConfig
override the ones shipped in `assets/createacolony/config/blueprints/`.

### Asset packs (0.46.0)

Blueprints come in **asset packs**, as in MineColonies. The first time you use the Builder's Wand it
asks which pack you want; every building you place after that is built from that pack. The
**Change pack** tab at the bottom of the wand's list picks another one. A building keeps the pack it was
placed in for every level, also after you change packs. Your choice is saved with the world, per player.

- **Medieval Oak** (`blueprints/medievaloak/`, the default since 0.48.0): MineColonies' Medieval Oak style,
  converted from Aaron's copy of its `.blueprint` files by `tools/blueprints/convert_minecolonies.py`.
  Every building, levels 1 to 5 (the Tavern has 3, as in MineColonies), and the Supply Camp. MineColonies
  has no potter, prospector or quarry, so those use its closest workshops: the Glassblower for the
  Potter's Hut, the Sifter for the Prospector's Hut and the Stonemason for the Quarry. What our jobs need
  and MineColonies does not have is added: a fire pit in the Tavern (in place of an oven in the Cook's Hut
  when it has none), the Mine waypoint at the bottom of the Miner's shaft ladder, the Quarry waypoint 8
  blocks beyond the Stonemason's north wall so the pit misses the building, and a bed and a weapons
  chest in Guard Towers without them. Some buildings are open-sided in MineColonies and have no door, so
  they are placed the way they were drawn. The Warehouse's storage racks become chests, but only those
  within the warehouse block's reach (`WarehouseRadius` 5, `WarehouseHeight` 3) count: 10 to 24 of them.
  Pictures of every level are in `tools/blueprints/previews/medievaloak/`.
  Since 0.66.0 MineColonies' alternative Medieval Oak buildings are part of this pack too, as the
  **Alternative** type (`blueprints/medievaloak/<type>/alt/`) of the Town Hall, Builder, Farmer, Forester
  (Lumberjack), Cook, Residence, Guard Tower, Warehouse and Miner: pick it with Previous/Next type in the
  placement window, as with the Small and Medium houses. Buildings and players that were on the old
  Medieval Oak (Alt) pack move to Medieval Oak, with the Alternative type.
- **Dev's Builds** (`blueprints/devsbuilds/`): Aaron's own buildings: Town Hall 1 to 4, Builder's Hut 1
  to 4, Guard Tower 1 to 5, Cook's Hut 1 and 2 (new ones in 0.60.0) and Residence 1 in two types:
  Medium and Small (0.60.0; the older Standard house was removed in 0.60.1), and since 0.134.0 Smelter 1,
  Blacksmith 1, Sawmill 1 and 2, Gatherer 1 and Forester (Lumberjack) 1 in two types: Small and Large.

Since 0.64.0 a pack never borrows another pack's design: when your pack has no design for a building, the
wand places just its block (no preview, no build order), so you can build your own building around it. The
hut works at level 0 like any other.

The old **Testing** style and its sample blueprints were removed in 0.46.0. Buildings that were placed
in it keep working: Town Halls and Builder's Huts (built from Dev's Builds since 0.26.1) move to Dev's
Builds, everything else to the default pack, which builds its next level. The 12th Century Inspired
(0.46.0) and KCD2 Inspired (0.47.0) packs were removed in 0.48.0 the same way: their buildings move to
Medieval Oak, and players who had picked one are asked for a pack again. Since the Warehouse now
has levels too, it still works as soon as it is placed, and keeps its land claim before it is built.

The placement window's **Level down** and **Level up** buttons flip the preview through every level
of the building, so you can see how big level 4 gets before you place level 1.

**Building types (0.60.0).** A building can come in several types at the same level, such as a small
or a medium house. Each type is a folder inside the building's folder
(`blueprints/<pack>/<type>/<variant>/level<N>.json`, for example `devsbuilds/residence/small/level1.json`);
the files right in the building's folder are the Standard type. The building list says how many types a
building has, and the placement window shows the type with **< Previous type** and **Next type >** to
switch between them, the outline changing as you go. The type can be changed until the building is
ordered, and it keeps its type for all its levels (a type with only level 1 stops at level 1). A new
type folder needs no code: its name is shown with a capital (`large` shows as "Large"), or from the lang
entry `variant-<name>` when there is one. Folder names are lower-case letters and numbers only.

Doors in a blueprint are hung the way they were in the export, top half included. The first door
also marks the building's front: when you place the hut, the building is turned so that side faces
you. Soil in a blueprint counts as built on any soil (grassy or bare, rich or poor), and filling a
hole in the ground costs any soil; rock, gravel or sand already there also counts, so builders never
dig out stone to put dirt in its place. Burned-out torches in a blueprint are built as lit ones, and a
torch counts as built whether it is lit or burned out. Items lying on the ground (ground storage) are
left out. Empty space below the hut's floor is dug out only when the blueprint has blocks both under
and over it (a cellar); elsewhere the ground below the floor is left alone.

**Wattle and daub (0.56.0)** is built the way a player builds it. Where a blueprint has a daub block
(`daub-tan-wattle`, `daub-tan-cracked` or `daub-tan-normal`), the builder puts up a wattle fence, puts
raw daub on it for the first coat ("wattle"), then more raw daub for "cracked" and again for "normal",
stopping at the stage the blueprint shows. How much raw daub each coat takes comes from the game's own
blocks (`daubUpgradeAmount`), and the Builder's Hut lists it under Items needed. When the colony allows
any wood or stone, any colour of raw daub will do: the wall takes the colour of the first coat, and later
coats use that colour, as the game requires. Otherwise the builder asks for the blueprint's colour. A
smoother coat than the blueprint's also counts as built, and a wattle fence or half-done daub where the
blueprint has daub is never torn down.
Since 0.56.1 builders also vary it: each daub block gets its own stage (wattle, cracked or smooth),
picked from where the block stands, so a building placed many times looks a little different each time.
The pick never changes for a spot, so finished walls stay finished. Set `RandomDaubStages` to false in
`ModConfig/createacolony.json` to build the blueprint's stages exactly.

### Converting MineColonies blueprints

`tools/blueprints/convert_minecolonies.py <style folder> [--library <out folder>]` (Python 3 with Pillow)
turns a MineColonies style (`.blueprint` files, gzipped NBT) into the Medieval Oak packs. Each Minecraft
block becomes the nearest block from Aaron's own exports (and a few plain variants of them, such as
chests, other fence joins, ladders on other walls) in `tools/blueprints/kit.py`: oak for every wood,
shale for every stone (natural stone as cobblestone, since rock cannot be carried), light mud brick for
plaster and pale bricks, oak plank stairs for Domum Ornamentum shingles, and the infill of its timber
frames as the frame block. Glass, carpets, plants, signs, redstone and other things with nothing close
are left out, and the run lists them per building. Structurize's "keep what is there" placeholders are
left out and its "solid ground" placeholder becomes soil. Beds lie the two ways the kit knows (hay beds
east to west, wooden beds north to south), in the same two blocks.

Every level is checked before it is written: one hut block with something under it and nothing on it,
doors with their top half and saved direction and nothing standing in their way, ladders, torches and
lanterns with something behind or under them, whole beds, and the work blocks the hut needs. Loose
furnishings in a doorway are taken out and planks go behind a ladder with nothing to hang on. It draws
all levels of each building, whole and cut away, in `tools/blueprints/previews/<pack>/`, with a list of
what each level needs. With `--library` it also writes every blueprint in the style, the buildings this
mod has no hut for too, as a plain WorldEdit export for `/we import`.

The in-game `/colony importmc` (below) does the same for one file, matching each block against the
installed game instead, so it can use other woods and rocks.

`tools/blueprints/import_devsbuilds.py <folder>` copies Aaron's exports into Dev's Builds (files named
like `TownHallLevel1.json`, `GuardTowerLevel5.json`, `CooksHutLevel2.json`; a word after the level number
is a building type, so `HouseLevel1Small.json` goes to `residence/small/level1.json`). It checks the same
things, can add a hut block where an export has none (`HUT_SPOTS`), and warns when a level would line up better a few blocks off, which
is how an export with the hut block in the wrong place shows up.

### VS Roofing roofs

With the [VS Roofing](https://mods.vintagestory.at/vsroofing) mod installed, a blueprint can have a
VS Roofing roof. Build the roof in game as usual and export it with the rest of the building. A builder
copies each roof block whole, frame, slope, materials and fill included, the way a WorldEdit paste
does, and pays for what the block holds: the frame item, the binding held in the other hand for stick,
bamboo and firewood frames (any of the config list `RoofBindings` will do: flax fibers, cattail tops,
papyrus tops or wild vines), the roofing materials, and any fill block or sod plant. These show up in
the Items needed list and couriers bring them. The roofing materials must be the exact kind in the
blueprint (oak planks stay oak planks, even when any wood is allowed). Without VS Roofing the roof
blocks are skipped, like any block from a mod that is not installed.

The config list `BlueprintBlockDetails` (default `["vsroofing:*"]`) says which blocks are copied with
their saved details. Do not add chests or other containers: whatever they held in the export would
come for free.

### Furniture, banners and food shelves (W4RD0's Furniture, Heraldry : Banners, Food Shelves)

With W4RD0's Furniture (1.5.1), Heraldry : Banners (2.0.0, with Heraldry) or Food Shelves (3.1.1) installed,
blueprints can hold their furniture, banner poles and food shelves. The mods are optional: without them those
blocks are skipped. A builder
places these blocks the way a player does, from their item, so the block sets itself up: a double bed
or a wide table puts down its other half, a shelf its other cells, and the block takes the item's wood
and colours. The builder pays for the furniture item in the wood and cloth colours the blueprint has
("Rustic table (Oak)" in Items needed; "(any wood)" when the colony allows any wood), and for a pole's
banner with the same design, which has to be made at the Heraldry workbench. What furniture held in the
export (a cupboard's contents, a shelf's display) is not copied. Food Shelves blocks work the same way: a
builder pays for a "Bread shelf (Oak)" in the blueprint's wood, and the food that stood on it in the export
is not copied. The config list `BlueprintPlaceAsItem` (default `["w4rd0sfurniture:*", "heraldrybanners:*",
"foodshelves:*"]`, added to older config files that still had the old default) says which blocks are placed
this way; any block that takes its wood from item attributes (Attribute Rendering Library) is placed this way too.

Settlers sleep in W4RD0's Furniture beds as well as vanilla beds. A double bed sleeps two, each on their
own half, and counts as two free beds for wanderers.

### Ordering buildings and tools

Right-click a hut block to open its window and order a level; the Town Hall's own upgrade is on the
Town Hall's Actions tab. The Builder's Hut window lists every work order and, under Items needed,
what its builder still needs with have/need counts. Builders clear soil and sand with a shovel, wood
with an axe and stone or ore with a pickaxe, of at least the block's mining tier, and wear the tool
down as they work. Put tools in the hut's storage or the warehouse; couriers bring missing ones. Set
`BuildersNeedTools` to false in `ModConfig/createacolony.json` to turn this off.

A builder works on each block from the ground below it, beside it, so roofs and the tops of walls are
built from underneath. It can reach `BuilderReach` (12) blocks; a block further than that from anywhere
the builder can stand shows as unreachable.

### Foresters and farmers

Place a **Forester's Hut** (planks, sticks and firewood) or a **Farmer's Hut** (planks, sticks and
seeds) in the colony and give a settler the Forester or Farmer job in the
Town Hall. Each works from the free hut of its kind nearest the start of the list.

- The forester needs an axe. It fells wild trees within `ForesterRadius` blocks of its hut (never
  placed logs), nearest first, with the game's own axe logic, and plants a tree seed or sapling of
  the same wood on the stump. It also clears wild bushes and leaves on the ground near its hut, which give sticks.
- The farmer needs a hoe. A **Field marker** (dry grass on two sticks, a scarecrow two blocks tall that needs
  the block above it free) marks out the square of
  `FieldRadius` blocks around it. The farmer harvests ripe crops and sows them again, sows bare
  farmland, and tills soil, up to `FieldsPerFarmer` fields each. Right-click a marker with seeds to
  choose its crop; sneak and right-click it with an empty hand to allow any.
- The farmer also pulls weeds on its fields, waters dry crops with a watering can (refilled at the
  nearest water) when its moisture drops below `FarmerWaterBelow`, spreads fertilizer from the hut on
  farmland whose nutrient drops below `FarmerFertilizeBelow`, and picks the crop whose nutrient the
  soil has the most of (crop rotation). It picks ripe berry bushes and fruit trees within
  `FarmerOrchardRadius` blocks of its hut.
- Both carry what they gather to the hut's storage, where couriers pick it up for the warehouse. They
  keep their tool and a stock of seeds (`ForesterSeedReserve`, `FarmerSeedReserve`); seeds over that
  go to the warehouse too. Missing tools and seeds are requested from the warehouse.

### The potter: from the stone age to the copper age

Place a **Potter's Hut** (planks, sticks and clay) and give a settler the **Potter** job. The potter
works with the game's own pottery, by hand:

- It shapes clay on the game's **clay forming block** next to the hut, one layer at a time, following
  the same clay forming recipes a player picks from, using whatever clay fits the recipe (blue, fire or
  red). Each piece takes as much clay as the handbook says.
- It digs a one-block **pit kiln** in soil, gravel or sand near the hut (solid, non-flammable ground
  around it and open sky above, as the game requires), lays up to a full load of raw pieces in it, adds
  the game's pit kiln fuel layer by layer (dry grass, sticks and firewood, read from the game's own pit
  kiln), lights it, and takes the fired pottery out when the game has fired it. Rain that puts the
  kiln out just means it gets lit again.
- The hut window's **Pottery** tab lists every piece the game's clay forming recipes make that fires
  in a pit kiln, with how many the colony has (warehouse, hut, potter and kiln) and how many to keep in
  stock. A new hut keeps 1 crucible and 2 ingot molds (`PotteryDefaultKeep`), what it takes to smelt
  copper; officers change it.
- Put clay and kiln fuel in the hut's storage or the warehouse; the potter asks couriers for what it
  is missing. Fired pottery and dug soil go to the hut storage and on to the warehouse.
- Config: `PotterSecondsPerLayer`, `PotterSecondsPerKilnStep`, `PotterClayReserve`,
  `PotterClayRequest`, `PotterSpotRadius`, `PotterMaxKilns`, `PotteryKeepChoices`, `PotteryDefaultKeep`.

### New settlers: the Tavern and wanderers

- A **Tavern** (planks, sticks and a rusty gear) is placed with the Builder's Wand like any hut. Once
  level 1 is built, a few people looking for work stop by each in-game day (`TavernRecruits`). Its
  **Recruits** tab lists them with what they ask in rusty gears (`RecruitCostMin`/`Max`,
  `RecruitCurrency`); **Hire** takes the gears from your inventory, then from the Tavern's storage, and
  the new settler steps out of the Tavern. Hiring is free in creative mode.
- **Wanderers** now and then turn up near the colony border and walk to the Town Hall
  (`WandererEveryDays`, sooner with a built Tavern: `WandererEveryDaysWithTavern`), one at a time and
  only while there is a free bed (`WanderersNeedFreeBed`). Members get a chat message. An officer
  welcomes them or turns them away on the Town Hall's Citizens tab; one nobody answers moves on after
  `WandererWaitDays`. `/colony wanderer` brings one now (admin, for testing).
- **Permadeath** is a switch on the Town Hall's Settings tab for the colony owner; the config
  `Permadeath` is only the default for new colonies.
- Settlers get names from `config/settlernames.json`.

### Placing buildings with the Builder's Wand

As in MineColonies, the Town Hall, huts and the Warehouse are not placed by hand (that just says to
use the wand). Craft a **Builder's Wand** (flint on a stick), keep the building's block in your
inventory, and right-click the ground with the wand. Pick the building from the list and the outline
of its highest level appears there: blue where free, red where something is in the way that the
builder will clear, yellow where the building's block goes. The window beside it turns the building
and nudges it forward, back, left, right, up or down (as you look). Esc closes the window but leaves
the outline up so you can walk around it; right-clicking with the wand, on a block or in the air,
brings the window back. **Accept** takes one of the building's blocks from your inventory and puts it
down; without one it tells you that you need one. **Change building** goes back to the list.

The list is sorted into tabs down its left side, and it opens on the tab you used last:
**Fundamentals** (Town Hall, Builder's Hut, Tavern), **Agriculture** (Farmer's and Forester's
Huts), **Craftsmanship** (Potter's and Cook's Huts), **Infrastructure** (Warehouse), and
**Education**, **Military**, **Walls** and **Decoration**, which are empty until their buildings
are added. The last tab, **Change pack**, picks the asset pack (see Asset packs above), and the
placement window shows which pack the building uses.

Right-clicking an existing hut, the Town Hall or the Warehouse with the wand shows its outline and
lets you turn and move the building (up to 8 blocks up or down, 16 to the sides) until its first
level is ordered; **Build here** orders it and **Not here** gives a hut back.

The preview is a **ghost** of the building: its real blocks, see-through, as the WorldEdit import tool
shows a schematic (roofs, doors and furniture included). Blocks in the way are still marked red and the
building's block yellow. Every turn, move or level change redraws the ghost where the building now stands. The game's own "preview transparency" setting decides how see-through
it is. Set `PlacementPreview` in the mod config to `"outline"` for the old blue outline, or `"both"`
for the two together; if the ghost cannot be made for some reason, the outline is shown instead.

While you place a building, the colony's other buildings close by get **box outlines** so you can set
the new one right beside them: purple for the size they are now (or are being built to), faint purple
for the size they grow to at their top level, and orange for any the new building would overlap. The
placement window names the buildings it overlaps. `NearbyOutlineRange` (default 32 blocks, 0 = off) and
`NearbyOutlineMax` (default 30) in the mod config change how far and how many.


**Placing by hand for making blueprints (0.49.0).** An admin can type `/colony handplace on` (or set
`PlaceHutsByHand` to true in `ModConfig/createacolony.json`) so the Town Hall, every hut and the Warehouse
go down by hand like any block, anywhere, with no wand and no colony. They face you as the wand would. A
block placed this way is only a block for building and exporting blueprints: it founds no colony, adds no
building, and its window does not open. `/colony handplace off` brings the wand back. The setting is saved.
### Work area and guards

Forester's and Farmer's Huts have a **Work area** tab in their window (officers change it):

- **How far out** the worker goes: 16 to 500 blocks (`GatherRadiusChoices`). The area is searched a
  chunk column at a time, nearest first, in a few milliseconds per tick; land no player is near is
  loaded from the save to be looked at, and land never explored is skipped. Long walks go in
  stretches of about 28 blocks, each ending on a free spot with floor under it, trying shorter and
  turned stretches around water, cliffs and out of holes. A forester, farmer or guard
  further than `FarWorkPlayerDistance` blocks from every player stays active and keeps the 3 by 3
  chunk columns around it loaded until it comes back or a player comes near.
- **Escort by guard**: a settler with the new **Guard** job follows the worker and fights monsters
  (`GuardTargets`, by default drifters, locusts, bells, bowtorns, shivers, wolves, hyenas and
  bears) within `GuardThreatRadius` blocks of it. Guards fight with the hardest-hitting weapon they
  carry and fetch one from the warehouse if they have none; spare guards keep watch at the Town Hall.
  The same creatures hunt settlers the way they hunt players (`HostilesAttackSettlers`).
- **Collect berries and fruit** (Farmer's Hut): picks ripe berry bushes and fruit trees within the
  range. Berries further than 32 blocks out wait until the fields have nothing to do.

### Food and meals
- A hungry settler eats one plain food item, one serving of a meal (from a bowl, a cooking pot or a crock,
  in a container or placed in the world) or one slice of a pie. Empty bowls and pots are left behind empty.
  A pot of food left in a fire pit counts once it is cooked; ovens, querns, troughs, barrels and buckets are
  never touched.
- Settlers remember the food groups (fruit, vegetable, protein, grain, dairy) of their last few meals and
  prefer the food group they have had least of, weighed against how far away it is.
- Food mods: Expanded Foods and A Culinary Artillery change the game's own meal nutrition, and settlers use
  that same code, so modded meals count with no hard dependency. Extra nutrients that Expanded Foods items
  carry (`expandedSats`) count too. Settlers find food on Food Shelves shelves like in any other container.
  A Culinary Artillery's mixing bowl, saucepan and cauldron are skipped by name so settlers never eat what is
  being made in them.

### The cook
- Craft a **Cook's Hut** (2 bread and 7 planks, see "Crafting recipes"), place it with the Builder's Wand and order
  level 1. Its blueprint has a fire pit; any finished fire pit within `CookFirepitRadius` (8) blocks works too.
- Give a settler the Cook job. The cook puts raw food (anything the game cooks in a fire pit into food, such
  as raw meat, fish or dough, from any mod) in the fire pit, adds firewood, lights it and takes the cooked food
  out, using the game's own fire pit. Cooked food stays in the hut's storage, where hungry settlers eat it.
- The Kitchen tab lists every food the game cooks this way, with how much is ready (hut, warehouse, cook and
  fire pit) and how much to keep. Since 0.124.0 it only lists what the cook can make from the food on hand (see "Kitchen: cook from what's on hand"). Raw food and firewood
  come from the hut storage; the cook asks couriers for raw food the warehouse has, and for firewood.
- A fire pit holding something the cook does not know how to cook is left alone.
- **Pot meals (0.17.0):** with an empty cooking pot in the hut storage or warehouse, the cook also cooks the
  game's meal recipes (stews, porridge, scrambled eggs and those food mods add) the way a player does: pot in
  the fire pit, its slots filled with ingredients that make the recipe, fire lit. It makes as many servings as
  the ingredients and the pot allow (up to 6), picking different ingredients where it can. The cooked pot goes
  into the hut storage and settlers eat from it a serving at a time; the emptied pot is used again. The
  Kitchen tab lists the meals it can make, with servings ready and to keep. Meals come
  before grilling. Recipes that need water or another liquid are left to players.
- **Food Shelves (0.51.0):** with the Food Shelves mod, any of its shelves, baskets and cabinets inside the
  Cook's Hut (the building as its blueprint stands) are kitchen shelves. The cook puts what it cooks (grilled
  food, pot meals, pies) on a shelf that takes it before anything goes into the hut storage, and moves dishes
  from the hut storage onto shelves with room. It also takes ingredients and raw food from the shelves (a
  vegetable basket, an egg shelf), and food on the shelves counts on the Kitchen tab. Couriers with nothing
  else to do bring ready food from the warehouse to kitchen shelves that have room for it. Settlers stock a
  shelf the way a player does: only what that shelf takes (bread on a bread shelf), one kind per segment.
- **Stone Bake Oven (0.55.0):** with the Stone Bake Oven mod (1.4.0), the cook bakes in an oven whose baking
  spaces are within `CookFirepitRadius` (8) blocks of the Cook's Hut. It puts dough on the baking spaces (four
  pieces each), makes pies itself (4 dough and 8 of one filling, such as berries or vegetables, on an empty
  baking space), puts firewood in the oven's heat source and lights it, keeps the fire going while something
  bakes, and takes the bread and pies out once baked, before they burn. They go on the kitchen shelves or into
  the hut storage. The Kitchen tab has a row for each bake ("Oven: Bread", "Oven: pie (any filling)") with how
  many to keep (bread `KitchenDefaultKeep`, pies `OvenDefaultPieKeep`, 2). Bakes take a Cooking level from
  `config/cooking.json` "baking": bread 1, pies 3, other mods' bakes 2. While the oven bakes, the cook gets on
  with the fire pit; a hut with an oven and no fire pit only bakes. Dough itself is not made by the cook yet:
  put it in the hut storage or the warehouse, and couriers bring it. The oven's cooking top (for pots) is left
  to players. Builders place the oven's blocks from a blueprint like any block: they pay for the block as it
  comes from crafting, any stone when the colony allows it, and never take its heat source for a fire pit.

### The smelter and the blacksmith (0.50.0)
Both huts have a simple placeholder model and a placeholder level 1 blueprint (an open plank shed with a fire
pit or a forge), ready to be replaced by real buildings. Both are on the wand's Craftsmanship tab, and the
Smithing skill grows with both jobs.
- **Smelter's Hut** (2 charcoal + 7 planks). The smelter uses the game's own fire pit (the nearest within
  `SmelterRadius`, 8), crucible and ingot molds. It sets up to `SmelterMolds` (4) fired ingot molds out beside
  the fire pit, fills a fired crucible with one kind of nugget or metal bit (whole ingots only, up to
  `SmelterIngotsPerBatch`, 4), puts it on the fire with fuel hot enough for the metal (charcoal or coal for
  copper; it asks couriers for `SmelterFuelCode`, charcoal, when the warehouse has none), lets the game melt it,
  and pours the metal into the molds. Cooled ingots come out of the molds into the hut storage. Metal that
  cools in the crucible goes back on the fire. The Smelting tab lists every metal with ingots ready (warehouse,
  hut, smelter, molds and crucible) and how many to keep (`SmelterDefaultKeep`: 4 copper, others 0). No
  alloys (bronze) and no bloomery yet.
- **Blacksmith's Hut** (2 ingots + 7 planks). The blacksmith needs a hammer, the game's forge (in the
  blueprint) and an anvil placed by a player, both within `BlacksmithRadius` (8). It heats the ingots for a
  piece in the forge with charcoal, takes them out once they are hot enough to work, and strikes the anvil
  once per `BlacksmithVoxelsPerStrike` (8) voxels of the piece instead of moving voxels one by one. Cold
  ingots go back in the forge. When the game has a 2-item crafting recipe that puts the piece on a handle (a
  pickaxe head and a stick make a pickaxe) and the blacksmith has the handle, it makes the finished tool. What
  can be made comes from the game's smithing recipes; the anvil's tier must be high enough for the metal.
  The Smithing tab shows everything that can be made from the metals the colony has ingots of, with the
  ingots each takes; nothing is kept until an officer picks something.

### Building types and new Dev's Builds (0.60.0)
- Buildings can have several types per level, picked in the placement window (see "Building types" above).
- Dev's Builds: new Cook's Hut levels 1 and 2 (replacing the old level 1), and Small and Medium houses for
  the Residence level 1. 0.60.1 removes the older Standard house; Dev's Builds houses placed with it use
  the first remaining type (Medium).

### Survival Handbook pages (0.59.0)
- Seven guide pages in the game's Survival Handbook (Guides), titled "Vintage Colonies: 1 - Getting Started" to
  "7 - Tips and Commands": getting started, settlers, building with the wand, the huts, storage and couriers,
  guards, and tips. Files `config/handbook/vintagecolonies-*.json`, text `handbook-vc-*` in the lang file.

### Creative tab (0.58.0)
- Every block and item of the mod is also on its own **Vintage Colonies** tab of the creative inventory
  (`"vintagecolonies"` in each `creativeinventory`; the tab name is `game:tabname-vintagecolonies` in the lang file).

### Hut models (0.57.0)
- Every hut block that was a plain cube now has a small model on a crate, like the new huts: Builder (toolbox and
  hammer), Cook (pot on a fire), Forester (stump and axe), Guard Tower (lookout), Miner (rock, ore and pickaxe),
  Potter (pots), Prospector (rock, pick and map), Quarry (stone blocks), Residence (bed), Tavern (barrel and
  mugs) and Warehouse (crates). The Supply Camp is a crate and a small tent. The Town Hall and Farmer's Hut models
  (by Aaron's friend) are unchanged. Generator: `tools/models/hut_models.py`.
- The Field marker is a scarecrow two blocks tall. Only its post is solid, and it needs the block above it free.

### The Explorer, the Gatherer and colony map markers (0.77.0)
- **Explorer's Hut** (1 stick + 7 planks, wand's Infrastructure tab) and **Gatherer's Hut** (1 fruit + 7 planks,
  Agriculture tab), each with a simple placeholder model and a placeholder level 1 blueprint. Neither job needs
  a tool. Both have the Work area tab (range, escort) and a new **Markers** tab: a switch for each kind of
  marker and a "Remove our markers" button.
- The **explorer** walks out over the land around its hut in `ExplorerStep` (32) block squares, nearest first,
  up to `ExplorerRadius` (300), and looks around `ExplorerLookRadius` (20) blocks at each stop. It marks ore
  lying on the ground or showing at the surface, clay, peat and terra preta, traders (by trader type), animals
  to hunt, danger (wolves, bears, drifters and the like), translocators and mountains. When it has seen
  everything it starts over after `ExplorerRevisitDays` (7). New Exploring skill.
- The **gatherer** forages up to `GathererRadius` (48) blocks out: ripe berries and fruit, mushrooms that are
  safe to eat (going by what eating them does), and loose stones, flints and sticks on unclaimed or colony land,
  not in or next to a building (`GathererPicksUpStones`, 0.84.0). Around wherever it works it marks berry bushes, fruit trees, mushrooms (poisonous
  ones apart), wild crops, cattails, resin and wild bees. With nothing to pick it walks out to look around at
  most every two minutes. New Foraging skill.
- **Colony markers** is a new world map layer (`Map/ColonyMarkerMapLayer.cs`), listed after the game's Owned
  creatures, with Colony prospecting moved there too. Every member sees the markers of their colonies. Since
  0.79.0 the icons are Vintage Colonies' own (see below), with the game's own as a fallback. The layer adds the two
  colours More Map Icons names that the game's colour list leaves out (#03D941 and #885530), so the Add
  waypoint window can make Olivine, Emerald, Brown Coal and Peat too.
- A marker of the same kind is never put within `MarkerSpacing` blocks of another (per group: animals and
  danger 96, ore 32, plants 16...). At most `MaxColonyMarkers` (600) from huts per colony.
- A **grave marker** shows where a settler died ("RIP name") or was knocked out ("name fell here"), where their
  things dropped. Only their latest spot is kept (`MarkSettlerDeaths`).
- Code: `ColonyManager.Markers.cs` (marker kinds, LookAround), `ColonyManager.Exploring.cs`,
  `AiTaskSettlerExplorer.cs`, `AiTaskSettlerGatherer.cs`. Not tested in game.

### Vintage Colonies' own map icons (0.79.0)
- 120 new waypoint icons (`assets/createacolony/textures/icons/worldmap/vc-*.svg`), one for every marker on
  More Map Icons' sheets: elements, ores and minerals, traders, animals, plants, places and arrows. Colour
  variants (Olivine, Emerald, Brown Coal, Peat and so on) are their own icon with a letter on it. They show up
  in the Add waypoint window for every player, with or without More Map Icons, and picking one fills in its
  name (`game:wpSuggestion-vc-*` in the lang file).
- Colony markers use these icons now, so they look the same for everyone.
- More Map Icons has no licence, so none of its art is used. The pictures come from game-icons.net (CC BY 3.0)
  and the letters from DejaVu Sans Bold: see THIRD_PARTY_NOTICES.md. `tools/icons/marker_icons.py` makes them
  again. Not tested in game.
- 0.79.1: the Add waypoint and Edit waypoint windows lay the icons out wider (`Map/WaypointIconGrid.cs`), about
  twice as wide as tall and as wide as the screen allows, so they no longer run off the bottom of the screen.

### A marker for every ore, crop, animal, trader and story place (0.80.0)
- When the game starts, Vintage Colonies makes a list of every ore, wild food, animal and story location there
  is, the game's and other mods' (`Map/MarkerSubjects.cs`), and gives each one a waypoint icon. Ores come from
  ore blocks; food only from what grows wild (crops, berry bushes, fruit trees and plants that can be picked);
  animals one per kind of creature (every bear is "Bear"); all traders share one icon; story locations come
  from every `worldgen/storystructures.json`.
- Things with a picture of their own use it (the worldmap icons, or one of 106 pictures in
  `textures/icons/vcart/`, also made by `tools/icons/marker_icons.py`). Things that share a picture, or have
  none (most other mods' things), get their kind's picture with letters from their name in the corner.
- The icons are added to the Add waypoint window with their names, so players can use them too.
- Explorers and Gatherers mark each thing with its own icon and name: each ore, each wild crop, berry bush and
  fruit tree by what it grows, each kind of animal, and traders. Animals that hunt players are marked as
  danger. Explorers also mark story locations they come near, under Landmarks. Two different things close
  together both get a marker; markers put down before 0.80.0 keep their old icons.
- 0.80.1: food only from wild plants (no gelatin and the like), one icon per kind of animal, one for traders.
- 0.80.2: no icons for tamed animals or helpers (animal bot, player bot, mech helper). The waypoint list keeps
  the game's and other mods' icons first, then ours by group: food, ore, animals, traders, story locations, the
  rest. The colour list has 36 more colours (pale and deep) and is as wide as the icons.
- The server and every player make the same list, so the names of the icons match. Not tested in game.

### The barracks (0.54.0)
- **Barracks** (2 bows + 7 planks), with a simple placeholder model and a placeholder level 1 blueprint (an open
  plank shed). It is on the wand's Military tab. It is a second kind of guard post and works like a Guard
  Tower: the same window tabs (Guard Scepter, Hostiles, Settings, Minimum stock, Statistics), tasks, night
  watch and armor. It has `ColonyManager.BarracksGuardsPerLevel` (2) guard places per level instead of one.
  Guards on the Guard task fight up to `BarracksRadius` (32) blocks from their spot instead of
  `GuardTowerRadius` (20), and the automatic patrol visits every hut within `BarracksPatrolRange` (96) instead of
  `GuardPatrolRange` (48). New guards fill Guard Towers with room first, then Barracks.

### The sawmill (0.52.0)
- **Sawmill** (2 logs + 7 planks), with a simple placeholder model and a placeholder level 1 blueprint (an open
  plank shed). It is on the wand's Craftsmanship tab. A settler with the Sawyer job needs a saw and works at the
  hut block itself, one crafting recipe every `SawmillSecondsPerCraft` (5) seconds; the saw wears as it would
  for a player. What it can make comes from the game's crafting recipes: every recipe whose ingredients are all
  wood (`SawmillWoodParts`: logs, planks, firewood, sticks, support beams and the like) and whose tool, if any,
  is a saw (`SawmillToolParts`). Recipes with more than `SawmillMaxInputs` (16) pieces of wood are left to
  players. The Sawmill tab lists what the colony has the wood for and how many to keep; a new Sawmill keeps 16
  planks of each wood (`SawmillDefaultKeep`). The sawyer fetches wood from the hut storage, or asks couriers
  for up to `SawmillBatch` (4) recipes' worth from the warehouse. What it makes goes to the warehouse. The new
  Carpentry skill grows with the work and makes it faster.

### Doors and gates

Settlers walk through doors and fence gates that open by hand. They open one as they reach it and close
it behind them once nobody is standing in it. Doors that need a key or a lever stay shut to them. This
patches the game's pathfinder (with Harmony) only while it plans a path for a settler.

### Hut storage

Every Builder's, Forester's and Farmer's Hut has its own 25-slot storage: sneak and right-click the
hut block, or press Storage in its window. Chests within two blocks of the hut add more room.

### The settler window

Right-click a settler to open its window, laid out like the player's character window with four tabs
along the top: **Character & Armour** (the settler's model, drag to turn it, between its clothing
slots, and its armor row), **Stats** (health, satiety, walk speed, status, bed, job, what it is doing,
family), **Inventory** (tool bar, bags, held item, pack, packed food) and **Traits & Skills**. See
"Settler gear, clothes and inventory (0.43.0)" for what you can do in it.

### Importing MineColonies blueprints

Put a MineColonies `.blueprint` file in `VintagestoryData/ModConfig/createacolony/import/` and run
`/colony importmc <file> <type> <level>`, e.g. `/colony importmc builder1 builder 1`. Each
Minecraft block is matched through `assets/createacolony/config/mcimport.json` (copy it to
`ModConfig/createacolony/mcimport.json` to edit); every candidate is checked against the installed
game, and blocks with no match are left empty and listed in chat. Check the license of a style pack
before shipping converted blueprints.

### Roads, ladders and getting unstuck (0.21.0)
- **Pathblocks** (config `PathBlocks`, default stone path, its slabs and stairs, and ladders) are roads. Settlers
  walk `PathSpeedMultiplier` (1.4) times faster on them, and the pathfinder counts a step on one as
  `PathStepCost` (0.65) of a normal step, so settlers take a road when it is up to about 1.5 times longer.
- **Ladders**: the game's pathfinder does not climb. When a settler finds no path to a goal two or more blocks
  up or down, it looks for a ladder within `LadderSearchRadius` (8), walks to it and climbs (`LadderClimbing`).
- **Willpower**: a settler that cannot get where it is going (no path, or no closer for
  `StuckNoProgressSeconds`) builds Willpower, twice as fast when boxed in. At `WillpowerSeconds` (20, give or
  take `WillpowerJitter`) it pops to a safe spot within `WillpowerRadius` (10) towards its goal, preferring
  Pathblocks, with sparks and a crackle. `WillpowerRecklessChance` (5%) of the time it does not look first, and
  guards never look out for monsters. The settler window shows "(stuck, Willpower N%)".
- Commands: `/colony stuck` lists stuck settlers; `/colony pathdebug [on|off]` shows sparks over them (blue to
  red) and logs to the server log. Config `PathDebug` turns it on at start.

### Growing the colony's land (0.22.0)
- The Town Hall claims a `TownHallClaimSize` (250) by 250 square centred on its block (100 before 0.33.0), and
  the square grows `TownHallClaimPerLevel` (50) blocks a side for each Town Hall level above 1, up to
  `TownHallClaimSizeMax` (500). Every hut, once its first
  level is finished, adds a square centred on its hut block; buildings with nothing to build (the Warehouse)
  claim as soon as they are placed. The colony's land is all those squares together, kept as the areas of the
  colony's one vanilla land claim, so hut placement, field markers, beds, food, chests and the /land tools all
  see the grown claim. Each square reaches `ClaimDepthBelow` (32) blocks under and `ClaimHeightAbove` (64)
  over its hut block.
- Square sizes (blocks along a side, config `HutClaimSizes`): Builder 40, Warehouse 40, Tavern 50, Farmer 60,
  Forester 60, Potter 30, Cook 30. Buildings not listed claim `ClaimSizeMin` (30), or `MilitaryClaimSize` (200)
  if they are on the Builder's Wand Military tab. Each hut level above 1 adds `HutClaimPerLevel` (10) blocks a
  side (0.33.0), so a hut near the border pushes it further out as it is upgraded. Every hut size is kept between
  `ClaimSizeMin` and `ClaimSizeMax`.
- A square never covers someone else's land claim: it shrinks two blocks at a time until it fits, down to
  `ClaimSizeMin`, and claims nothing if even that does not fit. Breaking a hut gives its square back.
- Builders build a blueprint's blocks in the colony's land and in unclaimed land around it (a hut only claims
  once built), never in someone else's claim.
- The work range of Forester's and Farmer's Huts (16 to 500 blocks) and guard escorts are unchanged: workers
  may gather in unclaimed land outside the colony, never in another claim.
- The Town Hall's Actions tab shows how much land is claimed. `/colony claims` lists what each building claims;
  `/colony claims show` marks the edge of the colony's land in blue, `/colony claims hide` clears it.

### Guard Tower and the military (0.23.0)
- **Guard Tower** (Military tab of the Builder's Wand; recipe: see "Crafting recipes"). Settlers with the
  Guard job move in by themselves, one per tower level (one while only the tower block stands). Guards without a
  tower still escort workers (Escort by guard) or watch the Town Hall.
- **Tower tasks**: Guard (stay at the tower or the spot set with the Guard Scepter, fighting within
  `GuardTowerRadius`), Patrol (Automatic: hut to hut within `GuardPatrolRange` of the tower; Manual: the scepter's
  points) or Follow (bodyguard to the player who chose it, loose or tight, brought to them past `GuardFollowTeleport`).
- **Night watch** (`GuardNightWatch`): tower guards sleep from `GuardSleepStartHour` to `GuardSleepEndHour` and keep
  watch at night; they stay out in temporal storms (`GuardsFightInStorms`). A guard with a monster close by, or an
  alarm to answer, leaves its meal, bed or shelter.
- **Alarm**: a monster that hurts a settler on the colony's land (plus `GuardAlarmMargin`) calls the nearest free guard.
- **Retreat** (tower setting): below `GuardRetreatHealth` a guard walks back to its tower until `GuardReturnHealth`.
- **Armor** (`GuardsWearArmor`): guards put on the best helmet, body and leg armor from their tower's storage and the
  warehouse. A blow hits head, body or legs and the armor there works as the game's player armor does, wearing down.
  It shows in the settler window's armor slots; it does not show on the settler's model yet.
- **Hut window Main tab** (every hut): name and level with rename, workers with Manage workers / Recall workers,
  build options, courier pickup priority (0 = never) and Request pickup now, a ? guide, Inventory and a list of
  everything stored. Guard Towers add Selection tools, Hostiles (per-tower on/off list), Settings, Minimum stock
  (couriers keep that many in the tower) and Statistics.

### The Town Hall window (0.24.0)

### Couriers carry full loads (0.160.0)
- When a courier brings something a hut asked for, it takes it from every warehouse chest, not just one.
- Other things the same hut asked for come along on the same trip, as much as the courier can carry.
- For a worker's own hut, the courier brings up to a full stack, so a crafter asking for 1 at a time gets enough
  for many. Builders' work orders, Minimum stock, tools, food that spoils, and things the hut would send back get
  what they asked for. Config: `CouriersCarryFullLoads`.
- Not tested in game.

### Builders place chiseled blocks (0.159.0)
- A build can now have chiseled blocks in it. The builder places each one with its full shape, as you chiseled it,
  and it shows in the ghost preview and turns with the building.
- Each chiseled block costs one full block of every material in it, as a player chisels it from one block. Planks
  laid sideways count as normal planks, and raw rock counts as the stones it drops.
- The materials are always the ones in the build, even with "any wood or stone" on, so it looks as you made it.
- Chisel inside your build, then export it as usual. A chiseled block saved without its shape is left out, and the
  Dev's Builds import script warns about it.

### Settlers can clear chiseled ruin blocks (0.158.0)
- Settlers now remove chiseled blocks (like the game's ruins) properly. Before, the game's break failed without a
  player, so the block stayed and a few stones dropped on each try. A builder clearing ruins broke the same block
  over and over, and thousands of stones piled up until the game slowed down (Aaron's report).
- A chiseled block gives the same few stones a player gets, and they go into the hut's storage. Any other block that
  fails to break is removed too, with a line in the server log.

### Miners and prospectors check the gatherer's ore finds (0.122.0)
- Ore bits a gatherer picks up now always get checked. Before, a prospector's reading up to 16 blocks away counted
  as checked, so the find was dropped without anyone going there (Aaron's report).
- Prospectors read right at the find first. Miners dig for that ore there whatever the reading says, before other
  prospected ore. With no reading yet, a miner goes and digs for it anyway. The Miner's Hut says "Ore a gatherer found".
- Miners only go on ore trips when their mineshaft is done or waiting for materials, as before.

### Settlers step onto the wall at a ladder's top (0.120.0)
- At the top of a ladder, a settler now steps onto the block the ladder hangs on and stands there before walking
  anywhere else. Before, it could step off the open side, fall and get stuck (Aaron's report).
- A settler that falls off a ladder on the way up plans its walk again from where it lands and tries the same ladder
  once more.

### Builders step out of their own way (0.116.0)
- A builder standing where its next block goes now hops to the nearest free spot beside it and places the block.
  Before, it walked off to that spot and back, and where it could not walk there (on a wall top) it stayed stuck
  in the gap (Aaron's screenshot). After three tries it builds elsewhere first and comes back.

### Gatherers take what they see (0.112.0)
- Aaron saw gatherers keep "looking" past things they could pick. Three causes: what a gatherer saw while
  looking around waited for the next sweep of its area (every 3 minutes); the sweep looked only at the ground as
  the world was made, so dug or raised ground was missed; and west or north of 0 the sweep skipped a strip of
  its area. Now each stop notes what is in reach at once, the sweep looks two blocks up and down, and the strip is
  swept.

### Builders ask for planks players carry (0.104.0)
- Planks laid sideways (and roof fill made of them) are their own blocks (planks-oak-ns), but players only ever
  carry upright planks. Builders asked for the sideways ones, so no planks a player had would do (Aaron's
  Residence). A block is now asked for as what it drops when broken, so any planks of the right wood count.

### Builders clear the ground over sunken parts (0.102.0)
- Empty space below a building's hut block is now dug out when part of the building is under it: a sunken room,
  stairs down or a path. Before, it was only dug when the blueprint also had blocks above it, so a low part with
  nothing over it yet kept its grass and soil on top (Aaron's screenshot). Space over saved ground alone (a yard
  lower than the floor) is still left alone, so no moat is dug round the house.

### Farmers soak the soil (0.98.0)
- A farmer with a watering can now waters farmland until it is at 100% moisture, not just until it shows as
  moist. It waters crops whose soil is below `FarmerWaterBelow` (now 0.5, was 0.3).

### Corpses for players and settlers (0.94.0)
Built in from [Player Corpse](https://mods.vintagestory.at/playercorpse) by DArkHekRoMaNT (MIT licence, see
`THIRD_PARTY_NOTICES.md`), brought up to 1.22 and without its CommonLib dependency.
- When a player dies, their things go into a corpse where they fell, instead of spilling on the ground. Hold
  right-click on it to take everything back. Only the owner can do this at first; after 240 in-game hours anyone can.
  A death waypoint is added to the map and goes when the corpse is collected.
- When a settler dies, their pack, bags, tools, armour and clothes go into "Name's corpse". A knocked-out settler
  leaves "Name's things" (their clothes stay on). Any member of their colony can collect it. The RIP marker on the
  map sits on the corpse. The "fell here" marker of a knocked-out settler goes once their things are collected.
- The Corpse Compass (rusty gears, bones and a temporal gear) points at your nearest corpse, or a settler's corpse
  from your colony.
- `/returnthings list <player>` and `/returnthings get <player> <give to> [id]` give back what a player lost in one
  of their last 10 deaths (needs the gamemode privilege).
- Config: `ModConfig/createacolony-corpses.json`. `PlayerCorpses` and `SettlerCorpses` turn each part off. With
  corpses off, things drop on the ground as before. If the Player Corpse mod is installed too, it makes the player
  corpses and this mod only makes settler ones.
- Code: `src/Corpses/`; entity `createacolony:corpse`, item `createacolony:corpsecompass`. No new save fields.

### What killed a settler; gatherer marks resin and beehives (0.93.0)
- The Town Hall's Dead Citizens page has a "Killed by" column: the creature (Wolf), the player or settler, or what
  happened (Starved, A fall, Drowned, Fire). Settlers who died before 0.93.0 show "unknown". Save data:
  `CitizenRecord` 63 `DeathCause`; `CitizenView` 31.
- The gatherer also marks resin on tree trunks and wild beehives in the trees (it looks up to 16 blocks above
  the ground). Wild crops never grow back, so once the gatherer picks the last wild crop near a crop marker,
  that marker comes off the map. Mushroom markers stay, as mushrooms regrow.

### Gatherer: ore bits and wild crops; clearer map markers (0.92.0)
- The gatherer picks up ore bits lying on the ground (native copper in claystone and the like). Each spot is
  marked "Underground copper deposit" (or the ore's own name) on the Colony markers map, and the prospectors
  take a reading there before walking anywhere else, so the miners dig it if the reading is good. One spot per
  ore within `OreHintSpacing` (16) blocks. Save data: `Colony` 91 `OreHints`.
- Wild crops (not on farmland) are picked only once fully grown, never near buildings.
- Colony markers on the world map are solid black with a light rim and a little bigger, so they are easy to see. `MapMarkerColor` ("#000000") sets the colour; "" gives each kind its own colour again. The settlers layer is now called "Colony settlers".

### Allowed to collect (0.91.0)
- A new **Allowed to collect** tab on the Forester's, Gatherer's, Farmer's, Miner's and Prospector's Huts and
  the Quarry. It lists everything that worker can take, each with a switch. Everything is on at first.
  Officers can turn things off one by one, or use All on / All off. The search box finds a row by name.
- **Forester**: each kind of tree, by its name (Oak, Birch...). It leaves trees that are turned off standing (names since 0.143.0). Fruit trees are on the list too, but start off, so foresters leave them standing until you turn them on (0.148.0).
- **Gatherer**: each berry and fruit, each edible mushroom, and loose stones, flint and sticks (0.110.0 adds ore bits, seashells, each reed and each clay, and peat).
- **Farmer**: each berry and fruit (crops are still chosen on each Field marker).
- **Miner, Prospector (Mine ore) and Quarry**: each ore, each rock and soil, gravel, sand, clay and peat. They
  never go after ore that is turned off. Blocks in the way still get dug, but what they drop is left on the ground.
  Choosing an ore on the Ore tab turns it back on.
- Building field 160 (CollectOff), ColonyAction 160 (SetCollect), ColonyManager.Collecting.cs. Not tested in game.

### Crafting recipes tab on huts (0.101.0)
- Aaron asked for a "Crafting recipes" tab on each hut, so the player can have a hut craft things, such as the
  Forester making firewood or sticks. The tab sits right under Main, as in MineColonies.
- Huts with the tab: Forester (firewood, sticks, debarked logs), Farmer (flax twine, rope, linen, hay), Gatherer
  (torches, baskets, flax twine), Miner and Quarry (cobblestone, stone bricks, torches or stone paths). The recipes
  come from the game's crafting grid, so only things the game has a recipe for show up. Each row says what it
  takes, such as "4 Firewood from 1 Log + Axe". Config HutRecipeOutputs and HutRecipeTools change the lists.
- Officers set how many to keep. Nothing is kept at first. When the colony (warehouse, hut and worker) has fewer,
  the worker crafts at the hut block between its other jobs: from what it carries, or what is in the hut storage.
  If the things are only in the warehouse, couriers are asked to bring them and the worker goes on with its job.
  A recipe that needs a tool uses the job's own tool (the Forester's axe); the worker never fetches another.
- The Smelter, Blacksmith and Sawmill keep their own tabs, which already do this. Building field 170 (RecipeKeep),
  ColonyAction 170 (SetRecipeKeep), ColonyManager.HutRecipes.cs, AiTaskSettlerWorker.PlanRecipe. Not tested in game.

### Builder's list: one line per item, daub from wattle, any farmland (0.97.0)
- Aaron's Residence asked for the same item more than once. With "any wood or stone" on, a roof's planks and logs
  were still asked for in the exact wood; now they are any wood too, so they join the other planks and logs.
- Wattle and daub was asked for as the finished daub blocks (one line per stage). Daub is now known by its code
  as well as its class, and raw daub is looked for again if it wasn't found the first time, so the list asks
  for wattle fences and raw daub and the builder makes the daub on the spot.
- Farmland counts however wet or fertile it is. Players can't pick farmland up in survival, so the builder makes
  it: soil goes down first (any soil, placed as the kind brought), then the builder tills it with a hoe, as a
  player does. The Builder's Hut asks for a hoe when it needs one, and keeps hoes like its other tools. Soil counts as any soil (farmland too), and
  the list says "Soil (any kind)". Not tested in game.

### The Mineshaft Entrance's preview (0.96.0)
- Aaron saw no building preview when placing the Mineshaft Entrance. The mine entrance piece (entrance.json) had
  its roofs but not their saved details, so they had nothing to draw. Its 61 roof details now come from Aaron's
  SurfaceMineshaftExample (same blocks, 11 layers up and 2 rows over), so the roofs show and get built properly.
- The ghost preview leaves out a roof block with no saved details, so one can't keep the rest from showing.
  Not tested in game.

### The Mineshaft Entrance's building (0.95.0)
- Aaron saw the Mineshaft Entrance had no building. Its building is now the pack's mine entrance piece: the
  placement outline shows it, and Build (or the first linked miner) sets the mine there. Linked miners dig and
  build it; builders never do, and Remove hut gives the block straight back and leaves the mine.
- Where the hut block sits comes from Aaron's surface example: entrance.json now has a
  createacolony:hut-mineshaftentrance block at (7, 12, 7), on the ground layer beside the shaft. A pack whose
  entrance has none puts the block just outside the entrance, on the side the player stood.
- The miners no longer treat the entrance building's box as off limits, and never dig the hut block. Not tested in game.

### Remove hut and the Mineshaft Entrance (0.90.0)
- **Remove hut** (Main tab, build options, officers): press it twice. The hut's workers lose their job at once,
  a builder takes the building down from the top, and the hut block goes back to the player who pressed it (into
  the builder's hut when they are away). Only the blueprint's blocks come down: natural ground, things players
  added and chests with items in them stay. A hut with nothing built goes straight back. Stop removing (or Cancel
  in the work orders lists) stops it. The Town Hall and Warehouse can't be removed, nor the only Builder's Hut.
- **Derelict mines**: a removed Miner's Hut's mineshaft is never taken down. It is left derelict (kept on the
  colony's DerelictMines list) until a Miner's Hut without a mine within 32 blocks, or a Mineshaft Entrance
  within 16 blocks of its entrance, takes it on.
- **Mineshaft Entrance** (Mining tab of the wand, no design, block only): holds a mine so several Miner's Huts
  work it. Put down near a Miner's Hut's mine it takes it over and links that hut; near a derelict mine it takes
  that on. Its Main tab lists the Miner's Huts with their miner and Link / Unlink; Levels and Settings change
  the shared mine for every linked hut. A new Miner's Hut within 32 blocks joins it by itself. A linked hut's
  own old mine is left derelict. The deepest level comes from the highest linked hut. Guards, Required resources,
  the trip to prospected ore and the materials stay per hut. Removing the entrance leaves its mine derelict.
- Building fields 56-59 and 62 (MineEntranceId, Removing, RemovedBy, MineRotation, MineStyle), Colony 90
  (DerelictMines). Not tested in game.

### Old previews no longer pile up (0.89.0)
- Placing a building after another showed the earlier buildings' ghosts inside the new one (the Town Hall and
  Builder's Hut inside a Guard Tower). Each new placement now gets a preview space of its own, and the game
  only draws the latest one, so old ghosts are never drawn again.

### Corner section (0.88.0)
- Aaron's corner (an L turn) ships as blueprints/medievaloak/mineshaft/corner.json and is picked at random with the
  others. The block of Mine waypoints at its far end marks the doorway where the next section joins; checked
  against his corner + T and corner + straight examples, both join at the same spots.
- Its empty quadrant was filled with shale so the miner leaves that rock alone.

### Mine levels (0.87.0)
- Aaron's new pieces: a new entrance (the surface, level 0) and a level piece (blueprints/medievaloak/mineshaft/level.json).
  Level pieces stack under the entrance's shaft, each just under the floor of the one above, lined up as in his
  combined example. Each level opens the floor above where its top layer is open and carries its ladder up.
- Tunnels start at each patch of Mine waypoints in a room's floor (away from the piece's edges), facing away from
  the middle. Sections (tee, straight, cross) branch off them at random on every level, so every level has the
  same middle and different branches. Sections keep off the rooms' dug and built spots, not their whole box.
- A level 0 Miner's Hut has level 1, each upgrade adds a level (`MinerMineLevelsPerHutLevel`), and a hut at its top
  level goes down as far as the world allows. Max depth still caps it. A style without level.json works as before.
- Preview: tools/blueprints/mineshaft_preview/render_levels.py.

### Far workers keep their hut loaded (0.86.1)
- A worker plans every trip from its hut, so when no player was near the hut it found no hut storage and stopped.
  Now the hut's chunk column stays loaded while it has a worker and no player is near.

### Bug fixes (0.86.0)
- Settlers lie along their bed like a player, using the player's lying pose, and stay put so a bedmate does not
  push them askew. Vanilla beds use the game's own sleeping spot; furniture beds use the mod's seat and facing.
- Guards (escorts too) go to bed at night unless a monster is within `GuardWakeRadius` (8) blocks on their level,
  or a settler is attacked. Monsters further off, or in caves below, no longer keep them up all night.
- The Residence hut block is storage, with Storage and Minimum stock like the other huts.
- Settlers resend their animations to players every few seconds, so ones coming back into view are not stiff.

### Gatherer fix (0.84.0)
- The gatherer only picked up loose stones on unclaimed land, but the Town Hall claims everything around the hut,
  so it found nothing and kept looking around. Now it also picks them up on the colony's own land, except in or
  within two blocks of a building. The Gatherer's Hut window shows how many things are ready to pick.

### Settlers move like players (0.83.0)
- A settler holding a torch or lantern holds it up the way a player does (`HoldLightsLikePlayers`).
- A settler with nothing to do now and then looks around, scratches its head, coughs or stretches, like an idle
  player. Late in the day it yawns (`IdleAnimations`).
- Settlers sit down to eat, as a player sits (`SitToEat`). On a taken break they often sit for a while too
  (`SitWhenIdle`). They pick a free chair, stool or bench nearby if there is one, also from other mods, else they
  sit on the floor. Eating sitting down takes a little longer.
- Eating uses the player's hand-to-mouth animation.

### Colony militia, settlers on the world map, tab order (0.85.0)
- Town Hall tabs: Prospecting moved down to just above Settings, which stays last. New **Colony militia** tab
  after Citizens.
- **Colony militia** lists every grown settler with their job, Guard or Civilian, and the weapon they would
  fight with (the one in hand, else the best one they carry), guards first. Two switches for officers:
  **Self Defence** (settlers with a weapon fight a creature that hurts them; this is 0.76.0's fighting back,
  now per colony, default from `FightBackWhenAttacked`) and **Support allies** (when a settler is hurt by a
  creature, other armed non-guard settlers within `SupportAllyRadius` (16) blocks come and fight it too; default
  from `SupportAllies`, on). Helpers that can no longer fight go back to work; they never run unless hit.
- **Colony settlers** world map layer (`Map/ColonySettlerMapLayer.cs`), next to Colony prospecting and Colony
  markers: every settler of the player's colonies as a dot (green, yellow wanderers, red knocked out), sent
  by the server every 3 seconds so far-away settlers show too. Pointing at one shows its name, job and colony.
- The Town map also names the settler under the pointer.

### Town map land drawn black (0.81.0)
- The Town map showed loaded land as solid black: its textures were made without a size, so nothing was
  uploaded. They now get their size (256 by 256) up front, and get smaller copies for zooming far out.

### The Town map shows all loaded land (0.78.0)
- The server draws every chunk near a colony that is loaded, whether a player or a settler loaded it (far
  workers keep their land loaded), in the world map's own colours and shading (`Map/ColonyLandMap.cs`). Each
  chunk is drawn again when it changes, or every 5 minutes while loaded, a dozen chunks every quarter second,
  those nearest the Town Hall first. The pictures are kept in the save, so land stays on the map after it
  unloads, like the world map. Land over `TownMapRange` (1024) blocks from the colony's land is left out.
- The Town map asks the server for new chunks every 2 seconds while it is open (its own network channel,
  `createacolony-landmap`, so it does not touch the main one) and keeps them as textures of 8 by 8 chunks.
- Zoom goes from the whole area down to 16 pixels a block, and dragging can move the map anywhere in the area.
  **Redraw map** is gone (the map keeps itself up to date); **Find me** moves the map to you.

The Town Hall window is laid out like MineColonies', with eight tabs down the left:

- **Actions**: the colony's name and the Town Hall's level on a panel like a hut's Main tab (the pencil
  renames the colony), the colony's numbers, the Town Hall's own upgrade, cancel and turn buttons, how
  much land the colony claims, and a **?** guide.
- **Town map**: the colony from above (0.24.0). Since 0.78.0 it looks like the world map and shows all land
  within `TownMapRange` (1024) blocks of the colony's land that you, another player or a settler has loaded,
  dark where nobody has been. The colony's land is tinted with a gold edge, and each building's ground is a box
  (gold Town Hall, blue huts, dashed orange while being built). Settlers are green dots (wanderers yellow,
  knocked out red) and you are white. Point at a building for its name and level. The mouse wheel (or **+** and
  **−**) zooms around the pointer, from the whole area down to 16 pixels a block; drag with the left button to
  move; **Fit** shows the whole colony again and **Find me** moves the map to you.
- **Banners**: the colony banner, see Colony banners below.
- **Information**: **Events**, a log of the last 100 things that happened (the colony founded, settlers
  hired, wanderers arriving, welcomed, turned away or leaving, settlers knocked out, recovering or dying,
  buildings placed, finished or taken down), newest first; and **Work orders**, every building waiting for a
  builder in the order builders take them, with Cancel for officers.
- **Citizens**: two pages (0.33.0). **Current Citizens**: every living settler with food, bed and job. Officers
  pick jobs, **Recall** a stuck settler straight to the Town Hall (only while it is in loaded land), and welcome
  or turn away wanderers. **Dead Citizens**: everyone who died for good, with their job, when they died and a
  Buried / Not buried marker. Burial and mourning are coming later, so for now everyone shows Not buried.
- **Statistics**: **Colony** shows settlers against beds, settlers lost for good, buildings, the colony's age,
  and each job's settlers against the places its huts offer; **Work done** adds up every hut's Statistics for
  today, yesterday, the last week, the last 100 days or all time.
- **Permissions**: the players in the colony and their roles (the owner sets roles and removes players).
- **Settings**: Permadeath (the owner's switch), how often wanderers come, and the warehouse.

MineColonies features with no system here yet are marked "Coming later" on their tab: citizen style, alliances, happiness, reordering work orders, custom ranks and
permission logs, auto hiring and housing, and enter/leave messages.

### Colony banners (0.25.1)

The Town Hall's **Banners** tab works with the [Heraldry](https://mods.vintagestory.at/heraldry) and
[Heraldry: Banners](https://mods.vintagestory.at/heraldrybanners) mods (both need Attribute Rendering Library).
Vintage Colonies does not need them; without them the tab says they are missing.

- Design a banner at the Heraldry Workbench, hold it, and press **Use banner in hand**. The Town Hall
  remembers the design and you keep the banner.
- **On the Town Hall**, **On Guard Towers** and **On every building** copy the colony banner's layers onto every
  banner hanging on a pole (or another display block, never a chest) inside those buildings' blueprint area.
  Each banner keeps its own shape (pennant, swallowtail, ...). Only buildings in loaded land change; chat says
  how many banners changed and how many buildings were skipped.
- **Put the colony banner on each building when a builder finishes it** does the same for each finished level,
  including banner poles the builder put up from the blueprint (0.25.0).
- Officers and the owner can use the tab.

### Families and the Residence (0.27.0, fixed in 0.27.1)

Settlers have families, as MineColonies citizens do (`src/Colonies/ColonyManager.Families.cs`).

- **Family names.** Every settler has a family name from `config/settlersurnames.json`; settlers from
  older saves get one the first time the colony loads. Children take their mother's or father's.
- **Couples.** Once a day, two single adults who have lived in the colony for `FamilyCoupleAfterDays`
  pair up (`FamilyCouplesPerDay`; `FamilyCouplesAnySex` lets two men or two women pair, but only a man and a
  woman have children). Never brothers and sisters, nor a parent and child. They move in together: one
  furniture double bed (W4RD0's Furniture), else beds side by side in the Residence one of them lives in.
- **Children.** Each day a couple has a `FamilyChildChancePerDay` chance of a child, if both are well and
  not hungry, they have fewer than `FamilyMaxChildren`, `FamilyChildCooldownDays` have passed since the last
  one, the colony is below `FamilyMaxColonySize` (0 = no limit) and, with `FamilyChildNeedsFreeBed`, there
  is a free bed. So housing limits growth. A child is a smaller settler (`entities/settlerchild.json`): no
  job, it sleeps near its parents, eats, sheds and plays near home. After `FamilyChildGrowDays` it grows up
  into a normal settler who can be given a job.
- **Residence** (Fundamentals tab of the Builder's Wand; planks, sticks and dry grass). Settlers who sleep
  in a bed inside it are its residents; settlers looking for a bed take one in a Residence first. Its
  window has a **Residents** tab with each household (a couple and their children, or someone on their
  own), jobs, how long children take to grow up and whether a couple can have a child right now. Level 1
  is a placeholder 7x4x7 room with two hay beds.
- **Settler window**: a Family section under Work shows their Residence, partner, parents, children,
  brothers and sisters, and for a couple whether a child may come. The Town Hall's Citizens tab shows
  children with their time to grow up instead of a job.
- `/colony family couple <name> <name>`, `/colony family child <name>` (the couple has a child now, if the
  real limits allow) and `/colony family grow <name>` are admin test commands. Names can be first names.
- `Families: false` in the config stops new couples and children.

### Raw ingredients are left for the cooks (0.119.0)

`src/Colonies/ColonyManager.Food.cs`. Not tested in game yet.

- Settlers don't eat or pack raw grain, raw meat and fish, or vegetables and eggs a pot meal can use, while
  there is other food. Fruit, cheese and cooked food are eaten as before.
- Below `EatRawBelowFraction` (0.2) of a full stomach they eat raw food too, but still pick ready food first.

### Fresh food: cellars and oldest first (0.100.0)

`src/Colonies/ColonyManager.Freshness.cs`. Not tested in game yet.

Food spoils by the game's own rules: slower in a cellar (a small closed room with soil or stone walls),
underground, or in winter. Settlers use that.

- **Oldest first.** Settlers eat and pack the food that will spoil soonest. Cooks, couriers and settlers cooking
  at home also take the oldest food first. `EatSpoilingFirst` sets how strong the pull is (0 turns it off).
- **Cool storage.** Food that can spoil goes in the coolest chest or shelf with room. Couriers (and helpers
  without a job) carry food from warm storage to cooler storage: warehouse food to a cooler warehouse chest or a
  Cook's Hut cellar, Cook's Hut food to its basement, home food to the home's basement. The cook does the same
  in its own hut when it has nothing to cook. Kitchen shelves are filled but never emptied this way.
- The Dev's Builds Cook's Hut level 2 basement is full of Food Shelves, so food ends up down there.
- The Cook's Hut window shows how fast food spoils in its coolest storage (100% is normal speed).
- `KeepFoodCool: false` turns the moving off. `CoolStoreMaxRateShare` (0.75) is how much cooler a store must be.

### The Hunter, skinning and cooking at home (0.99.0)

`src/Colonies/ColonyManager.Hunting.cs`, `src/Colonies/ColonyManager.HomeCooking.cs`,
`src/Settlers/AiTaskSettlerHunter.cs`, `src/Settlers/AiTaskSettlerChores.cs`. Not tested in game yet.

- **Hunter's Hut** (Builder's Wand, Agriculture tab; recipe: planks around two spears). Give a settler the
  Hunter job. It needs a spear (any weapon works) and a knife in the hut storage or the warehouse. It hunts
  animals up to `HunterRadius` blocks from the hut, skins its kills with the knife, and brings the meat,
  hides and the rest to the hut. Couriers take them to the warehouse. It leaves monsters (the guards'
  targets), animals bred in pens, babies and animals inside buildings alone. An animal it can't catch within
  `HunterChaseSeconds` is left alone for 5 minutes. The **Allowed to collect** tab says which animals it hunts.
- **Skinning.** Settlers without a job skin dead animals on the colony's land, and kills the hunter left behind,
  with a knife from their home chest or the warehouse. The meat, hide and the rest go into a chest at home.
- **Cooking at home.** If there is a fire pit in the home or up to `JoblessFirepitReach` blocks from it, they
  cook the raw meat (and other raw food from the home chest) on it with firewood from home or the warehouse,
  then put the cooked food in the home chest. Hungry settlers eat it from there. The Cook's Hut's fire pit is
  left to the cook.
- Config: `JoblessSkinAnimals`, `JoblessSkinRadius`, `JoblessCookAtHome`, `JoblessFirepitReach`,
  `HunterRadius`, `HunterSecondsPerHit`, `HunterChaseSeconds`, `HunterSparesLivestock`.

### Settlers without a job: home upkeep and helping out (0.53.0)

Settlers with no job no longer just stand about (`src/Colonies/ColonyManager.Zones.cs`,
`src/Settlers/AiTaskSettlerChores.cs`).

- **Zones.** Every building's zone is the ground its blueprint covers, one block wider all round. A
  settler's **home** is the building whose zone holds its bed (a Residence first, else the smallest zone).
- **Home upkeep.** A settler without a job looks after the farmland inside its home, like a farmer: it
  harvests ripe crops and sows again, pulls weeds, waters dry crops, sows bare farmland, and tills soil
  wherever the home's blueprint has farmland. Seeds (`JoblessSeedTake` at a time), a hoe and a watering can
  come from a chest in the home, else from the warehouse. When the plot needs nothing more, the harvest
  and what it took go into a chest in the home; with no chest, it takes them to the warehouse. Farmland that
  is part of a farmer's Field marker square is left to the farmer.
- **Helping out.** With nothing to do at home, it helps the couriers: deliveries to huts and builders, emptying
  drop-off chests and clearing huts into the warehouse.
- `JoblessTendHome` and `JoblessHelpHauling` in the config switch either off. More home duties (repairs,
  tidying) can be added to `AiTaskSettlerChores.Plan`.

### Skills (0.37.0)

Settlers learn by doing, Project Zomboid style (`src/Colonies/ColonyManager.Skills.cs`, skills listed in
`config/skills.json`). Each skill goes from level 0 to 10; level n costs `SkillXpPerLevel` x n XP on top
of the level below (75, 150, 225... 4,125 in all).

- **Job skills**: Building, Farming, Woodcutting, Pottery, Cooking, Hauling, Fighting, Prospecting,
  Mining (miner and quarrier), Smithing (blacksmith and smelter) and Carpentry (sawyer). XP comes from the
  work itself (a block built, a tree felled, a hit landed, a stack delivered); each usable level makes the
  job's work 4% faster (Fighting: 2% faster swings and 3% more damage a level).
- **Strength and Fitness** grow for everyone: Strength from felling trees, mining, fighting and full courier
  loads (+3% guard damage a level); Fitness from walking, one XP every 50 blocks (+1.5% walking speed a
  level, +1 max health every two levels).
- **The building limits what counts.** A settler's *usable* level is the lower of their real level and the
  cap of the hut they work at: `SkillCapByBuildingLevel` is 4, 6, 8, 10 for hut levels 1 to 4. Strength and
  Fitness go by the Residence they sleep in. Skill is no use without the tools: a Fighting 10 guard at a
  level 1 Guard Tower fights like a level 4 until the tower is upgraded. XP keeps growing past the cap.
- **Starting skills**: recruits, wanderers and starting settlers roll 0 to 2 in every skill and one
  background skill at 2 to 4. Tavern offers show their two best skills before you pay. Children start at
  nothing and get a third of their parents' average when they grow up. Settlers from older saves roll
  theirs once when the colony loads.
- **Windows**: a Skills panel in the settler window (level, a bar to the next level, "10 (using 4)" in
  orange when the building limits it, hover for XP and what to upgrade); the hut's Manage workers list
  shows each candidate's level in that hut's skill and sorts the best first.
- Level 5 and level 10 go in the Town Hall event log. `/colony skills <name> [skill] [level]` shows or sets
  skills (admin). `Skills: false` turns the whole thing off; `SkillXpMultiplier` speeds it up or down.

### RimWorld-style skills and happiness (0.61.0)

Design doc: https://claude.ai/artifact/D5hsBzzJ8fd3uJ5D6WpEFj. This replaces parts of the 0.37.0 section above.

- **Levels 0 to 20** with medieval names (Unschooled, Novice ... Journeyman ... Master ... Legend of the realm,
  lang `skill-rank-<n>`). Levels 0 to 10 cost what they did, so old saves keep every level; above
  `SkillSteepFrom` (10) steps grow `SkillSteepFactor` (2) times as fast: level 20 is 19,875 XP.
- **Hut caps** `SkillCapByBuildingLevel` 4, 8, 12, 16, 20 for hut levels 1 to 5. A building at the top level its
  blueprint style has always allows all 20 (Dev's Builds huts with fewer levels).
- **Passions** (`CitizenRecord.Passions`, 1 = Interested "+", 2 = Burning "++", marks in lang `passion-mark-*`):
  XP x1 / x2 / x3 (`SkillPassionXp`) and +8 / +14 happiness for 4 hours after each piece of that work. 0 to 3
  rolled per settler (`SkillPassionCountWeights`, a quarter Burning), likelier on their best skill. A job trait
  gives its skill a flame (two if it already had one) instead of the old 1.5x XP. Children take each parent's
  passion half the time when they grow up. Old saves roll passions once.
- **Incapable**: traits with an `incapable-<skill>` effect (Pacifist: fighting; Bad Back: woodcutting and
  mining; Ham-fisted: cooking and pottery) block those jobs, XP and usable level. Hut lists grey them out.
- **Decay**: levels above 10 lose `SkillDecayPerDay` XP every in-game day (5 at 11 up to 270 at 20), never below
  level 10. Good Memory halves it. **Daily soft cap**: `SkillDailyCap` (600) full XP per skill per day, then
  `SkillOverCapFactor` (0.2).
- **Learning rate**: XP = work x passion x trait `learning` (Fast Learner 1.5, Slow Learner 0.6) x mood x
  `SkillXpMultiplier`, then the daily cap. Fractions round by chance.
- **Milestone perks** (`perks` in skills.json, one value per `SkillPerkLevels` 5/10/15/20): Building tool wear,
  Farming/Woodcutting/Mining/Pottery extra yield (in `StoreHarvest`), Cooking extra portion (in
  `EmptyFirepit`), Hauling and Strength pack slots, Fighting swing speed, Prospecting work speed, Fitness hunger.
- **Happiness** (`ColonyManager.Happiness.cs`): 0 to 100, drifting `HappinessPerHour` (2) towards 50 plus all
  thoughts. Lasting thoughts (`CitizenRecord.Thoughts`): passion work, cooked meal, skill up, became a parent,
  knocked out, a death (family -20). Standing ones: hungry/starving, varied diet, bed in a Residence (+1 per
  Residence level) or no bed, partner, Town Hall level, no job, Sanguine/Melancholic (trait `mood`). Joyful 85+
  works 10% faster and learns 20% faster; Happy 70+ 5%/10%; Unhappy below 45 works 5% slower; Miserable below 30
  15% slower, learns 25% slower. At `HappinessBreakAt` (15) a settler stops working for 2 to 4 hours
  (`IsBrokenDown`, checked by every work task), then gets a +20 relief thought for a day.
- **Windows**: Skills panel shows passion marks, "v" when decaying, "Incapable"; hover has the level name, passion,
  today's XP against the cap and the next perk. Stats tab has a happiness bar with every thought on hover. Town
  Hall Overview shows the average happiness. `/colony mood <name> [0-100]` shows or sets happiness (admin).
- Config version 3 upgrades files still at the old skill defaults (max 10, caps 4/6/8/10, effect clamp 2).

### Traits, the population cap and meal levels (0.45.0)

**Traits** are what a settler is born with (`src/Colonies/ColonyManager.Traits.cs`, listed in
`config/colonytraits.json`). Every settler has 2 to 10 of them, always at least one good (points above 0) and
one bad (points below 0); quirks have 0 points. Each trait has a level from 0 to 20: a good trait's effect
grows 5% a level, a bad trait's penalty shrinks 4% a level, so a higher level is always better for the
settler. Traits gain 1 XP every in-game day (3 while in use) and level L to L+1 costs (L+1) x 3 XP.

The file is `colonytraits.json`, not `traits.json`: the game reads every mod's `config/traits.json` as player
character traits, and 0.45.0's file of that name stopped every world from loading (fixed in 0.45.1).

- **General**: Hard Worker / Lazy (work speed), Tough / Frail (max health), Light Eater / Glutton
  (hunger), Fleet-footed / Sluggish (walking), Fast Healer / Slow Healer (time knocked out, healing),
  Careful / Clumsy (tool wear), Iron Stomach / Picky Eater (how much they mind eating the same food),
  Patient / Reckless (Willpower), Heavy Sleeper, Early Riser, Night Owl (sleep hours), Charming, Fertile,
  Loner (families), Cowardly (retreats early, runs from monsters).
- **Job traits** only work in their job and a settler has at most one: Master Mason (builder, some blocks
  cost nothing), Green Thumb, Lumberjack, Steady Hands, Good Cook (cooks a level above their skill),
  Pack Mule (bigger pack), Prospector's Eye, Hard Rock, Sharp-eyed (guard range), Brave (retreats late).
  A job trait also starts its skill two levels higher and makes it grow half again as fast.
- **Earned**: a guard with 25 kills becomes a Veteran for life (+1 damage, swings faster). Anyone knocked
  out three times in ten days is Shaken for ten quiet days (slower, cowardly; Brave is suppressed).
- **Children** inherit: a trait both parents have passes on 75% of the time at the average of their
  levels, one parent's 35% of the time at half its level, plus one new random trait. Clashing traits
  (Tough and Frail) go to the shared or higher one. Children can end up with more than 10.
- **Tavern** offers show each recruit's traits; the price adds one gear per trait point and one more per
  10 levels of good traits. The settler window's Traits & Skills tab lists traits (green good, orange bad,
  grey when idle) with a hover for what each does at its level; the Town Hall list shows them on hover.
  The hut's Manage workers list marks a matching job trait with a star and sorts those first.
- Effects stack with skills inside the same 50% to 200% cap. `/colony traits <name> [add|remove|level
  <trait> <n>]` for testing (admin). `Traits: false` turns them off; `TraitsMin/Max`, `TraitLevelMax`,
  `TraitXpPerLevel`, `TraitVeteranKills`, `TraitShakenCount/Days`, the inherit chances and
  `TraitGearsPerPoint` are in the config. Settlers from older saves roll theirs once.

**Population cap** (`ColonyManager.Population.cs`): `PopulationBase` (20) plus `PopulationPerTownHallLevel`
(5) for each level the Town Hall has been built to; a freshly placed Town Hall is level 0, so 20, and 25
at level 1. When full: no hiring at the Tavern, visitors cannot be welcomed, wanderers stop coming and
couples have no children. The Town Hall shows "Settlers 18 / 20" (orange when full) and the Tavern says
how much room is left. `PopulationBase: 0` removes the limit.

**Meal levels** (`ColonyManager.Cooking.cs`, `config/cooking.json`): every pot recipe takes a Cooking
level: porridge 2, scrambled eggs and soup 3, vegetable stew 4, meaty stew and jam 6. Recipes not listed
(other mods') go by their number of ingredient types: 1 to 2 types level 2, 3 types level 4, 4 or more
level 6; `cooking.json` already names the A Culinary Artillery and Expanded Foods meals for when the cook
learns their stations. The cook makes only the meals at or under their *usable* Cooking level (so the
Cook's Hut level caps the menu: level 1 allows up to 4) plus one with Good Cook. Grilling in the fire pit
never needs a level. The Kitchen tab shows each meal's level and greys out what the cook cannot make yet
("Needs Cooking 6 (cook has 4)"). A cook from an older save gets at least Cooking 4, so stew keeps coming.

### The Primitive Craftsman (0.103.0)
- **Primitive Craftsman** (2 flint + 7 planks), with a simple placeholder model and a placeholder level 1
  blueprint (a copy of the Sawmill's). A settler with the new **Craftsman** job knaps flint and stones into
  tool heads at the hut block, from every knapping recipe in the game (axe, knife, shovel, hoe, spear and
  arrow heads, and other mods' too). One stone per head, `CraftsmanStrikes` (8) strikes
  `CraftsmanSecondsPerStrike` (1.5) seconds apart. When a crafting recipe without tools takes the head and up
  to `CraftsmanMaxHandleParts` (4) other things (a stick), the craftsman puts the head on it and makes the
  finished tool (`CraftsmanSecondsPerHaft`, 4 seconds). It gets the sticks before knapping, and knaps no new
  head while one waits for a handle.
- The Knapping tab (crafter-hut keep-in-stock tab, `ColonyManager.Knapping.cs`) lists the tools the colony has
  stones for. A new hut keeps 2 of each flint axe, knife, shovel, hoe and spear (`CraftsmanDefaultKeep`).
  Stones and sticks come from the hut storage, or couriers bring up to `CraftsmanBatch` (4) from the
  warehouse. Finished tools go to the warehouse, where other workers take the tools they need.
- New Knapping skill. Not tested in game yet.

