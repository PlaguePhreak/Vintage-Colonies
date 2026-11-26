using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Keep spare fuel (0.202.0, Aaron): every hut that burns fuel keeps one more full firing in its storage, and asks the
/// couriers again as soon as it drops below that, so a firing never starts with a wait. A switch on the hut's Requests
/// tab, on by default. The Potter's Hut does its own counting (pit kilns take three fuels, ColonyManager.Pottery.cs);
/// this file covers the Cook's Hut, the Bakery, the Smelter and the Blacksmith.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Huts with the Keep spare fuel switch.</summary>
    public static bool BurnsFuel(string type) =>
        type is BuildingTypes.Potter or BuildingTypes.Cook or BuildingTypes.Bakery or BuildingTypes.Smelter or BuildingTypes.Blacksmith;

    readonly Dictionary<string, long> lastSpareFuel = new();

    /// <summary>Part of the colony tick, once a minute: tops up the spare fuel of every hut that has a worker.</summary>
    void TickSpareFuel(Colony colony)
    {
        long ms = sapi.World.ElapsedMilliseconds;
        if (lastSpareFuel.TryGetValue(colony.ColonyId, out long last) && ms - last < 60_000) return;
        lastSpareFuel[colony.ColonyId] = ms;
        foreach (var hut in colony.Buildings.Where(b => b.KeepSpareFuel && b.Level >= 1 && !b.Removing && !b.IsStation).ToList())
        {
            if (SpareFuelOf(colony, hut) is not { } spec) continue;
            var workers = WorkersAt(colony, hut);
            if (workers.Count == 0) continue;
            var storage = HutStorage(colony, hut).ToList();
            if (storage.Count == 0) continue;
            int have = CountItems(storage, spec.Fuel) + workers.Sum(w => PackCount(w, spec.Fuel));
            // One firing in use and one spare.
            if (have >= spec.PerFiring * 2) continue;
            string tag = RequestTag(hut);
            // Already asked for some fuel, by the worker or by an earlier top-up.
            bool asked = colony.Requests.Any(r => r.RequestedBy == tag && r.Remaining > 0 && ParseToolCode(r.ItemCode) == null && FitsRequest(r.ItemCode, spec.Fuel));
            if (asked) continue;
            AddRequest(colony, spec.Code, spec.PerFiring * 4 - have, storage[0].Pos, tag);
        }
    }

    /// <summary>True when a request's code names something that is this fuel.</summary>
    bool FitsRequest(string code, ItemMatch fuel)
    {
        foreach (var part in code.Split('{')[0].Split('|'))
        {
            if (part.Contains('*')) continue;
            var loc = new AssetLocation(part);
            var thing = (CollectibleObject?)sapi.World.GetItem(loc) ?? sapi.World.GetBlock(loc);
            if (thing is { Id: > 0 } && fuel(new ItemStack(thing))) return true;
        }
        return false;
    }

    /// <summary>What a hut burns, the code to ask for, and how much one firing takes. Null for huts this file does not cover.</summary>
    (ItemMatch Fuel, string Code, int PerFiring)? SpareFuelOf(Colony colony, Building hut)
    {
        switch (hut.Type)
        {
            case BuildingTypes.Cook:
            case BuildingTypes.Bakery:
                return (IsFirewood, "game:firewood", Math.Max(1, Config.CookFirewood));
            case BuildingTypes.Smelter:
            {
                ItemMatch hot = s => IsSmeltFuel(s, Config.SmelterKeepFuelTemperature);
                return (hot, CodeInWarehouse(colony, hot) ?? Config.SmelterFuelCode, Math.Max(1, Config.SmelterFuel));
            }
            case BuildingTypes.Blacksmith:
            {
                string code = BestStack(WarehouseContainers(colony), IsForgeFuel, ForgeFuelRank)?.Collectible.Code.ToString() ?? Config.SmelterFuelCode;
                return (IsForgeFuel, code, Math.Max(1, Config.BlacksmithFuel));
            }
        }
        return null;
    }
}
