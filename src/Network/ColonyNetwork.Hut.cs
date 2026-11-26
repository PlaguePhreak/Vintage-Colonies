using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace CreateAColony.Network;

/// <summary>
/// The hut window's Main tab (name, workers, recall, couriers, storage list) for every hut, and the
/// Guard Tower's own tabs (Selection tools, Hostiles, Settings, Minimum stock, Statistics).
/// </summary>
public partial class ColonyNetwork
{
    string? HutAction(IServerPlayer player, Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null) return "createacolony:bad-building";
        var citizen = colony.Citizens.Find(c => c.CitizenId == packet.CitizenId);
        int.TryParse(packet.Text, out int number);

        switch (packet.Action)
        {
            case ColonyAction.RenameBuilding:
                return Manager.RenameBuilding(colony, building, packet.Text);
            case ColonyAction.AssignWorker:
                if (citizen == null) return "createacolony:bad-citizen";
                string? assignError = Manager.AssignWorker(colony, building, citizen);
                if (assignError == null) citizen.JobSetByPlayer = true;
                return assignError;
            case ColonyAction.UnassignWorker:
                return citizen == null ? "createacolony:bad-citizen" : Manager.UnassignWorker(colony, building, citizen);
            case ColonyAction.RecallWorkers:
                int came = Manager.RecallWorkers(colony, building);
                player.SendMessage(0, Lang.GetL(player.LanguageCode, "createacolony:hut-recalled", came), EnumChatType.Notification);
                return null;
            case ColonyAction.SetPickupPriority:
                Manager.SetPickupPriority(colony, building, number);
                return null;
            case ColonyAction.PickupNow:
                if (!ColonyManager.HasOwnStorage(building.Type)) return "createacolony:bad-building";
                Manager.RequestPickupNow(colony, building);
                return null;
            case ColonyAction.AddMinStock:
                if (player.InventoryManager.ActiveHotbarSlot?.Itemstack is not { } held) return "createacolony:minstock-hold";
                return Manager.AddMinStock(colony, building, held);
            case ColonyAction.SetCollect:
            {
                if (!ColonyManager.HasCollectTab(building.Type)) return "createacolony:bad-building";
                if (packet.Text is "*" or "-")
                {
                    Manager.SetCollect(colony, building, packet.Text, packet.Text == "*");
                    return null;
                }
                int bar = packet.Text.LastIndexOf('|');
                if (bar <= 0) return "createacolony:bad-building";
                Manager.SetCollect(colony, building, packet.Text[..bar], packet.Text[(bar + 1)..] == "1");
                return null;
            }
            case ColonyAction.SetMinStock:
            {
                int bar = packet.Text.LastIndexOf('|');
                if (bar <= 0 || !int.TryParse(packet.Text[(bar + 1)..], out int count)) return "createacolony:bad-building";
                Manager.SetMinStock(colony, building, packet.Text[..bar], count);
                return null;
            }
        }

        if (!ColonyManager.IsGuardPost(building.Type)) return "createacolony:bad-building";
        switch (packet.Action)
        {
            case ColonyAction.GetGuardScepter:
                return GiveScepter(player, colony, building);
            case ColonyAction.ClearGuardPoints:
                building.PatrolPoints.Clear();
                building.GuardSpot = null;
                return null;
            case ColonyAction.SetHostile:
                if (packet.Text == "*")
                {
                    building.HostilesOff.Clear();
                    return null;
                }
                int sep = packet.Text.LastIndexOf('|');
                if (sep <= 0) return "createacolony:bad-building";
                string code = packet.Text[..sep];
                building.HostilesOff.Remove(code);
                if (packet.Text[(sep + 1)..] == "0" && Manager.HostileTypes().Contains(code)) building.HostilesOff.Add(code);
                return null;
            case ColonyAction.SetGuardTask:
                if (number is < GuardTasks.Guard or > GuardTasks.PatrolMine) return "createacolony:bad-building";
                // Only Guard Towers patrol mines, as in MineColonies (Barracks do not).
                if (number == GuardTasks.PatrolMine && building.Type != BuildingTypes.GuardTower) return "createacolony:bad-building";
                building.GuardTask = number;
                if (number == GuardTasks.Follow) building.FollowPlayerUid = player.PlayerUID;
                return null;
            case ColonyAction.SetRetreat:
                building.Retreat = packet.Text == "1";
                return null;
            case ColonyAction.SetPatrolManual:
                building.PatrolManual = packet.Text == "1";
                return null;
            case ColonyAction.SetFollowTight:
                building.FollowTight = packet.Text == "1";
                return null;
        }
        return null;
    }

    /// <summary>
    /// Hands the player a Guard Scepter for this tower. On the Patrol task a new scepter starts the patrol
    /// route over, as in MineColonies; the points clicked with it make the new route.
    /// </summary>
    string? GiveScepter(IServerPlayer player, Colony colony, Building tower)
    {
        var item = sapi!.World.GetItem(new AssetLocation(CreateAColonyModSystem.ModId, "guardscepter"));
        if (item == null) return "createacolony:bad-building";
        var stack = new ItemStack(item);
        stack.Attributes.SetString(Items.ItemGuardScepter.ColonyAttr, colony.ColonyId);
        stack.Attributes.SetInt(Items.ItemGuardScepter.TowerAttr, tower.BuildingId);
        stack.Attributes.SetString("towerName", ColonyManager.BuildingName(tower));
        if (tower.GuardTask == GuardTasks.Patrol)
        {
            tower.PatrolPoints.Clear();
            tower.PatrolManual = true;
        }
        if (!player.InventoryManager.TryGiveItemstack(stack, true)) sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
        player.SendMessage(0, Lang.GetL(player.LanguageCode, tower.GuardTask == GuardTasks.Patrol ? "createacolony:scepter-given-patrol" : "createacolony:scepter-given-guard"),
            EnumChatType.Notification);
        return null;
    }

    /// <summary>Fills in the Main tab and, for a Guard Tower, its own tabs.</summary>
    void AddHutAdmin(HutSnapshot snapshot, Colony colony, Building building)
    {
        var config = Manager.Config;
        snapshot.DisplayName = ColonyManager.BuildingName(building);
        snapshot.HutPos = building.HutPos.Copy();
        snapshot.Job = ColonyManager.JobOf(building.Type) ?? "";
        snapshot.WorkerSlots = ColonyManager.WorkerSlots(building);
        snapshot.PickupPriority = building.PickupPriority;
        snapshot.PickupNow = building.PickupNow;
        if (snapshot.Job.Length > 0)
        {
            var workers = Manager.WorkersAt(colony, building);
            // The hut's job skill goes on every row, and the best candidates for it come first.
            var skill = Manager.Config.Skills ? Manager.SkillForJob(snapshot.Job) : null;
            CitizenView View(CitizenRecord c) => new()
            {
                CitizenId = c.CitizenId, Name = c.Name, Job = ColonyManager.JobLabel(c),
                Skill = skill?.Code ?? "", SkillLevel = skill == null ? 0 : Manager.SkillLevel(c, skill.Code),
                JobTrait = Manager.JobTraitFor(c, snapshot.Job).Length > 0,
                Passion = skill == null ? 0 : Manager.Passion(c, skill.Code),
                Incapable = Manager.IncapableOfJob(c, snapshot.Job) ?? "",
                WorkRole = c.WorkRole
            };
            snapshot.Workers = workers.Select(View).ToList();
            snapshot.Candidates = colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild && !workers.Contains(c))
                .OrderBy(c => Manager.IncapableOfJob(c, snapshot.Job) != null).ThenBy(c => c.JobCode != null).ThenByDescending(c => skill == null ? 0 : Manager.Passion(c, skill.Code)).ThenByDescending(c => Manager.JobTraitFor(c, snapshot.Job).Length > 0).ThenByDescending(c => skill == null ? 0 : Manager.SkillLevel(c, skill.Code)).ThenBy(c => c.Name)
                .Select(View).ToList();
        }
        if (snapshot.HasStorage)
        {
            snapshot.Items = Manager.HutItems(colony, building).Take(200)
                .Select(r => new HutItemView { Stack = r.Sample.ToBytes(), Count = r.Count, Where = r.Where }).ToList();
        }
        snapshot.MinStock = building.MinStock.ToList();
        snapshot.Stats = Manager.StatSummary(building).Select(r => new StatView { Key = r.Key, Counts = r.Counts }).ToList();
        if (ColonyManager.HasCollectTab(building.Type))
        {
            var kinds = Manager.CollectKinds(building.Type);
            snapshot.CollectKeys = kinds.Select(k => k.Key).ToList();
            snapshot.CollectStacks = kinds.Select(k => k.Sample.ToBytes()).ToList();
            snapshot.CollectOff = Manager.CollectOffKeys(building);
        }

        if (!ColonyManager.IsGuardPost(building.Type)) return;
        snapshot.IsGuardTower = true;
        snapshot.GuardTask = building.GuardTask;
        snapshot.GuardSpot = building.GuardSpot?.Copy();
        snapshot.PatrolManual = building.PatrolManual;
        snapshot.PatrolPoints = building.PatrolPoints.Select(p => p.Copy()).ToList();
        snapshot.FollowName = building.FollowPlayerUid.Length == 0 ? "" : sapi!.World.PlayerByUid(building.FollowPlayerUid)?.PlayerName ?? "";
        snapshot.FollowTight = building.FollowTight;
        snapshot.Retreat = building.Retreat;
        snapshot.Hostiles = Manager.HostileTypes();
        snapshot.HostilesOff = building.HostilesOff.ToList();
        snapshot.Info = Manager.WorkerHutInfo(colony, building);
    }
}
