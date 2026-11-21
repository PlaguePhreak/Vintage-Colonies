using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A tanner works from the Tannery (ColonyManager.Tanning.cs). In order: it takes finished hides and leather out
/// of the barrels, puts hides (or lime, logs, borax) into barrels whose liquid takes them and seals them, scrapes
/// soaked hides at the hut with its knife, and starts a new liquid in an empty barrel: it carries water from the
/// nearest water in a bucket, then adds the solid. It asks couriers for hides and the rest.
/// </summary>
public class AiTaskSettlerTanner : AiTaskSettlerWorker
{
    enum Work { None, Unload, Load, Scrape, Draw, Pour }

    Work work;
    ColonyManager.TanStep? tanStep;
    /// <summary>Litres of water in the tanner's bucket.</summary>
    float water;
    /// <summary>Barrels being filled with water for a liquid step: how many litres each should hold.</summary>
    readonly Dictionary<BlockPos, float> waterWanted = new();
    BlockPos? pourInto;

    public AiTaskSettlerTanner(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Tanner;
    protected override string HutType => BuildingTypes.Tannery;
    protected override EnumTool Tool => EnumTool.Knife;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.TannerSecondsPerStep;
    protected override bool WorkNeedsTool => work == Work.Scrape;

    /// <summary>Keeps a bucket, and the hides and other things it takes to the barrels.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsBucket(stack) || manager!.IsTanSolid(stack);

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        work = Work.None;
        var barrels = m.TannerBarrels(hut).Where(b => !IsUnreachable(b.Pos)).ToList();
        m.RequestTanSupplies(colony, hut, storage, citizen);
        if (barrels.Count == 0)
        {
            SetStatus("tanner-no-barrel");
            return false;
        }
        var here = entity.Pos.XYZ;
        barrels = barrels.OrderBy(b => b.Pos.DistanceSqTo(here.X, here.Y, here.Z)).ToList();

        // Finished hides and leather out first.
        if (!m.PackFull(citizen) && barrels.FirstOrDefault(ColonyManager.BarrelDone) is { } done)
        {
            work = Work.Unload;
            SetStatus("tanner-unloading");
            return GoWork(done.Pos);
        }

        // A barrel whose liquid is ready for something: hides before liquids.
        foreach (var barrel in barrels.Where(b => !b.Sealed && b.Inventory[0].Empty && !b.Inventory[1].Empty))
        {
            if (waterWanted.TryGetValue(barrel.Pos, out float wanted) && ColonyManager.BarrelLitres(barrel) < wanted - 0.01f) continue;
            var liquid = barrel.Inventory[1].Itemstack;
            foreach (var s in m.TanSteps().Where(s => s.Liquid.SatisfiesAsIngredient(liquid, false)).OrderByDescending(s => s.IsHide))
            {
                if (m.LoadUnits(citizen, barrel, s) > 0)
                {
                    work = Work.Load;
                    tanStep = s;
                    SetStatus("tanner-loading");
                    return GoWork(barrel.Pos);
                }
                ItemMatch fits = st => s.Solid.SatisfiesAsIngredient(st, false);
                if (!m.PackFull(citizen) && storage.FirstOrDefault(c => c.Inventory.Any(sl => !sl.Empty && fits(sl.Itemstack))) is { } chest)
                {
                    work = Work.Load;
                    tanStep = s;
                    SetStatus("tanner-fetching");
                    int want = s.Liquid.ConsumeQuantity.HasValue ? m.Config.TannerHideTake
                        : liquid.StackSize / System.Math.Max(1, s.Liquid.Quantity) * System.Math.Max(1, s.Solid.Quantity);
                    return FetchSupplies(chest.Pos, fits, want);
                }
            }
        }

        // Soaked hides to scrape.
        if (m.CarriesScrapable(citizen))
        {
            work = Work.Scrape;
            workRange = 2.5f;
            SetStatus("tanner-scraping");
            return GoWork(hut.HutPos);
        }
        ItemMatch scrapable = s => m.ScrapeRecipes().Any(r => r.Inputs[0].Part.SatisfiesAsIngredient(s, false));
        if (!m.PackFull(citizen) && storage.FirstOrDefault(c => c.Inventory.Any(sl => !sl.Empty && scrapable(sl.Itemstack))) is { } hides)
        {
            work = Work.Scrape;
            SetStatus("tanner-fetching");
            return FetchSupplies(hides.Pos, scrapable, m.Config.TannerHideTake);
        }

        // A new liquid: water first, then the solid (the step above loads it once the water is in).
        foreach (var barrel in barrels.Where(b => !b.Sealed && b.Inventory[0].Empty))
        {
            var inside = barrel.Inventory[1].Itemstack;
            bool plainWater = inside == null || inside.Collectible.Code.ToString() == ColonyManager.WaterCode;
            if (!plainWater) continue;
            if (!waterWanted.TryGetValue(barrel.Pos, out float litres))
            {
                if (m.NextLiquidStep(colony, hut, citizen, barrels, barrel, storage) is not { } next) continue;
                litres = next.Units * next.Step.Liquid.Quantity / (float)(m.Water(1)?.StackSize ?? 100);
                if (litres <= ColonyManager.BarrelLitres(barrel)) continue;
                waterWanted[barrel.Pos.Copy()] = litres;
            }
            if (ColonyManager.BarrelLitres(barrel) >= litres - 0.01f)
            {
                waterWanted.Remove(barrel.Pos);
                continue;
            }
            if (!HasBucket(citizen))
            {
                if (!m.PackFull(citizen) && storage.FirstOrDefault(c => c.Inventory.Any(sl => !sl.Empty && ColonyManager.IsBucket(sl.Itemstack))) is { } bucketChest)
                {
                    SetStatus("tanner-fetching");
                    return FetchSupplies(bucketChest.Pos, s => ColonyManager.IsBucket(s), 1);
                }
                if (m.CodeInWarehouse(colony, s => ColonyManager.IsEmptyBucket(s)) is { } bucket) m.EnsureCraftRequest(colony, hut, bucket, 1);
                SetStatus("tanner-needs-bucket");
                return false;
            }
            if (water > 0)
            {
                work = Work.Pour;
                pourInto = barrel.Pos.Copy();
                workRange = 2.5f;
                SetStatus("tanner-water");
                return GoWork(barrel.Pos);
            }
            if (m.FindWaterNear(colony, barrel.Pos, m.Config.TannerWaterRadius) is { } pond && !IsUnreachable(pond))
            {
                work = Work.Draw;
                pourInto = barrel.Pos.Copy();
                workRange = 4f;
                SetStatus("tanner-water");
                return GoWork(pond);
            }
            SetStatus("tanner-no-water");
            return false;
        }

        bool anyHides = m.PackItems(citizen).Any(m.IsTanHide) || storage.Any(c => c.Inventory.Any(s => !s.Empty && m.IsTanHide(s.Itemstack)));
        SetStatus(barrels.Any(b => b.Sealed) ? "tanner-waiting" : anyHides ? "tanner-needs-supplies" : "tanner-no-hides");
        return false;
    }

    bool HasBucket(CitizenRecord citizen) => ColonyManager.IsBucket(Hand?.Itemstack) || manager!.PackCount(citizen, s => ColonyManager.IsBucket(s)) > 0;

    protected override string WorkAnimation(ItemSlot? hand) => work == Work.Scrape ? "knifecut"
        : SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic";

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        var be = entity.World.BlockAccessor.GetBlockEntity(target) as BlockEntityBarrel;
        switch (work)
        {
            case Work.Unload:
                if (be != null && ColonyManager.BarrelDone(be)) m.UnloadBarrel(colony, citizen, hut, be);
                break;
            case Work.Load:
                if (be != null && tanStep != null && !be.Sealed) m.LoadBarrel(colony, citizen, hut, be, tanStep);
                waterWanted.Remove(target);
                break;
            case Work.Scrape:
                if (Hand is { } hand && m.ScrapeHide(colony, citizen, hut, entity, hand)) SyncHand();
                break;
            case Work.Draw:
                water = m.Config.TannerBucketLitres;
                entity.World.PlaySoundAt(new AssetLocation("sounds/block/water"), entity, null, true, 16);
                break;
            case Work.Pour:
                if (be != null && pourInto != null && waterWanted.TryGetValue(pourInto, out float wanted)) water -= m.PourWater(be, water, wanted);
                if (water < 0.01f) water = 0;
                break;
        }
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-tanner-" + (work == Work.None ? "idle" : work.ToString().ToLowerInvariant()));
}
