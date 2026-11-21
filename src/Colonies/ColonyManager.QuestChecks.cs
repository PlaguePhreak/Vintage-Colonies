using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Quests;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The quest book's automatic checks beyond "carries an item": what a container holds, a cooked meal, blocks
/// and animals near the player, the room they stand in, what they ride, blocks they break, and the colony's
/// own numbers. Most of these are true only for a moment (you look at a barrel, you stand in the cellar), so
/// the best value seen is remembered per quest and task: once done, a task stays done.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Checks whose best value is remembered (see QuestObjectiveDef and ObjectiveTypes).</summary>
    static readonly HashSet<string> StickyChecks = new()
    {
        ObjectiveTypes.Holds, ObjectiveTypes.Meal, ObjectiveTypes.BlockNear, ObjectiveTypes.EntityNear,
        ObjectiveTypes.Room, ObjectiveTypes.Mounted, ObjectiveTypes.Depth,
    };

    const int NearBlocksRange = 6, NearBlocksHeight = 3, NearEntitiesRange = 14;

    /// <summary>Progress of a remembered check: the best of what was seen before and what is true now.</summary>
    int StickyProgress(IServerPlayer player, QuestDef q, int index, QuestObjectiveDef o, int needed)
    {
        var counters = QuestsOf(player.PlayerUID).Counters;
        string key = q.Code + "#" + index;
        int best = counters.GetValueOrDefault(key);
        if (best >= needed) return best;
        int now;
        try { now = LiveCheck(player, o); }
        catch (Exception e)
        {
            Mod.Logger.Warning("Quest {0}: the {1} check failed ({2}); it counts as done", q.Code, o.Type, e.Message);
            now = needed;
        }
        if (now > best)
        {
            counters[key] = now;
            SavePlayerQuests(player.PlayerUID);
        }
        return Math.Max(best, now);
    }

    int LiveCheck(IServerPlayer player, QuestObjectiveDef o)
    {
        var entity = player.Entity;
        if (entity == null) return 0;
        switch (o.Type)
        {
            case ObjectiveTypes.Holds:
                return HeldContents(player).Where(s => QuestItems.Matches(o.Code, s.Collectible.Code)).Sum(s => Math.Max(1, s.StackSize)) > 0 ? Math.Max(1, o.Count) : 0;
            case ObjectiveTypes.Meal:
                return MealServings(player, o);
            case ObjectiveTypes.BlockNear:
                return CountBlocksNear(entity.Pos.AsBlockPos, o.Code, Math.Max(1, o.Count));
            case ObjectiveTypes.EntityNear:
                return sapi.World.GetEntitiesAround(entity.Pos.XYZ, NearEntitiesRange, NearEntitiesRange,
                    e => e.Alive && e is not EntityPlayer && QuestItems.Matches(o.Code, e.Code) && e.WatchedAttributes.GetInt("generation") >= o.Level).Length;
            case ObjectiveTypes.Room:
                return InRoom(entity.Pos.AsBlockPos, o.Code) ? 1 : 0;
            case ObjectiveTypes.Mounted:
                return entity.MountedOn?.Entity is { } mount && (o.Code.Length == 0 || QuestItems.Matches(o.Code, mount.Code)) ? 1 : 0;
            case ObjectiveTypes.Depth:
                return Math.Max(0, sapi.World.SeaLevel - (int)entity.Pos.Y);
        }
        return 0;
    }

    /// <summary>What the containers the player carries hold, plus what is inside the block they are looking at (a barrel, a shelf, a trough).</summary>
    IEnumerable<ItemStack> HeldContents(IServerPlayer player)
    {
        foreach (var slot in CarriedSlots(player))
        {
            var stack = slot.Itemstack;
            if (stack.Collectible is BlockLiquidContainerBase liquid && liquid.GetContent(stack) is { } content) yield return content;
            else if (stack.Collectible is BlockContainer container)
            {
                foreach (var inside in container.GetNonEmptyContents(sapi.World, stack) ?? Array.Empty<ItemStack>()) yield return inside;
            }
        }
        if (player.CurrentBlockSelection?.Position is { } pos && sapi.World.BlockAccessor.GetBlockEntity(pos) is IBlockEntityContainer be && be.Inventory != null)
        {
            foreach (var slot in be.Inventory)
            {
                if (slot?.Itemstack != null) yield return slot.Itemstack;
            }
        }
    }

    /// <summary>Servings of a cooked meal of the wanted recipe in a pot, bowl or crock the player carries. Level 1 asks for a sealed crock.</summary>
    int MealServings(IServerPlayer player, QuestObjectiveDef o)
    {
        double best = 0;
        foreach (var slot in CarriedSlots(player))
        {
            var attrs = slot.Itemstack.Attributes;
            string? recipe = attrs?.GetString("recipeCode");
            if (string.IsNullOrEmpty(recipe)) continue;
            if (o.Code.Length > 0 && !o.Code.Split('|').Any(p => WildcardUtil.Match(p, recipe))) continue;
            if (o.Level >= 1 && attrs!.GetBool("sealed") != true) continue;
            best = Math.Max(best, Math.Max(1, attrs!.GetDecimal("quantityServings", 1)));
        }
        return (int)Math.Floor(best + 0.01);
    }

    int CountBlocksNear(BlockPos centre, string pattern, int enough)
    {
        int found = 0;
        var accessor = sapi.World.BlockAccessor;
        var pos = new BlockPos(centre.dimension);
        for (int dy = -NearBlocksHeight; dy <= NearBlocksHeight; dy++)
        {
            for (int dx = -NearBlocksRange; dx <= NearBlocksRange; dx++)
            {
                for (int dz = -NearBlocksRange; dz <= NearBlocksRange; dz++)
                {
                    pos.Set(centre.X + dx, centre.Y + dy, centre.Z + dz);
                    var block = accessor.GetBlock(pos);
                    if (block.Id == 0 || !QuestItems.Matches(pattern, block.Code)) continue;
                    if (++found >= enough) return found;
                }
            }
        }
        return found;
    }

    /// <summary>The game's own room check: "" any closed room, "cellar" a small room with little skylight, "greenhouse" a closed room that is mostly skylight.</summary>
    bool InRoom(BlockPos pos, string kind)
    {
        var room = sapi.ModLoader.GetModSystem<RoomRegistry>()?.GetRoomForPosition(pos);
        if (room == null || room.ExitCount != 0) return false;
        return kind switch
        {
            "cellar" => room.IsSmallRoom && room.SkylightCount <= room.NonSkylightCount / 2,
            "greenhouse" => room.SkylightCount > room.NonSkylightCount,
            _ => true,
        };
    }

    /// <summary>Counts a block the player broke towards every break task it fits. A task's code is "tool@block"; either side may be left out.</summary>
    void OnQuestBreak(IServerPlayer player, int oldBlockId, BlockSelection selection)
    {
        if (player == null || breakTasks == null && (breakTasks = Quests.All.SelectMany(q => q.Objectives)
                .Where(o => o.Type == ObjectiveTypes.Break).Select(o => o.Code).Distinct().ToList()).Count == 0) return;
        if (breakTasks!.Count == 0) return;
        var block = sapi.World.GetBlock(oldBlockId);
        if (block?.Code == null) return;
        var held = player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible.Code;
        bool counted = false;
        foreach (string code in breakTasks)
        {
            int at = code.IndexOf('@');
            string tool = at < 0 ? "" : code[..at], target = at < 0 ? code : code[(at + 1)..];
            if (tool.Length > 0 && (held == null || !QuestItems.Matches(tool, held))) continue;
            if (target.Length > 0 && !QuestItems.Matches(target, block.Code)) continue;
            var counters = QuestsOf(player.PlayerUID).Counters;
            counters["break:" + code] = counters.GetValueOrDefault("break:" + code) + 1;
            counted = true;
        }
        if (counted) SavePlayerQuests(player.PlayerUID);
    }

    List<string>? breakTasks;
    List<string>? placeTasks;

    /// <summary>Counts a block the player placed towards every place task it fits.</summary>
    void OnQuestPlace(IServerPlayer player, int oldBlockId, BlockSelection selection, ItemStack withItemStack)
    {
        if (player == null || selection?.Position == null) return;
        placeTasks ??= Quests.All.SelectMany(q => q.Objectives).Where(o => o.Type == ObjectiveTypes.Place).Select(o => o.Code).Distinct().ToList();
        if (placeTasks.Count == 0) return;
        var block = sapi.World.BlockAccessor.GetBlock(selection.Position);
        if (block?.Code == null) return;
        bool counted = false;
        foreach (string code in placeTasks)
        {
            if (code.Length > 0 && !QuestItems.Matches(code, block.Code)) continue;
            var counters = QuestsOf(player.PlayerUID).Counters;
            counters["place:" + code] = counters.GetValueOrDefault("place:" + code) + 1;
            counted = true;
        }
        if (counted) SavePlayerQuests(player.PlayerUID);
    }

    readonly Dictionary<string, bool> placeableBlocks = new();

    /// <summary>
    /// How many of a block the player has set down. Blocks placed by hand are counted as they are placed. Things
    /// the game builds without a placing event (a pile on the ground, a firepit, tilled soil, a sown crop) are
    /// counted when they stand near the player, and that count is kept. A pattern that is no block at all, only
    /// an item, falls back to carrying it.
    /// </summary>
    int PlaceProgress(IServerPlayer player, QuestObjectiveDef o)
    {
        var counters = QuestsOf(player.PlayerUID).Counters;
        string key = "place:" + o.Code;
        int placed = counters.GetValueOrDefault(key);
        int need = Math.Max(1, o.Count);
        if (o.Code.Length == 0 || placed >= need || player.Entity == null) return placed;
        if (!placeableBlocks.TryGetValue(o.Code, out bool isBlock))
            placeableBlocks[o.Code] = isBlock = o.Code.Split('|').Any(part => part.Length > 0 && sapi.World.SearchBlocks(Loc(part)).Length > 0);
        if (!isBlock) return CountCarried(player, o.Code);
        int near = CountBlocksNear(player.Entity.Pos.AsBlockPos, o.Code, need);
        if (near > placed)
        {
            counters[key] = placed = near;
            SavePlayerQuests(player.PlayerUID);
        }
        return placed;
    }

    /// <summary>The Grand Tour: remembers each kind of hut the player has stood beside, and returns how many kinds so far.</summary>
    int TourProgress(IServerPlayer player, Colony colony)
    {
        var counters = QuestsOf(player.PlayerUID).Counters;
        if (player.Entity != null)
        {
            var at = player.Entity.Pos.AsBlockPos;
            bool found = false;
            foreach (var b in colony.Buildings)
            {
                if (b.Level < 1 || b.HutPos.DistanceTo(at) > 8 || counters.ContainsKey("tour:" + b.Type)) continue;
                counters["tour:" + b.Type] = 1;
                found = true;
            }
            if (found) SavePlayerQuests(player.PlayerUID);
        }
        return counters.Keys.Count(k => k.StartsWith("tour:"));
    }

    /// <summary>The colony's own numbers a quest can ask for.</summary>
    int ColonyNumber(Colony colony, QuestObjectiveDef o)
    {
        switch (o.Type)
        {
            case ObjectiveTypes.Research:
                return colony.ResearchDone.Count;
            case ObjectiveTypes.Happiness:
                return Math.Max(0, AverageHappiness(colony));
            case ObjectiveTypes.AverageLevel:
            {
                // Ten times the average level, so 2.5 is asked for as 25.
                var huts = colony.Buildings.Where(b => b.Type == o.Code && b.Level > 0).ToList();
                return huts.Count == 0 ? 0 : (int)Math.Floor(huts.Average(b => b.Level) * 10 + 0.001);
            }
            case ObjectiveTypes.WorkHuts:
                // Different kinds of hut, other than homes and the Town Hall, at the level asked for.
                if (o.Code.Length > 0) return colony.Buildings.Where(b => b.Level >= Math.Max(1, o.Level) && o.Code.Split('|').Contains(b.Type)).Select(b => b.Type).Distinct().Count();
                return colony.Buildings.Where(b => b.Level >= Math.Max(1, o.Level) && b.Type is not ("townhall" or "residence" or "boardinghouse" or "tavern"))
                    .Select(b => b.Type).Distinct().Count();
            case ObjectiveTypes.TopSkill:
                return colony.Citizens.Count(c => c.Alive && !c.Visitor && c.SkillXp.Values.Any(xp => LevelOf(xp) >= (o.Level > 0 ? o.Level : MaxLevel)));
        }
        return 0;
    }
}
