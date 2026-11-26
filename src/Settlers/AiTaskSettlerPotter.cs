using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A potter works from a Potter's Hut with the game's own pottery: it shapes clay on a clay forming
/// block by the hut, layer by layer, into whatever the hut keeps too few of, and fires the pieces in
/// pit kilns it digs nearby, several at once. Fired pottery goes to the hut storage, and couriers take it on to the
/// warehouse. It works by hand; clay and kiln fuel come from the hut storage, which couriers fill.
/// </summary>
public class AiTaskSettlerPotter : AiTaskSettlerWorker
{
    enum Work { Form, Dig, Load, Fuel, Light, Empty, Cover }

    Work work;

    public AiTaskSettlerPotter(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Potter;
    protected override string HutType => BuildingTypes.Potter;
    protected override EnumTool Tool => EnumTool.Knife; // unused: a potter needs no tool
    protected override bool NeedsTool => false;
    protected override ItemMatch Stock => ColonyManager.IsClay;
    protected override float WorkSeconds => work == Work.Form ? manager!.Config.PotterSecondsPerLayer : manager!.Config.PotterSecondsPerKilnStep;
    protected override bool WorkNeedsTool => false;

    /// <summary>Raw pieces wait in its inventory for the kiln, and it carries kiln fuel from the hut to the kiln.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => manager!.IsRawPottery(stack) || manager.IsKilnMaterial(stack)
        || manager.Config.KilnCover && manager.IsKilnCover(stack);

    protected override string WorkAnimation(ItemSlot? hand) => work == Work.Dig ? "breakhand" : "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var kilns = m.Kilns(colony, hut);
        var pack = m.PackItems(citizen);
        string? status = null;

        // Fired pottery comes out first, then kilns get lit, then the ones being built get their fuel.
        if (kilns.FirstOrDefault(k => k.State == ColonyManager.KilnState.Fired).Pos is { } fired) return Go(Work.Empty, fired, "kiln-emptying");
        // The spare fuel is kept up whether or not a kiln waits (0.202.0).
        m.EnsureKilnFuel(colony, hut, citizen, false);
        // A kiln ready to light (new, or one the rain put out) gets a cover two above it first (0.202.0).
        foreach (var (ready, _) in kilns.Where(k => k.State == ColonyManager.KilnState.Ready))
        {
            if (!m.KilnWaitsForCover(ready)) return Go(Work.Light, ready, "kiln-lighting");
            if (m.PackCount(citizen, m.IsKilnCover) > 0) return Go(Work.Cover, ready, "kiln-covering");
            var coverChest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && m.IsKilnCover(s.Itemstack)));
            if (coverChest != null && !m.PackFull(citizen))
            {
                SetStatus("kiln-covering");
                return FetchSupplies(coverChest.Pos, m.IsKilnCover, 1);
            }
            int bare = kilns.Count(k => k.State != ColonyManager.KilnState.Lit && !m.KilnCovered(k.Pos) && m.CanCoverKiln(k.Pos));
            m.EnsurePotterRequest(colony, hut, m.KilnCoverCode, System.Math.Max(1, bare));
            status ??= "needs-kiln-cover";
        }
        foreach (var (pos, _) in kilns.Where(k => k.State == ColonyManager.KilnState.Building))
        {
            if (m.NextKilnMaterial(colony, hut, citizen, pos) is not { } fuel) continue;
            int have = m.PackCount(citizen, s => s.Equals(entity.World, fuel, GlobalConstants.IgnoredStackAttributes));
            if (have >= fuel.StackSize) return Go(Work.Fuel, pos, "kiln-fueling");
            var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && s.Itemstack.Equals(entity.World, fuel, GlobalConstants.IgnoredStackAttributes)));
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("kiln-fueling");
                return FetchSupplies(chest.Pos, s => s.Equals(entity.World, fuel, GlobalConstants.IgnoredStackAttributes), fuel.StackSize - have);
            }
            // All the fuel for every kiln in one go (0.202.0), then this layer's own fuel if it is something else.
            m.EnsureKilnFuel(colony, hut, citizen, true);
            m.EnsurePotterRequest(colony, hut, fuel.Collectible.Code.ToString(), fuel.StackSize);
            status ??= "needs-kiln-fuel";
        }
        if (status == null && kilns.Any(k => k.State == ColonyManager.KilnState.Lit)) status = "kiln-firing";

        // Shape the next piece, or carry on with the one on the clay forming block.
        var forming = m.FormingRecipe(hut);
        if (forming != null) return Go(Work.Form, hut.FormPos!, "forming");

        // A couple of free slots stay for kiln fuel, so a pack full of pieces never stops the kiln.
        bool room = pack.Count < m.PackSlots(citizen) - 2;
        var clay = pack.Where(ColonyManager.IsClay).ToList();
        var next = room ? m.NextPottery(colony, hut, citizen, clay) : null;
        if (next == null && room)
        {
            // Not enough clay on hand: fetch it from the hut, or ask for some.
            var hutClay = storage.SelectMany(c => c.Inventory).Where(s => !s.Empty && ColonyManager.IsClay(s.Itemstack)).Select(s => s.Itemstack!).ToList();
            var fromHut = m.NextPottery(colony, hut, citizen, clay.Concat(hutClay).ToList());
            if (fromHut is { } wanted)
            {
                var chest = storage.First(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsClay(s.Itemstack) && wanted.Recipe.Ingredient!.SatisfiesAsIngredient(s.Itemstack, false)));
                SetStatus("fetching-clay");
                return FetchSupplies(chest.Pos, s => ColonyManager.IsClay(s) && wanted.Recipe.Ingredient!.SatisfiesAsIngredient(s, false),
                    System.Math.Max(ColonyManager.ClayCost(wanted.Recipe), m.SeedReserve(Job) - clay.Sum(s => s.StackSize)));
            }
            if (m.WantsPottery(colony, hut, citizen))
            {
                m.EnsurePotterRequest(colony, hut, "game:clay-*", m.Config.PotterClayRequest);
                status ??= "needs-clay";
            }
        }

        // Fire a full load, or whatever is made once nothing more can be, in an empty pit or a new one.
        var raw = pack.FirstOrDefault(m.IsRawPottery);
        var emptyPit = kilns.FirstOrDefault(k => k.State == ColonyManager.KilnState.Empty).Pos;
        bool canDig = kilns.Count < m.KilnsAllowed(hut);
        if ((emptyPit != null || canDig) && raw != null
            && (next == null || pack.Where(m.IsRawPottery).Sum(s => s.StackSize) >= ColonyManager.KilnLoad(raw)))
        {
            if (emptyPit != null)
            {
                // The kiln is made by adding its first fuel, so the potter brings that along.
                var first = m.FirstKilnMaterials(raw);
                bool Same(ItemStack s, ItemStack f) => s.Equals(entity.World, f, GlobalConstants.IgnoredStackAttributes);
                if (first.Count == 0 || first.Any(f => m.PackCount(citizen, s => Same(s, f)) >= f.StackSize)) return Go(Work.Load, emptyPit, "kiln-loading");
                foreach (var f in first)
                {
                    var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && Same(s.Itemstack, f)));
                    if (chest == null || m.PackFull(citizen)) continue;
                    SetStatus("kiln-fueling");
                    return FetchSupplies(chest.Pos, s => Same(s, f), f.StackSize - m.PackCount(citizen, s => Same(s, f)));
                }
                m.EnsureKilnFuel(colony, hut, citizen, true);
                m.EnsurePotterRequest(colony, hut, first[0].Collectible.Code.ToString(), first[0].StackSize);
                status ??= "needs-kiln-fuel";
            }
            else if (m.FindKilnSpot(colony, hut) is { } pit) return Go(Work.Dig, pit, "kiln-digging");
            else if (kilns.Count == 0) status = "no-kiln-spot";
        }

        if (next is { } piece)
        {
            hut.FormPos = m.FindFormSpot(colony, hut);
            if (hut.FormPos != null) return Go(Work.Form, hut.FormPos, "forming");
            status = "no-form-spot";
        }

        SetStatus(status ?? (raw != null ? "kiln-waiting" : "pottery-stocked"));
        return false;
    }

    bool Go(Work next, Vintagestory.API.MathTools.BlockPos pos, string status)
    {
        work = next;
        SetStatus(status);
        return GoWork(pos);
    }

    /// <summary>The recipe to start when the potter reaches an empty clay forming spot.</summary>
    ClayFormingRecipe? NextRecipe(Colony colony, CitizenRecord citizen, Building hut) =>
        manager!.NextPottery(colony, hut, citizen, manager.PackItems(citizen).Where(ColonyManager.IsClay).ToList())?.Recipe;

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (work)
        {
            case Work.Form:
                if (m.FormingRecipe(hut) != null) m.FormLayer(colony, hut, citizen);
                else if (NextRecipe(colony, citizen, hut) is { } recipe && m.StartForming(colony, hut, citizen, recipe, target)) m.FormLayer(colony, hut, citizen);
                break;
            case Work.Dig:
                m.DigKiln(colony, hut, citizen, target);
                break;
            case Work.Load:
                m.LoadKiln(colony, hut, citizen, target);
                break;
            case Work.Fuel:
                m.AddKilnMaterial(colony, hut, citizen, target);
                break;
            case Work.Light:
                m.LightKiln(colony, hut, citizen, target);
                break;
            case Work.Empty:
                m.EmptyKiln(colony, hut, citizen, target);
                break;
            case Work.Cover:
                m.CoverKiln(colony, hut, citizen, target);
                break;
        }
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-potter-" + work.ToString().ToLowerInvariant());
}
