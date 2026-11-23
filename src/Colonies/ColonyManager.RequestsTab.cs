using System.Collections.Generic;
using System.Linq;
using CreateAColony.Quests;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The Requests tab of every hut (Aaron, 0.142.0): what the hut and its worker asked the couriers for, whether the
/// colony has it, who is bringing it, and which other worker is getting it (the gatherer cutting grass for the
/// potter, the Sawmill sawing planks). Also the Courier's Station's list of couriers and where they move things.
/// </summary>
public partial class ColonyManager
{
    /// <summary>One row of the Requests tab. Color 0: on its way. 1: waiting. 2: nobody has it.</summary>
    public sealed record RequestRow(DeliveryRequest Request, ItemStack? Sample, int InColony, string Status, int Color);

    /// <summary>The open requests of a hut: its own, its Minimum stock, and anything asked to be brought to its storage.</summary>
    public List<DeliveryRequest> RequestsOf(Colony colony, Building hut)
    {
        var tags = new HashSet<string> { RequestTag(hut), MinStockTag(hut) };
        var storage = new HashSet<BlockPos>(HutStorage(colony, hut).Select(c => c.Pos));
        return colony.Requests.Where(r => r.Remaining > 0 && (tags.Contains(r.RequestedBy) || storage.Contains(r.DestPos)))
            .OrderBy(r => r.RequestId).ToList();
    }

    public List<RequestRow> RequestRows(Colony colony, Building hut)
    {
        var rows = new List<RequestRow>();
        var requests = RequestsOf(colony, hut);
        if (requests.Count == 0) return rows;
        var warehouse = WarehouseContainers(colony).ToList();
        bool haulers = colony.Citizens.Any(c => c.Alive && (c.JobCode == Jobs.Courier || Config.JoblessHelpHauling && IsJobless(c)));
        double now = sapi.World.Calendar.TotalHours;
        foreach (var r in requests)
        {
            var match = Matcher(r.ItemCode);
            int inColony = CountItems(warehouse, match);
            var sample = RequestSample(r.ItemCode, warehouse, match);
            string status;
            int color;
            var courier = r.LeasedByCitizenId != 0 && r.LeaseUntilTotalHours > now ? colony.Citizens.Find(c => c.CitizenId == r.LeasedByCitizenId) : null;
            if (courier != null)
            {
                status = Lang.Get(PackCount(courier, match) > 0 ? "createacolony:request-bringing" : "createacolony:request-fetching", courier.Name);
                color = 0;
            }
            else if (inColony > 0 || SpareForRequest(colony, r) != null)
            {
                status = Lang.Get(haulers ? "createacolony:request-waiting" : "createacolony:request-no-courier");
                color = 1;
            }
            else if (sample != null && sample.Collectible.Code.Equals(DryGrassCode) && GrassShort(colony) > 0
                && WorkingHuts(colony).FirstOrDefault(b => b.Type == BuildingTypes.Gatherer && MayCollect(b, "grass")) is { } gatherer)
            {
                status = Lang.Get("createacolony:request-cutting-grass", BuildingName(gatherer));
                color = 1;
            }
            else if (MakerName(colony, r.ItemCode, r.RequestedBy) is { } maker)
            {
                status = Lang.Get("createacolony:request-made-by", maker);
                color = 1;
            }
            else
            {
                status = Lang.Get("createacolony:request-nobody");
                color = 2;
            }
            rows.Add(new RequestRow(r, sample, inColony, status, color));
        }
        return rows;
    }

    /// <summary>One of the things a request asks for, for its icon: one from the warehouse when it has some.</summary>
    ItemStack? RequestSample(string code, List<BlockEntityContainer> warehouse, System.Func<ItemStack, bool> match)
    {
        if (ParseToolCode(code) is { } tool) return ToolSample(tool.Type);
        if (BestStack(warehouse, match, _ => 0) is { } found)
        {
            var one = found.Clone();
            one.StackSize = 1;
            return one;
        }
        return QuestItems.Sample(sapi.World, SplitTypes(code).Code);
    }

    /// <summary>A short name for where a container or spot is: the warehouse, a drop-off chest, or the building it is in.</summary>
    public string PlaceName(Colony colony, BlockPos? pos, List<BlockEntityContainer> warehouse)
    {
        if (pos == null) return "";
        if (warehouse.Any(c => c.Pos.Equals(pos))) return Lang.Get("createacolony:building-warehouse");
        if (DropOffContainers(colony).Any(c => c.Pos.Equals(pos))) return Lang.Get("createacolony:place-dropoff");
        var stored = colony.Buildings.FirstOrDefault(b => !b.IsStation && (b.HutPos.Equals(pos) || HasOwnStorage(b.Type) && HutStorage(colony, b).Any(c => c.Pos.Equals(pos))));
        if (stored != null) return BuildingName(stored);
        var inside = colony.Buildings.Where(b => !b.IsStation && b.Level > 0)
            .Select(b => (B: b, Box: BuildingBox(b))).FirstOrDefault(z => InBox(pos, z.Box.Min, z.Box.Max)).B;
        return inside != null ? BuildingName(inside) : Lang.Get("createacolony:place-somewhere");
    }

    /// <summary>
    /// Creative mode (0.197.0): puts up to want of what the request asks for into the chests from nothing, as the
    /// Requests tab's give buttons do with carried items in survival. Returns how many went in.
    /// </summary>
    public int CreativeFill(Colony colony, DeliveryRequest request, List<BlockEntityContainer> dest, int want)
    {
        var match = Matcher(request.ItemCode);
        var sample = RequestSample(request.ItemCode, WarehouseContainers(colony).ToList(), match);
        if (sample == null || !match(sample)) return 0;
        int given = 0;
        int max = System.Math.Max(1, sample.Collectible.MaxStackSize);
        while (given < want)
        {
            var part = sample.Clone();
            part.StackSize = System.Math.Min(max, want - given);
            int before = part.StackSize;
            foreach (var chest in dest)
            {
                if (part.StackSize <= 0) break;
                PutItems(chest, part);
            }
            given += before - part.StackSize;
            if (part.StackSize > 0) break; // the chests are full
        }
        return given;
    }
}
