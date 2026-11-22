using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Winter readiness (Aaron's design doc "Cook's Hut, School, winter, settler requests and research tree", part 3).
/// The colony looks ahead to the first frost where its Town Hall stands, with the world's own climate and season
/// length, and counts what it has for the winter: food, firewood and warm clothes. The Town Hall's Winter tab shows
/// it from midsummer on, members get one warning 30 days and one 10 days before the frost when something is short,
/// and in the last months before it settlers get ready by themselves (a Town Hall switch turns that off): foresters
/// keep more firewood, tailors make warm clothes first, and cooks leave food that keeps for the winter.
/// </summary>
public partial class ColonyManager
{
    /// <summary>What the Winter tab shows: WinterReadiness.State values.</summary>
    public static class WinterState
    {
        /// <summary>Winter is more than half a year off.</summary>
        public const int Early = 0;
        /// <summary>Counting down to the first frost.</summary>
        public const int Coming = 1;
        /// <summary>It is winter now.</summary>
        public const int Now = 2;
        /// <summary>It never freezes here.</summary>
        public const int None = 3;
        /// <summary>Turned off in the server config.</summary>
        public const int Off = 4;
    }

    /// <summary>How long a winter count is kept before it is worked out again.</summary>
    const long WinterCountMs = 60_000;

    /// <summary>The weather ahead at a Town Hall, worked out once a day: days to the first frost, how long winter lasts, and its coldest day.</summary>
    sealed class WinterForecast
    {
        public double Day;
        public int State;
        /// <summary>Whole days to the first frost (Coming), or days of winter left (Now).</summary>
        public int Days;
        /// <summary>The first frost as a whole game day, to tell one winter from the next.</summary>
        public int FrostDay;
        /// <summary>Days of winter still to get through: all of the next one, or what is left of this one.</summary>
        public int WinterDays;
        public float Coldest;
    }

    public sealed class WinterReadiness
    {
        public int State;
        public int Days;
        public int WinterDays;
        public float Coldest;
        public int FoodMeals, FoodNeeded;
        public int Firewood, FirewoodNeeded;
        public int Dressed, People;
        public long Ms;
        public bool FoodShort => FoodMeals < FoodNeeded;
        public bool WoodShort => Firewood < FirewoodNeeded;
        public bool ClothesShort => Dressed < People;
        public bool AnyShort => FoodShort || WoodShort || ClothesShort;
    }

    readonly Dictionary<string, WinterForecast> winterForecasts = new();
    readonly Dictionary<string, WinterReadiness> winterReadiness = new();
    /// <summary>Foresters, tailors and Cook's Huts that get ready for winter right now (refreshed with the count).</summary>
    readonly HashSet<Building> winterPrepHuts = new();

    /// <summary>
    /// Looks ahead day by day (four times a day) with the climate the game will have at the Town Hall: winter is the
    /// days whose average is below freezing. Up to a year ahead; a place that never freezes has no winter.
    /// </summary>
    WinterForecast Forecast(Colony colony)
    {
        var cal = sapi.World.Calendar;
        double today = Math.Floor(cal.TotalDays);
        if (winterForecasts.TryGetValue(colony.ColonyId, out var known) && known.Day == today) return known;
        var ba = sapi.World.BlockAccessor;
        var pos = colony.TownHallPos;
        int year = Math.Max(1, (int)cal.DaysPerYear);
        ClimateCondition? climate = null;
        float DayMean(double day)
        {
            float sum = 0;
            for (int q = 0; q < 4; q++)
            {
                double at = day + q * 0.25;
                climate = climate == null
                    ? ba.GetClimateAt(pos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, at)
                    : ba.GetClimateAt(pos, climate, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, at);
                sum += climate?.Temperature ?? 10f;
            }
            return sum / 4;
        }

        var f = new WinterForecast { Day = today, State = WinterState.None, Coldest = float.MaxValue };
        float first = DayMean(today);
        int start = -1, end = -1;
        if (first < 0)
        {
            // Winter now: how long until it thaws.
            start = 0;
            for (int d = 0; d <= year; d++)
            {
                float t = d == 0 ? first : DayMean(today + d);
                if (t >= 0) { end = d; break; }
                f.Coldest = Math.Min(f.Coldest, t);
            }
            f.State = WinterState.Now;
            f.FrostDay = (int)today; // this winter's warnings are over
            if (end < 0) end = year;
            f.Days = f.WinterDays = end;
        }
        else
        {
            for (int d = 1; d <= year; d++)
            {
                float t = DayMean(today + d);
                if (start < 0)
                {
                    if (t < 0) { start = d; f.Coldest = t; }
                    continue;
                }
                if (t >= 0) { end = d; break; }
                f.Coldest = Math.Min(f.Coldest, t);
            }
            if (start >= 0)
            {
                if (end < 0) end = Math.Min(start + year, start + year / 2);
                f.Days = start;
                f.FrostDay = (int)today + start;
                f.WinterDays = end - start;
                f.State = start > year / 2 ? WinterState.Early : WinterState.Coming;
            }
        }
        if (f.Coldest == float.MaxValue) f.Coldest = 0;
        winterForecasts[colony.ColonyId] = f;
        return f;
    }

    /// <summary>Where the colony stands for the winter, counted again every minute.</summary>
    public WinterReadiness Readiness(Colony colony)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (winterReadiness.TryGetValue(colony.ColonyId, out var known) && now - known.Ms < WinterCountMs) return known;
        var r = new WinterReadiness { Ms = now };
        if (!Config.WinterReadiness)
        {
            r.State = WinterState.Off;
            winterReadiness[colony.ColonyId] = r;
            return r;
        }
        var f = Forecast(colony);
        r.State = f.State;
        r.Days = f.Days;
        r.WinterDays = f.WinterDays;
        r.Coldest = f.Coldest;
        var people = colony.Citizens.Where(c => c.Alive && !c.Visitor).ToList();
        r.People = people.Count;
        if (f.State is WinterState.Coming or WinterState.Now)
        {
            int days = f.WinterDays;
            float mealSize = Math.Max(1f, Config.HungerPerDay / 2f);
            r.FoodNeeded = (int)Math.Ceiling(people.Count * days * 2.0);
            // Food in the colony's chests, valued as a settler would eat it (spoiling food counts for less).
            var eater = people.Select(c => sapi.World.GetEntityById(c.EntityId)).OfType<EntityAgent>().FirstOrDefault(e => e.Alive);
            if (eater != null)
            {
                double satiety = 0;
                foreach (var container in FindBlockEntities<BlockEntityContainer>(colony))
                {
                    foreach (var slot in container.Inventory)
                    {
                        if (slot.Empty || EvaluateFood(container, slot, eater) is not { } food) continue;
                        int portions = slot.Itemstack.Block is IBlockMealContainer meal
                            ? Math.Max(1, (int)meal.GetQuantityServings(sapi.World, slot.Itemstack))
                            : slot.Itemstack.StackSize;
                        satiety += food.Satiety * portions;
                    }
                }
                r.FoodMeals = (int)(satiety / mealSize);
            }
            else if (known != null)
            {
                r.FoodMeals = known.FoodMeals; // nobody loaded to weigh it with: keep the last count
            }

            r.Firewood = CountItems(FindBlockEntities<BlockEntityContainer>(colony), IsFirewood);
            int kitchens = colony.Buildings.Count(b => b.Level > 0 && b.Type is BuildingTypes.Cook or BuildingTypes.Bakery);
            r.FirewoodNeeded = days * (kitchens * Math.Max(0, Config.WinterCookFirewoodPerDay) + HomeFireWoodPerDay(colony));

            float limit = BodyTemperatureResistance();
            r.Dressed = people.Count(c => f.Coldest + WornClothes(c).Sum(s => s == null ? 0 : WarmthOf(s)) >= limit);
        }
        winterReadiness[colony.ColonyId] = r;

        winterPrepHuts.RemoveWhere(b => colony.Buildings.Contains(b));
        if (colony.WinterPrep && r.State == WinterState.Coming && r.Days <= Config.WinterPrepMonths * sapi.World.Calendar.DaysPerMonth)
        {
            foreach (var b in colony.Buildings)
            {
                if (b.Type == BuildingTypes.Forester && r.WoodShort || b.Type == BuildingTypes.Tailor && r.ClothesShort || b.Type == BuildingTypes.Cook)
                {
                    winterPrepHuts.Add(b);
                }
            }
        }
        return r;
    }

    /// <summary>Firewood the colony's home fires burn on a winter day: each lived-in home with a hearth.</summary>
    int HomeFireWoodPerDay(Colony colony) => !Config.HomeFires ? 0
        : LivedInHomes(colony).Count(h => HomeHearth(colony, h) != null) * Math.Max(0, Config.HomeFireFirewoodPerDay);

    /// <summary>True while the colony's settlers get ready for winter.</summary>
    public bool WinterPrepping(Colony colony) => colony.WinterPrep && Readiness(colony) is { State: WinterState.Coming } r
        && r.Days <= Config.WinterPrepMonths * sapi.World.Calendar.DaysPerMonth;

    /// <summary>True while this hut gets ready for winter (see the class summary).</summary>
    public bool PrepsForWinter(Building hut) => winterPrepHuts.Contains(hut);

    /// <summary>
    /// Part of the colony tick: keeps the count fresh and sends the two warnings (30 and 10 days before the first frost)
    /// when something is short, each once a winter, to members online and to the Town Hall's event log.
    /// </summary>
    void TickWinter(Colony colony)
    {
        if (!Config.WinterReadiness) return;
        var r = Readiness(colony);
        if (r.State != WinterState.Coming || !r.AnyShort) return;
        var f = Forecast(colony);
        var warnDays = (Config.WinterWarnDays ?? Array.Empty<int>()).OrderByDescending(d => d).ToArray();
        int stage = 0;
        for (int i = 0; i < warnDays.Length; i++) if (r.Days <= warnDays[i]) stage = i + 1;
        if (stage == 0) return;
        if (colony.WinterWarnedFor == f.FrostDay && colony.WinterWarnStage >= stage) return;
        colony.WinterWarnedFor = f.FrostDay;
        colony.WinterWarnStage = stage;
        string shortOf = WinterShortText(r);
        TellMembers(colony, "createacolony:winter-warning", colony.Name, r.Days, shortOf);
        LogEvent(colony, "event-winter-warning", r.Days.ToString(), shortOf);
    }

    /// <summary>What is short, as "food, firewood".</summary>
    static string WinterShortText(WinterReadiness r) => string.Join(", ", new[]
    {
        r.FoodShort ? Vintagestory.API.Config.Lang.Get("createacolony:winter-short-food") : null,
        r.WoodShort ? Vintagestory.API.Config.Lang.Get("createacolony:winter-short-firewood") : null,
        r.ClothesShort ? Vintagestory.API.Config.Lang.Get("createacolony:winter-short-clothes") : null
    }.OfType<string>());

    // ---- Home fires (0.191.0) ------------------------------------------------------------------

    /// <summary>How often home fires are looked after.</summary>
    const long HomeFireTickMs = 10_000;
    /// <summary>How long a home's hearth is remembered before it is looked for again.</summary>
    const long HearthCacheMs = 60_000;

    readonly Dictionary<string, long> nextHomeFireTick = new();
    readonly Dictionary<Building, (long Ms, BlockPos? Pos)> hearths = new();
    /// <summary>Settlers whose home's fire burns right now, with that home: inside it they are never cold.</summary>
    readonly Dictionary<int, Building> warmResidents = new();

    /// <summary>Homes (Residences and Boarding Houses) that are built and have someone living in them.</summary>
    IEnumerable<Building> LivedInHomes(Colony colony)
    {
        var lived = colony.Citizens.Where(c => c.Alive && !c.Visitor && c.HomeBedPos != null).Select(c => HomeOf(colony, c)).OfType<Building>().ToHashSet();
        return colony.Buildings.Where(b => IsHome(b.Type) && b.Level > 0 && lived.Contains(b));
    }

    /// <summary>The home's fire pit (in it or right by it; see HomeFirepit), remembered for a minute.</summary>
    BlockEntityFirepit? HomeHearth(Colony colony, Building home)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (hearths.TryGetValue(home, out var known) && now - known.Ms < HearthCacheMs)
        {
            return known.Pos == null ? null : sapi.World.BlockAccessor.GetBlockEntity(known.Pos) as BlockEntityFirepit;
        }
        var pit = HomeFirepit(colony, home);
        hearths[home] = (now, pit?.Pos.Copy());
        return pit;
    }

    /// <summary>True in the cold months: winter at the Town Hall, or colder than HomeFireBelowTemperature there now.</summary>
    bool HomeFireWeather(Colony colony)
    {
        if (IsWinterAt(colony.TownHallPos)) return true;
        var climate = sapi.World.BlockAccessor.GetClimateAt(colony.TownHallPos, EnumGetClimateMode.NowValues);
        return climate != null && climate.Temperature < Config.HomeFireBelowTemperature;
    }

    /// <summary>
    /// Part of the colony tick (every 10 seconds): in the cold months each lived-in home with a hearth asks couriers
    /// for firewood when it has less than HomeFireKeep in its chests, and in the evening and at night, when someone
    /// who lives there is at home, the fire is fed from those chests and lit. Settlers in a home whose fire burns are warm.
    /// </summary>
    void TickHomeFires(Colony colony)
    {
        if (!Config.HomeFires) return;
        long now = sapi.World.ElapsedMilliseconds;
        if (nextHomeFireTick.TryGetValue(colony.ColonyId, out long next) && now < next) return;
        nextHomeFireTick[colony.ColonyId] = now + HomeFireTickMs;

        foreach (var id in colony.Citizens.Select(c => c.CitizenId)) warmResidents.Remove(id);
        if (!HomeFireWeather(colony)) return;
        float hour = sapi.World.Calendar.HourOfDay;
        bool evening = hour >= Config.HomeFireFromHour || hour < Config.HomeFireToHour;
        foreach (var home in LivedInHomes(colony).ToList())
        {
            if (HomeHearth(colony, home) is not { } pit) continue;
            var chests = HomeChests(home);
            var residents = colony.Citizens.Where(c => c.Alive && !c.Visitor && HomeOf(colony, c) == home).ToList();

            // Firewood delivered: one open request at a time, from the warehouse.
            if (chests.Count > 0)
            {
                int have = CountItems(chests, IsFirewood) + (pit.fuelSlot.Empty ? 0 : pit.fuelSlot.StackSize);
                string tag = RequestTag(home);
                if (have < Config.HomeFireKeep && !colony.Requests.Any(r => r.RequestedBy == tag && r.ItemCode == FirewoodCode)
                    && CountItems(WarehouseContainers(colony), IsFirewood) > 0)
                {
                    AddRequest(colony, FirewoodCode, Config.HomeFireKeep - have, chests[0].Pos, tag);
                }
            }

            // Lit in the evening while someone is home.
            if (!pit.IsBurning && evening && residents.Any(c => AtHome(home, c)))
            {
                if (pit.fuelSlot.Empty)
                {
                    foreach (var chest in chests)
                    {
                        var slot = chest.Inventory.FirstOrDefault(s => !s.Empty && IsFirewood(s.Itemstack));
                        if (slot == null) continue;
                        pit.fuelSlot.Itemstack = slot.TakeOut(Math.Min(Math.Max(1, Config.HomeFireLoad), slot.StackSize));
                        slot.MarkDirty();
                        pit.fuelSlot.MarkDirty();
                        break;
                    }
                }
                if (!pit.fuelSlot.Empty && IsFirewood(pit.fuelSlot.Itemstack))
                {
                    pit.igniteFuel();
                    pit.canIgniteFuel = true;
                    pit.MarkDirty(true);
                }
            }

            if (pit.IsBurning) foreach (var c in residents) warmResidents[c.CitizenId] = home;
        }
    }

    const string FirewoodCode = "game:firewood";

    /// <summary>True when the settler is loaded and inside its home's walls.</summary>
    bool AtHome(Building home, CitizenRecord c) =>
        c.IsDowned == false && sapi.World.GetEntityById(c.EntityId) is { Alive: true } e && InsideHome(home, e.Pos.AsBlockPos);

    bool InsideHome(Building home, BlockPos pos)
    {
        var (min, max) = ZoneOf(home);
        return pos.X >= min.X && pos.X <= max.X && pos.Y >= min.Y - 1 && pos.Y <= max.Y && pos.Z >= min.Z && pos.Z <= max.Z;
    }

    /// <summary>True when the settler stands in its home while the home's fire burns (IsCold asks).</summary>
    bool WarmAtHome(CitizenRecord c, BlockPos pos) => warmResidents.TryGetValue(c.CitizenId, out var home) && InsideHome(home, pos);

    /// <summary>A farmer with nothing to grow in winter helps the couriers (AiTaskSettlerCourier).</summary>
    public bool WinterFarmerHelps(Colony colony, CitizenRecord c)
    {
        if (!Config.WinterFarmersHaul || c.JobCode != Jobs.Farmer || c.WorkplacePos == null || IsBrokenDown(c)) return false;
        if (!WorkerStatus.TryGetValue(c.CitizenId, out var status)) return false;
        return status == "no-sowing-winter" || (status is "growing" or "no-sowing-weather" && IsWinterAt(c.WorkplacePos));
    }

    // ---- Getting ready in autumn ------------------------------------------------------------

    /// <summary>A forester getting ready for winter keeps more firewood: at least WinterForesterFirewoodKeep, or twice its tab's number.</summary>
    int WinterRecipeKeep(Building hut, string key, int keep)
    {
        if (hut.Type != BuildingTypes.Forester || !PrepsForWinter(hut) || !key.EndsWith(":firewood")) return keep;
        return Math.Max(keep * 2, Math.Max(0, Config.WinterForesterFirewoodKeep));
    }

    /// <summary>A tailor getting ready for winter makes clothes that keep warm first (the order of the rest stays).</summary>
    List<SawKind> WinterOrder(Building hut, List<SawKind> kinds) =>
        hut.Type == BuildingTypes.Tailor && PrepsForWinter(hut) ? kinds.OrderByDescending(k => WarmthOf(k.Sample) > 0.5f).ToList() : kinds;

    /// <summary>True for food a cook getting ready for winter leaves alone: it keeps fresh longer than WinterCookSavesAboveDays.</summary>
    bool SavedForWinter(ItemStack stack)
    {
        double days = Config.WinterCookSavesAboveDays;
        if (days <= 0) return false;
        var props = stack.Collectible.GetTransitionableProperties(sapi.World, stack, null);
        var perish = props?.FirstOrDefault(p => p.Type == EnumTransitionType.Perish);
        if (perish == null) return stack.Collectible.NutritionProps != null || IsMealIngredient(stack); // never spoils: keeps best of all
        return perish.FreshHours.avg / Math.Max(1, sapi.World.Calendar.HoursPerDay) > days;
    }
}
