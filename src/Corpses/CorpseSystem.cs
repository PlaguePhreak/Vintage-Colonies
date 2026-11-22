using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CreateAColony.Colonies;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CreateAColony.Corpses;

/// <summary>
/// Corpses for players and settlers. A dead player's things go into a corpse instead of spilling on the ground,
/// with a death waypoint and a copy on disk for /returnthings. Settlers use the same corpse (see SpawnSettlerCorpse).
/// Ported from DeathContentManager, Commands and Core in Player Corpse by DArkHekRoMaNT (MIT, see THIRD_PARTY_NOTICES.md).
/// </summary>
public class CorpseSystem : ModSystem
{
    public const string EntityCode = "createacolony:corpse";
    /// <summary>The original mod. When it is installed it makes the player corpses, and this system only makes settler ones.</summary>
    public const string PlayerCorpseModId = "playercorpse";

    static readonly MethodInfo? ResendWaypointsMethod = AccessTools.Method(typeof(WaypointMapLayer), "ResendWaypoints");

    ICoreServerAPI? sapi;
    bool playerCorpses;
    bool createWaypoint;

    /// <summary>Server only; the client gets what it needs from each corpse's attributes.</summary>
    public CorpseConfig? Config { get; private set; }
    /// <summary>Client only: the progress ring shown while collecting.</summary>
    public CorpseRing? Ring { get; private set; }

    public override void Start(ICoreAPI api)
    {
        api.RegisterEntity("EntityColonyCorpse", typeof(EntityColonyCorpse));
        api.RegisterItemClass("ItemCorpseCompass", typeof(ItemCorpseCompass));

        if (api is ICoreServerAPI server)
        {
            // Loaded here, before the assets, so the compass patch can read CorpseCompassEnabled.
            try
            {
                Config = server.LoadModConfig<CorpseConfig>(CorpseConfig.FileName) ?? new CorpseConfig();
            }
            catch (Exception e)
            {
                Mod.Logger.Error("Bad {0}, using defaults: {1}", CorpseConfig.FileName, e.Message);
                Config = new CorpseConfig();
            }
            server.StoreModConfig(Config, CorpseConfig.FileName);
            api.World.Config.SetBool("createacolony:CorpseCompassEnabled", Config.CorpseCompassEnabled);
        }
    }

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (Config == null) return;
        playerCorpses = Config.PlayerCorpses && !api.ModLoader.IsModEnabled(PlayerCorpseModId);
        createWaypoint = Config.CreateWaypoint.Equals("Always", StringComparison.OrdinalIgnoreCase);
        if (Config.CreateWaypoint.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            // Leave the death waypoint to a mod that adds one already.
            var others = api.Assets.TryGet(new AssetLocation("createacolony:config/hasdeathwaypointsmods.json"))?.ToObject<string[]>() ?? Array.Empty<string>();
            createWaypoint = !others.Any(api.ModLoader.IsModEnabled);
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.OnEntityDeath += OnEntityDeath;
        if (!playerCorpses) return;

        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands
            .Create("returnthings")
            .RequiresPrivilege(Config!.NeedPrivilegeForReturnThings)
            .WithDescription("Returns things lost at the last death")
            .BeginSubCommand("list")
                .WithArgs(parsers.PlayerUids("player"))
                .HandleWith(ShowDeathList)
            .EndSubCommand()
            .BeginSubCommand("get")
                .WithArgs(parsers.PlayerUids("player"), parsers.OnlinePlayer("give to player"), parsers.OptionalInt("id", 0))
                .HandleWith(ReturnThings)
            .EndSubCommand();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        Ring = new CorpseRing(api);
    }

    public override void Dispose()
    {
        Ring?.Dispose();
        if (sapi != null) sapi.Event.OnEntityDeath -= OnEntityDeath;
    }

    public void Log(string format, params object[] args)
    {
        string message = string.Format(format, args);
        Mod.Logger.Notification(message);
        if (Config?.DebugMode == true) sapi?.SendMessageToGroup(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
    }

    // ---- Players ----------------------------------------------------------------------------

    void OnEntityDeath(Entity entity, DamageSource damageSource)
    {
        if (!playerCorpses || entity is not EntityPlayer { Player: IServerPlayer player }) return;
        if (player.Entity?.Properties?.Server?.Attributes?.GetBool("keepContents") ?? false) return;
        // The game drops a dead player's things one frame later, to catch anything handed back in the same frame
        // (a meal's bowl). Queued now, this runs just before that and takes everything first.
        sapi!.Event.EnqueueMainThreadTask(() => OnPlayerDeath(player), "createacolony-playercorpse");
    }

    void OnPlayerDeath(IServerPlayer player)
    {
        if (player.Entity == null) return;
        var corpse = CreateCorpse(player.Entity.ServerPos.AsBlockPos, player.PlayerName);
        if (corpse == null) return;
        corpse.OwnerUID = player.PlayerUID;
        corpse.Inventory = TakeContentFromPlayer(player);

        if (corpse.Inventory.Empty)
        {
            Log("Inventory is empty, {0}'s corpse not created", player.PlayerName);
            return;
        }
        if (createWaypoint) CreateDeathPoint(player, corpse);
        if (Config!.MaxDeathContentSavedPerPlayer > 0) SaveDeathContent(corpse.Inventory, player);
        sapi!.World.SpawnEntity(corpse);
        Log("Created {0} at {1}, id {2}", corpse.GetName(), corpse.RelativePos(), corpse.EntityId);
    }

    InventoryGeneric TakeContentFromPlayer(IServerPlayer player)
    {
        var config = Config!;
        int slots = config.SaveInventoryTypes.Sum(name => player.InventoryManager.GetOwnInventory(name)?.Count ?? 0);
        var inv = new InventoryGeneric(slots, $"playercorpse-{player.PlayerUID}", sapi);

        bool vanillaDropsArmor = player.Entity.Properties.Server?.Attributes?.GetBool("dropArmorOnDeath") ?? false;
        bool dropArmor = vanillaDropsArmor || !config.DropArmorOnDeath.Equals("Vanilla", StringComparison.OrdinalIgnoreCase);
        bool dropCloth = config.DropArmorOnDeath.Equals("ArmorAndCloth", StringComparison.OrdinalIgnoreCase);

        int next = 0;
        foreach (string invClassName in config.SaveInventoryTypes)
        {
            // Armour stays on unless it is set to drop.
            if (invClassName == GlobalConstants.characterInvClassName && !dropArmor) continue;
            var own = player.InventoryManager.GetOwnInventory(invClassName);
            if (own == null) continue;

            // XSkills adds slots to the end of the backpack inventory that are not really backpack slots.
            int count = own.Count;
            if (invClassName == GlobalConstants.backpackInvClassName && player.InventoryManager.GetOwnInventory("xskillshotbar") != null) count = Math.Max(0, count - 3);

            for (int i = 0; i < count && next < inv.Count; i++)
            {
                if (own[i] is { } slot) inv[next].Itemstack = TakeSlotContent(slot, dropCloth);
                next++;
            }
        }
        return inv;
    }

    static ItemStack? TakeSlotContent(ItemSlot slot, bool dropCloth)
    {
        if (slot.Empty) return null;
        // Clothes (not armour) stay on unless set to drop.
        if (slot.Inventory.ClassName == GlobalConstants.characterInvClassName)
        {
            bool isArmor = slot.Itemstack.ItemAttributes?["protectionModifiers"].Exists ?? false;
            if (!isArmor && !dropCloth) return null;
        }
        return slot.TakeOutWhole();
    }

    void CreateDeathPoint(IServerPlayer player, EntityColonyCorpse corpse)
    {
        if (GetMapLayer() is not { } mapLayer)
        {
            Mod.Logger.Error("Failed to create waypoint, map layer is null");
            return;
        }
        int color;
        try { color = ColorTranslator.FromHtml(Config!.WaypointColor).ToArgb(); }
        catch { color = Color.Crimson.ToArgb(); }

        mapLayer.AddWaypoint(new Waypoint
        {
            Position = player.Entity.ServerPos.AsBlockPos.ToVec3d(),
            Title = Lang.Get("createacolony:death-waypoint-name", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            Pinned = Config!.PinWaypoint,
            Icon = Config.WaypointIcon,
            Color = color,
            OwningPlayerUid = player.PlayerUID,
            Guid = corpse.CorpseId
        }, player);
    }

    void RemoveDeathPoint(IServerPlayer player, EntityColonyCorpse corpse)
    {
        if (GetMapLayer() is not { } mapLayer) return;
        int removed = mapLayer.Waypoints.RemoveAll(w => w.OwningPlayerUid == corpse.OwnerUID && w.Guid == corpse.CorpseId);
        if (removed == 0) return;
        // Tell the corpse's owner, who may not be the one who picked it up.
        var owner = sapi!.World.PlayerByUid(corpse.OwnerUID) as IServerPlayer ?? player;
        if (owner.ConnectionState == EnumClientState.Playing) ResendWaypointsMethod?.Invoke(mapLayer, new object[] { owner });
    }

    WaypointMapLayer? GetMapLayer() => sapi!.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers.OfType<WaypointMapLayer>().FirstOrDefault();

    /// <summary>Called by the corpse when someone takes everything from it.</summary>
    public void OnCollected(EntityColonyCorpse corpse, IPlayer byPlayer)
    {
        if (sapi == null) return;
        if (!corpse.IsSettler)
        {
            if (Config!.RemoveWaypointOnCollect && byPlayer is IServerPlayer sp) RemoveDeathPoint(sp, corpse);
            return;
        }
        // The marker for a knocked-out settler says their things lie there; once taken, it goes.
        // A death's RIP marker stays.
        if (corpse.KnockedOut && sapi.ModLoader.GetModSystem<ColonyManager>() is { } manager && manager.GetById(corpse.ColonyId) is { } colony)
        {
            manager.ClearFallen(colony, corpse.CitizenId, corpse.ServerPos.XYZ);
        }
    }

    // ---- Settlers ---------------------------------------------------------------------------

    /// <summary>
    /// Puts a dead or knocked-out settler's things into a corpse where they fell. Returns where the corpse lies,
    /// or null when settler corpses are off or there is nothing to hold (the caller then drops the things as before).
    /// </summary>
    public Vec3d? SpawnSettlerCorpse(Entity settler, Colony colony, CitizenRecord citizen, List<ItemStack> things, bool knockedOut)
    {
        if (sapi == null || Config?.SettlerCorpses != true) return null;
        var manager = sapi.ModLoader.GetModSystem<ColonyManager>();
        // With a Graveyard a settler who died always leaves a body, for the undertaker to bury.
        bool body = !knockedOut && manager != null && manager.HasGraveyard(colony);
        if (things.Count == 0 && !body) return null;
        var corpse = CreateCorpse(settler.ServerPos.AsBlockPos, citizen.Name);
        if (corpse == null) return null;
        corpse.ColonyId = colony.ColonyId;
        corpse.CitizenId = citizen.CitizenId;
        corpse.KnockedOut = knockedOut;
        var inv = new InventoryGeneric(things.Count, $"settlercorpse-{colony.ColonyId}-{citizen.CitizenId}-{corpse.CorpseId}", sapi);
        for (int i = 0; i < things.Count; i++) inv[i].Itemstack = things[i];
        corpse.Inventory = inv;
        sapi.World.SpawnEntity(corpse);
        Log("Created {0} at {1}, id {2}", corpse.GetName(), corpse.RelativePos(), corpse.EntityId);
        if (!knockedOut) manager?.OnBodyMade(colony, citizen);
        return corpse.ServerPos.XYZ;
    }

    // ---- Shared -----------------------------------------------------------------------------

    EntityColonyCorpse? CreateCorpse(BlockPos deathPos, string ownerName)
    {
        var type = sapi!.World.GetEntityType(new AssetLocation(EntityCode));
        if (type == null || sapi.World.ClassRegistry.CreateEntity(type) is not EntityColonyCorpse corpse)
        {
            Mod.Logger.Error("Unable to make a corpse: entity type {0} is missing", EntityCode);
            return null;
        }
        corpse.OwnerName = ownerName;
        corpse.CreationTime = sapi.World.Calendar.TotalHours;
        corpse.CreationRealDatetime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        corpse.CollectionSeconds = Config!.CorpseCollectionTime;
        corpse.FreeAfterHours = Config.FreeCorpseAfterTime;
        _ = corpse.CorpseId;

        // Lie on the floor, centred on the block, so the corpse does not jitter or climb.
        var pos = FindFloor(deathPos).ToVec3d().Add(.5, 0, .5);
        corpse.ServerPos.SetPos(pos);
        corpse.Pos.SetPos(pos);
        corpse.World = sapi.World;
        return corpse;
    }

    /// <summary>The nearest spot above a block with collision, at or below pos.</summary>
    BlockPos FindFloor(BlockPos pos)
    {
        var floorPos = new BlockPos(pos.dimension);
        for (int y = pos.Y; y > 0; y--)
        {
            floorPos.Set(pos.X, y, pos.Z);
            var block = sapi!.World.BlockAccessor.GetBlock(floorPos);
            if (block.BlockId != 0 && block.CollisionBoxes?.Length > 0)
            {
                floorPos.Set(pos.X, y + 1, pos.Z);
                return floorPos;
            }
        }
        return pos;
    }

    // ---- /returnthings ----------------------------------------------------------------------

    string GetDeathDataPath(string playerUid)
    {
        string uidFixed = Regex.Replace(playerUid, "[^0-9a-zA-Z]", "");
        string worldId = Regex.Replace(sapi!.World.SavegameIdentifier ?? "world", "[^0-9a-zA-Z-]", "");
        return sapi.GetOrCreateDataPath(Path.Combine("ModData", worldId, "createacolony-corpses", uidFixed));
    }

    string[] GetDeathDataFiles(string playerUid)
    {
        return Directory.GetFiles(GetDeathDataPath(playerUid)).OrderByDescending(f => new FileInfo(f).Name).ToArray();
    }

    void SaveDeathContent(InventoryGeneric inventory, IPlayer player)
    {
        string path = GetDeathDataPath(player.PlayerUID);
        string[] files = GetDeathDataFiles(player.PlayerUID);
        for (int i = files.Length - 1; i > Config!.MaxDeathContentSavedPerPlayer - 2; i--)
        {
            File.Delete(files[i]);
        }
        var tree = new TreeAttribute();
        inventory.ToTreeAttributes(tree);
        File.WriteAllBytes(Path.Combine(path, $"inventory-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.dat"), tree.ToBytes());
    }

    InventoryGeneric LoadDeathContent(string playerUid, string file)
    {
        var tree = new TreeAttribute();
        tree.FromBytes(File.ReadAllBytes(file));
        var inv = new InventoryGeneric(tree.GetInt("qslots"), $"playercorpse-{playerUid}", sapi);
        inv.FromTreeAttributes(tree);
        return inv;
    }

    TextCommandResult ShowDeathList(TextCommandCallingArgs args)
    {
        if (args[0] is not PlayerUidName[] { Length: 1 } players) return TextCommandResult.Error(Lang.Get("Name one player"));
        string[] files = GetDeathDataFiles(players[0].Uid);
        if (files.Length == 0) return TextCommandResult.Error(Lang.Get("No data saved"));
        var sb = new StringBuilder();
        for (int i = 0; i < files.Length; i++) sb.AppendLine($"{i}. {Path.GetFileName(files[i])}");
        return TextCommandResult.Success(sb.ToString());
    }

    TextCommandResult ReturnThings(TextCommandCallingArgs args)
    {
        if (args[0] is not PlayerUidName[] { Length: 1 } players) return TextCommandResult.Error(Lang.Get("Name one player"));
        var player = players[0];
        var giveTo = (IPlayer)args[1];
        int id = (int)args[2];
        string[] files = GetDeathDataFiles(player.Uid);

        if (!sapi!.World.AllOnlinePlayers.Contains(giveTo) || giveTo.Entity == null)
        {
            return TextCommandResult.Error(Lang.Get("Player {0} is offline or not fully loaded.", giveTo.PlayerName));
        }
        if (id < 0 || files.Length <= id) return TextCommandResult.Error(Lang.Get("Index {0} not found", id));

        foreach (var slot in LoadDeathContent(player.Uid, files[id]))
        {
            if (slot.Empty) continue;
            if (!giveTo.InventoryManager.TryGiveItemstack(slot.Itemstack))
            {
                sapi.World.SpawnItemEntity(slot.Itemstack, giveTo.Entity.ServerPos.XYZ.AddCopy(0, 1, 0));
            }
            slot.Itemstack = null;
            slot.MarkDirty();
        }
        return TextCommandResult.Success(Lang.Get("Returned things from {0} to {1} with index {2}", player.Name, giveTo.PlayerName, id));
    }
}
