using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Buildings, work orders and what builders need to know to carry them out. The blueprint never
/// remembers which blocks are done: a step is done when the world already has the right block, so a
/// player can help build, and a builder interrupted mid-wall simply carries on where the wall ends.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Chests this close to a hut block add to that building's storage.</summary>
    public const int HutStorageRadius = 2;

    BlueprintLibrary? blueprints;
    public BlueprintLibrary Blueprints => blueprints ??= new BlueprintLibrary(sapi, Config);


    /// <summary>What each work order is waiting on, for the Town Hall window. Not saved: builders refresh it.</summary>
    public readonly Dictionary<int, string> BuildStatus = new();

    // ---- Steps a builder keeps failing (0.198.0) --------------------------------------------
    // Aaron's play test: the only builder stood at one block of the Cook's Hut for six game days. A block the builder
    // tries and fails too often is set aside, the rest of the build goes on, and set-aside blocks get another go at the
    // end. Kept in memory only: a restart simply gives every block a fresh try.

    /// <summary>Failed tries per building and block.</summary>
    readonly Dictionary<int, Dictionary<BlockPos, int>> stepFailures = new();
    /// <summary>Blocks set aside per building, until everything else is done.</summary>
    readonly Dictionary<int, HashSet<BlockPos>> skippedSteps = new();
    /// <summary>Where a building's build is stuck after its set-aside blocks failed again, for the status and report.</summary>
    public readonly Dictionary<int, BlockPos> StuckStep = new();
    /// <summary>Buildings whose set-aside blocks are having their second go.</summary>
    readonly HashSet<int> retryingSteps = new();

    /// <summary>Counts a failed try at a block of a work order; enough of them set it aside for later.</summary>
    public void StepFailed(Building building, BlockPos pos, int weight = 1)
    {
        if (!stepFailures.TryGetValue(building.BuildingId, out var tries)) stepFailures[building.BuildingId] = tries = new();
        int n = tries.GetValueOrDefault(pos) + weight;
        if (n < Config.BuilderStepTries)
        {
            tries[pos.Copy()] = n;
            return;
        }
        tries.Remove(pos);
        if (!skippedSteps.TryGetValue(building.BuildingId, out var skipped)) skippedSteps[building.BuildingId] = skipped = new();
        skipped.Add(pos.Copy());
        if (retryingSteps.Contains(building.BuildingId)) StuckStep[building.BuildingId] = pos.Copy();
        Mod.Logger.Notification("[createacolony] Builder set aside block {0} of {1} {2} after failing at it; it gets another try at the end.", pos, building.Type, building.BuildingId);
    }

    /// <summary>A block was built: forget its failures.</summary>
    public void StepDone(Building building, BlockPos pos)
    {
        if (stepFailures.TryGetValue(building.BuildingId, out var tries)) tries.Remove(pos);
        if (StuckStep.TryGetValue(building.BuildingId, out var stuck) && stuck.Equals(pos)) StuckStep.Remove(building.BuildingId);
    }

    /// <summary>Forgets every set-aside block of a building, when its work order ends or starts over.</summary>
    public void ForgetStepFailures(int buildingId)
    {
        stepFailures.Remove(buildingId);
        skippedSteps.Remove(buildingId);
        retryingSteps.Remove(buildingId);
        StuckStep.Remove(buildingId);
    }

    /// <summary>True when a building's build failed at a block twice over, so builders try other work orders first.</summary>
    public bool IsBuildStuck(Building building) => StuckStep.ContainsKey(building.BuildingId);

    // ---- Buildings ------------------------------------------------------------------------

    public Building? GetBuilding(Colony colony, BlockPos hutPos) => colony.Buildings.Find(b => b.HutPos.Equals(hutPos));

    public Building AddBuilding(Colony colony, string type, BlockPos hutPos, int rotation)
    {
        var existing = GetBuilding(colony, hutPos);
        if (existing != null) return existing;

        var building = new Building { BuildingId = colony.NextBuildingId++, Type = type, HutPos = hutPos.Copy(), Rotation = rotation };
        colony.Buildings.Add(building);
        // A new Healer's Hut keeps a few bandages from the start (ColonyManager.Healing.cs).
        if (type == BuildingTypes.Healer) KeepBandagesAtStart(building);
        if (type == BuildingTypes.Gatherer) KeepPoulticesAtStart(building);
        if (type is BuildingTypes.Stonemason or BuildingTypes.Fletcher) KeepRecipesAtStart(building);
        LogEvent(colony, "event-placed", "@building-" + type);
        MarkDirty(colony);
        RefreshClaim(colony);
        return building;
    }

    public void RemoveBuilding(Colony colony, BlockPos hutPos)
    {
        var building = GetBuilding(colony, hutPos);
        if (building == null) return;
        LeaveMine(colony, building);
        colony.Buildings.Remove(building);
        colony.LinkMines();
        LogEvent(colony, "event-removed", BuildingName(building));
        CancelBuildRequests(colony, building);
        foreach (var c in colony.Citizens.Where(c => c.WorkplacePos != null && c.WorkplacePos.Equals(hutPos))) c.WorkplacePos = null;
        MarkDirty(colony);
        RefreshClaim(colony);
    }

    /// <summary>Colonies saved before buildings existed get their Town Hall and Warehouse as buildings.</summary>
    void EnsureCoreBuildings(Colony colony)
    {
        if (GetBuilding(colony, colony.TownHallPos) == null) AddBuilding(colony, BuildingTypes.TownHall, colony.TownHallPos, 0);
        if (colony.WarehousePos != null && GetBuilding(colony, colony.WarehousePos) == null)
        {
            AddBuilding(colony, BuildingTypes.Warehouse, colony.WarehousePos, 0);
        }
    }

    /// <summary>Blueprint rotation that turns a blueprint's front towards the player placing the hut.</summary>
    public static int RotationFor(IPlayer player)
    {
        var facing = BlockFacing.HorizontalFromYaw(player.Entity.Pos.Yaw);
        return facing.HorizontalAngleIndex * 90 % 360;
    }

    /// <summary>
    /// Blueprint rotation for a building type: one with a door is turned so the door's side faces the player
    /// placing it. Turning a blueprint by 90 moves each side one step clockwise (east to south).
    /// </summary>
    public int RotationFor(IPlayer player, string type, string style = BlueprintStyles.Default, string variant = "")
    {
        if (Blueprints.Get(style, type, 1, 0, variant)?.Front is not { } front) return RotationFor(player);
        int towards = BlockFacing.HorizontalFromYaw(player.Entity.Pos.Yaw).HorizontalAngleIndex;
        return GameMath.Mod(front - towards, 4) * 90;
    }

    /// <summary>
    /// A hut's storage: the hut block's own inventory first, then every storage block (chests, trunks, crates,
    /// clay storage vessels, other mods' storage) inside the building its blueprint makes, nearest the hut first.
    /// A hut without a blueprint uses the storage within HutStorageRadius of its block.
    /// </summary>
    public IEnumerable<BlockEntityContainer> HutStorage(Colony colony, Building building)
    {
        var ba = sapi.World.BlockAccessor;
        var hut = building.HutPos;
        if (ba.GetBlockEntity(hut) is BlockEntityContainer own)
        {
            yield return own;
        }
        else if (ba.GetBlock(hut) is Blocks.BlockColonyHut { EntityClass: { } entityClass })
        {
            // Huts placed before they had their own storage get it now.
            ba.SpawnBlockEntity(entityClass, hut);
            if (ba.GetBlockEntity(hut) is BlockEntityContainer added) yield return added;
        }
        // A work station uses its host's storage (ColonyManager.Stations.cs).
        if (building.IsStation)
        {
            if (HostOf(colony, building) is { } host) foreach (var c in HutStorage(colony, host)) yield return c;
            yield break;
        }
        foreach (var pos in StorageSpots(colony, building))
        {
            if (ba.GetBlockEntity(pos) is BlockEntityContainer c && IsStorageVessel(c)) yield return c;
        }
    }

    /// <summary>Where a building's storage blocks stand, looked up again every StorageRescanMs (finding them means looking through the whole building).</summary>
    readonly Dictionary<int, (List<BlockPos> Spots, long AtMs)> storageSpots = new();
    const long StorageRescanMs = 5000;

    List<BlockPos> StorageSpots(Colony colony, Building building)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (storageSpots.TryGetValue(building.BuildingId, out var cached) && now - cached.AtMs < StorageRescanMs && now >= cached.AtMs) return cached.Spots;

        var hut = building.HutPos;
        bool hasBlueprint = Blueprints.Get(building, Math.Max(1, building.Level)) is { Steps.Count: > 0 };
        var (min, max) = hasBlueprint ? BuildingBox(building)
            : (hut.AddCopy(-HutStorageRadius, -HutStorageRadius, -HutStorageRadius), hut.AddCopy(HutStorageRadius, HutStorageRadius, HutStorageRadius));
        // Other buildings' hut blocks and the drop-off points are never this hut's storage.
        var notOurs = new HashSet<BlockPos>(colony.Buildings.Where(b => b != building).Select(b => b.HutPos));
        notOurs.UnionWith(DropOffContainers(colony).Select(c => c.Pos));
        var spots = FindBlockEntities<BlockEntityContainer>(new Cuboidi(min.X, min.Y, min.Z, max.X, max.Y, max.Z))
            .Where(c => !c.Pos.Equals(hut) && !notOurs.Contains(c.Pos) && IsStorageVessel(c))
            .Select(c => c.Pos.Copy())
            .OrderBy(p => p.DistanceSqTo(hut.X, hut.Y, hut.Z))
            .ToList();
        storageSpots[building.BuildingId] = (spots, now);
        return spots;
    }

    /// <summary>
    /// True for blocks made to store things: chests, trunks, crates, clay storage vessels and other mods' storage
    /// blocks (by their class name, or the config's HutStorageBlocks), but not fire pits, ovens, shelves or barrels.
    /// </summary>
    public bool IsStorageVessel(BlockEntityContainer c)
    {
        if (c is BlockEntityGenericTypedContainer or BlockEntityGenericContainer or BlockEntityCrate) return true; // chests, trunks, clay storage vessels, crates
        var code = c.Block?.Code;
        if (code != null && (Config.HutStorageBlocks ?? Array.Empty<string>()).Any(p => WildcardUtil.Match(new AssetLocation(p), code))) return true;
        // Food Shelves' baskets and cabinets are the Cook's serving shelves (ColonyManager.FoodShelves.cs).
        if (code?.Domain == "foodshelves") return false;
        string name = c.GetType().Name;
        if (NotStorageNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return false;
        return StorageNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    static readonly string[] StorageNames = { "Chest", "Trunk", "Crate", "Vessel", "Storage", "Basket", "Cabinet", "Drawer", "Coffer", "Box" };
    static readonly string[] NotStorageNames = { "Firepit", "Oven", "Kiln", "Forge", "Display", "Shelf", "Barrel", "Quern", "Bloomery", "Mold", "Trough", "Rack", "Pot", "Bucket", "Bowl" };

    // ---- Work orders ----------------------------------------------------------------------

    /// <summary>Asks for the next level of a building. Returns a lang key on failure.</summary>
    public string? StartWorkOrder(Colony colony, Building building)
    {
        if (building.HasWorkOrder) return "createacolony:build-already";
        if (building.Removing) return "createacolony:remove-already";
        if (building.Type == BuildingTypes.MineshaftEntrance) return StartEntrance(colony, building);
        if (building.IsStation) return "createacolony:station-no-levels";
        int next = building.Level + 1;
        if (next > Blueprints.MaxLevel(building) || Blueprints.Get(building, next) == null) return "createacolony:build-no-blueprint";

        building.TargetLevel = next;
        building.Cursor = 0;
        building.BuilderCitizenId = 0;
        BuildStatus.Remove(building.BuildingId);
        ForgetStepFailures(building.BuildingId);
        MarkDirty(colony);
        return null;
    }

    public void CancelWorkOrder(Colony colony, Building building)
    {
        building.Upkeep = false;
        building.Repairing = false;
        building.TargetLevel = 0;
        building.Cursor = 0;
        building.BuilderCitizenId = 0;
        BuildStatus.Remove(building.BuildingId);
        ForgetStepFailures(building.BuildingId);
        CancelBuildRequests(colony, building);
        MarkDirty(colony);
    }

    /// <summary>The builder's hut a builder works from, assigning a free one if needed.</summary>
    public Building? EnsureBuilderHut(Colony colony, CitizenRecord builder) => EnsureWorkplace(colony, builder, BuildingTypes.Builder);

    public void CompleteWorkOrder(Colony colony, Building building)
    {
        if (building.Repairing)
        {
            FinishRepair(colony, building);
            return;
        }
        if (building.Upkeep)
        {
            // Upkeep done: the building stays at its level, quietly.
            building.Upkeep = false;
            building.TargetLevel = 0;
            building.Cursor = 0;
            building.BuilderCitizenId = 0;
            BuildStatus.Remove(building.BuildingId);
        ForgetStepFailures(building.BuildingId);
            CancelBuildRequests(colony, building);
            MarkDirty(colony);
            return;
        }
        building.Level = building.TargetLevel;
        building.TargetLevel = 0;
        building.Cursor = 0;
        building.BuilderCitizenId = 0;
        BuildStatus.Remove(building.BuildingId);
        ForgetStepFailures(building.BuildingId);
        CancelBuildRequests(colony, building);
        MarkDirty(colony);
        RefreshClaim(colony);

        LogEvent(colony, "event-built", "@building-" + building.Type, building.Level.ToString());
        // A new Armourer or Weaponsmith takes over its kind of smithing from the Blacksmith.
        TakeOverSmithing(colony, building);
        if (colony.BannerOnNew) ReplaceBanners(colony, new[] { building });
        string message = Lang.Get("createacolony:build-finished", Lang.Get("createacolony:building-" + building.Type), building.Level);
        foreach (var member in colony.Members)
        {
            if (sapi.World.PlayerByUid(member.PlayerUid) is IServerPlayer p && p.ConnectionState == EnumClientState.Playing)
            {
                p.SendMessage(0, message, EnumChatType.Notification);
            }
        }
    }

    public static string RequestTag(Building building) => "building:" + building.BuildingId;

    /// <summary>Turns "any wood or stone" on or off. Open material requests are dropped so builders ask again in the new terms.</summary>
    public void SetAnyWoodOrStone(Colony colony, bool on)
    {
        if (colony.AnyWoodOrStone == on) return;
        colony.AnyWoodOrStone = on;
        foreach (var b in colony.Buildings.Where(b => b.HasWorkOrder)) CancelBuildRequests(colony, b);
        MarkDirty(colony);
    }

    void CancelBuildRequests(Colony colony, Building building)
    {
        string tag = RequestTag(building);
        colony.Requests.RemoveAll(r => r.RequestedBy == tag);
    }

    // ---- Steps ----------------------------------------------------------------------------

    public BlockPos StepPos(Building building, BuildStep step) => building.HutPos.AddCopy(step.Dx + building.XOffset, step.Dy + building.YOffset, step.Dz + building.ZOffset);

    /// <summary>
    /// True when the world already matches the step, or the step must not be touched: in someone else's
    /// land claim, a hut block, or a container (a chest full of the player's things is never torn down).
    /// A hut only claims land once it is built, so its blueprint may reach past the colony's claim into
    /// unclaimed land; builders build there too.
    /// </summary>
    public bool IsStepDone(Colony colony, BuildStep step, BlockPos pos, Building? building = null)
    {
        if (!CanWorkAt(colony, pos)) return true;
        // A repair leaves furniture and decorations alone (ColonyManager.Repair.cs).
        if (building is { Repairing: true } && IsKeptInRepair(step, pos)) return true;
        var ba = sapi.World.BlockAccessor;
        var current = ba.GetBlock(pos);
        if (current.Id == step.BlockId || Fulfils(colony, current, step.BlockId, pos)) return true;
        if (GetBuilding(colony, pos) != null) return true;
        // A half-built fire pit already has its container (the fuel slot), but the firewood still has to go on.
        // A wattle fence or a first coat of daub waits for more daub the same way.
        if (InProgress(colony, current, step.BlockId, pos)) return false;
        if (ba.GetBlockEntity(pos) is BlockEntityContainer) return true;
        // The top half of a door belongs to the door below it; clearing it would break the door. The same goes
        // for the other cells of a furniture shelf.
        if (step.BlockId == 0 && (current is BlockMultiblock || IsFurnitureCell(pos))) return true;
        // Water and lava are left alone when clearing; builders do not drain lakes.
        if (step.BlockId == 0 && current.IsLiquid()) return true;
        return false;
    }

    /// <summary>Finds the next step that still needs doing, starting at the cursor and wrapping round once.</summary>
    public bool FindNextStep(Colony colony, Building building, Blueprint blueprint, out BuildStep step, out BlockPos pos)
    {
        // Gaps under the floor are filled first, from the ground up (ColonyManager.Foundations.cs).
        var skipped = skippedSteps.GetValueOrDefault(building.BuildingId);
        (BuildStep Step, BlockPos Pos)? setAside = null;
        foreach (var gap in FoundationGaps(colony, building, blueprint))
        {
            if (IsStepDone(colony, gap.Step, gap.Pos)) continue;
            if (skipped != null && skipped.Contains(gap.Pos))
            {
                setAside ??= (gap.Step, gap.Pos);
                continue;
            }
            step = gap.Step;
            pos = gap.Pos;
            return true;
        }
        int count = blueprint.Steps.Count;
        for (int n = 0; n < count; n++)
        {
            int i = (building.Cursor + n) % count;
            step = blueprint.Steps[i];
            pos = StepPos(building, step);
            if (IsStepDone(colony, step, pos, building)) continue;
            // Upkeep only digs out ground that got in; everything else in the building stays as it is.
            if (building.Upkeep && !IsUpkeepStep(step, pos)) continue;
            // A block the builder kept failing at waits until the rest is done.
            if (skipped != null && skipped.Contains(pos))
            {
                setAside ??= (step, pos);
                continue;
            }
            if (building.Cursor != i)
            {
                building.Cursor = i;
                MarkDirty(colony);
            }
            return true;
        }
        if (setAside is { } last)
        {
            // Only set-aside blocks are left: give them all another go. One that fails again shows as stuck.
            retryingSteps.Add(building.BuildingId);
            skipped!.Clear();
            step = last.Step;
            pos = last.Pos;
            return true;
        }
        step = default;
        pos = building.HutPos;
        return false;
    }

    /// <summary>Materials still needed for the rest of the work order, by item code (a pattern when any wood or stone will do).</summary>
    public Dictionary<string, (ItemStack Sample, int Count)> MaterialsNeeded(Colony colony, Building building, Blueprint blueprint)
    {
        var needs = new Dictionary<string, (ItemStack, int)>();
        if (!Config.BuildersNeedMaterials) return needs;
        var foundation = FoundationGaps(colony, building, blueprint).Where(g => !IsStepDone(colony, g.Step, g.Pos)).ToList();
        foreach (var (step, pos) in building.Upkeep ? foundation : foundation.Concat(blueprint.Steps.Select(s => (s, StepPos(building, s)))))
        {
            if (step.BlockId == 0) continue;
            if (IsStepDone(colony, step, pos, building)) continue;
            foreach (var cost in RemainingCost(colony, step, pos))
            {
                needs[cost.Code] = needs.TryGetValue(cost.Code, out var n) ? (n.Item1, n.Item2 + cost.Count) : (cost.Sample, cost.Count);
            }
        }
        return needs;
    }

    /// <summary>
    /// Makes sure couriers are asked for whatever the builder's hut is short of. Returns false when
    /// the builder's hut has no chest to deliver to.
    /// </summary>
    public bool EnsureMaterialRequests(Colony colony, Building order, Building builderHut, CitizenRecord builder, Blueprint blueprint)
    {
        var storage = HutStorage(colony, builderHut).Cast<BlockEntityContainer>().ToList();
        if (storage.Count == 0) return false;

        string tag = RequestTag(order);
        foreach (var (code, (sample, count)) in MaterialsNeeded(colony, order, blueprint))
        {
            var match = Matcher(code);
            int have = CountItems(storage, match) + PackCount(builder, match);
            var open = colony.Requests.Where(r => r.RequestedBy == tag && r.ItemCode == code).ToList();
            int coming = open.Sum(r => r.Remaining);
            int shortfall = count - have - coming;
            if (shortfall > 0) AddRequest(colony, code, shortfall, storage[0].Pos, tag);
            // Things a player put into the storage by hand count too (0.200.0): requests nobody is carrying for
            // shrink by what is already there, so couriers don't bring it a second time.
            int surplus = have + coming - open.Sum(r => System.Math.Min(r.Extra, r.Remaining)) - count;
            foreach (var request in open.Where(r => r.LeasedByCitizenId == 0))
            {
                if (surplus <= 0) break;
                int part = System.Math.Min(surplus, request.Remaining);
                RecordDelivery(colony, request, part);
                surplus -= part;
            }
        }
        return true;
    }

    /// <summary>
    /// A chest at a worker's hut that holds something its worker does not need (cleared dirt and
    /// leftovers at a Builder's Hut, logs and crops at a Forester's or Farmer's Hut), and a test for
    /// which items are not needed. Couriers take those to the warehouse.
    /// </summary>
    public (BlockEntityContainer Chest, System.Func<ItemStack, bool> IsJunk)? FindHutJunk(Colony colony)
    {
        foreach (var hut in HutsForPickup(colony).ToList())
        {
            // Request pickup now: everything goes, until the hut's storage is empty.
            System.Func<ItemStack, bool> isJunk = hut.PickupNow ? _ => true : SpareAt(colony, hut);
            var chest = HutStorage(colony, hut).FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && isJunk(s.Itemstack)));
            if (chest != null) return (chest, isJunk);
            if (hut.PickupNow)
            {
                hut.PickupNow = false;
                MarkDirty(colony);
            }
        }
        return null;
    }

    /// <summary>What a hut's storage can give away: what its worker does not need and its Minimum stock tab does not keep.</summary>
    public System.Func<ItemStack, bool> SpareAt(Colony colony, Building hut)
    {
        var isJunk = hut.Type == BuildingTypes.Builder ? BuilderHutJunk(colony, hut) : WorkerHutJunk(colony, hut);
        if (hut.MinStock.Count == 0) return isJunk;
        return s => isJunk(s) && !IsMinStock(hut, s);
    }

    /// <summary>At a Builder's Hut everything but the current work order's materials and the builder's tools.</summary>
    System.Func<ItemStack, bool> BuilderHutJunk(Colony colony, Building hut)
    {
        var needed = new List<System.Func<ItemStack, bool>>();
        var builder = colony.Citizens.Find(c => c.Alive && c.JobCode == Jobs.Builder && c.WorkplacePos != null && c.WorkplacePos.Equals(hut.HutPos));
        var order = builder == null ? null : colony.Buildings.Find(b => b.HasWorkOrder && b.BuilderCitizenId == builder.CitizenId);
        var blueprint = order == null ? null : Blueprints.Get(order, order.TargetLevel);
        if (blueprint != null) needed.AddRange(MaterialsNeeded(colony, order!, blueprint).Keys.Select(Matcher));
        return s => !needed.Any(m => m(s)) && !IsBuilderTool(s);
    }

    // ---- Changing the world ---------------------------------------------------------------

    /// <summary>Removes a block for construction; what it drops goes into the builder's hut storage.</summary>
    public void BreakForBuild(Colony colony, Building builderHut, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var old = ba.GetBlock(pos);
        if (old.Id == 0) return;

        var drops = DropsOf(old, pos); // some blocks expect a player when dropping: those drop nothing
        BreakQuietly(pos);

        // Into the chests beside the hut first and the hut block last (0.200.0): dug-up soil filled the hut block's
        // own slots within minutes, and then nothing could be given or delivered to the hut.
        var storage = HutStorage(colony, builderHut).ToList();
        if (storage.Count > 1 && storage[0].Pos.Equals(builderHut.HutPos)) storage = storage.Skip(1).Append(storage[0]).ToList();
        foreach (var drop in drops)
        {
            foreach (var container in storage)
            {
                if (drop.StackSize <= 0) break;
                PutItems(container, drop);
            }
            if (drop.StackSize > 0) sapi.World.SpawnItemEntity(drop, pos.ToVec3d().Add(0.5, 0.5, 0.5));
        }
    }

    public void PlaceForBuild(int blockId, BlockPos pos, ItemStack? fromItem = null)
    {
        var ba = sapi.World.BlockAccessor;
        var block = sapi.World.GetBlock(blockId);
        // A fence joins the neighbours it has here, as when a player places one.
        if (block is BlockFence fence && sapi.World.GetBlock(fence.CodeWithVariant("type", fence.GetOrientations(sapi.World, pos))) is { } joined)
        {
            block = joined;
            blockId = joined.Id;
        }
        // Furniture and banner poles go down from their item, which gives them their wood and puts down their other parts.
        if (fromItem != null) ba.SetBlock(blockId, pos, WithLanternParts(fromItem));
        else ba.SetBlock(blockId, pos);
        ba.TriggerNeighbourBlockUpdate(pos);
        if (block.Sounds?.Place != null) sapi.World.PlaySoundAt(block.Sounds.Place, pos, 0);
    }

    bool IsFurnitureCell(BlockPos pos) => sapi.World.BlockAccessor.GetBlockEntity(pos)?.GetType().Name == "BlockEntityFurnitureDisplayCell";

    /// <summary>Done and total steps, for the progress bar. Open windows refresh every few seconds, so it is cached briefly.</summary>
    public (int Done, int Total) Progress(Colony colony, Building building, Blueprint blueprint) =>
        ForWindow(colony, building, blueprint, "progress", () =>
        {
            int done = blueprint.Steps.Count(s => IsStepDone(colony, s, StepPos(building, s), building));
            return (done, blueprint.Steps.Count);
        });

    /// <summary>What is still needed for an open window: the same as MaterialsNeeded, cached briefly.</summary>
    public Dictionary<string, (ItemStack Sample, int Count)> MaterialsForWindow(Colony colony, Building building, Blueprint blueprint) =>
        ForWindow(colony, building, blueprint, "materials", () => MaterialsNeeded(colony, building, blueprint));

    /// <summary>The tools still needed, for an open window, cached briefly.</summary>
    public HashSet<ToolNeed> ToolsForWindow(Colony colony, Building building, Blueprint blueprint) =>
        ForWindow(colony, building, blueprint, "tools", () => ToolsNeeded(colony, building, blueprint));

    /// <summary>
    /// Window figures, by colony, building and what they are. Each walks the whole blueprint, and windows ask
    /// for them every few seconds while a building is under way; a few seconds old is fine to look at.
    /// </summary>
    readonly Dictionary<(string, int, string), (Blueprint Blueprint, long AtMs, object Value)> windowCache = new();
    const long WindowCacheMs = 5000;

    T ForWindow<T>(Colony colony, Building building, Blueprint blueprint, string what, Func<T> compute) where T : notnull
    {
        var key = (colony.ColonyId, building.BuildingId, what);
        long now = sapi.World.ElapsedMilliseconds;
        if (windowCache.TryGetValue(key, out var hit) && hit.Blueprint == blueprint && now - hit.AtMs < WindowCacheMs) return (T)hit.Value;
        var value = compute();
        if (windowCache.Count > 500) windowCache.Clear();
        windowCache[key] = (blueprint, now, value);
        return value;
    }
}
