using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// What the six huts of 0.184.0 share (Charcoal Burner, Stonemason, Bakery, Fisher, Fletcher, Composter). The
/// Stonemason and the Fletcher do all their work on the Crafting recipes tab (AiTaskSettlerRecipeMaker): the Stonemason
/// makes anything the game's grid recipes make from stone alone (config HutRecipeInputParts), the Fletcher arrows and
/// bows. Both start with a few things to keep (config HutRecipeStartKeep), and make what builders and guard towers ask
/// couriers for when the warehouse has none (ColonyManager.Orders.cs).
/// </summary>
public partial class ColonyManager
{
    /// <summary>The huts added in 0.184.0, for the lists of hut types that show worker info and their placing message.</summary>
    public static bool IsSupplyHut(string type) => type is BuildingTypes.CharcoalBurner or BuildingTypes.Stonemason or BuildingTypes.Bakery
        or BuildingTypes.Fisher or BuildingTypes.Fletcher or BuildingTypes.Composter;

    /// <summary>A new hut's Crafting recipes tab starts with these (config HutRecipeStartKeep: code patterns and amounts).</summary>
    void KeepRecipesAtStart(Building hut)
    {
        if (!Config.HutRecipeStartKeep.TryGetValue(hut.Type, out var keeps)) return;
        foreach (var kind in HutRecipeKinds(hut.Type))
        {
            foreach (var (pattern, keep) in keeps)
            {
                if (!WildcardUtil.Match(new Vintagestory.API.Common.AssetLocation(pattern), kind.Sample.Collectible.Code)) continue;
                if (keep > 0) hut.RecipeKeep[kind.Key] = keep;
                break;
            }
        }
    }

    /// <summary>At a Stonemason's or Fletcher's Hut its tools and what its recipes need stay; the rest goes to the warehouse.</summary>
    ItemMatch MakerHutJunk(Colony colony, Building hut)
    {
        var recipeInputs = HutRecipeInputs(colony, hut);
        var tools = Config.HutRecipeTools.GetValueOrDefault(hut.Type) ?? Array.Empty<string>();
        return s => !tools.Contains(FirstPart(s.Collectible.Code)) && !recipeInputs(s);
    }

    /// <summary>A line for the Stonemason's or Fletcher's Hut: how many things it keeps and can make.</summary>
    public string MakerInfo(Colony colony, Building hut)
    {
        var kinds = HutRecipeKinds(colony, hut.Type);
        return Lang.Get("createacolony:maker-keeping", kinds.Count(k => RecipeKeep(hut, k.Key) > 0), kinds.Count);
    }
}
