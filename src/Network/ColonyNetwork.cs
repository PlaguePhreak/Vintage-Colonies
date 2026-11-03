using System.Collections.Generic;
using System.Linq;
using CreateAColony.Blocks;
using CreateAColony.Colonies;
using CreateAColony.Gui;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.API.Server;

namespace CreateAColony.Network;

/// <summary>
/// The one network channel between the Town Hall window and the server. The client only ever asks;
/// the server checks the player's colony role, applies the change, and sends back a fresh snapshot.
/// </summary>
public partial class ColonyNetwork : ModSystem
{
    const string ChannelName = "createacolony";
    const int MaxNameLength = 40;
    /// <summary>How many kinds of food the Kitchen tab lists as on hand.</summary>
    const int KitchenOnHandShown = 24;

    ICoreServerAPI? sapi;
    IServerNetworkChannel? serverChannel;
    IClientNetworkChannel? clientChannel;
    GuiDialogTownHall? dialog;

    // Who has which colony's window open, so changes can refresh everyone watching.
    readonly Dictionary<string, HashSet<string>> viewersByColony = new();
    // Who has which hut window open: player uid -> (colony, hut position).
    readonly Dictionary<string, (string ColonyId, BlockPos HutPos)> hutViewers = new();
    GuiDialogHut? hutDialog;
    // Who has which settler window open: player uid -> (colony, citizen id).
    readonly Dictionary<string, (string ColonyId, int CitizenId)> citizenViewers = new();
    GuiDialogCitizen? citizenDialog;

    public override void Start(ICoreAPI api)
    {
        // Message types must be registered in the same order on both sides.
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<TownHallOpenRequest>()
            .RegisterMessageType<ColonySnapshot>()
            .RegisterMessageType<ColonyActionPacket>()
            .RegisterMessageType<HutOpenRequest>()
            .RegisterMessageType<HutSnapshot>()
            .RegisterMessageType<CitizenSnapshot>()
            .RegisterMessageType<PlacementSnapshot>()
            .RegisterMessageType<WandChooserSnapshot>()
            .RegisterMessageType<GhostPreviewPacket>()
            .RegisterMessageType<QuestBookOpenRequest>()
            .RegisterMessageType<QuestBookSnapshot>()
            .RegisterMessageType<QuestActionPacket>()
            .RegisterMessageType<SettlerSlotPacket>()
            .RegisterMessageType<HandPlacePacket>()
            .RegisterMessageType<DisplayRedrawPacket>();
    }

    // ---- Server ---------------------------------------------------------------------------

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network.GetChannel(ChannelName)
            .SetMessageHandler<TownHallOpenRequest>(OnOpenRequest)
            .SetMessageHandler<ColonyActionPacket>(OnAction)
            .SetMessageHandler<HutOpenRequest>(OnHutOpenRequest)
            .SetMessageHandler<QuestBookOpenRequest>(OnQuestBookOpen)
            .SetMessageHandler<QuestActionPacket>(OnQuestAction)
            .SetMessageHandler<SettlerSlotPacket>(OnSettlerSlot);
        StartQuestsServer(api);
        api.Event.PlayerNowPlaying += player => serverChannel!.SendPacket(new HandPlacePacket { On = Manager.Config.PlaceHutsByHand }, player);
        // Construction progress changes without anyone clicking, so open windows refresh on their own.
        api.Event.RegisterGameTickListener(_ =>
        {
            var watched = viewersByColony.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).Concat(hutViewers.Values.Select(v => v.ColonyId));
            foreach (string colonyId in watched.Distinct().ToList())
            {
                if (Manager.GetById(colonyId) is { } c && c.Buildings.Any(b => b.HasWorkOrder || b.Removing)) RefreshViewers(c, periodic: true);
            }
            // Settlers change what they do all the time, so their windows always refresh.
            foreach (var (uid, view) in citizenViewers.ToList()) SendCitizenWindow(uid, view.ColonyId, view.CitizenId, openWindow: false);
        }, 3000);
        api.Event.PlayerDisconnect += player =>
        {
            foreach (var viewers in viewersByColony.Values) viewers.Remove(player.PlayerUID);
            hutViewers.Remove(player.PlayerUID);
            placementViewers.Remove(player.PlayerUID);
            placementLevels.Remove(player.PlayerUID);
            ForgetGhost(player.PlayerUID);
            planned.Remove(player.PlayerUID);
            wandSpots.Remove(player.PlayerUID);
            citizenViewers.Remove(player.PlayerUID);
            questViewers.Remove(player.PlayerUID);
        };
    }

    ColonyManager Manager => sapi!.ModLoader.GetModSystem<ColonyManager>();

    /// <summary>Tells every player the PlaceHutsByHand setting after /colony handplace changes it.</summary>
    public void SendHandPlace() => serverChannel!.BroadcastPacket(new HandPlacePacket { On = Manager.Config.PlaceHutsByHand });

    /// <summary>Tells players to redraw what a rack or shelf shows, after a settler put something on it.</summary>
    public void SendDisplayRedraw(BlockPos pos) => serverChannel?.BroadcastPacket(new DisplayRedrawPacket { X = pos.X, Y = pos.Y, Z = pos.Z });

    void OnOpenRequest(IServerPlayer player, TownHallOpenRequest req)
    {
        if (sapi!.World.BlockAccessor.GetBlockEntity(req.Pos) is not BlockEntityTownHall { ColonyId: not null } be) return;
        var colony = Manager.GetById(be.ColonyId);
        if (colony != null) OpenTownHall(player, colony);
    }

    /// <summary>Opens a colony's Town Hall window for a player, wherever they are (the Colony Ledger uses this).</summary>
    public void OpenTownHall(IServerPlayer player, Colony colony)
    {
        // Visitors may look but not change anything; the window hides controls by role.
        if (!viewersByColony.TryGetValue(colony.ColonyId, out var viewers)) viewersByColony[colony.ColonyId] = viewers = new();
        viewers.Add(player.PlayerUID);
        serverChannel!.SendPacket(BuildSnapshot(colony, player.PlayerUID, openWindow: true), player);
    }

    /// <summary>Whether a player has this colony's Town Hall window open.</summary>
    public bool IsViewing(string playerUid, string colonyId) => viewersByColony.TryGetValue(colonyId, out var viewers) && viewers.Contains(playerUid);

    void OnAction(IServerPlayer player, ColonyActionPacket packet)
    {
        // A building planned with the wand has no colony or building yet.
        if (packet.BuildingId == 0 && packet.Action is >= ColonyAction.OpenPlacement and <= ColonyAction.WandChange or ColonyAction.PlacementLevel or ColonyAction.WandPack or ColonyAction.PlacementVariant)
        {
            PlannedAction(player, packet);
            return;
        }
        var colony = Manager.GetById(packet.ColonyId);
        if (colony == null) return;
        var role = colony.RoleOf(player.PlayerUID);

        if (packet.Action == ColonyAction.OpenStorage)
        {
            OpenStorage(player, colony, role, packet.BuildingId);
            return;
        }

        if (packet.Action is >= ColonyAction.OpenPlacement and <= ColonyAction.ReopenPlacement or ColonyAction.PlacementLevel or ColonyAction.PlacementVariant)
        {
            PlacementAction(player, colony, role, packet);
            return;
        }

        if (packet.Action == ColonyAction.CloseWindow)
        {
            if (viewersByColony.TryGetValue(colony.ColonyId, out var v)) v.Remove(player.PlayerUID);
            hutViewers.Remove(player.PlayerUID);
            citizenViewers.Remove(player.PlayerUID);
            return;
        }

        string? error = packet.Action switch
        {
            ColonyAction.Rename => Rename(colony, role, packet.Text),
            ColonyAction.SetJob => SetJob(colony, role, packet.CitizenId, packet.Text),
            ColonyAction.SetRole => SetRole(colony, role, packet.PlayerUid, (ColonyRole)packet.Role),
            ColonyAction.RemoveMember => SetRole(colony, role, packet.PlayerUid, ColonyRole.Visitor),
            ColonyAction.Build or ColonyAction.CancelBuild or ColonyAction.Rotate or ColonyAction.Repair => BuildAction(colony, role, packet),
            ColonyAction.CancelTeardown => CancelTeardown(colony, role, packet.BuildingId),
            ColonyAction.MovePriority => MovePriority(colony, role, packet),
            ColonyAction.SetAnyWoodOrStone => SetAnyWoodOrStone(colony, role, packet.Text == "1"),
            ColonyAction.SetWorkRadius or ColonyAction.SetEscort or ColonyAction.SetCollectBerries => WorkAreaAction(colony, role, packet),
            ColonyAction.SetPotteryKeep => PotteryAction(colony, role, packet),
            ColonyAction.SetKitchenKeep => KitchenAction(colony, role, packet),
            ColonyAction.SetCraftKeep => CraftAction(colony, role, packet),
            ColonyAction.SetRecipeKeep => RecipeAction(colony, role, packet),
            ColonyAction.TeachRecipe or ColonyAction.ForgetRecipe => TeachAction(player, colony, role, packet),
            ColonyAction.SetMineOre or ColonyAction.SetMineProspected or ColonyAction.NewMine => MiningAction(colony, role, packet),
            ColonyAction.SetWorkRole => WorkRoleAction(colony, role, packet),
            >= ColonyAction.SetMineLevel and <= ColonyAction.UnassignMineGuard => MineTabAction(colony, role, packet),
            ColonyAction.GiveResource or ColonyAction.GiveAllResources => GiveResource(player, colony, role, packet),
            ColonyAction.MoveInResidence or ColonyAction.MoveOutResidence => ResidenceAction(colony, role, packet),
            ColonyAction.SetMarkerGroup or ColonyAction.RemoveMarkers or ColonyAction.MarkRemovedAgain => MarkerAction(colony, role, packet),
            ColonyAction.RemoveMarker => RemoveMapMarker(colony, role, packet),
            ColonyAction.SetGatherMode => SetGatherMode(colony, role, packet),
            ColonyAction.WelcomeVisitor or ColonyAction.TurnAwayVisitor => VisitorAction(colony, role, packet),
            ColonyAction.RecallCitizen => RecallCitizen(colony, role, packet.CitizenId),
            ColonyAction.SetColonyBanner or ColonyAction.ReplaceBanners or ColonyAction.SetBannerOnNew => BannerAction(player, colony, role, packet),
            ColonyAction.SetPermadeath => SetPermadeath(colony, role, packet.Text == "1"),
            ColonyAction.SetSelfDefence or ColonyAction.SetSupportAllies => SetMilitia(colony, role, packet),
            ColonyAction.SetBailiffRule => SetBailiffRule(player, colony, role, packet.Text),
            ColonyAction.SetTithe => SetTithe(colony, role, packet.Text),
            ColonyAction.StartResearch or ColonyAction.PauseResearch => ResearchAction(colony, role, packet),
            ColonyAction.HireRecruit => HireAction(player, colony, role, packet),
            ColonyAction.RemoveHut => RemoveHut(player, colony, role, packet),
            ColonyAction.LinkMineHut or ColonyAction.UnlinkMineHut => LinkMineAction(colony, role, packet),
            ColonyAction.SetCollect or >= ColonyAction.RenameBuilding and <= ColonyAction.SetMinStock => HutAction(player, colony, role, packet),
            _ => null
        };

        if (error != null)
        {
            player.SendIngameError("createacolony-denied", Lang.GetL(player.LanguageCode, error));
        }
        else
        {
            Manager.MarkDirty(colony);
        }
        RefreshViewers(colony);
    }

    string? SetAnyWoodOrStone(Colony colony, ColonyRole role, bool on)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        Manager.SetAnyWoodOrStone(colony, on);
        return null;
    }

    string? VisitorAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var visitor = colony.Citizens.Find(c => c.CitizenId == packet.CitizenId);
        if (visitor == null || !visitor.Alive || !visitor.Visitor) return "createacolony:bad-citizen";
        if (packet.Action == ColonyAction.WelcomeVisitor) return Manager.Welcome(colony, visitor);
        Manager.TurnAway(colony, visitor);
        return null;
    }

    /// <summary>The Banners tab: set the colony banner from the player's hand, copy it onto buildings, or put it on new ones.</summary>
    string? BannerAction(IServerPlayer player, Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        switch (packet.Action)
        {
            case ColonyAction.SetColonyBanner:
                return Manager.SetColonyBanner(colony, player.InventoryManager.ActiveHotbarSlot?.Itemstack);
            case ColonyAction.SetBannerOnNew:
                colony.BannerOnNew = packet.Text == "1";
                return null;
        }
        if (Manager.ColonyBanner(colony) == null) return "createacolony:banner-none";
        var buildings = packet.Text switch
        {
            "townhall" => colony.Buildings.Where(b => b.Type == BuildingTypes.TownHall),
            "guardtower" => colony.Buildings.Where(b => ColonyManager.IsGuardPost(b.Type)),
            _ => colony.Buildings
        };
        var (replaced, unloaded) = Manager.ReplaceBanners(colony, buildings.ToList());
        string message = Lang.GetL(player.LanguageCode, "createacolony:banner-replaced", replaced);
        if (unloaded > 0) message += " " + Lang.GetL(player.LanguageCode, "createacolony:banner-unloaded", unloaded);
        player.SendMessage(0, message, EnumChatType.Notification);
        return null;
    }

    string? RecallCitizen(Colony colony, ColonyRole role, int citizenId)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var citizen = colony.Citizens.Find(c => c.CitizenId == citizenId);
        if (citizen == null) return "createacolony:bad-citizen";
        return Manager.RecallCitizen(colony, citizen);
    }

    static string? SetPermadeath(Colony colony, ColonyRole role, bool on)
    {
        if (role < ColonyRole.Owner) return "createacolony:denied-owner";
        colony.Permadeath = on;
        return null;
    }

    /// <summary>The Colony militia switches; officers set them.</summary>
    static string? SetMilitia(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        if (packet.Action == ColonyAction.SetSelfDefence) colony.SelfDefence = packet.Text == "1";
        else colony.SupportAllies = packet.Text == "1";
        return null;
    }

    /// <summary>The Bailiff tab's rules; officers set them.</summary>
    /// <summary>The share of wages settlers give the church (Church tab).</summary>
    string? SetTithe(Colony colony, ColonyRole role, string text)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        if (!int.TryParse(text, out int percent)) return null;
        Manager.SetTithe(colony, percent);
        return null;
    }

    string? SetBailiffRule(IServerPlayer player, Colony colony, ColonyRole role, string text)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var parts = text.Split('|');
        if (parts.Length != 2) return null;
        switch (parts[0])
        {
            case "wanderers":
                if (int.TryParse(parts[1], out int rule) && rule >= 0 && rule <= BailiffWanderers.Last) colony.WandererRule = rule;
                break;
            case "jobs":
                colony.BailiffGivesJobs = parts[1] == "1";
                break;
            case "rent":
                colony.BailiffCollectsRent = parts[1] == "1";
                break;
            case "lend":
                colony.BailiffLendsWorkers = parts[1] == "1";
                break;
            case "wages":
                colony.BailiffPaysWages = parts[1] == "1";
                break;
            case "currency" when parts[1] == "hand":
                var held = player.InventoryManager.ActiveHotbarSlot?.Itemstack;
                if (held == null || !Manager.IsCoin(held)) return "createacolony:currency-not-coin";
                Manager.SetCurrency(colony, held.Collectible.Code.ToString() == "createacolony:coin-copper" ? null : held);
                break;
            case "currency":
                Manager.SetCurrency(colony, null);
                break;
            case "tax":
                if (int.TryParse(parts[1], out int tax) && tax >= 0 && tax <= 50) colony.TaxPercent = tax;
                break;
        }
        return null;
    }

    /// <summary>Any member may hire at the Tavern; they pay for it.</summary>
    string? HireAction(IServerPlayer player, Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Member) return "createacolony:denied-member";
        var tavern = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (tavern == null || tavern.Type != BuildingTypes.Tavern) return "createacolony:bad-building";
        return Manager.Hire(player, colony, tavern, packet.Text);
    }

    static string? Rename(Colony colony, ColonyRole role, string name)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        name = name.Trim();
        if (name.Length == 0 || name.Length > MaxNameLength) return "createacolony:bad-name";
        colony.Name = name;
        return null;
    }

    string? SetJob(Colony colony, ColonyRole role, int citizenId, string job)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        if (!Jobs.IsValid(job)) return "createacolony:bad-job";
        var citizen = colony.Citizens.Find(c => c.CitizenId == citizenId);
        if (citizen == null || !citizen.Alive) return "createacolony:bad-citizen";
        if (citizen.Visitor) return "createacolony:visitor-no-job";
        if (citizen.WaitingForRoom && job.Length > 0) return "createacolony:waiting-no-job";
        if (citizen.IsChild && job.Length > 0) return "createacolony:child-no-job";
        if (job.Length > 0 && Manager.IncapableOfJob(citizen, job) != null) return "createacolony:job-incapable";
        if (job == Jobs.Bailiff && Manager.BailiffOf(colony) is { } other && other != citizen) return "createacolony:bailiff-taken";
        if (citizen.JobCode != (job.Length == 0 ? null : job)) citizen.WorkplacePos = null;
        citizen.JobCode = job.Length == 0 ? null : job;
        ColonyManager.EndLoan(citizen);
        return null;
    }

    string? CancelTeardown(Colony colony, ColonyRole role, int id)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var order = colony.Teardowns.Find(t => t.Id == id);
        if (order == null) return "createacolony:bad-building";
        Manager.CancelTeardown(colony, order);
        return null;
    }

    string? MovePriority(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || building.Type is BuildingTypes.Builder or BuildingTypes.TownHall) return Manager.MoveBuilderOrder(colony, packet.Text, packet.Role);
        if (!Manager.HasPriorities(building.Type)) return "createacolony:bad-building";
        if (building.Type == BuildingTypes.Gatherer && packet.Text.StartsWith(ColonyManager.GatherKeyPrefix))
        {
            return Manager.MoveGatherPriority(colony, building, packet.Text[ColonyManager.GatherKeyPrefix.Length..], packet.Role);
        }
        var shown = BuildHutSnapshot(colony, building, "", false).Priorities;
        return Manager.MoveHutPriority(colony, building, shown, packet.Text, packet.Role);
    }

    /// <summary>The order a crafting hut's worker goes through what it keeps: the rows with something kept, as ByPriority sorts them.</summary>
    void AddPriorities(HutSnapshot snapshot, Colony colony, Building building)
    {
        if (!Manager.HasPriorities(building.Type)) return;
        snapshot.HasPriorities = true;
        if (building.Type == BuildingTypes.Gatherer) snapshot.GatherOrder = ColonyManager.GatherOrder(building);
        if (building.Type == BuildingTypes.Tailor)
        {
            int toMend = Manager.MendingCount(colony, building, Manager.WorkerAt(colony, building, Jobs.Tailor));
            var kit = sapi.World.GetItem(new AssetLocation("game:sewingkit"));
            snapshot.WorkRows.Add(new PotteryView
            {
                Key = ColonyManager.MendKey,
                Stack = kit != null ? new ItemStack(kit).ToBytes() : System.Array.Empty<byte>(),
                Keep = 1,
                Have = toMend > 0 ? 0 : 1,
                Name = Lang.Get("createacolony:priority-mend"),
                PriorityText = Lang.Get("createacolony:priority-mend-count", toMend)
            });
        }
        var rows = snapshot.Craft.Concat(snapshot.Recipes).Concat(snapshot.Kitchen).Concat(snapshot.Pottery).Concat(snapshot.WorkRows)
            .Where(r => r.Keep > 0).GroupBy(r => r.Key).Select(g => g.First());
        snapshot.Priorities = Manager.ByPriority(building, rows.Select(r => (Kind: r.Key, r.Key, Short: r.Keep - r.Have))).ToList();
        // Until mending is placed on the list, the Tailor mends first (AiTaskSettlerTailor.WorkBeforeRecipes).
        if (!building.Priorities.Contains(ColonyManager.MendKey) && snapshot.Priorities.Remove(ColonyManager.MendKey)) snapshot.Priorities.Insert(0, ColonyManager.MendKey);
    }

    /// <summary>A trader's old house in the work order lists.</summary>
    BuildingView TeardownViewFor(Colony colony, TeardownOrder t) => new()
    {
        TeardownId = t.Id,
        Type = "traderhouse",
        Name = t.Owner,
        Removing = true,
        Status = Manager.BuildStatus.GetValueOrDefault(-t.Id, "queued"),
        ProgressPercent = t.Total <= 0 ? 0 : System.Math.Min(100, 100 * t.Done / t.Total)
    };

    string? BuildAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null) return "createacolony:bad-building";

        switch (packet.Action)
        {
            case ColonyAction.Build:
                return Manager.StartWorkOrder(colony, building);
            case ColonyAction.Repair:
                return Manager.StartRepair(colony, building);
            case ColonyAction.CancelBuild:
                if (building.Removing) Manager.CancelRemoval(colony, building);
                else Manager.CancelWorkOrder(colony, building);
                return null;
            default:
                // Turning is only allowed before anything is built, or the old walls would be torn down.
                if (building.Level > 0 || building.HasWorkOrder) return "createacolony:rotate-built";
                building.Rotation = (building.Rotation + 90) % 360;
                TurnModel(building);
                return null;
        }
    }

    /// <summary>The Work area settings of a Forester's or Farmer's Hut.</summary>
    string? WorkAreaAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || !ColonyManager.IsGatherHut(building.Type)) return "createacolony:bad-building";
        switch (packet.Action)
        {
            case ColonyAction.SetWorkRadius:
                if (!int.TryParse(packet.Text, out int radius) || !Manager.Config.GatherRadiusChoices.Contains(radius)) return "createacolony:bad-radius";
                building.WorkRadius = radius;
                break;
            case ColonyAction.SetEscort:
                building.Escort = packet.Text == "1";
                break;
            default:
                building.NoBerries = packet.Text != "1";
                break;
        }
        return null;
    }

    /// <summary>The Markers tab of an Explorer's or Gatherer's Hut: which groups to mark, and taking its markers away.</summary>
    string? MarkerAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || !ColonyManager.MarkerGroups.TryGetValue(building.Type, out var groups)) return "createacolony:bad-building";
        if (packet.Action == ColonyAction.RemoveMarkers)
        {
            Manager.RemoveHutMarkers(colony, building);
            return null;
        }
        if (packet.Action == ColonyAction.MarkRemovedAgain)
        {
            Manager.MarkRemovedAgain(colony, building);
            return null;
        }
        int sep = packet.Text.IndexOf('|');
        if (sep < 0) return "createacolony:bad-building";
        string group = packet.Text[..sep];
        if (!groups.Contains(group)) return "createacolony:bad-building";
        building.MarkersOff.Remove(group);
        if (packet.Text[(sep + 1)..] == "0") building.MarkersOff.Add(group);
        return null;
    }

    /// <summary>The Gatherer's Hut's choice of picking, looking around, or both.</summary>
    string? SetGatherMode(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null) return "createacolony:bad-building";
        return Manager.SetGatherMode(colony, building, packet.Text);
    }

    /// <summary>Right-click on a marker of the Colony markers map layer: takes that one marker off the map.</summary>
    string? RemoveMapMarker(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        if (!int.TryParse(packet.Text, out int markerId) || !Manager.RemoveMarker(colony, markerId)) return "createacolony:marker-gone";
        return null;
    }

    /// <summary>The Miner's Hut's ore choice, "Go to prospected ore" and new mine.</summary>
    string? MiningAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || building.Type is not (BuildingTypes.Miner or BuildingTypes.Prospector)) return "createacolony:bad-building";
        if (packet.Action == ColonyAction.SetMineProspected)
        {
            building.MineProspected = packet.Text == "1";
            return null;
        }
        if (packet.Action == ColonyAction.SetMineOre)
        {
            if (packet.Text.Length > 0 && !colony.OreSites.Any(s => s.Ores.Any(o => o.Code == packet.Text))) return "createacolony:bad-building";
            building.MineOre = packet.Text;
            // Choosing an ore to mine allows it on the Allowed to collect tab too.
            building.CollectOff.Remove("ore-" + packet.Text);
        }
        Manager.ClearMines(colony, building);
        return null;
    }

    /// <summary>Whether a prospector finds ore or mines it.</summary>
    string? WorkRoleAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        var citizen = colony.Citizens.Find(c => c.CitizenId == packet.CitizenId);
        if (building == null || !int.TryParse(packet.Text, out int work)) return "createacolony:bad-building";
        if (citizen == null) return "createacolony:bad-citizen";
        return Manager.SetWorkRole(colony, building, citizen, work);
    }

    /// <summary>How many of a kind of pottery a Potter's Hut keeps in stock.</summary>
    string? PotteryAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || building.Type != BuildingTypes.Potter) return "createacolony:bad-building";
        int bar = packet.Text.LastIndexOf('|');
        if (bar <= 0 || !int.TryParse(packet.Text[(bar + 1)..], out int keep) || keep < 0 || keep > 999) return "createacolony:bad-building";
        string key = packet.Text[..bar];
        if (!Manager.PotteryKinds().Any(k => k.Key == key)) return "createacolony:bad-building";
        Manager.SetPotteryKeep(colony, building, key, keep);
        return null;
    }

    /// <summary>How much of a cooked food a Cook's Hut keeps in stock.</summary>
    string? KitchenAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || building.Type != BuildingTypes.Cook) return "createacolony:bad-building";
        // No dish named: the cook chooses again.
        if (packet.Text.Length == 0)
        {
            Manager.SetKitchenKeep(colony, building, "", 0);
            return null;
        }
        int bar = packet.Text.LastIndexOf('|');
        if (bar <= 0 || !int.TryParse(packet.Text[(bar + 1)..], out int keep) || keep < 0 || keep > 999) return "createacolony:bad-building";
        string key = packet.Text[..bar];
        if (!Manager.KitchenKinds().Any(k => k.Key == key) && Manager.MealRecipe(key) == null && Manager.FindOvenKind(key) == null) return "createacolony:bad-building";
        Manager.SetKitchenKeep(colony, building, key, keep);
        return null;
    }

    /// <summary>The "keep in stock" tab of a crafter hut: each metal for a Smelter, each thing the anvil can make for a Blacksmith, each wooden thing for a Sawmill, each stone tool for a Primitive Craftsman.</summary>
    void AddCraftRows(HutSnapshot snapshot, Colony colony, Building building, string craftTab)
    {
        snapshot.CraftTab = craftTab;
        snapshot.KeepChoices = Manager.Config.CraftKeepChoices.ToList();
        if (building.Type == BuildingTypes.Smelter)
        {
            var smelter = Manager.WorkerAt(colony, building, Jobs.Smelter);
            foreach (var kind in Manager.SmeltKinds())
            {
                snapshot.Craft.Add(new PotteryView
                {
                    Key = kind.Key,
                    Stack = kind.Sample.ToBytes(),
                    Keep = Manager.CraftKeep(building, kind.Key),
                    Have = Manager.SmeltStock(colony, building, kind, smelter),
                    Clay = kind.UnitsPer.Keys.Min(code => ColonyManager.PiecesPerIngot(kind, code))
                });
            }
        }
        else if (building.Type is BuildingTypes.Blacksmith or BuildingTypes.Armourer or BuildingTypes.Weaponsmith)
        {
            var smith = Manager.WorkerAt(colony, building, ColonyManager.JobOf(building.Type)!);
            foreach (var kind in Manager.SmithRows(colony, building, smith))
            {
                snapshot.Craft.Add(new PotteryView
                {
                    Key = kind.Key,
                    Stack = kind.Sample.ToBytes(),
                    Keep = Manager.CraftKeep(building, kind.Key),
                    Have = Manager.SmithStock(colony, building, kind, smith),
                    Clay = kind.Ingots
                });
            }
        }
        else if (building.Type == BuildingTypes.Sawmill)
        {
            var sawyer = Manager.WorkerAt(colony, building, Jobs.Sawyer);
            foreach (var kind in Manager.SawRows(colony, building, sawyer))
            {
                snapshot.Craft.Add(new PotteryView
                {
                    Key = kind.Key,
                    Stack = kind.Sample.ToBytes(),
                    Keep = Manager.CraftKeep(building, kind.Key),
                    Have = Manager.SawStock(colony, building, kind, sawyer),
                    Clay = kind.Recipes[0].InputCount
                });
            }
        }
        else if (building.Type == BuildingTypes.Craftsman)
        {
            var craftsman = Manager.WorkerAt(colony, building, Jobs.Craftsman);
            foreach (var kind in Manager.KnapRows(colony, building, craftsman))
            {
                snapshot.Craft.Add(new PotteryView
                {
                    Key = kind.Key,
                    Stack = kind.Sample.ToBytes(),
                    Keep = Manager.CraftKeep(building, kind.Key),
                    Have = Manager.KnapStock(colony, building, kind, craftsman),
                    Clay = 1 + kind.Handles.Sum(h => h.Quantity)
                });
            }
        }
    }

    /// <summary>
    /// The rows of a hut's Crafting recipes tab: everything it can craft, with what each takes, and the recipes
    /// players taught it. A taught recipe's row shows that recipe; otherwise the game's with the fewest things.
    /// </summary>
    void AddRecipeRows(HutSnapshot snapshot, Colony colony, Building building)
    {
        // A Potter's or Cook's Hut keeps its own tab's choices.
        if (snapshot.KeepChoices.Count == 0) snapshot.KeepChoices = Manager.Config.CraftKeepChoices.ToList();
        snapshot.CanTeach = Manager.Config.CustomRecipes;
        var worker = Manager.WorkerAt(colony, building, ColonyManager.JobOf(building.Type) ?? "");
        foreach (var kind in Manager.HutRecipeKinds(colony, building.Type))
        {
            var recipe = kind.Recipes[0];
            snapshot.Recipes.Add(new PotteryView
            {
                Key = kind.Key,
                Stack = kind.Sample.ToBytes(),
                Keep = Manager.RecipeKeep(building, kind.Key),
                Have = Manager.RecipeStock(colony, building, kind, worker),
                Clay = recipe.InputCount,
                Name = Lang.Get("createacolony:recipes-row", recipe.Output.StackSize, kind.Sample.GetName()),
                Detail = Lang.Get(recipe.CustomId != 0 ? "createacolony:recipes-from-taught" : "createacolony:recipes-from", Manager.RecipeInputsText(recipe))
            });
        }
        foreach (var taught in Manager.TaughtRecipes(colony, building.Type))
        {
            var recipe = Manager.ToSawRecipe(taught);
            snapshot.Taught.Add(new PotteryView
            {
                Key = taught.Id.ToString(),
                Stack = recipe?.Output.ToBytes() ?? System.Array.Empty<byte>(),
                Name = recipe != null ? Lang.Get("createacolony:recipes-row", recipe.Output.StackSize, recipe.Output.GetName()) : taught.Output.Code,
                Detail = recipe != null ? Lang.Get("createacolony:recipes-from", Manager.RecipeInputsText(recipe)) : Lang.Get("createacolony:teach-broken")
            });
        }
        // A hut with nothing to craft yet still gets the tab, so a recipe can be taught there.
        snapshot.HasRecipes = snapshot.Recipes.Count > 0 || snapshot.Taught.Count > 0 || snapshot.CanTeach;
    }

    /// <summary>How many of a thing a hut's worker keeps crafted (Crafting recipes tab).</summary>
    string? RecipeAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || !Manager.HasHutRecipes(building.Type)) return "createacolony:bad-building";
        int bar = packet.Text.LastIndexOf('|');
        if (bar <= 0 || !int.TryParse(packet.Text[(bar + 1)..], out int keep) || keep < 0 || keep > 999) return "createacolony:bad-building";
        string key = packet.Text[..bar];
        if (!Manager.HutRecipeKinds(colony, building.Type).Any(k => k.Key == key)) return "createacolony:bad-building";
        Manager.SetRecipeKeep(colony, building, key, keep);
        return null;
    }

    /// <summary>Teach recipe and Forget on a hut's Crafting recipes tab (officers).</summary>
    string? TeachAction(IServerPlayer player, Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || !Manager.HasHutRecipes(building.Type)) return "createacolony:bad-building";
        if (packet.Action == ColonyAction.TeachRecipe) return Manager.TeachRecipe(colony, building, packet.Text, player.PlayerName);
        return int.TryParse(packet.Text, out int id) ? Manager.ForgetRecipe(colony, building, id) : "createacolony:bad-building";
    }

    /// <summary>How many of a thing a crafter hut keeps in stock.</summary>
    string? CraftAction(Colony colony, ColonyRole role, ColonyActionPacket packet)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        var building = colony.Buildings.Find(b => b.BuildingId == packet.BuildingId);
        if (building == null || ColonyManager.CraftTabOf(building.Type) == null) return "createacolony:bad-building";
        int bar = packet.Text.LastIndexOf('|');
        if (bar <= 0 || !int.TryParse(packet.Text[(bar + 1)..], out int keep) || keep < 0 || keep > 999) return "createacolony:bad-building";
        string key = packet.Text[..bar];
        if (!Manager.CraftKeys(building).Contains(key)) return "createacolony:bad-building";
        Manager.SetCraftKeep(colony, building, key, keep);
        return null;
    }

    /// <summary>Opens a hut's own storage for a member, as sneak + right-click on the hut does.</summary>
    void OpenStorage(IServerPlayer player, Colony colony, ColonyRole role, int buildingId)
    {
        var building = colony.Buildings.Find(b => b.BuildingId == buildingId);
        if (role < ColonyRole.Member || building == null) return;
        Manager.HutStorage(colony, building).FirstOrDefault(); // creates the storage of an older hut
        if (sapi!.World.BlockAccessor.GetBlockEntity(building.HutPos) is BlockEntityColonyHut storage)
        {
            storage.OnPlayerRightClick(player, new BlockSelection { Position = building.HutPos.Copy(), Face = BlockFacing.UP, HitPosition = new Vec3d(0.5, 0.5, 0.5) });
        }
    }

    string? SetRole(Colony colony, ColonyRole role, string targetUid, ColonyRole newRole)
    {
        if (role < ColonyRole.Owner) return "createacolony:denied-owner";
        if (targetUid == colony.OwnerUid) return "createacolony:owner-fixed";
        if (newRole == ColonyRole.Owner) return "createacolony:owner-fixed";
        var member = colony.Members.Find(m => m.PlayerUid == targetUid);
        if (member == null) return "createacolony:bad-member";
        Manager.SetMemberRole(colony, targetUid, member.LastKnownName, newRole);
        return null;
    }

    /// <summary>Sends an updated snapshot to everyone who has this colony's window open.</summary>
    public void RefreshViewers(Colony colony, bool periodic = false)
    {
        foreach (var (uid, hut) in hutViewers.Where(kv => kv.Value.ColonyId == colony.ColonyId).ToList())
        {
            var building = Manager.GetBuilding(colony, hut.HutPos);
            if (building != null && sapi!.World.PlayerByUid(uid) is IServerPlayer hp && hp.ConnectionState == EnumClientState.Playing)
            {
                var hutSnapshot = BuildHutSnapshot(colony, building, uid, openWindow: false);
                hutSnapshot.Periodic = periodic;
                serverChannel!.SendPacket(hutSnapshot, hp);
            }
            else
            {
                hutViewers.Remove(uid);
            }
        }

        if (serverChannel == null || !viewersByColony.TryGetValue(colony.ColonyId, out var viewers)) return;
        foreach (string uid in viewers.ToList())
        {
            if (sapi!.World.PlayerByUid(uid) is IServerPlayer p && p.ConnectionState == EnumClientState.Playing)
            {
                var snapshot = BuildSnapshot(colony, uid, openWindow: false);
                snapshot.Periodic = periodic;
                serverChannel.SendPacket(snapshot, p);
            }
            else
            {
                viewers.Remove(uid);
            }
        }
    }

    ColonySnapshot BuildSnapshot(Colony colony, string viewerUid, bool openWindow)
    {
        var config = Manager.Config;
        var snapshot = new ColonySnapshot
        {
            ColonyId = colony.ColonyId,
            Name = colony.Name,
            Level = colony.Level,
            PopulationCap = Manager.PopulationCap(colony),
            AverageHappiness = Manager.Config.Happiness ? Manager.AverageHappiness(colony) : -1,
            ViewerRole = (int)colony.RoleOf(viewerUid),
            BedCount = colony.Citizens.Count(c => c.Alive && c.HomeBedPos != null),
            OpenWindow = openWindow,
            HasWarehouse = colony.WarehousePos != null,
            WarehouseChests = Manager.WarehouseContainers(colony).Count(),
            OpenRequests = colony.Requests.Count,
            DropOffs = colony.DropOffs.Count,
            Permadeath = Manager.Permadeath(colony),
            SelfDefence = Manager.SelfDefence(colony),
            SupportAllies = Manager.SupportAllies(colony),
            FreeBeds = Manager.FreeBeds(colony),
            WandererDays = config.Wanderers ? Manager.WandererEveryDays(colony) : 0,
            WanderersNeedFreeBed = config.WanderersNeedFreeBed,
            ClaimColumns = Manager.ClaimedColumns(colony),
            ClaimSquares = Manager.ClaimAreas(colony).Count,
            TownHallClaimSize = Manager.GetBuilding(colony, colony.TownHallPos) is { } hall ? Manager.ClaimedSize(colony, hall) : 0,
            FoundedTotalDays = colony.FoundedTotalDays,
            BannerStack = colony.BannerStack,
            BannerOnNew = colony.BannerOnNew,
            HeraldryInstalled = sapi!.ModLoader.IsModEnabled("heraldrybanners"),
            BuildingCount = colony.Buildings.Count,
            Events = colony.Events.Select(e => new EventView { TotalDays = e.TotalDays, Key = e.Key, Args = e.Args.ToList() }).ToList(),
            WorkOrders = Manager.BuilderQueue(colony).Select(e => e.Building != null ? BuildingViewFor(colony, e.Building) : TeardownViewFor(colony, e.Teardown!)).ToList(),
            Jobs = Manager.JobCounts(colony).Select(j => new JobView { Job = j.Job, Workers = j.Workers, Places = j.Places }).ToList(),
            Stats = Manager.ColonyStatSummary(colony).Select(r => new StatView { Key = r.Key, Counts = r.Counts }).ToList(),
            Quests = sapi!.World.PlayerByUid(viewerUid) is IServerPlayer viewer ? QuestStates(viewer, colony) : new()
        };

        var bailiff = Manager.BailiffOf(colony);
        snapshot.BailiffName = bailiff?.Name ?? "";
        snapshot.BailiffState = bailiff == null ? BailiffState.None : bailiff.IsDowned ? BailiffState.Downed
            : Manager.IsBrokenDown(bailiff) ? BailiffState.Sulking : BailiffState.OnDuty;
        snapshot.WandererRule = colony.WandererRule;
        snapshot.BailiffGivesJobs = colony.BailiffGivesJobs;
        snapshot.BailiffCollectsRent = colony.BailiffCollectsRent;
        snapshot.BailiffLendsWorkers = colony.BailiffLendsWorkers;
        snapshot.BailiffPaysWages = colony.BailiffPaysWages;
        snapshot.TaxPercent = colony.TaxPercent;
        snapshot.HasMint = Manager.HasMint(colony);
        snapshot.Treasury = Manager.TreasuryCoins(colony);
        snapshot.NextPayday = Manager.NextPayday();
        snapshot.CurrencyName = Manager.CurrencyName(colony);
        snapshot.EconomyLevel = config.EconomyTownHallLevel;
        snapshot.EconomyOpen = Manager.EconomyOpen(colony);
        snapshot.OwnCoin = Manager.HasOwnCoin(colony);
        snapshot.CoinMetalName = snapshot.OwnCoin && sapi!.World.GetItem(Manager.CoinMetal(colony)) is { } ingot ? new ItemStack(ingot).GetName() : "";
        snapshot.WageBill = colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild).Sum(c => Manager.WageOf(c.JobCode));
        snapshot.Ledger = colony.Ledger.ToList();
        snapshot.EstateReport = Manager.EstateReport(colony);
        snapshot.OreReadings = colony.OreSites.Count(s => !s.Unread);
        snapshot.ProspectorHuts = colony.Buildings.Count(b => b.Type == BuildingTypes.Prospector);
        foreach (var ore in Manager.OreSummary(colony))
        {
            snapshot.Ores.Add(new OreView { Code = ore.Code, Best = ore.Best, Sites = ore.Sites, MinedOut = ore.MinedOut, Where = ColonyManager.DirectionFrom(colony.TownHallPos, ore.BestPos) });
        }

        foreach (var c in colony.Citizens)
        {
            string state = !c.Alive ? "dead"
                : c.IsDowned ? "downed"
                : c.Visitor ? "visitor"
                : c.WaitingForRoom ? "waiting"
                : c.Saturation >= 0 && c.Saturation < config.SaturationMax * config.EatBelowFraction ? "hungry"
                : "ok";
            snapshot.Citizens.Add(new CitizenView
            {
                CitizenId = c.CitizenId,
                Name = c.Name,
                Job = ColonyManager.JobLabel(c),
                State = state,
                FoodPercent = c.Saturation < 0 ? 100 : (int)(100 * c.Saturation / config.SaturationMax),
                HasBed = c.HomeBedPos != null,
                Visitor = c.Visitor,
                X = SeenAt(c).X,
                Z = SeenAt(c).Z,
                Child = c.IsChild,
                GrowsUpHours = Manager.HoursToGrowUp(c),
                DiedTotalDays = c.DiedTotalDays,
                DeathCause = c.DeathCause,
                Buried = c.Buried,
                VisitorHoursLeft = c.Visitor ? (int)System.Math.Ceiling(System.Math.Max(0, c.VisitorUntilTotalDays - sapi!.World.Calendar.TotalDays) * sapi.World.Calendar.HoursPerDay) : 0,
                TraitsText = Manager.TraitsText(c),
                Weapon = c.Alive && !c.IsChild && !c.Visitor ? Manager.BestWeapon(c)?.GetName() ?? "" : ""
            });
        }

        // The Town Hall's own upgrade sits on the Actions tab; every other building has its own hut window.
        if (Manager.GetBuilding(colony, colony.TownHallPos) is { } townHall) snapshot.Buildings.Add(BuildingViewFor(colony, townHall));

        foreach (var m in colony.Members.OrderByDescending(m => m.Role))
        {
            snapshot.Members.Add(new MemberView { PlayerUid = m.PlayerUid, Name = m.LastKnownName, Role = (int)m.Role });
        }

        // The Town map: buildings with the ground they stand on, and the colony's land.
        foreach (var b in colony.Buildings)
        {
            var (x1, z1, x2, z2) = Footprint(b);
            snapshot.MapBuildings.Add(new MapBuildingView
            {
                Type = b.Type, Name = ColonyManager.BuildingName(b), Level = b.Level, UnderConstruction = b.HasWorkOrder,
                X1 = x1, Z1 = z1, X2 = x2, Z2 = z2
            });
        }
        foreach (var area in Manager.ClaimAreas(colony))
        {
            snapshot.ClaimRects.AddRange(new[] { area.MinX, area.MinZ, area.MaxX - 1, area.MaxZ - 1 });
        }
        return snapshot;
    }

    /// <summary>Where a settler is now, or was last seen.</summary>
    BlockPos SeenAt(CitizenRecord c)
    {
        if (sapi!.World.GetEntityById(c.EntityId) is { Alive: true } entity) return entity.Pos.AsBlockPos;
        return c.LastSeenPos?.AsBlockPos ?? new BlockPos(0);
    }

    readonly Dictionary<string, (int, int, int, int)> footprints = new();

    /// <summary>
    /// The block columns a building covers, from the blueprint of its current (or first) level; just the
    /// hut block's surroundings for buildings without blueprints. Cached, since blueprints are big.
    /// </summary>
    (int X1, int Z1, int X2, int Z2) Footprint(Building b)
    {
        int level = b.Level > 0 ? b.Level : System.Math.Max(1, b.TargetLevel);
        string key = $"{b.BuildingId}|{b.Type}|{b.Style}|{b.Variant}|{level}|{b.Rotation}|{b.HutPos}|{b.XOffset}|{b.ZOffset}";
        if (footprints.TryGetValue(key, out var cached)) return cached;

        var p = b.HutPos;
        (int, int, int, int) result = (p.X - 1, p.Z - 1, p.X + 1, p.Z + 1);
        var blueprint = Manager.Blueprints.Get(b, level);
        if (blueprint != null && blueprint.Steps.Count > 0)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (var step in blueprint.Steps)
            {
                if (step.BlockId == 0) continue;
                int x = p.X + step.Dx + b.XOffset, z = p.Z + step.Dz + b.ZOffset;
                minX = System.Math.Min(minX, x); maxX = System.Math.Max(maxX, x);
                minZ = System.Math.Min(minZ, z); maxZ = System.Math.Max(maxZ, z);
            }
            if (minX <= maxX) result = (minX, minZ, maxX, maxZ);
        }
        if (footprints.Count > 2000) footprints.Clear();
        footprints[key] = result;
        return result;
    }

    BuildingView BuildingViewFor(Colony colony, Building b)
    {
        var view = new BuildingView
        {
            BuildingId = b.BuildingId,
            Type = b.Type,
            Level = b.Level,
            MaxLevel = Manager.Blueprints.MaxLevel(b),
            TargetLevel = b.HasWorkOrder ? b.TargetLevel : 0,
            Rotation = b.Rotation,
            Style = BlueprintStyles.Normalize(b.Style),
            Variant = Manager.Blueprints.Variants(Manager.Blueprints.StyleFor(b), b.Type).Count > 1 ? Manager.Blueprints.VariantFor(Manager.Blueprints.StyleFor(b), b.Type, b.Variant) : "",
            Name = ColonyManager.BuildingName(b),
            Status = b.HasWorkOrder || b.Removing ? Manager.BuildStatus.GetValueOrDefault(b.BuildingId, "queued") : "",
            Removing = b.Removing,
            Repairing = b.Repairing
        };
        if (b.Removing)
        {
            var (gone, all) = Manager.RemovalProgress(colony, b);
            view.ProgressPercent = all == 0 ? 100 : 100 * gone / all;
            return view;
        }
        var blueprint = b.HasWorkOrder ? Manager.Blueprints.Get(b, b.TargetLevel) : null;
        if (blueprint != null)
        {
            var (done, total) = Manager.Progress(colony, b, blueprint);
            view.ProgressPercent = total == 0 ? 100 : 100 * done / total;
        }
        return view;
    }

    void OnHutOpenRequest(IServerPlayer player, HutOpenRequest req)
    {
        var colony = Manager.GetAt(req.Pos);
        var building = colony == null ? null : Manager.GetBuilding(colony, req.Pos);
        if (colony == null || building == null) return;

        hutViewers[player.PlayerUID] = (colony.ColonyId, building.HutPos.Copy());
        serverChannel!.SendPacket(BuildHutSnapshot(colony, building, player.PlayerUID, openWindow: true), player);
    }

    /// <summary>A member moving items in or out of a settler's slots; the settler window refreshes straight away.</summary>
    void OnSettlerSlot(IServerPlayer player, SettlerSlotPacket packet)
    {
        var colony = Manager.GetById(packet.ColonyId);
        var citizen = colony?.Citizens.Find(c => c.CitizenId == packet.CitizenId);
        if (colony == null || citizen == null) return;
        string? message = Manager.ClickSettlerSlot(colony, citizen, player, packet.Area, packet.Index, packet.Right, packet.Shift);
        if (message != null) player.SendMessage(0, message, EnumChatType.Notification);
        SendCitizenWindow(player.PlayerUID, colony.ColonyId, citizen.CitizenId, openWindow: false);
    }

    /// <summary>Opens (or refreshes) the settler window for a player. Called when a player right-clicks a settler.</summary>
    public void OpenCitizenWindow(IServerPlayer player, Colony colony, CitizenRecord citizen)
    {
        citizenViewers[player.PlayerUID] = (colony.ColonyId, citizen.CitizenId);
        SendCitizenWindow(player.PlayerUID, colony.ColonyId, citizen.CitizenId, openWindow: true);
    }

    void SendCitizenWindow(string uid, string colonyId, int citizenId, bool openWindow)
    {
        var colony = Manager.GetById(colonyId);
        var citizen = colony?.Citizens.Find(c => c.CitizenId == citizenId);
        if (colony == null || citizen == null || sapi!.World.PlayerByUid(uid) is not IServerPlayer player
            || player.ConnectionState != EnumClientState.Playing)
        {
            citizenViewers.Remove(uid);
            return;
        }

        var config = Manager.Config;
        var entity = sapi.World.GetEntityById(citizen.EntityId) as EntityAgent;
        string activity;
        if (!citizen.Alive) activity = Lang.Get("createacolony:activity-dead");
        else if (citizen.IsDowned) activity = Lang.Get("createacolony:activity-downed");
        else
        {
            var task = entity?.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.ActiveTasksBySlot.OfType<Settlers.SettlerTaskBase>().FirstOrDefault();
            activity = task?.Describe(colony, citizen)
                ?? Lang.Get(citizen.Visitor ? "createacolony:activity-visitor-waiting" : "createacolony:activity-idle");
            // A settler building Willpower says so, so players can see it is stuck and help before it relocates itself.
            if (config.Willpower && entity?.GetBehavior<Settlers.EntityBehaviorColonyCitizen>()?.Willpower is { Reason: not Settlers.StuckReason.None } will)
            {
                activity += " " + Lang.Get("createacolony:activity-stuck", (int)(will.Fraction * 100));
            }
        }

        var health = entity?.WatchedAttributes.GetTreeAttribute("health");
        var snapshot = new CitizenSnapshot
        {
            ColonyId = colony.ColonyId,
            CitizenId = citizen.CitizenId,
            Name = citizen.Name,
            Job = ColonyManager.JobLabel(citizen),
            State = !citizen.Alive ? "dead" : citizen.IsDowned ? "downed" : citizen.Visitor ? "visitor" : citizen.WaitingForRoom ? "waiting"
                : citizen.Saturation >= 0 && citizen.Saturation < config.SaturationMax * config.EatBelowFraction ? "hungry" : "ok",
            FoodPercent = citizen.Saturation < 0 ? 100 : (int)(100 * citizen.Saturation / config.SaturationMax),
            HasBed = citizen.HomeBedPos != null,
            Activity = activity,
            Pack = Manager.PackItems(citizen).Select(s => s.ToBytes()).ToList(),
            Slots = Manager.PackSlots(citizen),
            Bags = Manager.PackBags(citizen).Select(s => s.ToBytes()).ToList(),
            Hand = entity?.RightHandItemSlot?.Itemstack?.ToBytes(),
            OpenWindow = openWindow,
            EntityId = entity?.EntityId ?? 0,
            Health = health?.GetFloat("currenthealth") ?? 0,
            MaxHealth = health?.GetFloat("maxhealth") ?? 0,
            WalkSpeed = entity?.Stats.GetBlended("walkspeed") ?? 1,
            Armor = Manager.WornArmor(citizen).Select(s => s?.ToBytes() ?? System.Array.Empty<byte>()).ToList(),
            Family = FamilyOf(colony, citizen),
            Child = citizen.IsChild,
            GrowsUpHours = Manager.HoursToGrowUp(citizen),
            Home = citizen.HomeBedPos != null && Manager.ResidenceAt(colony, citizen.HomeBedPos) is { } home ? ColonyManager.BuildingName(home) : "",
            FamilyNote = FamilyNote(colony, citizen),
            Surname = citizen.Surname,
            ToolBar = Manager.ToolBarItems(citizen).Select(st => st?.ToBytes() ?? System.Array.Empty<byte>()).ToList(),
            Clothes = Manager.WornClothes(citizen).Select(st => st?.ToBytes() ?? System.Array.Empty<byte>()).ToList(),
            Lunch = Manager.LunchItems(citizen).Select(st => st.ToBytes()).ToList(),
            CanEdit = colony.RoleOf(uid) >= ColonyRole.Member && citizen.Alive,
            ClothesClass = config.SettlerClothesClass,
            Skills = Manager.Config.Skills ? Manager.SkillInfos(colony, citizen)
                .Select(s => new SkillView
                {
                    Code = s.Code, Level = s.Level, Usable = s.Usable, Xp = s.Xp, NextXp = s.NextXp, LevelXp = s.LevelXp, CapNote = s.CapNote,
                    Passion = s.Passion, Incapable = s.Incapable, Decaying = s.Decaying, XpToday = s.XpToday, NextPerk = Manager.NextPerkText(s.Code, s.Usable)
                }).ToList() : new(),
            Traits = Manager.Config.Traits ? TraitViews(citizen) : new(),
            TraitScore = Manager.TraitScore(citizen),
            SkillDailyCap = Manager.Config.SkillDailyCap,
            // The player-style Stats page: food groups, satiety, the air where they stand, and their healing and hunger rates.
            Nutrition = Enumerable.Range(0, ColonyManager.FoodGroups)
                .Select(i => i < citizen.Nutrition.Count ? System.Math.Clamp(citizen.Nutrition[i] / config.SaturationMax, 0f, 1f) : 0f).ToList(),
            Saturation = citizen.Saturation < 0 ? config.SaturationMax : citizen.Saturation,
            SaturationMax = config.SaturationMax,
            Temperature = entity != null ? sapi.World.BlockAccessor.GetClimateAt(entity.Pos.AsBlockPos, EnumGetClimateMode.NowValues)?.Temperature ?? float.NaN : float.NaN,
            Wetness = entity?.WatchedAttributes.GetFloat(Settlers.EntityBehaviorSettlerNeeds.WetnessAttr) ?? 0,
            HealingRate = Manager.TraitMult(citizen, "regen"),
            HungerRate = Manager.TraitMult(citizen, "hunger") * Manager.HungerPerkFor(colony.ColonyId, citizen),
            Purse = citizen.Purse,
            Wage = citizen.IsChild || citizen.Visitor ? 0 : Manager.WageOf(citizen.JobCode),
            Unpaid = citizen.Unpaid,
            // Faith only means something once the colony has a Church.
            Faith = citizen.Faith < 0 || !Manager.HasChurch(colony) ? -1 : (int)System.Math.Round(citizen.Faith),
            FaithWord = citizen.Faith < 0 || !Manager.HasChurch(colony) ? "" : Manager.FaithWord(citizen.Faith)
        };
        if (Manager.Config.Happiness && citizen.Alive && !citizen.IsChild && !citizen.Visitor)
        {
            snapshot.Happiness = (int)System.Math.Round(Manager.HappinessOf(citizen));
            snapshot.Mood = Manager.MoodOf(Manager.HappinessOf(citizen));
            snapshot.Thoughts = Manager.ThoughtsOf(colony, citizen).OrderByDescending(t => t.Mood).Select(Manager.ThoughtText).ToList();
            double left = citizen.BreakdownUntilTotalDays - sapi!.World.Calendar.TotalDays;
            snapshot.BreakdownHours = left > 0 ? (int)System.Math.Ceiling(left * sapi!.World.Calendar.HoursPerDay) : 0;
        }
        serverChannel!.SendPacket(snapshot, player);
    }

    /// <summary>A settler's traits for their window: good ones first, with what each does at its level.</summary>
    List<TraitView> TraitViews(CitizenRecord citizen)
    {
        var list = new List<TraitView>();
        foreach (var record in citizen.Traits.OrderByDescending(t => Manager.Trait(t.Code)?.Points ?? 0).ThenBy(t => t.Code))
        {
            if (Manager.Trait(record.Code) is not { } def) continue;
            list.Add(new TraitView
            {
                Code = def.Code, Level = record.Level, Points = def.Points, Job = def.Job, Earned = def.Earned,
                Active = Manager.TraitActive(citizen, record), Effects = Manager.TraitEffectText(def, record.Level)
            });
        }
        return list;
    }

    HutSnapshot BuildHutSnapshot(Colony colony, Building building, string viewerUid, bool openWindow)
    {
        var snapshot = new HutSnapshot
        {
            ColonyId = colony.ColonyId,
            Building = BuildingViewFor(colony, building),
            ViewerRole = (int)colony.RoleOf(viewerUid),
            IsBuilderHut = building.Type == BuildingTypes.Builder,
            AnyWoodOrStone = colony.AnyWoodOrStone,
            HasStorage = ColonyManager.HasOwnStorage(building.Type),
            OpenWindow = openWindow
        };
        AddHutAdmin(snapshot, colony, building);
        if (ColonyManager.MarkerGroups.TryGetValue(building.Type, out var markerGroups))
        {
            snapshot.MarkerGroups = markerGroups.ToList();
            snapshot.MarkersOff = building.MarkersOff.ToList();
            snapshot.MarkerCount = Manager.MarkerCount(colony, building);
            snapshot.RemovedMarkerCount = Manager.RemovedMarkerCount(colony, building);
            if (building.Type == BuildingTypes.Gatherer) snapshot.GatherMode = building.GatherMode;
        }
        if (building.Type == BuildingTypes.Residence) AddResidence(snapshot, colony, building);

        if (ColonyManager.IsGatherHut(building.Type))
        {
            snapshot.IsGatherHut = true;
            snapshot.WorkRadius = Manager.GatherRadius(building);
            snapshot.RadiusChoices = Manager.Config.GatherRadiusChoices.Union(new[] { snapshot.WorkRadius }).OrderBy(r => r).ToList();
            snapshot.Escort = building.Escort;
            snapshot.CollectBerries = !building.NoBerries;
            snapshot.GuardName = Manager.GuardOf(colony, building)?.Name ?? "";
            snapshot.GuardCount = Manager.GuardCount(colony);
        }

        if (building.Type == BuildingTypes.Warehouse)
        {
            var containers = Manager.WarehouseContainers(colony).ToList();
            snapshot.Info = Lang.Get("createacolony:warehouse-summary", containers.Count, Manager.CountItems(containers, _ => true),
                containers.Sum(c => c.Inventory.Count(s => s.Empty)), containers.Sum(c => c.Inventory.Count));
        }
        if (building.Type is BuildingTypes.Forester or BuildingTypes.Farmer or BuildingTypes.Potter or BuildingTypes.Cook
            or BuildingTypes.Prospector or BuildingTypes.Miner or BuildingTypes.Quarry
            or BuildingTypes.Smelter or BuildingTypes.Blacksmith or BuildingTypes.Armourer or BuildingTypes.Weaponsmith or BuildingTypes.Sawmill or BuildingTypes.Craftsman or BuildingTypes.Tailor or BuildingTypes.Jeweller or BuildingTypes.Healer or BuildingTypes.Mint or BuildingTypes.Church
            or BuildingTypes.Explorer or BuildingTypes.Gatherer or BuildingTypes.Hunter) snapshot.Info = Manager.WorkerHutInfo(colony, building);
        if (building.Type == BuildingTypes.Tavern)
        {
            snapshot.IsTavern = true;
            snapshot.Info = Manager.TavernInfo(colony, building);
            snapshot.Recruits = Manager.TavernRecruits(colony, building).ToList();
            if (Manager.Config.Skills)
            {
                snapshot.RecruitSkills = snapshot.Recruits.Select(r => string.Join(", ",
                    Manager.BestSkills(r.SkillXp, r.Passions).Select(s => Lang.Get("createacolony:skill-level", Lang.Get("createacolony:skill-" + s.Code), s.Level) + Manager.PassionMark(s.Passion)))).ToList();
            }
            if (Manager.Config.Traits) snapshot.RecruitTraits = snapshot.Recruits.Select(r => Manager.TraitsText(new CitizenRecord { Traits = r.Traits })).ToList();
            snapshot.Population = Manager.Population(colony);
            snapshot.PopulationCap = Manager.PopulationCap(colony);
            if (sapi!.World.PlayerByUid(viewerUid) is { } viewer)
            {
                snapshot.Gears = Manager.GearsFor(viewer, colony, building);
                snapshot.Creative = viewer.WorldData.CurrentGameMode == EnumGameMode.Creative;
            }
            var code = new AssetLocation(Manager.Config.RecruitCurrency);
            var currency = sapi.World.GetItem(code) is { } item ? new ItemStack(item) : sapi.World.GetBlock(code) is { } block ? new ItemStack(block) : null;
            snapshot.Currency = currency?.ToBytes();
        }
        if (building.Type is BuildingTypes.Prospector or BuildingTypes.Miner)
        {
            snapshot.IsOreHut = true;
            snapshot.MineOre = building.MineOre;
            snapshot.MineProspected = building.MineProspected;
            snapshot.OreReadings = colony.OreSites.Count(s => !s.Unread);
            foreach (var ore in Manager.OreSummary(colony))
            {
                snapshot.Ores.Add(new OreView { Code = ore.Code, Best = ore.Best, Sites = ore.Sites, MinedOut = ore.MinedOut, Where = ColonyManager.DirectionFrom(building.HutPos, ore.BestPos) });
            }
        }
        if (building.Type == BuildingTypes.Quarry)
        {
            snapshot.IsQuarry = true;
            snapshot.PitInfo = Manager.QuarryInfo(building);
        }
        if (building.Type == BuildingTypes.Potter)
        {
            snapshot.IsPotterHut = true;
            snapshot.KeepChoices = Manager.Config.PotteryKeepChoices.ToList();
            var potter = Manager.WorkerAt(colony, building, Jobs.Potter);
            foreach (var kind in Manager.PotteryKinds())
            {
                snapshot.Pottery.Add(new PotteryView
                {
                    Key = kind.Key,
                    Stack = kind.Sample.ToBytes(),
                    Keep = Manager.PotteryKeep(building, kind),
                    Have = Manager.PotteryStock(colony, building, kind, potter),
                    Clay = kind.Recipes.Min(ColonyManager.ClayCost),
                    Detail = Manager.PotteryOrdered(colony, kind) is > 0 and var ordered ? Lang.Get("createacolony:pottery-ordered", ordered) : null
                });
            }
        }
        if (building.Type == BuildingTypes.Cook)
        {
            snapshot.IsCookHut = true;
            snapshot.KeepChoices = Manager.Config.KitchenKeepChoices.ToList();
            var cook = Manager.WorkerAt(colony, building, Jobs.Cook);
            Manager.UpgradeKitchenList(colony, building);
            snapshot.KitchenChosen = building.KitchenChosen;
            // Only the dishes the cook can make from the food on hand, and the ones the hut keeps (ColonyManager.KitchenMenu.cs).
            int cookLevel = cook == null ? 0 : Manager.CookLevel(colony, cook);
            foreach (var dish in Manager.KitchenMenu(colony, building, cook))
            {
                snapshot.Kitchen.Add(new PotteryView
                {
                    Key = dish.Key,
                    Name = dish.Name,
                    Stack = dish.Icon.ToBytes(),
                    Keep = dish.Keep,
                    Have = dish.Have,
                    Detail = dish.Detail,
                    NeedsLevel = dish.NeedsLevel,
                    CookLevel = System.Math.Max(0, cookLevel),
                    Locked = dish.Locked
                });
            }
            foreach (var (sample, count) in Manager.KitchenOnHand(colony, building, cook).Take(KitchenOnHandShown))
            {
                var one = sample.Clone();
                one.StackSize = 1;
                snapshot.KitchenOnHand.Add(new HutItemView { Stack = one.ToBytes(), Count = count });
            }
        }
        if (building.Type == BuildingTypes.Church) AddChurch(snapshot, colony, building);
        if (building.Type == BuildingTypes.Archive) AddResearch(snapshot, colony, building);
        if (building.IsStation)
        {
            snapshot.IsStation = true;
            snapshot.HostName = Manager.HostOf(colony, building) is { } host ? ColonyManager.BuildingName(host) : "";
        }
        AddRequests(snapshot, colony, building);
        if (building.Type == BuildingTypes.CourierStation) AddCouriers(snapshot, colony);
        if (ColonyManager.CraftTabOf(building.Type) is { } craftTab) AddCraftRows(snapshot, colony, building, craftTab);
        if (Manager.HasHutRecipes(building.Type)) AddRecipeRows(snapshot, colony, building);
        AddPriorities(snapshot, colony, building);
        if (building.Type is BuildingTypes.Miner or BuildingTypes.MineshaftEntrance) AddMineTabs(snapshot, colony, building, viewerUid);
        if (!snapshot.IsBuilderHut) return snapshot;

        foreach (var (order, teardown) in Manager.BuilderQueue(colony))
        {
            snapshot.Orders.Add(order != null ? BuildingViewFor(colony, order) : TeardownViewFor(colony, teardown!));
        }

        var builder = colony.Citizens.Find(c => c.Alive && c.JobCode == Jobs.Builder && c.WorkplacePos != null && c.WorkplacePos.Equals(building.HutPos));
        if (builder == null) return snapshot;
        snapshot.BuilderName = builder.Name;

        var current = colony.Buildings.Find(b => b.HasWorkOrder && b.BuilderCitizenId == builder.CitizenId)
            ?? colony.Buildings.Where(b => b.HasWorkOrder && b.BuilderCitizenId == 0).OrderBy(b => b.BuildingId).FirstOrDefault();
        var blueprint = current == null ? null : Manager.Blueprints.Get(current, current.TargetLevel);
        if (current == null || blueprint == null) return snapshot;
        snapshot.ResourcesFor = Lang.Get("createacolony:building-" + current.Type) + " " + current.TargetLevel;
        var view = BuildingViewFor(colony, current);
        snapshot.UsedPercent = view.ProgressPercent;
        snapshot.ResourcesStep = Lang.Get("createacolony:buildstatus-" + view.Status);

        var storage = Manager.HutStorage(colony, building).Cast<BlockEntityContainer>().ToList();
        var hand = (sapi!.World.GetEntityById(builder.EntityId) as EntityAgent)?.RightHandItemSlot?.Itemstack;

        foreach (var need in Manager.ToolsForWindow(colony, current, blueprint).OrderBy(n => n.Type))
        {
            var sample = Manager.ToolSample(need.Type);
            if (sample == null) continue;
            bool has = ColonyManager.ToolFits(hand, need.Type, need.Tier)
                || Manager.CountItems(storage, s => ColonyManager.ToolFits(s, need.Type, need.Tier)) > 0
                || Manager.PackCount(builder, s => ColonyManager.ToolFits(s, need.Type, need.Tier)) > 0;
            snapshot.Resources.Add(new ResourceView { Stack = sample.ToBytes(), Needed = 1, Have = has ? 1 : 0, IsTool = true });
        }
        foreach (var (code, (sample, count)) in Manager.MaterialsForWindow(colony, current, blueprint).OrderByDescending(kv => kv.Value.Count))
        {
            var match = ColonyManager.Matcher(code);
            int have = Manager.CountItems(storage, match) + Manager.PackCount(builder, match);
            string anyOf = AnyOfFor(code, sample);
            snapshot.Resources.Add(new ResourceView { Stack = sample.ToBytes(), Needed = count, Have = have, AnyOf = anyOf, Code = code });
        }
        AddMakers(snapshot, colony, ColonyManager.RequestTag(current));
        AddPlayerHas(snapshot, viewerUid);
        return snapshot;
    }

    // ---- Client ---------------------------------------------------------------------------

    ICoreClientAPI? capi;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        clientChannel = api.Network.GetChannel(ChannelName)
            .SetMessageHandler<ColonySnapshot>(OnSnapshot)
            .SetMessageHandler<HutSnapshot>(OnHutSnapshot)
            .SetMessageHandler<CitizenSnapshot>(OnCitizenSnapshot)
            .SetMessageHandler<PlacementSnapshot>(OnPlacementSnapshot)
            .SetMessageHandler<WandChooserSnapshot>(OnWandChooserSnapshot)
            .SetMessageHandler<GhostPreviewPacket>(OnGhostPreview)
            .SetMessageHandler<QuestBookSnapshot>(OnQuestBookSnapshot)
            .SetMessageHandler<HandPlacePacket>(p => WandOnly.ClientHandPlacing = p.On)
            .SetMessageHandler<DisplayRedrawPacket>(OnDisplayRedraw);
        StartQuestsClient(api);
    }

    /// <summary>
    /// Redraws a rack's or shelf's things a moment later, once the block entity's new contents have arrived
    /// (the packet can come first).
    /// </summary>
    void OnDisplayRedraw(DisplayRedrawPacket p)
    {
        var pos = new BlockPos(p.X, p.Y, p.Z);
        capi?.Event.RegisterCallback(_ =>
        {
            if (capi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityDisplay display) return;
            display.MarkMeshesDirty();
            capi.World.BlockAccessor.MarkBlockDirty(pos);
        }, 500);
    }

    /// <summary>Called by the Town Hall block when a player right-clicks it.</summary>
    public void RequestOpen(BlockPos pos)
    {
        clientChannel?.SendPacket(new TownHallOpenRequest { Pos = pos.Copy() });
    }

    public void SendAction(ColonyActionPacket packet) => clientChannel?.SendPacket(packet);

    public void SendSettlerSlot(SettlerSlotPacket packet) => clientChannel?.SendPacket(packet);

    /// <summary>Called by hut blocks (Builder's Hut, Warehouse) when a player right-clicks them.</summary>
    public void RequestOpenHut(BlockPos pos) => clientChannel?.SendPacket(new HutOpenRequest { Pos = pos.Copy() });

    void OnCitizenSnapshot(CitizenSnapshot snapshot)
    {
        if (citizenDialog != null && citizenDialog.IsOpened() && citizenDialog.CitizenId == snapshot.CitizenId && citizenDialog.ColonyId == snapshot.ColonyId)
        {
            citizenDialog.Update(snapshot);
            return;
        }
        if (!snapshot.OpenWindow) return;
        if (citizenDialog != null && citizenDialog.IsOpened())
        {
            citizenDialog.Replaced = true;
            citizenDialog.TryClose();
        }
        citizenDialog = new GuiDialogCitizen(capi!, this, snapshot);
        citizenDialog.TryOpen();
    }

    void OnHutSnapshot(HutSnapshot snapshot)
    {
        if (hutDialog != null && hutDialog.IsOpened() && hutDialog.BuildingId == snapshot.Building.BuildingId)
        {
            hutDialog.Update(snapshot);
            return;
        }
        if (!snapshot.OpenWindow) return;

        if (hutDialog != null && hutDialog.IsOpened())
        {
            // The server already moved us to the new hut; closing the old window must not undo that.
            hutDialog.Replaced = true;
            hutDialog.TryClose();
        }
        hutDialog = new GuiDialogHut(capi!, this, snapshot);
        hutDialog.TryOpen();
    }

    void OnSnapshot(ColonySnapshot snapshot)
    {
        if (dialog != null && dialog.IsOpened() && dialog.ColonyId == snapshot.ColonyId)
        {
            dialog.Update(snapshot);
            return;
        }
        if (!snapshot.OpenWindow) return;

        dialog = new GuiDialogTownHall(capi!, this, snapshot);
        dialog.TryOpen();
    }
}
