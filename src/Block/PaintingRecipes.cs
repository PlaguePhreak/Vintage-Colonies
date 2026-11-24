using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace CreateAColony.Blocks;

/// <summary>
/// Makes a grid recipe for every painting: a blank canvas in the middle and a bucket of each of its dyes
/// around it (above, below, left, right, then the corners), as Aaron asked. Dyes are liquids, so the recipes
/// are made here once the game's items are known: each bucket must hold enough of the right dye, and only the
/// dye is used up. A picture whose dye the game does not have is skipped, with a warning in the log.
/// </summary>
public class PaintingRecipes : ModSystem
{
    /// <summary>Litres of each dye one painting uses.</summary>
    const float LitresPerDye = 2;
    /// <summary>Grid cells for the dyes, in order; the canvas is cell 4, the middle.</summary>
    static readonly int[] DyeCells = { 1, 7, 3, 5, 0, 2, 6, 8 };
    /// <summary>Other names a dye colour might go by.</summary>
    static readonly Dictionary<string, string[]> Alternatives = new()
    {
        ["gray"] = new[] { "grey" },
        ["blue"] = new[] { "woad" },
    };
    /// <summary>Colours the game has no dye for, made from a mix of other dyes (Aaron: orange and black for brown).</summary>
    static readonly Dictionary<string, string[]> Mixes = new()
    {
        ["brown"] = new[] { "orange", "black" },
    };

    /// <summary>The dyes a painting really needs: mixed colours are swapped for their parts, each dye once.</summary>
    public static string[] RealDyes(IEnumerable<string> colours) =>
        colours.SelectMany(c => Mixes.TryGetValue(c, out var parts) ? parts : new[] { c }).Distinct().ToArray();

    // After the game's grid recipe loader (1), so items, blocks and recipes are all loaded.
    public override double ExecuteOrder() => 1.1;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>The game's liquid dye item for a colour (game:dye-red), or null when there is none.</summary>
    public static Item? DyeItem(IWorldAccessor world, string colour)
    {
        foreach (var name in new[] { colour }.Concat(Alternatives.GetValueOrDefault(colour) ?? System.Array.Empty<string>()))
        {
            var item = world.GetItem(new AssetLocation("game", "dye-" + name));
            if (item != null) return item;
        }
        return null;
    }

    /// <summary>The dye's name for tooltips: the game's own name when it has the dye, else the colour.</summary>
    public static string DyeName(IWorldAccessor world, string colour)
    {
        var item = DyeItem(world, colour);
        return item == null ? colour : item.GetHeldItemName(new ItemStack(item));
    }

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api is not ICoreServerAPI sapi) return;
        var world = sapi.World;
        var buckets = world.Blocks.Where(b => b is BlockBucket && b.Code != null).Select(b => b.Code.ToString()).Distinct().ToList();
        if (buckets.Count == 0)
        {
            sapi.Logger.Warning("[createacolony] No buckets found, so paintings cannot be made from dyes.");
            return;
        }

        int made = 0;
        foreach (var painting in world.SearchBlocks(new AssetLocation("createacolony", "painting-*-north")).OfType<BlockPainting>())
        {
            var dyes = RealDyes(painting.Dyes);
            if (dyes.Length == 0 || dyes.Length > DyeCells.Length) continue;
            var dyeItems = dyes.Select(colour => DyeItem(world, colour)).ToArray();
            if (dyeItems.Any(item => item == null))
            {
                sapi.Logger.Warning("[createacolony] No dye for painting {0} ({1}); it has no recipe.", painting.Art, string.Join(", ", dyes));
                continue;
            }
            foreach (var bucket in buckets)
            {
                if (Register(sapi, painting, dyeItems!, bucket)) made++;
            }
        }
        sapi.Logger.Event("[createacolony] {0} painting recipes made", made);
    }

    bool Register(ICoreServerAPI sapi, BlockPainting painting, Item[] dyeItems, string bucket)
    {
        var cells = Enumerable.Repeat("_", 9).ToArray();
        cells[4] = "C";
        var ingredients = new JObject
        {
            ["C"] = new JObject { ["type"] = "block", ["code"] = $"createacolony:painting-{BlockPainting.Blank}-north" }
        };
        for (int i = 0; i < dyeItems.Length; i++)
        {
            string key = ((char)('A' + i)).ToString();
            cells[DyeCells[i]] = key;
            ingredients[key] = new JObject
            {
                ["type"] = "block",
                ["code"] = bucket,
                ["recipeAttributes"] = new JObject
                {
                    ["requiresContent"] = new JObject { ["type"] = "item", ["code"] = dyeItems[i].Code.ToString() },
                    ["requiresLitres"] = LitresPerDye
                }
            };
        }
        var json = new JObject
        {
            ["ingredientPattern"] = string.Join(",", Enumerable.Range(0, 3).Select(row => string.Concat(cells.Skip(row * 3).Take(3)))),
            ["ingredients"] = ingredients,
            ["width"] = 3,
            ["height"] = 3,
            ["output"] = new JObject { ["type"] = "block", ["code"] = painting.Code.ToString() }
        };
        var name = new AssetLocation("createacolony", $"recipes/grid/painting-{painting.Art}-{bucket.Replace(':', '-')}");
        var recipe = json.ToObject<GridRecipe>(name.Domain);
        if (recipe == null) return false;
        recipe.Name = name;
        int loaded = 0, failed = 0;
        RecipeLoader.LoadRecipe(sapi, name, recipe, true, r => sapi.RegisterCraftingRecipe((GridRecipe)r), ref loaded, ref failed);
        return loaded > 0;
    }
}
