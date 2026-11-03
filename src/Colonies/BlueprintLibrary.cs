using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// One block a builder has to get right. BlockId 0 means "clear this spot". Saved, when set, holds the
/// block's saved details from the export (a VS Roofing roof's frame, shape and materials).
/// </summary>
public readonly record struct BuildStep(int Dx, int Dy, int Dz, int BlockId, SavedBlockEntity? Saved = null);

/// <summary>
/// A block entity's data as the WorldEdit export stored it, with the export it came from: item and block
/// numbers inside the data are that world's, and the export's code lists turn them into this world's.
/// </summary>
public class SavedBlockEntity
{
    public required string Data;
    public required BlockSchematic Source;

    /// <summary>A fresh copy of the saved data; callers may change it.</summary>
    public TreeAttribute Tree() => Source.DecodeBlockEntityData(Data);
}

/// <summary>A blueprint turned into an ordered list of build steps, relative to the hut block.</summary>
public class Blueprint
{
    public required string Type;
    public required int Level;
    public required List<BuildStep> Steps;
    /// <summary>
    /// The side the building's entrance is on (0 east, 1 north, 2 west, 3 south, as BlockFacing's
    /// HorizontalAngleIndex), or null when the blueprint has no door. Worked out from the first door.
    /// </summary>
    public int? Front;
    /// <summary>
    /// Waypoint blocks in the blueprint, by kind ("mine", "quarry"), as offsets from the hut block: where
    /// the Miner's Hut's shaft or the Quarry's pit goes.
    /// </summary>
    public Dictionary<string, (int Dx, int Dy, int Dz)> Waypoints = new();
    /// <summary>
    /// The lowest solid block of each column the building stands on (at or below the hut block's level), as offsets:
    /// builders fill any gap under these down to the ground (ColonyManager.Foundations.cs).
    /// </summary>
    public List<(int Dx, int Dy, int Dz)> Footing = new();
}

/// <summary>
/// One piece of a Miner's Hut's mineshaft (BlueprintLibrary.GetMinePiece): its steps as offsets from where it
/// lines up (the entrance's Mine waypoint, a section's bottom middle), digging first, top down; the sides it
/// is open on, each with the middle of that way out and the height of its floor (offsets); and its box around that spot.
/// </summary>
public class MinePiece
{
    public required string Name;
    public required List<BuildStep> Steps;
    public required List<(BlockFacing Face, int X, int Y, int Z)> Open;
    public (int X, int Y, int Z) Min, Max;
    /// <summary>
    /// The entrance: how many layers lower each loop of its stairs is when it goes deeper (its ground layer
    /// down to the layer above its bottom; the next loop's ground layer sits on this one's floor); 0 for a section.
    /// </summary>
    public int Period;
    /// <summary>Where the piece's waypoint blocks were (offsets, as for Steps): the ground there is left alone, and nothing is filled in on or above them.</summary>
    public HashSet<(int X, int Y, int Z)> Markers = new();
    /// <summary>
    /// The entrance: the middle of the shaft down through its ground layer, as an offset; the level pieces hang
    /// under it. A level piece: always 0, as it lines up by the middle of the hole in its top layer.
    /// </summary>
    public (int X, int Z) Shaft;
    /// <summary>The entrance: where a Mineshaft Entrance's hut block sits in it (its createacolony:hut-mineshaftentrance block), as an offset; null when it has none.</summary>
    public (int X, int Y, int Z)? HutSpot;

    readonly Dictionary<int, MinePiece> deeper = new();

    /// <summary>
    /// The entrance gone deeper: its ground layer and everything below it (the stairs round the opening) copied
    /// this many times underneath, each copy Period layers lower, as in Aaron's example. Where a copy's ground
    /// layer meets the floor of the loop above, the copy's blocks win and the floor's stay where the copy has
    /// natural ground. Every loop has the entrance's open sides, so tunnels can start on each of them.
    /// </summary>
    public MinePiece Deeper(int repeats)
    {
        if (repeats <= 0 || Period <= 0) return this;
        if (deeper.TryGetValue(repeats, out var piece)) return piece;
        var band = Steps.Where(s => s.Dy <= 0).ToList();
        var cells = new Dictionary<(int, int, int), BuildStep>();
        foreach (var step in Steps) cells[(step.Dx, step.Dy, step.Dz)] = step;
        var open = new List<(BlockFacing Face, int X, int Y, int Z)>(Open);
        var markers = new HashSet<(int X, int Y, int Z)>(Markers);
        for (int k = 1; k <= repeats; k++)
        {
            foreach (var step in band) cells[(step.Dx, step.Dy - k * Period, step.Dz)] = step with { Dy = step.Dy - k * Period };
            open.AddRange(Open.Select(o => (o.Face, o.X, o.Y - k * Period, o.Z)));
            foreach (var m in Markers.Where(m => m.Y <= 0)) markers.Add((m.X, m.Y - k * Period, m.Z));
        }
        // Digging first; the miner sorts the rest as it works (ColonyManager.ScanMineshaft).
        var steps = cells.Values.OrderBy(s => s.BlockId == 0 ? 0 : 1).ToList();
        piece = new MinePiece { Name = Name, Steps = steps, Open = open, Min = (Min.X, Min.Y - repeats * Period, Min.Z), Max = Max, Period = Period, Markers = markers, Shaft = Shaft, HutSpot = HutSpot };
        deeper[repeats] = piece;
        return piece;
    }
}

/// <summary>
/// Finds and prepares blueprints. A blueprint is a normal vanilla WorldEdit export (/we export), so
/// buildings are made in-game. They are looked up as blueprints/&lt;type&gt;/level&lt;N&gt;.json for the
/// Testing style and blueprints/&lt;style&gt;/&lt;type&gt;/level&lt;N&gt;.json for the others, first in
/// ModConfig/createacolony/ (so players can add their own without rebuilding the mod), then in the
/// mod's own assets. If the export contains the building's hut block, that is where the hut sits;
/// otherwise the hut goes at the bottom centre of the blueprint.
/// A type can have variants, other buildings of the same type that players pick between when placing
/// one (a small or a medium house): blueprints/&lt;style&gt;/&lt;type&gt;/&lt;variant&gt;/level&lt;N&gt;.json. The
/// files right in the type's folder are the variant named "" and come first.
/// </summary>
public class BlueprintLibrary
{
    public const int MaxLevels = 5;

    readonly ICoreServerAPI sapi;
    readonly ColonyConfig config;
    readonly Dictionary<(string, string, string, int, int), Blueprint?> cache = new();
    readonly Dictionary<(string Style, string Type, string Variant), int> maxLevels = new();
    readonly Dictionary<(string Style, string Type), List<string>> variants = new();

    public BlueprintLibrary(ICoreServerAPI sapi, ColonyConfig config)
    {
        this.sapi = sapi;
        this.config = config;
    }

    public static string UserFolder => Path.Combine(GamePaths.ModConfig, "createacolony", "blueprints");

    /// <summary>The highest level that has a blueprint in this style and variant (the first variant when it has none of that name), 0 if none.</summary>
    public int MaxLevel(string style, string type, string variant = "")
    {
        style = BlueprintStyles.Normalize(style);
        variant = VariantFor(style, type, variant);
        if (maxLevels.TryGetValue((style, type, variant), out int known)) return known;
        int max = 0;
        if (type == BuildingTypes.MineshaftEntrance) max = EntranceStyle(style) != null ? 1 : 0;
        else
        for (int level = 1; level <= MaxLevels; level++)
        {
            if (FindJson(style, type, variant, level) != null) max = level;
            else break;
        }
        maxLevels[(style, type, variant)] = max;
        return max;
    }

    /// <summary>
    /// The variants of a type in this style that have a level 1: "" (the files right in the type's folder)
    /// first, then its subfolders in alphabetical order. Empty when the type has no blueprints here.
    /// </summary>
    public List<string> Variants(string style, string type)
    {
        style = BlueprintStyles.Normalize(style);
        if (variants.TryGetValue((style, type), out var known)) return known;
        string folder = BlueprintStyles.Folder(style);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        string userType = Path.Combine(UserFolder, folder, type);
        if (Directory.Exists(userType))
        {
            foreach (var dir in Directory.GetDirectories(userType)) names.Add(Path.GetFileName(dir));
        }
        string prefix = $"config/blueprints/{folder}/{type}/";
        foreach (var asset in sapi.Assets.GetMany(prefix, CreateAColonyModSystem.ModId, false))
        {
            var rest = asset.Location.Path.Substring(prefix.Length).Split('/');
            if (rest.Length == 2) names.Add(rest[0]);
        }
        var list = new List<string>();
        if (type == BuildingTypes.MineshaftEntrance)
        {
            if (EntranceStyle(style) != null) list.Add("");
            variants[(style, type)] = list;
            return list;
        }
        if (FindJson(style, type, "", 1) != null) list.Add("");
        list.AddRange(names.Where(n => IsVariantName(n) && FindJson(style, type, n, 1) != null));
        variants[(style, type)] = list;
        return list;
    }

    /// <summary>Variant names are plain lower-case words and numbers, so they are safe in file paths and messages.</summary>
    public static bool IsVariantName(string name) => name.Length > 0 && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>The variant itself when the type has it in this style, otherwise its first one ("" when it has none).</summary>
    public string VariantFor(string style, string type, string? variant)
    {
        var list = Variants(style, type);
        return variant != null && list.Contains(variant) ? variant : list.FirstOrDefault() ?? "";
    }

    /// <summary>Forgets loaded blueprints, so edited files are picked up without a restart.</summary>
    public void Reload()
    {
        cache.Clear();
        mineCache.Clear();
        maxLevels.Clear();
        variants.Clear();
    }

    public int MaxLevel(Building building) => building.IsStation ? 0 : MaxLevel(StyleFor(building), building.Type, building.Variant);

    /// <summary>
    /// The style a building's blueprints come from: always its own asset pack. When that pack has no design for
    /// the type, the building has no blueprints (only its hut block is placed), so players can build their own
    /// around it; it never borrows another pack's design.
    /// </summary>
    public string StyleFor(Building building) => StyleFor(building.Style, building.Type);

    /// <summary>The pack the type's blueprints are looked up in: the pack itself, never another one.</summary>
    public string StyleFor(string? style, string type) => BlueprintStyles.Normalize(style);

    public Blueprint? Get(string style, string type, int level, int rotation, string variant = "")
    {
        style = BlueprintStyles.Normalize(style);
        variant = VariantFor(style, type, variant);
        var key = (style, type, variant, level, rotation);
        if (!cache.TryGetValue(key, out var blueprint))
        {
            blueprint = Load(style, type, variant, level, rotation);
            cache[key] = blueprint;
        }
        return blueprint;
    }

    /// <summary>The building's blueprint for a level, in its own style, variant and turn.</summary>
    /// <summary>A work station never has one: it is only its block.</summary>
    public Blueprint? Get(Building building, int level) => building.IsStation ? null : Get(StyleFor(building), building.Type, level, building.Rotation, building.Variant);

    string? FindJson(string style, string type, string variant, int level)
    {
        string folder = BlueprintStyles.Folder(style);
        string typeFolder = variant == "" ? type : $"{type}/{variant}";
        string file = Path.Combine(folder == "" ? UserFolder : Path.Combine(UserFolder, folder), typeFolder, $"level{level}.json");
        if (File.Exists(file)) return File.ReadAllText(file);
        string asset = folder == "" ? $"config/blueprints/{typeFolder}/level{level}.json" : $"config/blueprints/{folder}/{typeFolder}/level{level}.json";
        return sapi.Assets.TryGet(new AssetLocation(CreateAColonyModSystem.ModId, asset))?.ToText();
    }

    Blueprint? Load(string style, string type, string variant, int level, int rotation)
    {
        if (type == BuildingTypes.MineshaftEntrance) return EntranceBlueprint(style, level, rotation);
        string? json = FindJson(style, type, variant, level);
        if (variant != "") style += "/" + variant;
        if (json == null) return null;

        string error = "";
        var schematic = BlockSchematic.LoadFromString(json, ref error);
        if (schematic == null)
        {
            sapi.Logger.Error("[createacolony] Blueprint {0} level {1} ({2}) could not be read: {3}", type, level, style, error);
            return null;
        }
        schematic.Init(sapi.World.BlockAccessor);
        if (rotation != 0) schematic.TransformWhilePacked(sapi.World, EnumOrigin.BottomCenter, rotation);

        var ba = sapi.World.BlockAccessor;
        var blocks = new Dictionary<(int, int, int), Block>();
        var missing = new HashSet<string>();
        for (int i = 0; i < schematic.Indices.Count; i++)
        {
            uint index = schematic.Indices[i];
            int dx = (int)(index & BlockSchematic.PosBitMask);
            int dy = (int)((index >> 20) & BlockSchematic.PosBitMask);
            int dz = (int)((index >> 10) & BlockSchematic.PosBitMask);
            var code = schematic.BlockCodes[schematic.BlockIds[i]];
            var block = ba.GetBlock(code);
            // A torch that burned out while the building was being saved is built as a lit one.
            if (block != null && ColonyManager.IsTorch(block) && ba.GetBlock(ColonyManager.LitTorch(block.Code)) is { } lit) block = lit;
            if (block == null) missing.Add(code.ToString());
            else blocks[(dx, dy, dz)] = block;
        }
        if (missing.Count > 0)
        {
            sapi.Logger.Warning("[createacolony] Blueprint {0} level {1} ({2}) uses blocks that are not installed, they will be skipped: {3}",
                type, level, style, string.Join(", ", missing));
        }

        // Saved details for the blocks the config lets builders copy (never chests: their contents would come free).
        var saved = new Dictionary<(int, int, int), SavedBlockEntity>();
        foreach (var (index, data) in schematic.BlockEntities)
        {
            var at = ((int)(index & BlockSchematic.PosBitMask), (int)((index >> 20) & BlockSchematic.PosBitMask), (int)((index >> 10) & BlockSchematic.PosBitMask));
            if (blocks.TryGetValue(at, out var withData) && KeepsDetails(withData)) saved[at] = new SavedBlockEntity { Data = data, Source = schematic };
        }

        int? front = rotation == 0 ? EntranceSide(schematic, blocks) : null;

        // The lowest and highest block of each column, and the lowest built one (not natural ground). Empty space
        // below the hut's floor is dug out when it is a cellar (blocks under and over it), or when part of the
        // building is under it: a sunken room, stairs or a path, which the ground on top would bury (Aaron's
        // build kept its grass there before 0.102.0). Over saved ground alone (a yard lower than the floor) the
        // ground is left alone, so no moat is dug round the house.
        var lowest = new Dictionary<(int, int), int>();
        var highest = new Dictionary<(int, int), int>();
        var lowestBuilt = new Dictionary<(int, int), int>();
        foreach (var ((x, y, z), b) in blocks)
        {
            if (!lowest.TryGetValue((x, z), out int low) || y < low) lowest[(x, z)] = y;
            if (!highest.TryGetValue((x, z), out int high) || y > high) highest[(x, z)] = y;
            if (b.Id != 0 && !IsNaturalGround(b) && !IsWaypoint(b) && b.BlockMaterial != EnumBlockMaterial.Plant
                && (!lowestBuilt.TryGetValue((x, z), out int built) || y < built)) lowestBuilt[(x, z)] = y;
        }
        bool InCellar(int x, int y, int z) =>
            (lowest.TryGetValue((x, z), out int low) && low < y && highest.TryGetValue((x, z), out int high) && high > y)
            || (lowestBuilt.TryGetValue((x, z), out int built) && built < y);

        // Where the hut block sits inside the blueprint.
        string hutCode = HutBlockCode(type);
        var anchor = blocks.FirstOrDefault(kv => kv.Value.Code.ToString() == hutCode).Key;
        if (!blocks.Any(kv => kv.Value.Code.ToString() == hutCode)) anchor = (schematic.SizeX / 2, 0, schematic.SizeZ / 2);
        var (ax, ay, az) = anchor;

        // Waypoints mark where a mine shaft or a quarry pit goes. The builder only clears their spot, and
        // leaves out the blueprint's blocks where the miner or quarrier will dig, so the two never fight.
        var waypoints = new Dictionary<string, (int Dx, int Dy, int Dz)>();
        foreach (var ((x, y, z), b) in blocks)
        {
            if (IsWaypoint(b)) waypoints[b.Variant["type"] ?? b.Code.Path] = (x - ax, y - ay, z - az);
        }
        var dugOut = DugOut(waypoints);

        // Items lying on the ground (ground storage) are left out too: an empty one is nothing, and a full one would come free.
        bool IsMeta(Block b) => b.Id == BlockSchematic.FillerBlockId || b.Id == BlockSchematic.PathwayBlockId
            || b.Id == BlockSchematic.UndergroundBlockId || b.Id == BlockSchematic.AbovegroundBlockId || b.Id == BlockSchematic.ConnectorBlockId
            || b is BlockGroundStorage;

        var clear = new List<BuildStep>();
        var solid = new List<BuildStep>();
        var decor = new List<BuildStep>();
        for (int dy = 0; dy < schematic.SizeY; dy++)
        for (int dx = 0; dx < schematic.SizeX; dx++)
        for (int dz = 0; dz < schematic.SizeZ; dz++)
        {
            if ((dx, dy, dz) == anchor) continue;
            if (blocks.TryGetValue((dx, dy, dz), out var block))
            {
                if (IsMeta(block)) continue;
                if (IsWaypoint(block))
                {
                    clear.Add(new BuildStep(dx - ax, dy - ay, dz - az, 0));
                    continue;
                }
                if (dugOut.Contains((dx - ax, dy - ay, dz - az))) continue;
                // The top half of a door (or another part of a big block) comes with the door itself; until then the spot is cleared.
                if (block is BlockMultiblock || IsFurniturePart(block, saved.GetValueOrDefault((dx, dy, dz)))
                    || IsShapelessChisel(block, saved.GetValueOrDefault((dx, dy, dz))))
                {
                    clear.Add(new BuildStep(dx - ax, dy - ay, dz - az, 0));
                    continue;
                }
                var step = new BuildStep(dx - ax, dy - ay, dz - az, block.Id, saved.GetValueOrDefault((dx, dy, dz)));
                if (block.Id == 0) clear.Add(step);
                else if (IsSolid(block)) solid.Add(step);
                else decor.Add(step);
            }
            else if (dugOut.Contains((dx - ax, dy - ay, dz - az))) continue;
            else if (dy >= ay || InCellar(dx, dy, dz))
            {
                // Empty space at or above the hut's floor is cleared. Below it, only in a cellar or over a sunken
                // part of the building; elsewhere the ground is left alone.
                clear.Add(new BuildStep(dx - ax, dy - ay, dz - az, 0));
            }
        }

        // What the building stands on: the lowest solid block of each column, where it is at or below the hut block,
        // except over a mine shaft or a quarry pit.
        var openColumns = dugOut.Select(d => (d.Item1, d.Item3)).Concat(waypoints.Values.Select(w => (w.Dx, w.Dz))).ToHashSet();
        var footing = solid
            .Where(s => !openColumns.Contains((s.Dx, s.Dz)))
            .GroupBy(s => (s.Dx, s.Dz))
            .Select(g => g.OrderBy(s => s.Dy).First())
            .Where(s => s.Dy <= 0)
            .Select(s => (s.Dx, s.Dy, s.Dz))
            .ToList();

        // Clear from the top down so nothing is left floating, then walls bottom-up, then torches, doors and the like.
        clear.Reverse();
        return new Blueprint { Type = type, Level = level, Steps = clear.Concat(solid).Concat(decor).ToList(), Front = front, Waypoints = waypoints, Footing = footing };
    }

    // ---- Mineshaft pieces ------------------------------------------------------------------

    /// <summary>The pieces a Miner's Hut's mineshaft is made of: the way in, and the sections it grows from there.</summary>
    public const string MineEntrance = "entrance";
    /// <summary>
    /// One level of the mine under the entrance (Aaron, 0.87.0): the same middle on every level, stacked under
    /// the entrance's shaft, with sections branching off its tunnel starts. Optional: without it the mine grows
    /// from the entrance alone, as before.
    /// </summary>
    public const string MineLevel = "level";
    public static readonly string[] MineSections = { "cross", "tee", "straight", "corner" };

    readonly Dictionary<(string, string, int), MinePiece?> mineCache = new();

    /// <summary>A mineshaft piece (blueprints/&lt;style&gt;/mineshaft/&lt;name&gt;.json) turned by rotation degrees, or null when there is none.</summary>
    /// <summary>The pack a Mineshaft Entrance's mine pieces come from: its own, or the first with an entrance (as ColonyManager.MineshaftStyle).</summary>
    public string? EntranceStyle(string style) =>
        BlueprintStyles.All.Prepend(BlueprintStyles.Normalize(style)).FirstOrDefault(s => GetMinePiece(s, MineEntrance, 0) != null);

    /// <summary>
    /// A Mineshaft Entrance's building (Aaron, 0.95.0): its pack's mineshaft entrance piece, with the hut block on
    /// the ground just outside it, on the side the player placed it from. It shows as the placement preview; the
    /// linked Miner's Huts' miners dig and build it, never a builder.
    /// </summary>
    Blueprint? EntranceBlueprint(string style, int level, int rotation)
    {
        if (level != 1 || EntranceStyle(style) is not { } from || GetMinePiece(from, MineEntrance, rotation) is not { } piece) return null;
        var (ex, ey, ez) = EntranceBlockSpot(piece, rotation);
        var steps = piece.Steps.Where(s => (s.Dx, s.Dy, s.Dz) != (ex, ey, ez)).Select(s => s with { Dx = s.Dx - ex, Dy = s.Dy - ey, Dz = s.Dz - ez }).ToList();
        return new Blueprint { Type = BuildingTypes.MineshaftEntrance, Level = 1, Steps = steps };
    }

    /// <summary>
    /// Where the Mineshaft Entrance's hut block sits, in the entrance piece's offsets: where the piece has its hut
    /// block, else on the ground just outside the middle of its side towards the player.
    /// </summary>
    public static (int X, int Y, int Z) EntranceBlockSpot(MinePiece piece, int rotation)
    {
        if (piece.HutSpot is { } spot) return spot;
        var towards = BlockFacing.HORIZONTALS_ANGLEORDER[GameMath.Mod(rotation / 90, 4)].Normali;
        int midX = (piece.Min.X + piece.Max.X) / 2, midZ = (piece.Min.Z + piece.Max.Z) / 2;
        if (towards.X != 0) return (towards.X > 0 ? piece.Max.X + 1 : piece.Min.X - 1, 1, midZ);
        return (midX, 1, towards.Z > 0 ? piece.Max.Z + 1 : piece.Min.Z - 1);
    }

    public MinePiece? GetMinePiece(string style, string name, int rotation)
    {
        style = BlueprintStyles.Normalize(style);
        var key = (style, name, rotation);
        if (!mineCache.TryGetValue(key, out var piece)) mineCache[key] = piece = LoadMinePiece(style, name, rotation);
        return piece;
    }

    string? FindMineJson(string style, string name)
    {
        string folder = BlueprintStyles.Folder(style);
        string file = Path.Combine(UserFolder, folder, "mineshaft", name + ".json");
        if (File.Exists(file)) return File.ReadAllText(file);
        return sapi.Assets.TryGet(new AssetLocation(CreateAColonyModSystem.ModId, $"config/blueprints/{folder}/mineshaft/{name}.json"))?.ToText();
    }

    /// <summary>
    /// Reads a mineshaft piece. Unlike a building, its natural ground (rock, soil, gravel, sand, ore) is
    /// left as the miner finds it, every empty spot in it is dug out, and everything else (planks, logs,
    /// ladders, torches) is built. The entrance lines up by the middle of its ground layer with the ground
    /// beside the hut, and tunnels start off it at its rows of Mine waypoints; a section lines up by the
    /// middle of its bottom, and its open sides are the gaps along its edges in its bottom four layers.
    /// </summary>
    MinePiece? LoadMinePiece(string style, string name, int rotation)
    {
        string? json = FindMineJson(style, name);
        if (json == null) return null;
        string error = "";
        var schematic = BlockSchematic.LoadFromString(json, ref error);
        if (schematic == null)
        {
            sapi.Logger.Error("[createacolony] Mineshaft piece {0} ({1}) could not be read: {2}", name, style, error);
            return null;
        }
        schematic.Init(sapi.World.BlockAccessor);
        if (rotation != 0) schematic.TransformWhilePacked(sapi.World, EnumOrigin.BottomCenter, rotation);

        var ba = sapi.World.BlockAccessor;
        var blocks = new Dictionary<(int, int, int), Block>();
        for (int i = 0; i < schematic.Indices.Count; i++)
        {
            uint index = schematic.Indices[i];
            var at = ((int)(index & BlockSchematic.PosBitMask), (int)((index >> 20) & BlockSchematic.PosBitMask), (int)((index >> 10) & BlockSchematic.PosBitMask));
            var block = ba.GetBlock(schematic.BlockCodes[schematic.BlockIds[i]]);
            if (block != null && ColonyManager.IsTorch(block) && ba.GetBlock(ColonyManager.LitTorch(block.Code)) is { } lit) block = lit;
            if (block != null) blocks[at] = block;
        }
        var saved = new Dictionary<(int, int, int), SavedBlockEntity>();
        foreach (var (index, data) in schematic.BlockEntities)
        {
            var at = ((int)(index & BlockSchematic.PosBitMask), (int)((index >> 20) & BlockSchematic.PosBitMask), (int)((index >> 10) & BlockSchematic.PosBitMask));
            if (blocks.TryGetValue(at, out var withData) && KeepsDetails(withData)) saved[at] = new SavedBlockEntity { Data = data, Source = schematic };
        }

        int sx = schematic.SizeX, sy = schematic.SizeY, sz = schematic.SizeZ;
        bool entrance = name == MineEntrance;
        bool level = name == MineLevel;
        bool Empty(int x, int y, int z) => !blocks.TryGetValue((x, y, z), out var b) || b.Id == 0;

        // The entrance's ground layer: the highest layer whose edge is mostly filled in, by natural ground or by
        // a deck built over it (a fence round the top stands above it). It goes at the height of the ground
        // beside the hut; with MinerEntranceRepeatsPerHutLevel, it and everything below it repeat to go deeper.
        int ground = sy - 1;
        if (entrance)
        {
            for (int y = sy - 1; y >= 0; y--)
            {
                int edge = 0, filled = 0;
                for (int x = 0; x < sx; x++)
                for (int z = 0; z < sz; z++)
                {
                    if (x != 0 && z != 0 && x != sx - 1 && z != sz - 1) continue;
                    edge++;
                    if (blocks.TryGetValue((x, y, z), out var b) && b.Id != 0 && !IsWaypoint(b)) filled++;
                }
                if (filled * 2 < edge) continue;
                ground = y;
                break;
            }
        }
        var anchor = entrance ? (sx / 2, ground, sz / 2) : (sx / 2, 0, sz / 2);
        // The entrance's shaft: the opening in its ground layer nearest its middle. A level piece lines up by
        // the opening in its top layer nearest its middle, which goes right under it.
        (int X, int Z)? hole = entrance ? MiddleOpening(Empty, sx, sz, ground) : level ? MiddleOpening(Empty, sx, sz, sy - 1) : null;
        if (level && hole is { } topHole) anchor = (topHole.X, sy - 1, topHole.Z);
        var (ax, ay, az) = anchor;

        var digs = new List<BuildStep>();
        var solid = new List<BuildStep>();
        var decor = new List<BuildStep>();
        var markers = new HashSet<(int X, int Y, int Z)>();
        (int X, int Y, int Z)? hutSpot = null;
        for (int dy = sy - 1; dy >= 0; dy--)
        for (int dx = 0; dx < sx; dx++)
        for (int dz = 0; dz < sz; dz++)
        {
            blocks.TryGetValue((dx, dy, dz), out var block);
            // Where the Mineshaft Entrance's hut block goes (Aaron's surface example, 0.95.0): the spot is dug out
            // like air for a Miner's Hut's own mine; a Mineshaft Entrance stands there itself.
            if (block?.Code?.Path == "hut-" + BuildingTypes.MineshaftEntrance)
            {
                hutSpot = (dx - ax, dy - ay, dz - az);
                digs.Add(new BuildStep(dx - ax, dy - ay, dz - az, 0));
                continue;
            }
            // A waypoint sits in the floor where the tunnels start: the miner lays the floor's planks there (the
            // block beside it in the floor), never fill.
            // In a section, waypoints standing above the floor fill a doorway (Aaron's corner marks its far end so):
            // they are dug out like the air around them.
            if (block != null && IsWaypoint(block) && !entrance && !level && dy > 0)
            {
                digs.Add(new BuildStep(dx - ax, dy - ay, dz - az, 0));
                continue;
            }
            if (block != null && IsWaypoint(block))
            {
                markers.Add((dx - ax, dy - ay, dz - az));
                if (MarkerFloor(blocks, dx, dy, dz) is { } floor) solid.Add(new BuildStep(dx - ax, dy - ay, dz - az, floor.Id));
                continue;
            }
            if (block != null && (IsMetaBlock(block) || IsNaturalGround(block))) continue;
            if (block == null || block.Id == 0 || block is BlockMultiblock || IsFurniturePart(block, saved.GetValueOrDefault((dx, dy, dz)))
                || IsShapelessChisel(block, saved.GetValueOrDefault((dx, dy, dz))))
            {
                digs.Add(new BuildStep(dx - ax, dy - ay, dz - az, 0));
                continue;
            }
            var step = new BuildStep(dx - ax, dy - ay, dz - az, block.Id, saved.GetValueOrDefault((dx, dy, dz)));
            if (IsSolid(block)) solid.Add(step);
            else decor.Add(step);
        }

        // A level piece opens the floor of the level above where its top layer is open (the shaft) and carries its
        // ladders up through it, so the levels join.
        if (level)
        {
            for (int dx = 0; dx < sx; dx++)
            for (int dz = 0; dz < sz; dz++)
            {
                blocks.TryGetValue((dx, sy - 1, dz), out var top);
                if (top == null || top.Id == 0) digs.Add(new BuildStep(dx - ax, sy - ay, dz - az, 0));
                else if (top.Climbable) decor.Add(new BuildStep(dx - ax, sy - ay, dz - az, top.Id));
            }
        }

        // The open sides of a section, in its bottom four layers: the middle of the widest gap along each edge,
        // on its floor (the bottom layer), so pieces line up by their ways through whatever their size. Waypoints
        // count as gaps: they mark where the next section joins.
        (int From, int To)? Gap(BlockFacing face)
        {
            int length = face.Axis == EnumAxis.X ? sz : sx;
            (int From, int To)? best = null;
            for (int y = 0; y < Math.Min(4, sy); y++)
            {
                int start = -1;
                for (int i = 0; i <= length; i++)
                {
                    int x = face == BlockFacing.EAST ? sx - 1 : face == BlockFacing.WEST ? 0 : i;
                    int z = face == BlockFacing.SOUTH ? sz - 1 : face == BlockFacing.NORTH ? 0 : i;
                    bool empty = i < length && (Empty(x, y, z) || blocks.TryGetValue((x, y, z), out var w) && IsWaypoint(w));
                    if (empty && start < 0) start = i;
                    if (empty || start < 0) continue;
                    if (best is not { } b || i - 1 - start > b.To - b.From) best = (start, i - 1);
                    start = -1;
                }
            }
            return best;
        }
        var open = new List<(BlockFacing, int, int, int)>();
        void AddOpen(BlockFacing face, (int From, int To) run, int y)
        {
            int mid = (run.From + run.To) / 2;
            int x = face == BlockFacing.EAST ? sx - 1 : face == BlockFacing.WEST ? 0 : mid;
            int z = face == BlockFacing.SOUTH ? sz - 1 : face == BlockFacing.NORTH ? 0 : mid;
            open.Add((face, x - ax, y - ay, z - az));
        }
        if (entrance)
        {
            // Where tunnels start off the entrance's stairs: a row of two or more Mine waypoints along an edge, at
            // or under the ground layer. The tunnel's floor goes level with the landing: the waypoints' own layer
            // when they stand in place of its floor, the layer above when they sit under a built floor.
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                int length = face.Axis == EnumAxis.X ? sz : sx;
                for (int y = 0; y <= ground; y++)
                {
                    int start = -1;
                    for (int i = 0; i <= length; i++)
                    {
                        int x = face == BlockFacing.EAST ? sx - 1 : face == BlockFacing.WEST ? 0 : i;
                        int z = face == BlockFacing.SOUTH ? sz - 1 : face == BlockFacing.NORTH ? 0 : i;
                        bool mark = i < length && blocks.TryGetValue((x, y, z), out var b) && IsWaypoint(b);
                        if (mark && start < 0) start = i;
                        if (mark || start < 0) continue;
                        if (i - start >= 2)
                        {
                            bool underFloor = Enumerable.Range(start, i - start).All(j =>
                            {
                                var (fx, fz) = (face == BlockFacing.EAST ? sx - 1 : face == BlockFacing.WEST ? 0 : j, face == BlockFacing.SOUTH ? sz - 1 : face == BlockFacing.NORTH ? 0 : j);
                                return blocks.TryGetValue((fx, y + 1, fz), out var above) && above.Id != 0 && !IsNaturalGround(above) && !IsWaypoint(above);
                            });
                            AddOpen(face, (start, i - 1), underFloor ? y + 1 : y);
                        }
                        start = -1;
                    }
                }
            }
        }
        if (entrance || level)
        {
            // Tunnels also start off the rooms inside (Aaron's 0.87.0 entrance and levels): each patch of Mine
            // waypoints in a floor below the ground layer, away from the edges, is a doorway out, facing away from
            // the middle. The tunnel starts just past its outer edge, level with it.
            int mx = hole?.X ?? ax, mz = hole?.Z ?? az;
            var seen = new HashSet<(int, int, int)>();
            for (int y = 0; y < (entrance ? ground : sy); y++)
            for (int x = 1; x < sx - 1; x++)
            for (int z = 1; z < sz - 1; z++)
            {
                if (seen.Contains((x, y, z)) || !blocks.TryGetValue((x, y, z), out var b) || !IsWaypoint(b)) continue;
                var patch = new List<(int X, int Z)>();
                var todo = new Stack<(int X, int Z)>();
                todo.Push((x, z));
                seen.Add((x, y, z));
                while (todo.Count > 0)
                {
                    var (px, pz) = todo.Pop();
                    patch.Add((px, pz));
                    foreach (var (nx, nz) in new[] { (px + 1, pz), (px - 1, pz), (px, pz + 1), (px, pz - 1) })
                    {
                        if (seen.Contains((nx, y, nz)) || !blocks.TryGetValue((nx, y, nz), out var n) || !IsWaypoint(n)) continue;
                        seen.Add((nx, y, nz));
                        todo.Push((nx, nz));
                    }
                }
                if (patch.Count < 2 || patch.Any(p => p.X == 0 || p.Z == 0 || p.X == sx - 1 || p.Z == sz - 1)) continue;
                int x1 = patch.Min(p => p.X), x2 = patch.Max(p => p.X), z1 = patch.Min(p => p.Z), z2 = patch.Max(p => p.Z);
                double cx = (x1 + x2) / 2.0 - mx, cz = (z1 + z2) / 2.0 - mz;
                var face = Math.Abs(cx) >= Math.Abs(cz) ? (cx < 0 ? BlockFacing.WEST : BlockFacing.EAST) : (cz < 0 ? BlockFacing.NORTH : BlockFacing.SOUTH);
                int ox = face == BlockFacing.WEST ? x1 : face == BlockFacing.EAST ? x2 : (x1 + x2) / 2;
                int oz = face == BlockFacing.NORTH ? z1 : face == BlockFacing.SOUTH ? z2 : (z1 + z2) / 2;
                bool underFloor = patch.All(p => blocks.TryGetValue((p.X, y + 1, p.Z), out var above) && above.Id != 0 && !IsNaturalGround(above) && !IsWaypoint(above));
                open.Add((face, ox - ax, (underFloor ? y + 1 : y) - ay, oz - az));
            }
        }
        if (entrance)
        {
            // Without waypoints: one tunnel off the widest gap at its bottom.
            if (open.Count == 0)
            {
                var widest = BlockFacing.HORIZONTALS.Select(f => (Face: f, Gap: Gap(f))).Where(g => g.Gap != null)
                    .OrderByDescending(g => g.Gap!.Value.To - g.Gap.Value.From).FirstOrDefault();
                if (widest.Face != null) AddOpen(widest.Face, widest.Gap!.Value, 0);
            }
        }
        else if (!level)
        {
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                if (Gap(face) is { } gap) AddOpen(face, gap, 0);
            }
        }
        // A level piece repeats every Period layers: its top layer sits just under the floor of the level above.
        int period = entrance ? ground : level && open.Count > 0 ? -open.Max(o => o.Item3) + 1 : 0;
        return new MinePiece
        {
            Name = name, Steps = digs.Concat(solid).Concat(decor).ToList(), Open = open,
            Min = (-ax, -ay, -az), Max = (sx - 1 - ax, (level ? sy : sy - 1) - ay, sz - 1 - az),
            Period = period, Markers = markers, HutSpot = entrance ? hutSpot : null,
            Shaft = entrance && hole is { } shaft ? (shaft.X - ax, shaft.Z - az) : (0, 0)
        };
    }

    /// <summary>The middle of the opening in a layer nearest the layer's middle (one connected patch of empty spots), or null when the layer has none.</summary>
    static (int X, int Z)? MiddleOpening(System.Func<int, int, int, bool> empty, int sx, int sz, int y)
    {
        (int X, int Z)? start = null;
        int best = int.MaxValue;
        for (int x = 0; x < sx; x++)
        for (int z = 0; z < sz; z++)
        {
            int d = (x - sx / 2) * (x - sx / 2) + (z - sz / 2) * (z - sz / 2);
            if (d < best && empty(x, y, z)) (start, best) = ((x, z), d);
        }
        if (start is not { } first) return null;
        var patch = new HashSet<(int X, int Z)> { first };
        var todo = new Stack<(int X, int Z)>(patch);
        while (todo.Count > 0)
        {
            var (px, pz) = todo.Pop();
            foreach (var n in new[] { (px + 1, pz), (px - 1, pz), (px, pz + 1), (px, pz - 1) })
            {
                if (n.Item1 < 0 || n.Item2 < 0 || n.Item1 >= sx || n.Item2 >= sz || patch.Contains(n) || !empty(n.Item1, y, n.Item2)) continue;
                patch.Add(n);
                todo.Push(n);
            }
        }
        return ((patch.Min(p => p.X) + patch.Max(p => p.X)) / 2, (patch.Min(p => p.Z) + patch.Max(p => p.Z)) / 2);
    }

    /// <summary>
    /// The ground a mineshaft piece leaves as it is: rock, soil, gravel, sand, clay and ore as worldgen puts
    /// them down, and water or lava. Built stone (cobblestone, bricks) does not count and is built.
    /// </summary>
    public static bool IsNaturalGround(Block block)
    {
        if (block.IsLiquid() || block.BlockMaterial is EnumBlockMaterial.Lava) return true;
        if (block.Code?.Domain != "game") return false;
        string path = block.Code.Path;
        return NaturalPrefixes.Any(path.StartsWith);
    }

    static readonly string[] NaturalPrefixes =
    {
        "rock-", "soil-", "gravel-", "sand-", "ore-", "crackedrock-", "rawclay-", "peat-", "muddygravel", "forestfloor",
        "looseboulders", "loosestones", "looseores", "loosegears", "sludgygravel", "suevite", "meteorite", "glacierice", "packedglacierice"
    };

    static bool IsMetaBlock(Block b) => b.Id == BlockSchematic.FillerBlockId || b.Id == BlockSchematic.PathwayBlockId
        || b.Id == BlockSchematic.UndergroundBlockId || b.Id == BlockSchematic.AbovegroundBlockId || b.Id == BlockSchematic.ConnectorBlockId
        || b is BlockGroundStorage;

    /// <summary>A Mine or Quarry waypoint block (createacolony:waypoint-mine, createacolony:waypoint-quarry).</summary>
    public static bool IsWaypoint(Block block) => block.Code?.Domain == CreateAColonyModSystem.ModId && block.Code.Path.StartsWith("waypoint-");

    /// <summary>
    /// The spots, as offsets from the hut block, that the miner or quarrier digs out around the blueprint's
    /// waypoints: the shaft's steps and the top of its tunnels below a Mine waypoint, and the widest,
    /// deepest pit below a Quarry waypoint. The blueprint's own blocks there are left out of the building.
    /// </summary>
    HashSet<(int, int, int)> DugOut(Dictionary<string, (int Dx, int Dy, int Dz)> waypoints)
    {
        var dug = new HashSet<(int, int, int)>();
        // Shaft plans are worked out well above the bottom of the world, then moved back.
        const int Lift = 10000;
        if (waypoints.TryGetValue("mine", out var mine))
        {
            foreach (var spot in ColonyManager.ShaftPlan(new BlockPos(mine.Dx, mine.Dy + Lift, mine.Dz), 3, Math.Max(1, config.MinerTunnelLength)))
            {
                for (int h = 0; h < spot.Height; h++) dug.Add((spot.Feet.X, spot.Feet.Y + h - Lift, spot.Feet.Z));
            }
        }
        if (waypoints.TryGetValue("quarry", out var pit))
        {
            int r = Math.Max(1, Math.Max(config.QuarryRadius, config.QuarryMaxRadius));
            int depth = Math.Clamp(config.QuarryDepthPerLevel * MaxLevels, 1, 8 * r - 1);
            for (int i = -r; i <= r; i++)
            for (int j = -r; j <= r; j++)
            for (int k = 1; k <= depth; k++)
            {
                dug.Add((pit.Dx + i, pit.Dy - k, pit.Dz + j));
            }
        }
        return dug;
    }

    /// <summary>
    /// The side of the blueprint its first door is on. A door's saved turn says which way it faces, which
    /// gives the wall it sits in (east or west, north or south); the nearer edge of the blueprint on that line
    /// is the outside, whichever way round the door was hung.
    /// </summary>
    static int? EntranceSide(BlockSchematic schematic, Dictionary<(int, int, int), Block> blocks)
    {
        foreach (var (index, data) in schematic.BlockEntities)
        {
            var at = ((int)(index & BlockSchematic.PosBitMask), (int)((index >> 20) & BlockSchematic.PosBitMask), (int)((index >> 10) & BlockSchematic.PosBitMask));
            if (!blocks.TryGetValue(at, out var block) || !IsDoor(block)) continue;
            var facing = BlockFacing.HorizontalFromYaw(schematic.DecodeBlockEntityData(data).GetFloat("rotateYRad"));
            var (x, _, z) = at;
            if (facing.Axis == EnumAxis.X) return x <= schematic.SizeX - 1 - x ? BlockFacing.WEST.HorizontalAngleIndex : BlockFacing.EAST.HorizontalAngleIndex;
            return z <= schematic.SizeZ - 1 - z ? BlockFacing.NORTH.HorizontalAngleIndex : BlockFacing.SOUTH.HorizontalAngleIndex;
        }
        return null;
    }

    public static bool IsDoor(Block block) => block.HasBehavior<BlockBehaviorDoor>();

    static bool IsSolid(Block block) => block.SideSolid.Any;

    /// <summary>
    /// The floor planks to lay where a mineshaft piece has a waypoint: the planks most common near it in the same
    /// layer; null when there are none (a waypoint at the surface), and the spot is left as it is.
    /// </summary>
    static Block? MarkerFloor(Dictionary<(int, int, int), Block> blocks, int x, int y, int z)
    {
        var near = new List<Block>();
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        {
            if (blocks.TryGetValue((x + dx, y, z + dz), out var b) && b.Code.Path.StartsWith("planks-")) near.Add(b);
        }
        return near.GroupBy(b => b.Id).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault();
    }

    /// <summary>
    /// Doors always keep theirs (which way they face), support beams theirs (where each beam runs), chiseled blocks theirs (shape and
    /// materials); blocks placed from their item keep theirs to know which item (its wood and colours, a pole's banner); other blocks
    /// when the config lists them.
    /// </summary>
    bool KeepsDetails(Block block) =>
        block.EntityClass != null && (IsDoor(block) || block is BlockSupportBeam || block is BlockMicroBlock || PlacedAsItem(block) || config.BlueprintBlockDetails.Any(p => WildcardUtil.Match(new AssetLocation(p), block.Code)));

    /// <summary>
    /// True for blocks a builder places from their item, as a player does, so they set themselves up: the
    /// config's list (W4RD0's Furniture, Heraldry : Banners), and any block that takes its look from the item's
    /// attributes (Attribute Rendering Library: "types" such as wood and cloth).
    /// </summary>
    public bool PlacedAsItem(Block block) =>
        block.Id != 0 && (UsesAttributes(block) || config.BlueprintPlaceAsItem.Any(p => WildcardUtil.Match(new AssetLocation(p), block.Code)));

    /// <summary>A block whose wood, cloth and the like live in its block entity's "types", copied from the item it was placed from.</summary>
    public static bool UsesAttributes(Block block) => block.BlockBehaviors.Any(IsAttributeBehavior);

    /// <summary>Attribute Rendering Library's block behavior, found by name so the library stays optional.</summary>
    public static bool IsAttributeBehavior(BlockBehavior behavior) => behavior.GetType().Name == "BlockBehaviorShapeTexturesFromAttributes";

    /// <summary>
    /// A cell of a W4RD0's Furniture shelf or display other than its first one. The first cell places the
    /// others when it goes down, so until then the spot is cleared, like the top half of a door.
    /// </summary>
    static bool IsFurniturePart(Block block, SavedBlockEntity? saved)
    {
        if (saved == null || block.GetType().Name != "BlockFurnitureDisplay") return false;
        var tree = saved.Tree();
        return tree.GetInt("partX") != 0 || tree.GetInt("partY") != 0 || tree.GetInt("partZ") != 0;
    }

    /// <summary>
    /// A chiseled block the blueprint saved without its shape. It cannot be built (it would be an empty block and
    /// cost a chiseled block nobody can carry), so the spot is cleared instead.
    /// </summary>
    static bool IsShapelessChisel(Block block, SavedBlockEntity? saved) => block is BlockMicroBlock && saved == null;

    public static string HutBlockCode(string type) => type switch
    {
        BuildingTypes.TownHall => "createacolony:townhall",
        BuildingTypes.Warehouse => "createacolony:warehouse",
        BuildingTypes.SupplyCamp => "createacolony:supplycamp",
        _ => $"createacolony:hut-{type}"
    };
}

public static class BuildingTypes
{
    public const string TownHall = "townhall";
    public const string Warehouse = "warehouse";
    public const string Builder = "builder";
    public const string Forester = "forester";
    public const string Farmer = "farmer";
    public const string Potter = "potter";
    public const string Tavern = "tavern";
    public const string Cook = "cook";
    public const string GuardTower = "guardtower";
    public const string Residence = "residence";
    public const string Prospector = "prospector";
    public const string Miner = "miner";
    public const string Quarry = "quarry";
    public const string Smelter = "smelter";
    public const string Blacksmith = "blacksmith";
    public const string Armourer = "armourer";
    public const string Weaponsmith = "weaponsmith";
    public const string Sawmill = "sawmill";
    public const string Craftsman = "craftsman";
    public const string Tailor = "tailor";
    public const string Jeweller = "jeweller";
    /// <summary>Since 0.137.0 a work station inside a Church (ColonyManager.Stations.cs); older saves may still have a Healer's Hut.</summary>
    public const string Healer = "healer";
    /// <summary>The Courier's Station (0.142.0): a work station inside the Warehouse that shows what the couriers are doing.</summary>
    public const string CourierStation = "courierstation";
    /// <summary>The Church: its priests and bishop hold services and funerals, take tithes and run the colony's charity (ColonyManager.Church.cs).</summary>
    public const string Church = "church";
    /// <summary>The Royal Mint: a minter strikes the colony's coins (ColonyManager.Economy.cs).</summary>
    public const string Mint = "mint";
    /// <summary>The Archive: researchers study the colony's research tree (ColonyManager.Research.cs).</summary>
    public const string Archive = "archive";
    public const string Explorer = "explorer";
    public const string Gatherer = "gatherer";
    public const string Hunter = "hunter";
    public const string Barracks = "barracks";
    /// <summary>Put at a mine's entrance; several Miner's Huts can work the one mine through it.</summary>
    public const string MineshaftEntrance = "mineshaftentrance";
    /// <summary>Not a colony building: only used to find the supply camp's own blueprint.</summary>
    public const string SupplyCamp = "supplycamp";
}
