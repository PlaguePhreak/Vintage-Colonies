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
/// A blacksmith works from a Blacksmith's Hut with the game's forge and anvil: it heats the ingots for a
/// piece in the forge with charcoal, carries them hot to the anvil and hammers them into the piece, then
/// puts the piece on a stick when the piece is a tool head. When the ingots cool before the piece is done
/// they go back in the forge. Ingots, sticks and charcoal come from the hut storage, which couriers fill
/// from the warehouse; what it makes goes to the hut storage and on to the warehouse.
/// </summary>
public class AiTaskSettlerBlacksmith : AiTaskSettlerWorker
{
    enum Work { Heat, Fuel, TakeHot, Smith }

    Work work;
    ColonyManager.SmithKind? kind;
    int strikes;

    public AiTaskSettlerBlacksmith(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Blacksmith;
    protected override string HutType => BuildingTypes.Blacksmith;
    protected override EnumTool Tool => EnumTool.Hammer;
    protected override bool WorkNeedsTool => work == Work.Smith;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => work == Work.Smith ? manager!.Config.BlacksmithSecondsPerStrike : manager!.Config.BlacksmithSecondsPerStep;

    /// <summary>It carries charcoal, and the ingots and handle for the piece it is making.</summary>
    protected override bool AlsoKeeps(ItemStack stack) =>
        ColonyManager.IsForgeFuel(stack) || kind?.IsIngot(stack) == true || kind?.IsHandle(stack) == true;

    protected override string WorkAnimation(ItemSlot? hand) => work == Work.Smith && HoldsTool ? base.WorkAnimation(hand) : "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var forge = m.SmithForge(hut);
        if (forge == null)
        {
            SetStatus("smith-no-forge");
            return false;
        }
        var anvil = m.SmithAnvil(hut);
        if (anvil == null)
        {
            SetStatus("smith-no-anvil");
            return false;
        }

        // Ingots in the forge: take them out once hot, else keep the fire going.
        if (forge.WorkItemStack is { } heating)
        {
            if (!ColonyManager.IsIngot(heating) && !ColonyManager.IsBloom(heating))
            {
                SetStatus("smith-forge-busy");
                return false;
            }
            if (ColonyManager.IsWorkable(heating) && !m.PackFull(citizen)) return Go(Work.TakeHot, forge.Pos, "smith-taking-hot");
            if (forge.IsBurning)
            {
                SetStatus("smith-heating");
                return false;
            }
            return PlanFuel(colony, citizen, hut, storage, forge);
        }

        // The piece being made, if the ingots for it are on hand.
        if (kind != null && m.PackCount(citizen, kind.IsIngot) >= kind.Ingots && kind.Tier <= anvil.OwnMetalTier
            && m.CraftWant(colony, hut, kind.Key) > m.SmithStock(colony, hut, kind, citizen))
        {
            var hot = m.PackItems(citizen).FirstOrDefault(kind.IsIngot);
            if (hot != null && ColonyManager.IsWorkable(hot)) return Go(Work.Smith, anvil.Pos, "smith-hammering");
            if (forge.FuelLevel > 0 || m.PackCount(citizen, ColonyManager.IsForgeFuel) > 0) return Go(Work.Heat, forge.Pos, "smith-heating");
            return FetchFuel(colony, hut, storage);
        }
        kind = null;
        strikes = 0;

        var wanted = m.WantedSmithing(colony, hut, citizen, anvil.OwnMetalTier);
        if (wanted.Count == 0)
        {
            SetStatus("smith-stocked");
            return false;
        }

        foreach (var want in wanted)
        {
            // The handle first, so a tool head is not made without the stick to finish it.
            if (want.Handle != null && m.PackCount(citizen, want.IsHandle) == 0)
            {
                var handleChest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && want.IsHandle(s.Itemstack)));
                if (handleChest != null && !m.PackFull(citizen))
                {
                    SetStatus("smith-fetching-handle");
                    return FetchSupplies(handleChest.Pos, want.IsHandle, 4);
                }
                string? handleCode = m.CodeInWarehouse(colony, want.IsHandle)
                    ?? (want.Handle.Code is { } c && !c.Path.Contains('*') ? c.ToString() : null);
                if (handleCode != null) m.EnsureCraftRequest(colony, hut, handleCode, 4);
            }

            int have = m.PackCount(citizen, want.IsIngot);
            if (have >= want.Ingots)
            {
                kind = want;
                strikes = 0;
                if (forge.FuelLevel > 0 || m.PackCount(citizen, ColonyManager.IsForgeFuel) > 0) return Go(Work.Heat, forge.Pos, "smith-heating");
                return FetchFuel(colony, hut, storage);
            }
            var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && want.IsIngot(s.Itemstack)));
            if (chest != null && !m.PackFull(citizen))
            {
                SetStatus("smith-fetching-ingots");
                return FetchSupplies(chest.Pos, want.IsIngot, want.Ingots - have);
            }
            // Blooms are asked for even when the warehouse has none: the smelter makes them in the bloomery.
            if (want.FromBloom || m.CodeInWarehouse(colony, want.IsIngot) != null)
            {
                m.EnsureCraftRequest(colony, hut, want.Ingot.ToString(), want.Ingots - have);
                SetStatus("smith-waiting-ingots");
                return false;
            }
        }
        SetStatus("smith-needs-ingots");
        return false;
    }

    bool PlanFuel(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, BlockEntityForge forge)
    {
        if (forge.FuelLevel > 0 || manager!.PackCount(citizen, ColonyManager.IsForgeFuel) > 0) return Go(Work.Fuel, forge.Pos, "smith-lighting");
        return FetchFuel(colony, hut, storage);
    }

    bool FetchFuel(Colony colony, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        // Charcoal first, then whatever other forge fuel there is (ColonyManager.ForgeFuelRank).
        if (m.BestStack(storage, ColonyManager.IsForgeFuel, ColonyManager.ForgeFuelRank) is { } best)
        {
            var code0 = best.Collectible.Code;
            System.Func<ItemStack, bool> same = s => s.Collectible.Code.Equals(code0);
            var chest = storage.First(c => c.Inventory.Any(s => !s.Empty && same(s.Itemstack)));
            SetStatus("smith-fetching-fuel");
            return FetchSupplies(chest.Pos, same, m.Config.BlacksmithFuel * 2);
        }
        string code = m.BestStack(m.WarehouseContainers(colony), ColonyManager.IsForgeFuel, ColonyManager.ForgeFuelRank)?.Collectible.Code.ToString() ?? m.Config.SmelterFuelCode;
        m.EnsureCraftRequest(colony, hut, code, m.Config.BlacksmithFuel * 4);
        SetStatus("smith-needs-fuel");
        return false;
    }

    bool Go(Work next, BlockPos pos, string status)
    {
        work = next;
        SetStatus(status);
        return GoWork(pos);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        var ba = entity.World.BlockAccessor;
        switch (work)
        {
            case Work.Heat:
                if (ba.GetBlockEntity(target) is BlockEntityForge heatForge && kind != null) m.HeatIngots(colony, citizen, heatForge, kind);
                break;
            case Work.Fuel:
                if (ba.GetBlockEntity(target) is BlockEntityForge fuelForge) m.FuelForge(colony, citizen, fuelForge);
                break;
            case Work.TakeHot:
                if (ba.GetBlockEntity(target) is BlockEntityForge hotForge) m.TakeFromForge(colony, citizen, hotForge);
                break;
            case Work.Smith:
                if (kind == null) break;
                if (Hand is { } hand && HoldsTool) m.WearTool(entity, hand);
                entity.World.PlaySoundAt(new AssetLocation("sounds/effect/anvilhit"), target.X + 0.5, target.Y + 0.5, target.Z + 0.5, null, true, 16);
                if (++strikes < kind.Strikes) break;
                if (m.FinishPiece(colony, hut, citizen, kind)) strikes = 0;
                break;
        }
    }

    protected override string DescribeWork() => work == Work.Smith && kind != null
        ? Lang.Get("createacolony:activity-blacksmith-smith", kind.Sample.GetName(), strikes, kind.Strikes)
        : Lang.Get("createacolony:activity-blacksmith-" + work.ToString().ToLowerInvariant());
}
