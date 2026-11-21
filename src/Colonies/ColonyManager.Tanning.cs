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
/// The Tannery. Its tanner makes leather the game's way, in the barrels near the hut (config TannerBarrelRadius):
/// it fills a barrel with water from the nearest water, adds lime (or borax) for limewater, soaks raw hides in it,
/// scrapes the soaked hides at the hut with its knife, then soaks them in weak tannin (oak or acacia logs in water)
/// and strong tannin until they are leather. It reads the steps from the game's barrel and crafting recipes, so
/// other mods' tanning recipes work too. Hides come from the warehouse (couriers bring them when asked), the
/// leather goes back with the couriers. It only touches barrels that are empty or hold tanning things.
/// </summary>
public partial class ColonyManager
{
    public const string WaterCode = "game:waterportion";

    /// <summary>One barrel step: a solid soaked in a liquid. Hide steps soak a hide, the rest make a liquid the hide steps need.</summary>
    public class TanStep
    {
        public BarrelRecipe Recipe = null!;
        public BarrelRecipeIngredient Solid = null!;
        public BarrelRecipeIngredient Liquid = null!;
        public bool IsHide;
        /// <summary>The code of the liquid a liquid step makes, or null for a hide step.</summary>
        public string? Makes;
        public string LiquidCode => Liquid.Code?.ToString() ?? "";
        public bool FromWater => LiquidCode == WaterCode;
    }

    List<TanStep>? tanSteps;
    List<SawRecipe>? scrapeRecipes;

    static bool IsLiquidIngredient(BarrelRecipeIngredient i) => i.Litres >= 0;

    /// <summary>The hide steps and the liquid steps they need, from the game's barrel recipes, worked out once.</summary>
    public List<TanStep> TanSteps()
    {
        if (tanSteps != null) return tanSteps;
        var steps = new List<TanStep>();
        var recipes = sapi.GetBarrelRecipes().Where(r => r.Ingredients is { Length: 2 } && r.Output != null).ToList();
        foreach (var r in recipes)
        {
            var liquid = r.Ingredients!.FirstOrDefault(IsLiquidIngredient);
            var solid = r.Ingredients!.FirstOrDefault(i => !IsLiquidIngredient(i));
            if (liquid == null || solid == null || solid.Code?.Path.StartsWith("hide-") != true) continue;
            steps.Add(new TanStep { Recipe = r, Solid = solid, Liquid = liquid, IsHide = true });
        }
        var needed = steps.Select(s => s.LiquidCode).Where(c => c != WaterCode).ToHashSet();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var r in recipes)
            {
                if (r.Output!.Litres <= 0 || r.Output.Code == null || !needed.Contains(r.Output.Code.ToString())) continue;
                if (steps.Any(s => s.Recipe == r)) continue;
                var liquid = r.Ingredients!.FirstOrDefault(IsLiquidIngredient);
                var solid = r.Ingredients!.FirstOrDefault(i => !IsLiquidIngredient(i));
                if (liquid == null || solid == null) continue;
                steps.Add(new TanStep { Recipe = r, Solid = solid, Liquid = liquid, Makes = r.Output.Code.ToString() });
                if (liquid.Code?.ToString() is { } code && code != WaterCode && needed.Add(code)) grew = true;
            }
        }
        return tanSteps = steps;
    }

    /// <summary>The game's crafting recipes that turn one hide into another with a knife: scraping soaked hides.</summary>
    public List<SawRecipe> ScrapeRecipes()
    {
        if (scrapeRecipes != null) return scrapeRecipes;
        var knives = ToolSamples(new[] { "knife" });
        return scrapeRecipes = ScanGridRecipes(i => i.Code?.Path.StartsWith("hide-soaked") == true,
                tool => knives.Any(k => tool.SatisfiesAsIngredient(k, false)),
                o => FirstPart(o.Collectible.Code) == "hide", 1)
            .SelectMany(k => k.Recipes).Where(r => r.Tool != null && r.Inputs.Count == 1).ToList();
    }

    /// <summary>A solid some tanning step takes: a hide, lime, borax, a log.</summary>
    public bool IsTanSolid(ItemStack s) =>
        TanSteps().Any(t => t.Solid.SatisfiesAsIngredient(s, false)) || ScrapeRecipes().Any(r => r.Inputs[0].Part.SatisfiesAsIngredient(s, false));

    /// <summary>A hide that is not leather yet.</summary>
    public bool IsTanHide(ItemStack s) => FirstPart(s.Collectible.Code) == "hide" && IsTanSolid(s);

    /// <summary>Water or a liquid a tanning step takes or makes.</summary>
    public bool IsTanLiquid(ItemStack s)
    {
        string code = s.Collectible.Code.ToString();
        return code == WaterCode || TanSteps().Any(t => t.LiquidCode == code || t.Makes == code);
    }

    /// <summary>True for a barrel the tanner may use: empty, or holding only tanning things.</summary>
    public bool TanBarrel(BlockEntityBarrel b)
    {
        var solid = b.Inventory[0].Itemstack;
        var liquid = b.Inventory[1].Itemstack;
        if (liquid != null && !IsTanLiquid(liquid)) return false;
        return solid == null || IsTanSolid(solid) || FirstPart(solid.Collectible.Code) is "hide" or "leather";
    }

    /// <summary>The barrels near the Tannery the tanner may use.</summary>
    public List<BlockEntityBarrel> TannerBarrels(Building hut) =>
        AllNear<BlockEntityBarrel>(hut.HutPos, Math.Max(1, Config.TannerBarrelRadius)).Where(TanBarrel).ToList();

    static float ItemsPerLitre(ItemStack s) => BlockLiquidContainerBase.GetContainableProps(s)?.ItemsPerLitre ?? 100f;

    /// <summary>Litres of liquid in the barrel.</summary>
    public static float BarrelLitres(BlockEntityBarrel b) => b.Inventory[1].Itemstack is { } l ? l.StackSize / ItemsPerLitre(l) : 0;

    /// <summary>A stack of water, so many litres.</summary>
    public ItemStack? Water(float litres)
    {
        var item = sapi.World.GetItem(new AssetLocation(WaterCode));
        if (item == null) return null;
        var stack = new ItemStack(item);
        stack.StackSize = (int)Math.Round(litres * ItemsPerLitre(stack));
        return stack;
    }

    /// <summary>Pours water the tanner carries into the barrel, up to the litres wanted; returns the litres poured.</summary>
    public float PourWater(BlockEntityBarrel barrel, float carried, float wantLitres)
    {
        var slot = barrel.Inventory[1];
        if (slot.Itemstack != null && slot.Itemstack.Collectible.Code.ToString() != WaterCode) return 0;
        float room = Math.Min(barrel.CapacityLitres - BarrelLitres(barrel), wantLitres - BarrelLitres(barrel));
        float pour = Math.Min(carried, room);
        if (pour <= 0 || Water(pour) is not { } water) return 0;
        if (slot.Itemstack == null) slot.Itemstack = water;
        else slot.Itemstack.StackSize += water.StackSize;
        slot.MarkDirty();
        barrel.MarkDirty(true);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/water"), barrel.Pos.X + 0.5, barrel.Pos.Y + 0.5, barrel.Pos.Z + 0.5, null);
        return pour;
    }

    /// <summary>The same kind of solid as the first one the settler carries for this step, and how many it has.</summary>
    (ItemStack? Kind, int Count) CarriedSolid(CitizenRecord citizen, TanStep step)
    {
        var kind = PackItems(citizen).FirstOrDefault(s => step.Solid.SatisfiesAsIngredient(s, false));
        return kind == null ? (null, 0) : (kind, PackCount(citizen, s => s.Equals(sapi.World, kind, GlobalConstants.IgnoredStackAttributes)));
    }

    /// <summary>How many batches of the step the settler can put in the barrel now (0 when none).</summary>
    public int LoadUnits(CitizenRecord citizen, BlockEntityBarrel barrel, TanStep step)
    {
        var liquid = barrel.Inventory[1].Itemstack;
        if (liquid == null || !barrel.Inventory[0].Empty || !step.Liquid.SatisfiesAsIngredient(liquid, false)) return 0;
        var (kind, have) = CarriedSolid(citizen, step);
        if (kind == null) return 0;
        int solidQ = Math.Max(1, step.Solid.Quantity), liqQ = Math.Max(1, step.Liquid.Quantity);
        int byLiquid = step.Liquid.ConsumeQuantity.HasValue ? liquid.StackSize / liqQ : liquid.StackSize % liqQ == 0 ? liquid.StackSize / liqQ : 0;
        int units = Math.Min(byLiquid, kind.Collectible.MaxStackSize / solidQ);
        // A step that turns all the liquid into another needs solids for all of it.
        if (!step.Liquid.ConsumeQuantity.HasValue) return have >= units * solidQ ? units : 0;
        return Math.Min(units, have / solidQ);
    }

    /// <summary>Puts the solids in the barrel and seals it; false (and nothing changed) if the game's recipe does not take them.</summary>
    public bool LoadBarrel(Colony colony, CitizenRecord citizen, Building hut, BlockEntityBarrel barrel, TanStep step)
    {
        int units = LoadUnits(citizen, barrel, step);
        var (kind, _) = CarriedSolid(citizen, step);
        if (units <= 0 || kind == null) return false;
        var solids = TakeFromPack(colony, citizen, s => s.Equals(sapi.World, kind, GlobalConstants.IgnoredStackAttributes), units * Math.Max(1, step.Solid.Quantity));
        if (solids == null) return false;
        var slot = barrel.Inventory[0];
        slot.Itemstack = solids;
        slot.MarkDirty();
        // The barrel finds the recipe itself; one without seal time (limewater) is done at once.
        if (barrel.CurrentRecipe != null)
        {
            barrel.SealBarrel();
        }
        else if (!slot.Empty)
        {
            AddToPack(colony, citizen, slot.Itemstack);
            slot.Itemstack = null;
            slot.MarkDirty();
            return false;
        }
        barrel.MarkDirty(true);
        AddStat(hut, "barrel");
        return true;
    }

    /// <summary>True when the barrel has a solid left in it that is done (or nothing will take): the tanner takes it out.</summary>
    public static bool BarrelDone(BlockEntityBarrel b) => !b.Sealed && !b.Inventory[0].Empty && b.CurrentRecipe == null;

    /// <summary>Takes the solid out of a barrel into the tanner's inventory.</summary>
    public void UnloadBarrel(Colony colony, CitizenRecord citizen, Building hut, BlockEntityBarrel barrel)
    {
        var slot = barrel.Inventory[0];
        if (slot.Empty) return;
        var stack = slot.Itemstack;
        int n = AddToPack(colony, citizen, stack);
        if (FirstPart(stack.Collectible.Code) == "leather") AddStat(hut, "leather", n);
        slot.Itemstack = stack.StackSize > 0 ? stack : null;
        slot.MarkDirty();
        barrel.MarkDirty(true);
    }

    /// <summary>Scrapes one soaked hide the tanner carries with the knife in its hand. False when it has none.</summary>
    public bool ScrapeHide(Colony colony, CitizenRecord citizen, Building hut, Vintagestory.API.Common.Entities.Entity tanner, ItemSlot knife)
    {
        if (!IsKnife(knife.Itemstack)) return false;
        foreach (var r in ScrapeRecipes())
        {
            var (part, quantity) = r.Inputs[0];
            if (PackCount(citizen, s => part.SatisfiesAsIngredient(s, false)) < quantity) continue;
            if (TakeFromPack(colony, citizen, s => part.SatisfiesAsIngredient(s, false), quantity) == null) continue;
            AddToPack(colony, citizen, r.Output.Clone());
            if (tanner is Vintagestory.API.Common.EntityAgent agent) WearTool(agent, knife);
            AddStat(hut, "scraped");
            return true;
        }
        return false;
    }

    /// <summary>True when the settler carries a hide it can scrape.</summary>
    public bool CarriesScrapable(CitizenRecord citizen) => ScrapeRecipes().Any(r => PackCount(citizen, s => r.Inputs[0].Part.SatisfiesAsIngredient(s, false)) >= r.Inputs[0].Quantity);

    /// <summary>How far along the hide is: the later steps come first, so leather gets finished.</summary>
    static int TanStage(TanStep step)
    {
        string path = step.Solid.Code?.Path ?? "";
        return path.Contains("prepared") ? 3 : path.Contains("scraped") ? 2 : path.Contains("soaked") ? 1 : 0;
    }

    /// <summary>
    /// The liquid step to start in an empty barrel: one made from water whose liquid a hide on hand needs (directly,
    /// or through another liquid, as strong tannin is made from weak), that no other barrel already holds or makes.
    /// With the number of batches the solids at hand allow and the barrel holds. Null when there is none.
    /// </summary>
    public (TanStep Step, int Units)? NextLiquidStep(Colony colony, Building hut, CitizenRecord citizen, List<BlockEntityBarrel> barrels, BlockEntityBarrel barrel,
        IEnumerable<BlockEntityContainer> storage)
    {
        var steps = TanSteps();
        var stores = storage.ToList();
        int Have(BarrelRecipeIngredient ing) =>
            PackCount(citizen, s => ing.SatisfiesAsIngredient(s, false)) + CountItems(stores, s => ing.SatisfiesAsIngredient(s, false));
        var busy = barrels.Where(b => b != barrel).SelectMany(b => new[]
        {
            b.Inventory[1].Itemstack?.Collectible.Code.ToString(),
            b.CurrentRecipe?.Output?.Code?.ToString()
        }).OfType<string>().ToHashSet();

        foreach (var hide in steps.Where(s => s.IsHide && Have(s.Solid) > 0).OrderByDescending(TanStage))
        {
            string? want = hide.LiquidCode;
            for (int depth = 0; want != null && depth < 4; depth++)
            {
                if (busy.Contains(want)) break;
                var maker = steps.FirstOrDefault(s => s.Makes == want && s.FromWater && Have(s.Solid) >= Math.Max(1, s.Solid.Quantity));
                if (maker != null)
                {
                    float litresPerUnit = maker.Liquid.Quantity / ItemsPerLitre(maker.Liquid.ResolvedItemStack ?? Water(1)!);
                    int byBarrel = (int)(barrel.CapacityLitres / Math.Max(0.01f, litresPerUnit));
                    int units = Math.Min(Math.Min(byBarrel, Have(maker.Solid) / Math.Max(1, maker.Solid.Quantity)), Math.Max(1, Config.TannerBatches));
                    if (units > 0) return (maker, units);
                }
                // Made from another liquid (strong tannin from weak): start that one instead.
                want = steps.FirstOrDefault(s => s.Makes == want && !s.FromWater)?.LiquidCode;
            }
        }
        return null;
    }

    /// <summary>A solid for one of the steps in the warehouse, so the tanner asks couriers for it: hides first.</summary>
    public void RequestTanSupplies(Colony colony, Building hut, IEnumerable<BlockEntityContainer> storage, CitizenRecord citizen)
    {
        var stores = storage.ToList();
        bool hides = PackItems(citizen).Any(IsTanHide) || CountItems(stores, IsTanHide) > 0;
        if (!hides && CodeInWarehouse(colony, IsTanHide) is { } hide) EnsureCraftRequest(colony, hut, hide, Config.TannerHideRequest);
        if (!hides) return;
        foreach (var step in TanSteps().Where(s => !s.IsHide))
        {
            ItemMatch fits = s => step.Solid.SatisfiesAsIngredient(s, false);
            if (PackCount(citizen, fits) + CountItems(stores, fits) > 0) continue;
            if (CodeInWarehouse(colony, fits) is { } code) EnsureCraftRequest(colony, hut, code, Config.TannerSupplyRequest);
        }
    }

    /// <summary>The Tannery window line.</summary>
    string TannerInfo(Building hut) => Lang.Get("createacolony:tanner-progress", TannerBarrels(hut).Count, hut.StatTotals.GetValueOrDefault("scraped"), hut.StatTotals.GetValueOrDefault("leather"));

    /// <summary>At the Tannery everything but its knife, buckets and tanning things goes to the warehouse: the leather, mostly.</summary>
    ItemMatch TannerHutJunk(Colony colony, Building hut)
    {
        var recipeInputs = HutRecipeInputs(colony, hut);
        return s => !IsKnife(s) && !IsBucket(s) && !IsTanSolid(s) && !recipeInputs(s);
    }
}
