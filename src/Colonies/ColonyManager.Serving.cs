using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Cook's Hut serves meals (Aaron's design doc "Cook's Hut, School, winter, settler requests and research tree",
/// part 1). Tables inside the hut with a chair, stool or bench beside them are places to eat. At the two meal times
/// (morning and evening) the cook ladles the hut's pot meals into bowls and sets them on the tables, as many as the
/// hut's level allows (6, 12, 20). Settlers who are a little hungry come in, take a bowl, sit down and eat, and are
/// a little happier for it ("Ate at a table"). Empty bowls are cleared away. Outside meal times, and when a hut has no
/// tables, settlers eat as they always did. The cook also fetches water in a bucket for pot recipes that need it.
/// </summary>
public partial class ColonyManager
{
    /// <summary>How long a hut's tables are remembered before they are looked for again.</summary>
    const long TableCacheMs = 30_000;

    readonly Dictionary<Building, (long Ms, List<BlockPos> Spots)> tableSpots = new();

    /// <summary>Who ate where at today's meal times, per colony: [meal time] = (day, at a table, elsewhere).</summary>
    readonly Dictionary<string, (int Day, int Served, int Elsewhere)[]> mealCounts = new();

    /// <summary>Which meal time it is now (0 morning, 1 evening), or -1 between meals.</summary>
    public int MealTimeNow()
    {
        if (!Config.CookServes || Config.MealTimes == null) return -1;
        float hour = sapi.World.Calendar.HourOfDay;
        for (int i = 0; i < Config.MealTimes.Length; i++)
        {
            float from = Config.MealTimes[i];
            if (hour >= from && hour < from + Config.MealTimeHours) return i;
        }
        return -1;
    }

    /// <summary>How many settlers a Cook's Hut of this level serves at one meal time (config ServeSeatsByLevel).</summary>
    public int ServePlaces(Building hut)
    {
        var byLevel = Config.ServeSeatsByLevel;
        if (byLevel == null || byLevel.Length == 0) return 0;
        int level = Math.Max(1, hut.Level);
        return byLevel[Math.Min(level, byLevel.Length) - 1];
    }

    /// <summary>A table: its code says table and it is not a work table.</summary>
    static bool IsTable(Block block)
    {
        string path = block.Code?.Path ?? "";
        return path.Contains("table") && !path.Contains("craft") && !path.Contains("work") && !path.Contains("drafting") && !path.Contains("tanning");
    }

    /// <summary>
    /// Where the cook sets bowls: the top of each table inside the Cook's Hut that has a seat within 2 blocks and
    /// room on top (nothing there, or a bowl). As many as the hut serves at once, nearest the hut block first.
    /// </summary>
    public List<BlockPos> ServingSpots(Building hut)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (tableSpots.TryGetValue(hut, out var known) && now - known.Ms < TableCacheMs) return known.Spots;
        var spots = new List<BlockPos>();
        if (hut.Type == BuildingTypes.Cook && Staffable(hut) && Config.CookServes)
        {
            var ba = sapi.World.BlockAccessor;
            var (min, max) = ZoneOf(hut);
            var pos = new BlockPos(0);
            var seats = new List<BlockPos>();
            var tables = new List<BlockPos>();
            for (int x = min.X; x <= max.X; x++)
            for (int y = min.Y; y <= max.Y; y++)
            for (int z = min.Z; z <= max.Z; z++)
            {
                pos.Set(x, y, z);
                var block = ba.GetBlock(pos);
                if (block.Id == 0) continue;
                if (Settlers.SettlerSeats.IsSeat(block)) seats.Add(pos.Copy());
                else if (IsTable(block)) tables.Add(pos.Copy());
            }
            var h = hut.HutPos;
            foreach (var table in tables.OrderBy(t => t.DistanceSqTo(h.X, h.Y, h.Z)))
            {
                if (spots.Count >= ServePlaces(hut)) break;
                var top = table.UpCopy();
                var above = ba.GetBlock(top);
                if (above.Id != 0 && !IsBowlBlock(above)) continue;
                if (!seats.Any(s => Math.Abs(s.X - table.X) <= 2 && Math.Abs(s.Z - table.Z) <= 2 && Math.Abs(s.Y - table.Y) <= 1)) continue;
                spots.Add(top);
            }
        }
        tableSpots[hut] = (now, spots);
        return spots;
    }

    static bool IsBowlBlock(Block block) => block is BlockMeal || IsEmptyBowl(new ItemStack(block));

    /// <summary>A bowl with a meal in it set out on this spot.</summary>
    BlockEntityMeal? ServedBowl(BlockPos spot) =>
        sapi.World.BlockAccessor.GetBlockEntity(spot) is BlockEntityMeal { QuantityServings: > 0 } bowl && !MealMeshCache.ContentsRotten(((IBlockEntityMealContainer)bowl).inventory) ? bowl : null;

    /// <summary>The spots at the colony's Cook's Huts with a meal set out now.</summary>
    IEnumerable<BlockPos> ServedSpots(Colony colony) => colony.Buildings
        .Where(b => b.Type == BuildingTypes.Cook)
        .SelectMany(ServingSpots)
        .Where(p => ServedBowl(p) != null);

    /// <summary>True when the food is a meal set out on a Cook's Hut table.</summary>
    public bool IsServedMeal(Colony colony, BlockEntityContainer container) =>
        container is BlockEntityMeal && colony.Buildings.Any(b => b.Type == BuildingTypes.Cook && ServingSpots(b).Contains(container.Pos));

    /// <summary>
    /// True when the settler comes in for the meal: it is a meal time, a bowl is set out, and the settler has eaten
    /// less than ServeHungerBelow of a full belly (so it is not quite hungry yet, but would eat).
    /// </summary>
    public bool ComesToTable(Colony colony, CitizenRecord c)
    {
        if (MealTimeNow() < 0 || c.Visitor || c.IsDowned || c.Saturation < 0) return false;
        if (c.Saturation >= Config.SaturationMax * Config.ServeHungerBelow) return false;
        return ServedSpots(colony).Any();
    }

    /// <summary>The nearest meal set out on a table, for a settler who comes in for it.</summary>
    public FoodChoice? ServedMealFor(Colony colony, CitizenRecord citizen, EntityAgent eater, System.Func<BlockPos, bool>? skip = null)
    {
        foreach (var spot in ServedSpots(colony).OrderBy(p => p.DistanceSqTo(eater.Pos.X, eater.Pos.Y, eater.Pos.Z)))
        {
            if (skip != null && skip(spot)) continue;
            if (ServedBowl(spot) is { } bowl && FindFoodIn(bowl, citizen, eater, false) is { } choice) return choice;
        }
        return null;
    }

    /// <summary>
    /// After a settler ate: counts it for the Kitchen tab when it is a meal time (at a table or elsewhere), and a meal
    /// at a table gives the "Ate at a table" thought.
    /// </summary>
    public void NoteMeal(Colony colony, CitizenRecord c, bool atTable)
    {
        if (atTable) AddThought(c, "ateattable");
        int meal = MealTimeNow();
        if (meal < 0) return;
        int day = (int)sapi.World.Calendar.TotalDays;
        if (!mealCounts.TryGetValue(colony.ColonyId, out var counts)) mealCounts[colony.ColonyId] = counts = new (int, int, int)[Config.MealTimes.Length];
        if (meal >= counts.Length) return;
        var (d, served, elsewhere) = counts[meal];
        if (d != day) (served, elsewhere) = (0, 0);
        counts[meal] = (day, served + (atTable ? 1 : 0), elsewhere + (atTable ? 0 : 1));
    }

    /// <summary>The Kitchen tab's serving line: tables, and how many ate at them and elsewhere at the last meal time.</summary>
    public string ServingText(Colony colony, Building hut)
    {
        if (!Config.CookServes) return "";
        int places = ServingSpots(hut).Count;
        if (places == 0) return Lang.Get("createacolony:serving-no-tables");
        string text = Lang.Get("createacolony:serving-places", places, ServePlaces(hut));
        if (mealCounts.TryGetValue(colony.ColonyId, out var counts))
        {
            int today = (int)sapi.World.Calendar.TotalDays;
            for (int i = 0; i < counts.Length; i++)
            {
                var (day, served, elsewhere) = counts[i];
                if (day < today - 1 || served + elsewhere == 0) continue;
                text += "\n" + Lang.Get("createacolony:serving-meal-" + Math.Min(i, 1), served, elsewhere);
            }
        }
        return text;
    }

    // ---- The cook's side -------------------------------------------------------------------

    /// <summary>Table spots with nothing on them, or an empty bowl, waiting for a meal.</summary>
    public List<BlockPos> FreeServingSpots(Building hut) => ServingSpots(hut).Where(p => ServedBowl(p) == null).ToList();

    /// <summary>A bowl left on a table: an eaten bowl, or a meal gone rotten. The cook clears these away.</summary>
    public BlockPos? DirtyServingSpot(Building hut, System.Func<BlockPos, bool> skip)
    {
        var ba = sapi.World.BlockAccessor;
        return ServingSpots(hut).FirstOrDefault(p => !skip(p) && IsBowlBlock(ba.GetBlock(p)) && ServedBowl(p) == null);
    }

    /// <summary>A crock or pot in the kitchen with a meal that is not rotten, to ladle into bowls.</summary>
    public BlockEntityContainer? LadleSource(Colony colony, Building hut, System.Func<BlockPos, bool> skip) =>
        KitchenStores(colony, hut).FirstOrDefault(c => !skip(c.Pos) && c.Inventory.Any(s => !s.Empty && s.Itemstack.Block is IBlockMealContainer m and not BlockMeal
            && m.GetQuantityServings(sapi.World, s.Itemstack) >= 1 && !IsRottenMeal(s.Itemstack)));

    /// <summary>True for a bowl with a meal in it the cook can set out.</summary>
    public bool IsFilledBowl(ItemStack s) => s.Block is BlockMeal meal && meal.GetQuantityServings(sapi.World, s) > 0 && !IsRottenMeal(s);

    /// <summary>
    /// At a crock or pot: fills the empty bowls the cook carries with one serving each, up to want bowls. Returns how many it filled.
    /// </summary>
    public int LadleBowls(Colony colony, CitizenRecord cook, BlockEntityContainer container, int want)
    {
        var items = PackItems(cook);
        int filled = 0;
        while (filled < want)
        {
            int at = items.FindIndex(IsEmptyBowl);
            if (at < 0) break;
            var bowl = items[at];
            var mealBlock = bowl.Block.Attributes?["mealBlockCode"].AsString() is { } code ? sapi.World.GetBlock(new AssetLocation(code)) : null;
            if (mealBlock is not IBlockMealContainer bowlMeal) break;
            var slot = container.Inventory.FirstOrDefault(s => !s.Empty && s.Itemstack.Block is IBlockMealContainer m and not BlockMeal
                && m.GetQuantityServings(sapi.World, s.Itemstack) >= 1 && !IsRottenMeal(s.Itemstack));
            if (slot == null) break;
            var meal = (IBlockMealContainer)slot.Itemstack.Block;
            string? recipeCode = meal.GetRecipeCode(sapi.World, slot.Itemstack);
            var contents = (meal.GetNonEmptyContents(sapi.World, slot.Itemstack) ?? Array.Empty<ItemStack>()).Select(s => s.Clone()).ToArray();
            if (contents.Length == 0 || !TakeServing(container, slot)) break;
            slot.MarkDirty();
            var full = new ItemStack((Block)bowlMeal);
            bowlMeal.SetContents(recipeCode, full, contents, 1);
            if (bowl.StackSize > 1) bowl.StackSize--;
            else items.RemoveAt(at);
            items.Add(full);
            filled++;
        }
        if (filled > 0)
        {
            SavePack(colony, cook, items);
            container.MarkDirty(true);
        }
        return filled;
    }

    /// <summary>Sets one filled bowl the cook carries on the spot. True when it did.</summary>
    public bool SetOutBowl(Colony colony, CitizenRecord cook, BlockPos spot)
    {
        var ba = sapi.World.BlockAccessor;
        var here = ba.GetBlock(spot);
        if (here.Id != 0 && !IsBowlBlock(here)) return false;
        if (ServedBowl(spot) != null) return false;
        if (here.Id != 0 && !ClearBowl(colony, cook, spot)) return false;
        var bowl = TakeFromPack(colony, cook, IsFilledBowl, 1);
        if (bowl?.Block == null) return false;
        ba.SetBlock(bowl.Block.BlockId, spot, bowl);
        if (ba.GetBlockEntity(spot) is not BlockEntityMeal)
        {
            ba.SetBlock(0, spot);
            AddToPack(colony, cook, bowl);
            return false;
        }
        sapi.World.PlaySoundAt(new AssetLocation("sounds/player/build"), spot.X + 0.5, spot.Y + 0.5, spot.Z + 0.5, null);
        return true;
    }

    /// <summary>Takes a bowl off a table into the cook's inventory (an empty one, or one gone rotten for washing).</summary>
    public bool ClearBowl(Colony colony, CitizenRecord cook, BlockPos spot)
    {
        var ba = sapi.World.BlockAccessor;
        var block = ba.GetBlock(spot);
        if (!IsBowlBlock(block)) return false;
        var stack = block.OnPickBlock(sapi.World, spot) ?? new ItemStack(block);
        ba.SetBlock(0, spot);
        AddToPack(colony, cook, stack);
        return true;
    }

    // ---- Water for pot recipes ------------------------------------------------------------

    ItemStack? waterSample;

    /// <summary>One water portion, to plan recipes with.</summary>
    ItemStack? WaterSample() => waterSample ??= sapi.World.GetItem(new AssetLocation(WaterCode)) is { } water ? new ItemStack(water) : null;

    public static bool IsWater(ItemStack? s) => s?.Collectible.Code?.ToString() == WaterCode;

    /// <summary>A bucket or jug the cook can carry water in: empty, or with water in it.</summary>
    public static bool IsWaterVessel(ItemStack s) => s.Collectible is BlockLiquidContainerBase box && box is not BlockBarrel
        && !IsEmptyBowl(s) && box.CapacityLitres >= 1 && (box.GetContent(s) == null || IsWater(box.GetContent(s)));

    /// <summary>Litres of water in the cook's vessels.</summary>
    public float CookWaterLitres(CitizenRecord cook) => PackItems(cook).Where(IsWaterVessel).Sum(s => IsWater(ContentOf(s)) ? LitresOf(ContentOf(s)!) * s.StackSize : 0);

    /// <summary>Water portions in the cook's vessels.</summary>
    public int CookWaterPortions(CitizenRecord cook) => PackItems(cook).Where(IsWaterVessel).Sum(s => IsWater(ContentOf(s)) ? ContentOf(s)!.StackSize * s.StackSize : 0);

    /// <summary>True when the cook could cook with water: config CookUsesWater, water near the hut, and a vessel it has or can fetch.</summary>
    bool WaterAvailable(Colony colony, Building hut, CitizenRecord cook) =>
        Config.CookUsesWater && WaterSample() != null && WashWater(colony, hut.HutPos) != null
        && (PackItems(cook).Any(IsWaterVessel) || KitchenStores(colony, hut).Any(c => c.Inventory.Any(s => !s.Empty && IsWaterVessel(s.Itemstack))));

    /// <summary>At the water: fills every vessel the cook carries to the brim.</summary>
    public void FillWater(Colony colony, CitizenRecord cook)
    {
        if (WaterSample() is not { } sample) return;
        var items = PackItems(cook);
        bool did = false;
        foreach (var s in items.Where(IsWaterVessel))
        {
            var box = (BlockLiquidContainerBase)s.Collectible;
            var props = BlockLiquidContainerBase.GetContainableProps(sample);
            if (props == null) return;
            var water = sample.Clone();
            water.StackSize = (int)(box.CapacityLitres * props.ItemsPerLitre);
            box.SetContent(s, water);
            did = true;
        }
        if (did) SavePack(colony, cook, items);
    }

    /// <summary>Pours litres of water from the cook's vessels into the pot slot. True when all of it went in.</summary>
    bool PourWater(Colony colony, CitizenRecord cook, ItemSlot into, int portions)
    {
        var items = PackItems(cook);
        int left = portions;
        foreach (var s in items.Where(IsWaterVessel))
        {
            if (left <= 0) break;
            var box = (BlockLiquidContainerBase)s.Collectible;
            if (!IsWater(box.GetContent(s))) continue;
            var props = BlockLiquidContainerBase.GetContainableProps(box.GetContent(s)!);
            if (props == null) continue;
            var taken = box.TryTakeContent(s, left);
            if (taken == null || taken.StackSize <= 0) continue;
            if (into.Empty) into.Itemstack = taken;
            else into.Itemstack.StackSize += taken.StackSize;
            left -= taken.StackSize;
        }
        SavePack(colony, cook, items);
        into.MarkDirty();
        return left <= 0;
    }
}
