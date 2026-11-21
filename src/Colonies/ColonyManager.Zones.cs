using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Building zones and home upkeep. Every building's zone is the ground its blueprint covers (the same box
/// banners and kitchen shelves use, one block wider all round). A settler's home is the building whose zone
/// holds its bed, best a Residence. Settlers without a job look after their home: for now that means the
/// farmland inside it, worked the way a farmer works a field (ColonyManager.Farming.cs), with seeds, hoes and
/// watering cans from chests in the home, else from the warehouse. More home duties (repairs, tidying) can
/// be added to HomeDuties.
/// </summary>
public partial class ColonyManager
{
    const long ZoneCacheMs = 10_000;
    const long PlotCacheMs = 30_000;

    readonly Dictionary<Colony, (long Until, List<(Building B, BlockPos Min, BlockPos Max)> Zones)> zoneCache = new();
    readonly Dictionary<int, (long Until, List<BlockPos> Plots)> plotCache = new();

    /// <summary>The blocks a building's zone covers.</summary>
    public (BlockPos Min, BlockPos Max) ZoneOf(Building building) => BuildingBox(building);

    /// <summary>Every building's zone, worked out again every few seconds.</summary>
    public List<(Building B, BlockPos Min, BlockPos Max)> Zones(Colony colony)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (zoneCache.TryGetValue(colony, out var known) && known.Until > now) return known.Zones;
        var zones = colony.Buildings.Where(b => !b.IsStation).Select(b => { var (min, max) = BuildingBox(b); return (b, min, max); }).ToList();
        zoneCache[colony] = (now + ZoneCacheMs, zones);
        return zones;
    }

    static bool InBox(BlockPos pos, BlockPos min, BlockPos max) =>
        pos.X >= min.X && pos.X <= max.X && pos.Y >= min.Y && pos.Y <= max.Y && pos.Z >= min.Z && pos.Z <= max.Z;

    /// <summary>
    /// The building whose zone a block lies in, or null. Where zones overlap, a Residence wins, then the
    /// smallest zone (a hut standing inside a bigger building's yard).
    /// </summary>
    public Building? ZoneAt(Colony colony, BlockPos pos) => Zones(colony)
        .Where(z => InBox(pos, z.Min, z.Max))
        .OrderByDescending(z => IsHome(z.B.Type))
        .ThenBy(z => (long)(z.Max.X - z.Min.X + 1) * (z.Max.Y - z.Min.Y + 1) * (z.Max.Z - z.Min.Z + 1))
        .Select(z => z.B)
        .FirstOrDefault();

    /// <summary>A settler's home: the building its bed is in, or null when it has no bed or the bed is in no building.</summary>
    public Building? HomeOf(Colony colony, CitizenRecord citizen) =>
        citizen.HomeBedPos == null ? null : ZoneAt(colony, citizen.HomeBedPos);

    /// <summary>
    /// True for a settler who has no job and so looks after its home and helps others: a grown-up colony
    /// member who is not knocked out.
    /// </summary>
    public static bool IsJobless(CitizenRecord citizen) =>
        string.IsNullOrEmpty(citizen.JobCode) && citizen.Alive && !citizen.Visitor && !citizen.WaitingForRoom && !citizen.IsChild && !citizen.IsDowned;

    // ---- Home plots -----------------------------------------------------------------------

    /// <summary>
    /// The ground of a home's crop plots: every farmland block inside its zone, plus the spots where its blueprint
    /// has farmland (so soil there is tilled, also after it was trampled back to soil). Farmland that belongs to a farmer's field is left to the farmer.
    /// </summary>
    public List<BlockPos> HomePlots(Colony colony, Building home)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (plotCache.TryGetValue(home.BuildingId, out var known) && known.Until > now) return known.Plots;

        var (min, max) = ZoneOf(home);
        var plots = new HashSet<BlockPos>();
        foreach (var farmland in FindBlockEntities<BlockEntityFarmland>(new Cuboidi(min.X, min.Y, min.Z, max.X, max.Y, max.Z)))
        {
            plots.Add(farmland.Pos.Copy());
        }
        // Only once the building stands: while it is going up, the builders lay the farmland.
        if (home.Level >= 1 && Blueprints.Get(home, home.Level) is { } blueprint)
        {
            foreach (var step in blueprint.Steps)
            {
                if (sapi.World.GetBlock(step.BlockId)?.Code?.Path.StartsWith("farmland-") == true) plots.Add(StepPos(home, step));
            }
        }
        var list = plots.Where(p => !InFarmedField(colony, p)).ToList();
        plotCache[home.BuildingId] = (now + PlotCacheMs, list);
        return list;
    }

    /// <summary>True when the ground is part of a Field marker's square that a farmer looks after.</summary>
    bool InFarmedField(Colony colony, BlockPos ground)
    {
        int r = Math.Max(1, Config.FieldRadius);
        foreach (var field in colony.Fields)
        {
            if (Math.Abs(field.Pos.X - ground.X) > r || Math.Abs(field.Pos.Z - ground.Z) > r || Math.Abs(field.Pos.Y - ground.Y) > 3) continue;
            var farmer = colony.Citizens.Find(c => c.CitizenId == field.FarmerCitizenId);
            if (farmer != null && farmer.Alive && farmer.JobCode == Jobs.Farmer) return true;
        }
        return false;
    }

    /// <summary>The chests inside a home, where the household keeps its seeds, tools and harvest.</summary>
    public List<BlockEntityContainer> HomeChests(Building home)
    {
        var (min, max) = ZoneOf(home);
        var h = home.HutPos;
        return FindBlockEntities<BlockEntityGenericTypedContainer>(new Cuboidi(min.X, min.Y, min.Z, max.X, max.Y, max.Z))
            .OrderBy(c => c.Pos.DistanceSqTo(h.X, h.Y, h.Z))
            .Cast<BlockEntityContainer>()
            .ToList();
    }

    /// <summary>Where a settler can get something for its home duties: a chest in its home first, else the warehouse.</summary>
    public BlockEntityContainer? HomeSupply(Colony colony, Building home, ItemMatch match) =>
        HomeChests(home).Concat(WarehouseContainers(colony))
            .FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && match(s.Itemstack)));

    /// <summary>
    /// Puts what a settler gathered at home into its inventory. What does not fit goes into a chest in the home,
    /// and only if there is none with room does it land on the ground.
    /// </summary>
    public void StoreAtHome(Colony colony, Building home, CitizenRecord worker, IEnumerable<ItemStack> drops, BlockPos at)
    {
        var chests = HomeChests(home);
        foreach (var drop in drops)
        {
            if (drop == null || drop.StackSize <= 0) continue;
            AddToPack(colony, worker, drop);
            foreach (var chest in chests)
            {
                if (drop.StackSize <= 0) break;
                PutItems(chest, drop);
            }
            if (drop.StackSize > 0) sapi.World.SpawnItemEntity(drop, at.ToVec3d().Add(0.5, 0.5, 0.5));
        }
    }

    /// <summary>Harvests a ripe or dead crop on a home plot; the crop goes to the settler. Returns the crop's type.</summary>
    public string? HarvestAtHome(Colony colony, Building home, CitizenRecord worker, BlockPos ground)
    {
        var ba = sapi.World.BlockAccessor;
        var cropPos = ground.UpCopy();
        var crop = ba.GetBlock(cropPos);
        if (crop.Id == 0) return null;
        StoreAtHome(colony, home, worker, DropsOf(crop, cropPos), cropPos);
        BreakQuietly(cropPos);
        if (crop.Sounds?.Break != null) sapi.World.PlaySoundAt(crop.Sounds.Break, cropPos, 0);
        return crop is BlockCrop ? VariantOf(crop, "type") : null;
    }

    /// <summary>Pulls up weeds on a home plot.</summary>
    public bool WeedAtHome(Colony colony, Building home, CitizenRecord worker, BlockPos ground)
    {
        var pos = ground.UpCopy();
        var weed = sapi.World.BlockAccessor.GetBlock(pos);
        if (weed.Id == 0 || !Clearable(weed)) return false;
        StoreAtHome(colony, home, worker, DropsOf(weed, pos), pos);
        BreakQuietly(pos);
        return true;
    }

    /// <summary>Tills a home plot with the hoe in hand. Grass in the way goes to the settler.</summary>
    public bool TillAtHome(Colony colony, Building home, CitizenRecord worker, EntityAgent entity, BlockPos ground, ItemSlot hoe)
    {
        var abovePos = ground.UpCopy();
        var above = sapi.World.BlockAccessor.GetBlock(abovePos);
        if (above.Id != 0 && Clearable(above))
        {
            StoreAtHome(colony, home, worker, DropsOf(above, abovePos), abovePos);
            BreakQuietly(abovePos);
        }
        // With the way cleared, Till has nothing left to put in a hut chest.
        return Till(colony, home, worker, entity, ground, hoe);
    }

    /// <summary>Any field: a home plot takes whatever seed the settler has.</summary>
    public static readonly FieldRecord AnyCropField = new();
}
