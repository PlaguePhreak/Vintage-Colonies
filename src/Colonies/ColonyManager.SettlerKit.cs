using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// What every grown settler keeps on it (Aaron, 0.202.0): up to four reed baskets for more carrying room, and a spare
/// weapon to fight back with. Both come from the Warehouse or the Town Hall's storage and are handed over there,
/// once a minute; missing baskets are asked for on the Requests tab, a few at a time, so a hut that can make them does.
/// Also short notes a settler shows after what it is doing ("needs a knife to skin a dead animal").
/// </summary>
public partial class ColonyManager
{
    /// <summary>Requests the settlers make for themselves: baskets, and a knife to skin with.</summary>
    public const string SettlersTag = "settlers";
    /// <summary>Hand baskets only: "game:basket*" would take basket traps too.</summary>
    public const string BasketCode = "game:basket|game:basket-*";
    /// <summary>The most baskets asked for at once, so builds waiting for reed chests are not starved of reeds.</summary>
    const int BasketsAskedAtOnce = 4;
    public const string SkinningKnifeCode = "tool:knife:0";

    readonly Dictionary<string, long> lastSettlerKit = new();
    readonly Dictionary<int, (string Text, long UntilMs)> settlerNotes = new();

    // ---- Notes ----------------------------------------------------------------------------

    /// <summary>A short note shown after what the settler is doing, for a while: why it waits, what it lacks.</summary>
    public void SetNote(CitizenRecord citizen, string text, int seconds = 120) =>
        settlerNotes[citizen.CitizenId] = (text, sapi.World.ElapsedMilliseconds + seconds * 1000L);

    public void ClearNote(CitizenRecord citizen) => settlerNotes.Remove(citizen.CitizenId);

    public string? NoteOf(CitizenRecord citizen)
    {
        if (!settlerNotes.TryGetValue(citizen.CitizenId, out var note)) return null;
        if (note.UntilMs >= sapi.World.ElapsedMilliseconds) return note.Text;
        settlerNotes.Remove(citizen.CitizenId);
        return null;
    }

    /// <summary>What the settler is doing, with its note after it when it has one.</summary>
    public string WithNote(CitizenRecord citizen, string activity) =>
        NoteOf(citizen) is { } note ? activity + " (" + note + ")" : activity;

    // ---- Baskets and spare weapons ----------------------------------------------------------

    /// <summary>The Warehouse and the Town Hall's storage: where settlers get things for themselves.</summary>
    List<BlockEntityContainer> CommonStores(Colony colony)
    {
        var townHall = GetBuilding(colony, colony.TownHallPos);
        return WarehouseContainers(colony).Cast<BlockEntityContainer>()
            .Concat(townHall == null ? Enumerable.Empty<BlockEntityContainer>() : HutStorage(colony, townHall))
            .Distinct().ToList();
    }

    static bool KeepsKit(CitizenRecord c) => c.Alive && !c.IsChild && !c.Visitor && !c.WaitingForRoom && !c.IsDowned;

    /// <summary>Part of the colony tick, once a minute.</summary>
    void TickSettlerKit(Colony colony)
    {
        long ms = sapi.World.ElapsedMilliseconds;
        if (lastSettlerKit.TryGetValue(colony.ColonyId, out long last) && ms - last < 60_000) return;
        lastSettlerKit[colony.ColonyId] = ms;
        var stores = CommonStores(colony);
        GiveBaskets(colony, stores);
        GiveSpareWeapons(colony, stores);
        // A knife asked for to skin with has come, or was made: the request is done.
        if (CountItems(stores, s => IsKnife(s)) > 0)
        {
            foreach (var r in colony.Requests.Where(r => r.RequestedBy == SettlersTag && r.ItemCode == SkinningKnifeCode && r.LeasedByCitizenId == 0).ToList())
                CancelRequest(colony, r, "a knife is in the stores");
        }
    }

    /// <summary>Hands baskets from the stores to the settlers with the fewest, and asks for the ones still missing.</summary>
    void GiveBaskets(Colony colony, List<BlockEntityContainer> stores)
    {
        int keep = Math.Min(Config.SettlerBaskets, MaxBags);
        var open = colony.Requests.Where(r => r.RequestedBy == SettlersTag && r.ItemCode == BasketCode).ToList();
        if (keep <= 0)
        {
            foreach (var r in open.Where(r => r.LeasedByCitizenId == 0)) CancelRequest(colony, r, "settlers keep no baskets");
            return;
        }
        // Couriers and builders first: they carry the most.
        static int Rank(CitizenRecord c) => c.JobCode is Jobs.Courier ? 0 : c.JobCode is Jobs.Builder ? 1 : 2;
        int missing = 0;
        foreach (var citizen in colony.Citizens.Where(KeepsKit).OrderBy(c => c.Bags.Count).ThenBy(Rank).ThenBy(c => c.CitizenId).ToList())
        {
            while (citizen.Bags.Count < keep)
            {
                var basket = stores.Count == 0 ? null : TakeItems(stores, IsBasket, 1);
                if (basket == null) break;
                if (AddBag(colony, citizen, basket)) continue;
                // Not a bag after all: back it goes.
                PutBack(stores, basket);
                break;
            }
            missing += Math.Max(0, keep - citizen.Bags.Count);
        }

        int ask = Math.Min(missing, BasketsAskedAtOnce);
        if (open.Count == 0)
        {
            if (ask <= 0 || stores.Count == 0) return;
            var townHall = GetBuilding(colony, colony.TownHallPos);
            var dest = (townHall == null ? null : HutStorage(colony, townHall).FirstOrDefault()) ?? WarehouseContainers(colony).FirstOrDefault();
            if (dest != null) AddRequest(colony, BasketCode, ask, dest.Pos, SettlersTag);
            return;
        }
        foreach (var r in open)
        {
            if (r.LeasedByCitizenId != 0) continue;
            if (ask <= 0) CancelRequest(colony, r, "every settler has its baskets");
            else if (r.Remaining <= 0)
            {
                // Delivered and handed out: ask for the next few.
                r.Quantity = r.Delivered + ask;
                MarkDirty(colony);
            }
        }
    }

    void PutBack(List<BlockEntityContainer> stores, ItemStack stack)
    {
        foreach (var store in stores)
        {
            if (stack.StackSize <= 0) return;
            PutItems(store, stack);
        }
    }

    /// <summary>
    /// A grown settler with no weapon is handed a spare one from the stores: a spear first, never a knife (those are
    /// tools), never one a hut has asked for.
    /// </summary>
    void GiveSpareWeapons(Colony colony, List<BlockEntityContainer> stores)
    {
        if (!Config.SettlersTakeSpareWeapons || !Config.FightBackWhenAttacked || stores.Count == 0) return;
        var asked = colony.Requests.Where(r => r.Remaining > 0).Select(r => Matcher(r.ItemCode)).ToList();
        bool Spare(ItemStack s) => IsWeapon(s) && s.Collectible.Tool != EnumTool.Knife && !asked.Any(fits => fits(s));
        if (CountItems(stores, Spare) == 0) return;
        foreach (var citizen in colony.Citizens.Where(c => KeepsKit(c) && c.JobCode != Jobs.Guard).ToList())
        {
            // Only settlers that are loaded: what one holds in its hand is not in its inventory.
            if (sapi.World.GetEntityById(citizen.EntityId) is not EntityAgent { Alive: true } agent) continue;
            if (agent.RightHandItemSlot?.Itemstack is { } held && IsWeapon(held) && held.Collectible.Tool != EnumTool.Knife) continue;
            if (PackCount(citizen, s => IsWeapon(s) && s.Collectible.Tool != EnumTool.Knife) > 0) continue;
            var weapon = TakeItems(stores, s => Spare(s) && s.Collectible.Tool == EnumTool.Spear, 1) ?? TakeItems(stores, Spare, 1);
            if (weapon == null) return;
            string name = weapon.GetName();
            if (AddToPack(colony, citizen, weapon) > 0) LogEvent(colony, "event-spare-weapon", citizen.Name, name);
            if (weapon.StackSize > 0) PutBack(stores, weapon);
        }
    }

    // ---- A knife to skin with ---------------------------------------------------------------

    /// <summary>Asks, once, for a knife so a dead animal can be skinned: to the Town Hall's storage or the Warehouse.</summary>
    public void EnsureSkinningKnifeRequest(Colony colony)
    {
        if (colony.Requests.Any(r => r.RequestedBy == SettlersTag && r.ItemCode == SkinningKnifeCode)) return;
        var townHall = GetBuilding(colony, colony.TownHallPos);
        var dest = (townHall == null ? null : HutStorage(colony, townHall).FirstOrDefault()) ?? WarehouseContainers(colony).FirstOrDefault();
        if (dest != null) AddRequest(colony, SkinningKnifeCode, 1, dest.Pos, SettlersTag);
    }
}
