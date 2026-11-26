using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace CreateAColony.Network;

/// <summary>
/// The Miner's Hut's Levels, Settings, Guards and Required resources tabs, and the give button on every
/// Required resources tab (the player's items go straight into the hut's storage).
/// </summary>
public partial class ColonyNetwork
{
    string? MineTabAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var mine = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (mine == null || mine.Type is not (BuildingTypes.Miner or BuildingTypes.MineshaftEntrance)) return "createacolony:bad-building";
        if (mine.Type != BuildingTypes.Miner && packet.Action is ColonyAction.AssignMineGuard or ColonyAction.UnassignMineGuard) return "createacolony:bad-building";
        int.TryParse(packet.Text, out int number);
        switch (packet.Action)
        {
            case ColonyAction.SetMineLevel: Manager.SetMineLevel(colony, mine, number); return null;
            case ColonyAction.RepairMineLevel: Manager.RepairMineLevel(colony, mine, number); return null;
            case ColonyAction.SetMaxMineLevels: Manager.SetMaxMineLevels(colony, mine, number); return null;
            case ColonyAction.SetFillBlock: return Manager.SetFillBlock(colony, mine, packet.Text);
            case ColonyAction.AssignMineGuard:
                var guard = colony.Citizens.Find(c => c.CitizenId == packet.CitizenId);
                return guard == null ? "createacolony:bad-citizen" : Manager.AssignMineGuard(colony, mine, guard);
            case ColonyAction.UnassignMineGuard: Manager.UnassignMineGuard(colony, mine, packet.CitizenId); return null;
        }
        return null;
    }

    /// <summary>Remove hut. When nothing was built yet the hut block comes straight back.</summary>
    string? RemoveHut(IServerPlayer player, Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null) return "createacolony:bad-building";
        string name = ColonyManager.BuildingName(building);
        bool mine = building.Type == BuildingTypes.Miner && building.Mine.HasMine || building.Type == BuildingTypes.MineshaftEntrance && building.HasMine;
        string? result = Manager.StartRemoval(colony, building, player);
        if (result == "createacolony:remove-done")
        {
            player.SendMessage(0, Lang.GetL(player.LanguageCode, result, name), EnumChatType.Notification);
            return null;
        }
        if (result != null) return result;
        player.SendMessage(0, Lang.GetL(player.LanguageCode, mine ? "createacolony:remove-started-mine" : "createacolony:remove-started", name), EnumChatType.Notification);
        return null;
    }

    /// <summary>Link and Unlink on a Mineshaft Entrance's Main tab; Text is the Miner's Hut's BuildingId.</summary>
    string? LinkMineAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var entrance = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId && b.Type == BuildingTypes.MineshaftEntrance);
        int.TryParse(packet.Text, out int hutId);
        var hut = colony.Buildings.Find(b => b.BuildingId == hutId && b.Type == BuildingTypes.Miner);
        if (entrance == null || hut == null) return "createacolony:bad-building";
        return packet.Action == ColonyAction.LinkMineHut ? Manager.LinkHut(colony, entrance, hut) : Manager.UnlinkHut(colony, entrance, hut);
    }

    /// <summary>The viewer's hotbar and backpack slots, where the give button takes items from.</summary>
    static IEnumerable<ItemSlot> CarriedSlots(IPlayer player) =>
        new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName }
            .Select(name => player.InventoryManager.GetOwnInventory(name))
            .Where(inv => inv != null)
            .SelectMany(inv => inv);

    static int CarriedCount(IPlayer player, string code)
    {
        var match = ColonyManager.Matcher(code);
        return CarriedSlots(player).Where(s => !s.Empty && match(s.Itemstack)).Sum(s => s.StackSize);
    }

    /// <summary>
    /// Text "code|count" (GiveResource), or one such line per item (GiveAllResources, Fulfil all): moves up
    /// to count of each item from the player's hotbar and backpack into the hut's storage.
    /// </summary>
    string? GiveResource(IServerPlayer player, Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Member) return "createacolony:denied-member";
        var hut = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (hut == null || !ColonyManager.HasOwnStorage(hut.Type)) return "createacolony:bad-building";
        var storage = Manager.HutStorage(colony, hut).Cast<Vintagestory.GameContent.BlockEntityContainer>().ToList();
        if (storage.Count == 0) return "createacolony:give-no-storage";
        int given = 0, kinds = 0;
        bool full = false;
        // The same rule as the Requests tab's give buttons (0.200.0): free in creative, all of the hut's storage,
        // and what goes in counts toward the hut's open requests.
        bool free = Creative(player);
        foreach (string line in packet.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(200))
        {
            int bar = line.LastIndexOf('|');
            if (bar <= 0 || !int.TryParse(line[(bar + 1)..], out int want) || want <= 0) continue;
            string code = line[..bar];
            want = Math.Min(want, 999);
            int could = free ? want : Math.Min(want, CarriedCount(player, code));
            int moved = free ? Manager.CreativeFill(colony, code, storage, want) : Give(player, storage, code, want);
            if (moved < could) full = true;
            if (moved <= 0) continue;
            Manager.CountTowardRequests(colony, hut, code, moved);
            given += moved;
            kinds++;
        }
        if (given == 0) return full ? "createacolony:give-full" : "createacolony:give-none";
        player.SendMessage(0, Lang.GetL(player.LanguageCode, kinds > 1 ? "createacolony:give-done-all" : "createacolony:give-done", given, kinds), EnumChatType.Notification);
        if (full) player.SendMessage(0, Lang.GetL(player.LanguageCode, "createacolony:give-full-rest"), EnumChatType.Notification);
        return null;
    }

    /// <summary>Moves up to want of the item from the player into the storage. Returns how many moved.</summary>
    int Give(IServerPlayer player, List<Vintagestory.GameContent.BlockEntityContainer> storage, string code, int want)
    {
        var match = ColonyManager.Matcher(code);
        int given = 0;
        foreach (var slot in CarriedSlots(player))
        {
            if (given >= want) break;
            if (slot.Empty || !match(slot.Itemstack)) continue;
            var part = slot.TakeOut(Math.Min(slot.StackSize, want - given));
            int before = part.StackSize;
            foreach (var chest in storage)
            {
                if (part.StackSize <= 0) break;
                Manager.PutItems(chest, part);
            }
            given += before - part.StackSize;
            // What did not fit goes back where it came from.
            if (part.StackSize > 0)
            {
                if (slot.Empty) slot.Itemstack = part;
                else slot.Itemstack.StackSize += part.StackSize;
            }
            slot.MarkDirty();
            // The hut's storage is full.
            if (part.StackSize > 0) break;
        }
        return given;
    }

    /// <summary>"wood" or "rock" when any wood or any stone will do for the item code, as the Builder's list says it; else empty.</summary>
    internal static string AnyOfFor(string code, ItemStack sample) =>
        code == "game:soil-*" ? "soil"
        : code.Contains('|') ? "binding"
        : ColonyManager.AnyType(code) ? "wood"
        : code.StartsWith("heraldrybanners:banner") && code.Contains('{') ? "design"
        : !code.Contains('*') ? ""
        : sample.Collectible.Variant?.ContainsKey("wood") == true ? "wood"
        : sample.Collectible.Variant?.ContainsKey("rock") == true ? "rock" : "";

    /// <summary>
    /// Fills in a Miner's Hut's Levels, Settings, Guards and Required resources tabs. A Mineshaft Entrance gets
    /// the Levels and Settings tabs and its list of Miner's Huts. Levels and Settings are the shared mine's.
    /// </summary>
    void AddMineTabs(HutSnapshot snapshot, Colony colony, Building mine, string viewerUid)
    {
        var shared = mine.Mine;
        snapshot.IsMiner = true;
        snapshot.MineLevel = shared.MineLevel;
        snapshot.RepairLevel = shared.RepairLevel;
        snapshot.MaxMineLevels = shared.MaxMineLevels;
        snapshot.MineLevelsAllowed = Manager.MineshaftLevelsAllowed(mine);
        snapshot.FillBlock = shared.FillBlock;
        snapshot.MineEntranceName = mine.Entrance != null ? ColonyManager.BuildingName(mine.Entrance) : "";
        // A Mineshaft Entrance shows its mine's levels only once it has a mine; without one it would start one beside itself.
        bool showLevels = mine.Type == BuildingTypes.Miner || mine.HasMine || mine.LinkedHuts.Count > 0;
        if (showLevels && Manager.MineLevels(colony, mine) is { } levels)
        {
            snapshot.MineLevels = levels.Select(l => new MineLevelView { Level = l.Level, Y = l.Y, Sections = l.Sections, Left = l.Left, Working = l.Working }).ToList();
        }

        if (mine.Type == BuildingTypes.MineshaftEntrance)
        {
            snapshot.IsMineEntrance = true;
            snapshot.MineInfo = !mine.HasMine ? Lang.Get("createacolony:entrance-no-mine")
                : (Manager.MineshaftInfo(colony, mine) ?? "") + (mine.LinkedHuts.Count == 0 ? "\n" + Lang.Get("createacolony:entrance-derelict") : "");
            foreach (var hut in colony.Buildings.Where(b => b.Type == BuildingTypes.Miner && !b.Removing)
                .OrderByDescending(b => b.Entrance == mine).ThenBy(b => b.BuildingId))
            {
                snapshot.MineHuts.Add(new MineHutView
                {
                    BuildingId = hut.BuildingId, Name = ColonyManager.BuildingName(hut), Level = hut.Level,
                    Worker = Manager.WorkerAt(colony, hut, Jobs.Miner)?.Name ?? "", Linked = hut.Entrance == mine,
                    Elsewhere = hut.Entrance != null && hut.Entrance != mine ? ColonyManager.BuildingName(hut.Entrance) : ""
                });
            }
            return;
        }

        Manager.CleanMineGuards(colony, mine);
        snapshot.MineGuardSlots = ColonyManager.MineGuardSlots(mine);
        foreach (var guard in Manager.MinePatrolGuards(colony).OrderBy(c => c.Name))
        {
            var with = Manager.MineGuardedBy(colony, guard);
            var view = new CitizenView { CitizenId = guard.CitizenId, Name = guard.Name, Job = with == null || with == mine ? "" : ColonyManager.BuildingName(with) };
            if (with == mine) snapshot.MineGuards.Add(view);
            else snapshot.MineGuardChoices.Add(view);
        }

        // Required resources: the mineshaft piece the miner works on now.
        if (Manager.MineshaftResources(colony, mine) is not { } work) return;
        snapshot.ResourcesFor = work.Project;
        snapshot.UsedPercent = work.Total == 0 ? 100 : work.Done * 100 / work.Total;
        snapshot.ResourcesStep = Lang.Get("createacolony:resources-step-mine", work.Done, work.Total);
        var miner = Manager.WorkerAt(colony, mine, Jobs.Miner);
        snapshot.BuilderName = miner?.Name ?? "";
        var storage = Manager.HutStorage(colony, mine).Cast<Vintagestory.GameContent.BlockEntityContainer>().ToList();
        foreach (var need in work.Needs)
        {
            var match = ColonyManager.Matcher(need.Code);
            int have = Manager.CountItems(storage, match) + (miner == null ? 0 : Manager.PackCount(miner, match));
            snapshot.Resources.Add(new ResourceView { Stack = need.Sample.ToBytes(), Needed = need.Count, Have = have, Code = need.Code, AnyOf = AnyOfFor(need.Code, need.Sample) });
        }
        AddMakers(snapshot, colony, ColonyManager.RequestTag(mine));
        AddPlayerHas(snapshot, viewerUid);
    }

    /// <summary>For each thing the hut is short of: how many the warehouse has, and which other hut makes it.</summary>
    void AddMakers(HutSnapshot snapshot, Colony colony, string tag)
    {
        var warehouse = Manager.WarehouseContainers(colony).ToList();
        foreach (var row in snapshot.Resources.Where(r => !r.IsTool && r.Code.Length > 0 && r.Have < r.Needed))
        {
            row.InColony = Manager.CountItems(warehouse, ColonyManager.Matcher(row.Code));
            row.MadeBy = Manager.MakerName(colony, row.Code, tag) ?? "";
        }
    }

    /// <summary>How many of each required resource the viewer carries, for the green rows and the give button.</summary>
    void AddPlayerHas(HutSnapshot snapshot, string viewerUid)
    {
        if (sapi!.World.PlayerByUid(viewerUid) is not { } viewer) return;
        // In creative the player can give everything a row still needs, as on the Requests tab.
        bool free = Creative(viewer);
        foreach (var row in snapshot.Resources.Where(r => !r.IsTool && r.Code.Length > 0))
        {
            row.PlayerHas = free ? Math.Max(0, row.Needed - row.Have) : CarriedCount(viewer, row.Code);
        }
    }
}
