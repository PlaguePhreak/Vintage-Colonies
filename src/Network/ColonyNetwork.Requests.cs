using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CreateAColony.Network;

/// <summary>The Requests tab of every hut and the Courier's Station's courier list (0.142.0).</summary>
public partial class ColonyNetwork
{
    void AddRequests(HutSnapshot snapshot, Colony colony, Building building, string viewerUid)
    {
        var viewer = sapi!.World.PlayerByUid(viewerUid);
        snapshot.HasRequestsTab = ColonyManager.HasOwnStorage(building.Type) || ColonyManager.JobOf(building.Type) != null;
        if (!snapshot.HasRequestsTab) return;
        string ownTag = ColonyManager.RequestTag(building);
        foreach (var row in Manager.RequestRows(colony, building))
        {
            var r = row.Request;
            string forText = "";
            // A builder's requests are tagged with the building it is working on.
            if (r.RequestedBy != ownTag && r.RequestedBy.StartsWith("building:") && int.TryParse(r.RequestedBy.Substring(9), out int id)
                && colony.Buildings.Find(b => b.BuildingId == id) is { } order)
            {
                forText = ColonyManager.BuildingName(order) + (order.TargetLevel > 0 ? " " + order.TargetLevel : "");
            }
            else if (!r.RequestedBy.StartsWith("building:") && !r.RequestedBy.StartsWith("minstock:")) forText = r.RequestedBy;
            string name = row.Sample == null ? r.ItemCode
                : ColonyManager.ParseToolCode(r.ItemCode) is { } tool ? Lang.Get("createacolony:items-tool", Lang.Get("createacolony:tool-" + tool.Type.ToString().ToLowerInvariant()))
                : row.Sample.GetName();
            snapshot.Requests.Add(new RequestView
            {
                Stack = row.Sample?.ToBytes() ?? System.Array.Empty<byte>(),
                Name = name,
                Quantity = r.Quantity,
                Delivered = r.Delivered,
                Extra = r.Extra,
                InColony = row.InColony,
                Status = row.Status,
                Color = row.Color,
                For = forText,
                RequestId = r.RequestId,
                // In creative mode the player can give everything a request needs (0.197.0).
                PlayerHas = viewer == null ? 0 : Creative(viewer) && row.Sample != null ? NeededFor(r) : CarriedCount(viewer, r.ItemCode),
                Needed = NeededFor(r)
            });
        }
    }

    static bool Creative(IPlayer player) => player.WorldData?.CurrentGameMode == EnumGameMode.Creative;

    /// <summary>How many a player gives for a request: what the hut still needs, without the spare.</summary>
    static int NeededFor(DeliveryRequest r) => Math.Max(1, r.Remaining - r.Extra);

    /// <summary>
    /// The Requests tab's give buttons (Aaron, 0.167.0, like MineColonies): Text holds request ids, comma separated.
    /// For each, moves what the hut still needs from the player's hotbar and backpack into the chest the request is
    /// for, and counts it as delivered, so couriers stop bringing it.
    /// </summary>
    string? FillRequests(IServerPlayer player, Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Member) return "createacolony:denied-member";
        var hut = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (hut == null) return "createacolony:bad-building";
        var open = Manager.RequestsOf(colony, hut);
        int given = 0, kinds = 0;
        bool full = false;
        foreach (string part in packet.Text.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(200))
        {
            if (!int.TryParse(part.Trim(), out int id) || open.Find(r => r.RequestId == id) is not { } request) continue;
            // The chest the request is for first, then the rest of that hut's storage: the hut block and every chest
            // in its building (0.200.0; before, a full hut block stopped every give).
            var dest = new List<BlockEntityContainer>();
            if (sapi!.World.BlockAccessor.GetBlockEntity(request.DestPos) is BlockEntityContainer chest) dest.Add(chest);
            dest.AddRange(Manager.HutStorage(colony, Manager.RequestHut(colony, request) ?? hut).Where(c => !dest.Any(d => d.Pos.Equals(c.Pos))));
            if (dest.Count == 0) continue;
            int want = Math.Min(NeededFor(request), 999);
            // Creative mode (0.197.0): the request is filled from nothing.
            bool free = Creative(player);
            int could = free ? want : Math.Min(want, CarriedCount(player, request.ItemCode));
            int moved = free ? Manager.CreativeFill(colony, request, dest, want) : Give(player, dest, request.ItemCode, want);
            if (moved < could) full = true;
            if (moved <= 0) continue;
            Manager.RecordDelivery(colony, request, moved);
            given += moved;
            kinds++;
        }
        if (given == 0) return full ? "createacolony:give-full" : "createacolony:give-none";
        player.SendMessage(0, Lang.GetL(player.LanguageCode, kinds > 1 ? "createacolony:requests-given-all" : "createacolony:requests-given", given, kinds), EnumChatType.Notification);
        if (full) player.SendMessage(0, Lang.GetL(player.LanguageCode, "createacolony:give-full-rest"), EnumChatType.Notification);
        return null;
    }

    void AddCouriers(HutSnapshot snapshot, Colony colony)
    {
        snapshot.IsCourierStation = true;
        var warehouse = Manager.WarehouseContainers(colony).ToList();
        string warehouseName = Lang.Get("createacolony:building-warehouse");
        foreach (var c in colony.Citizens.Where(c => c.Alive && !c.Visitor).OrderBy(c => c.JobCode != Jobs.Courier).ThenBy(c => c.Name))
        {
            var entity = sapi!.World.GetEntityById(c.EntityId) as EntityAgent;
            var task = entity?.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.ActiveTasksBySlot.OfType<Settlers.SettlerTaskBase>().FirstOrDefault();
            bool courier = c.JobCode == Jobs.Courier;
            // Settlers without a job show up while they are hauling.
            if (!courier && task is not Settlers.AiTaskSettlerCourier) continue;
            var view = new CourierView { Name = c.Name, Helping = !courier };
            if (courier) snapshot.CourierJobs++;
            if (c.IsDowned) view.Doing = Lang.Get("createacolony:activity-downed");
            else view.Doing = task?.Describe(colony, c) ?? Lang.Get("createacolony:activity-idle");
            if (task is Settlers.AiTaskSettlerCourier hauling)
            {
                var (from, to) = hauling.Route(colony);
                view.From = from == null ? "" : Manager.PlaceName(colony, from, warehouse);
                view.To = to == null ? warehouseName : Manager.PlaceName(colony, to, warehouse);
            }
            var load = Manager.PackItems(c).GroupBy(s => s.GetName()).Select(g => g.Sum(s => s.StackSize) + "x " + g.Key).ToList();
            view.Carrying = string.Join(", ", load.Take(3)) + (load.Count > 3 ? ", ..." : "");
            snapshot.Couriers.Add(view);
        }
        snapshot.Info = Lang.Get("createacolony:courierstation-summary", snapshot.CourierJobs, snapshot.Couriers.Count(v => v.Helping), colony.Requests.Count(r => r.Remaining > 0));
    }
}
