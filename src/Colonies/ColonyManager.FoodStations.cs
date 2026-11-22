using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Expanded Foods stations (0.194.0), behind a check: only with A Culinary Artillery (the library Expanded Foods
/// runs on) and config CookUsesFoodStations. Nothing here references that mod's code; its recipes are read by
/// reflection, so a mod update that changes them only turns this part off.
///
/// Kneading recipes (dough, breaded food) are made at a mixing bowl, and simmering recipes (boiled eggs and the
/// like) at a saucepan, either placed near the Cook's Hut or sitting in a fire pit there. Both show on the Cook's
/// Crafting recipes tab like any other recipe; the cook makes what that tab asks it to keep. The cook does the
/// work by hand at the station: it does not run the station's own mixing or heating. Simmering recipes that need
/// water get it from the cook's bucket (ColonyManager.Serving.cs); ones that make a liquid, or need another
/// liquid, are left out. Expanded Foods bread is baked in a clay oven (ColonyManager.StoneOven.cs).
/// </summary>
public partial class ColonyManager
{
    const string ExpandedFoodsDomain = "expandedfoods";
    public const string MixingBowlStation = "mixingbowl";
    public const string SaucepanStation = "saucepan";

    /// <summary>True when the cook may use the food mods' stations.</summary>
    public bool FoodStationsOn => Config.CookUsesFoodStations && sapi.ModLoader.IsModEnabled(AcaDomain);

    /// <summary>True when the cook may bake in a game clay oven (with Expanded Foods, whose bread bakes there).</summary>
    public bool ClayOvensOn => Config.CookUsesClayOven && (sapi.ModLoader.IsModEnabled(ExpandedFoodsDomain) || sapi.ModLoader.IsModEnabled(AcaDomain));

    List<SawKind>? stationKinds;

    /// <summary>Everything the mixing bowl and the saucepan make, as Crafting recipes tab kinds. Empty without the mod.</summary>
    public List<SawKind> StationKinds()
    {
        if (stationKinds != null) return stationKinds;
        var kinds = new Dictionary<string, SawKind>();
        if (!FoodStationsOn) return stationKinds = new List<SawKind>();
        try
        {
            var registry = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "ACulinaryArtillery.ACARecipeRegistrySystem");
            if (registry == null) return stationKinds = new List<SawKind>();
            int dough = 0, simmer = 0;
            foreach (var recipe in ListField(registry, "DoughRecipes")) dough += AddDoughRecipe(kinds, recipe);
            foreach (var recipe in ListField(registry, "SimmerRecipes")) simmer += AddSimmerRecipe(kinds, recipe);
            Mod.Logger.Notification("Food stations: {0} kneading and {1} simmering recipes the cook can make", dough, simmer);
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Could not read A Culinary Artillery's recipes, the cook will not use its stations: {0}", e.Message);
            kinds.Clear();
        }
        foreach (var kind in kinds.Values) kind.Recipes.Sort((a, b) => a.InputCount.CompareTo(b.InputCount));
        return stationKinds = kinds.Values.OrderBy(k => k.Key).ToList();
    }

    static IEnumerable<object> ListField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(owner) is IEnumerable list ? list.Cast<object>() : Enumerable.Empty<object>();

    static T? Member<T>(object owner, string name) where T : class
    {
        var type = owner.GetType();
        return (type.GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(owner)
            ?? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(owner)) as T;
    }

    static bool RecipeEnabled(object recipe) => recipe.GetType().GetProperty("Enabled")?.GetValue(recipe) is not false;

    /// <summary>
    /// A kneading recipe: each ingredient may be one of several things, so it becomes one recipe for each mix
    /// (up to StationRecipeMixes of them). Returns how many it added.
    /// </summary>
    int AddDoughRecipe(Dictionary<string, SawKind> kinds, object recipe)
    {
        if (!RecipeEnabled(recipe) || Member<JsonItemStack>(recipe, "Output")?.ResolvedItemstack is not { } output) return 0;
        if (Member<Array>(recipe, "Ingredients") is not { Length: > 0 } ingredients) return 0;
        var choices = new List<CraftingRecipeIngredient[]>();
        foreach (var ing in ingredients)
        {
            var inputs = (ing == null ? null : Member<CraftingRecipeIngredient[]>(ing, "Inputs"))?.Where(StationPartOk).ToArray();
            if (inputs == null || inputs.Length == 0) return 0;
            choices.Add(inputs);
        }
        int added = 0;
        foreach (var mix in Mixes(choices, Math.Max(1, Config.StationRecipeMixes)))
        {
            AddStationRecipe(kinds, new SawRecipe
            {
                Inputs = mix.Select(p => (p, Math.Max(1, p.Quantity))).ToList(),
                Output = output.Clone(),
                Station = MixingBowlStation
            });
            added++;
        }
        return added;
    }

    /// <summary>A simmering recipe with solid ingredients (and maybe water) that makes something solid. Returns 1 if added.</summary>
    int AddSimmerRecipe(Dictionary<string, SawKind> kinds, object recipe)
    {
        if (!RecipeEnabled(recipe) || Member<CombustibleProperties>(recipe, "Simmering") is not { } props) return 0;
        if (props.SmeltedStack?.ResolvedItemstack is not { } output || IsLiquid(output)) return 0;
        if (Member<CraftingRecipeIngredient[]>(recipe, "Ingredients") is not { Length: > 0 } ingredients) return 0;
        int water = 0;
        var parts = new List<(CraftingRecipeIngredient, int)>();
        foreach (var part in ingredients)
        {
            if (part.ResolvedItemStack is { } r && IsWater(r)) { water += part.Quantity * Math.Max(1, props.SmeltedRatio); continue; }
            if (!StationPartOk(part)) return 0;
            parts.Add((part, Math.Max(1, part.Quantity * Math.Max(1, props.SmeltedRatio))));
        }
        if (parts.Count == 0) return 0;
        var made = output.Clone();
        made.StackSize = Math.Max(1, props.SmeltedStack.StackSize);
        AddStationRecipe(kinds, new SawRecipe { Inputs = parts, Output = made, Station = SaucepanStation, Water = water });
        return 1;
    }

    /// <summary>A part the cook can carry: a resolved or wildcard solid, not a liquid.</summary>
    static bool StationPartOk(CraftingRecipeIngredient part) =>
        part.Code != null && (part.ResolvedItemStack == null ? part.IsWildCard : !IsLiquid(part.ResolvedItemStack));

    static void AddStationRecipe(Dictionary<string, SawKind> kinds, SawRecipe recipe)
    {
        string key = recipe.Output.Collectible.Code.ToString();
        if (!kinds.TryGetValue(key, out var kind)) kinds[key] = kind = new SawKind { Key = key, Sample = recipe.Output.Clone(), Recipes = new List<SawRecipe>() };
        kind.Recipes.Add(recipe);
    }

    /// <summary>One pick from each list, in order, at most max mixes.</summary>
    static IEnumerable<CraftingRecipeIngredient[]> Mixes(List<CraftingRecipeIngredient[]> choices, int max)
    {
        var at = new int[choices.Count];
        for (int n = 0; n < max; n++)
        {
            yield return choices.Select((c, i) => c[at[i]]).ToArray();
            int k = choices.Count - 1;
            while (k >= 0 && ++at[k] >= choices[k].Length) at[k--] = 0;
            if (k < 0) yield break;
        }
    }

    /// <summary>The Cook's Hut's kinds plus the stations', joined by what they make.</summary>
    List<SawKind> WithStationKinds(List<SawKind> kinds)
    {
        var stations = StationKinds();
        if (stations.Count == 0) return kinds;
        var byKey = kinds.ToDictionary(k => k.Key, k => k);
        foreach (var s in stations)
        {
            if (byKey.TryGetValue(s.Key, out var have)) byKey[s.Key] = new SawKind { Key = have.Key, Sample = have.Sample, Recipes = have.Recipes.Concat(s.Recipes).ToList() };
            else byKey[s.Key] = s;
        }
        return byKey.Values.OrderBy(k => k.Key).ToList();
    }

    /// <summary>Where the cook uses a station near the Cook's Hut: a placed mixing bowl or saucepan, or a fire pit holding a saucepan. Null when there is none.</summary>
    public BlockPos? StationPos(Building hut, string station)
    {
        int r = Config.CookFirepitRadius;
        var h = hut.HutPos;
        var area = new Cuboidi(h.X - r, h.Y - 4, h.Z - r, h.X + r, h.Y + 4, h.Z + r);
        string beName = station == MixingBowlStation ? "BlockEntityMixingBowl" : "BlockEntitySaucepan";
        BlockPos? best = null;
        double bestDist = double.MaxValue;
        foreach (var be in FindBlockEntities<BlockEntity>(area))
        {
            bool fits = be.GetType().Name == beName
                || station == SaucepanStation && be is BlockEntityFirepit pit && pit.inputSlot.Itemstack?.Collectible.Code.Path.Contains("saucepan") == true;
            if (!fits) continue;
            double d = be.Pos.DistanceSqTo(h.X, h.Y, h.Z);
            if (d < bestDist) { bestDist = d; best = be.Pos.Copy(); }
        }
        return best;
    }

    /// <summary>Makes a station recipe once from the cook's inventory, with water from its bucket. False when something is missing.</summary>
    public bool CraftAtStation(Colony colony, Building hut, CitizenRecord cook, SawRecipe recipe)
    {
        if (recipe.Water > 0 && CookWaterPortions(cook) < recipe.Water) return false;
        if (!HasSawInputs(cook, recipe)) return false;
        if (recipe.Water > 0 && !PourWater(colony, cook, new DummySlot(), recipe.Water)) return false;
        return CraftHutRecipe(colony, hut, cook, recipe);
    }

    /// <summary>A line for the Cook's Hut window about the stations, or null without the mod.</summary>
    public string? StationInfo(Building hut)
    {
        if (!FoodStationsOn || StationKinds().Count == 0) return null;
        string Has(string s) => Lang.Get(StationPos(hut, s) != null ? "createacolony:stations-yes" : "createacolony:stations-no");
        return Lang.Get("createacolony:stations-info", Has(MixingBowlStation), Has(SaucepanStation));
    }
}
