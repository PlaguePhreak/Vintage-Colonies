using System;
using System.IO;
using System.Text;
using CreateAColony.Colonies;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Corpses;

/// <summary>
/// A corpse that holds what a player or settler had on them when they died. Hold right-click to take it all.
/// Ported from EntityPlayerCorpse in Player Corpse by DArkHekRoMaNT (MIT, see THIRD_PARTY_NOTICES.md),
/// without the CommonLib dependency, with its id kept across saves, and with settler corpses added.
/// </summary>
public class EntityColonyCorpse : EntityAgent
{
    /// <summary>Client tells the server it has held right-click for the whole collection time.</summary>
    const int HeldPacketId = 141325;

    long lastInteractMs;
    float secondsPassed;

    long LastInteractPassedMs
    {
        get => World.ElapsedMilliseconds - lastInteractMs;
        set => lastInteractMs = value;
    }

    public InventoryGeneric? Inventory { get; set; }

    public double CreationTime
    {
        get => WatchedAttributes.GetDouble("creationTime", Api.World.Calendar.TotalHours);
        set => WatchedAttributes.SetDouble("creationTime", value);
    }

    public string CreationRealDatetime
    {
        get => WatchedAttributes.GetString("creationRealDatetime", "no data");
        set => WatchedAttributes.SetString("creationRealDatetime", value);
    }

    /// <summary>The player who died, or empty for a settler.</summary>
    public string OwnerUID
    {
        get => WatchedAttributes.GetString("ownerUID", "");
        set => WatchedAttributes.SetString("ownerUID", value);
    }

    public string OwnerName
    {
        get => WatchedAttributes.GetString("ownerName", "");
        set => WatchedAttributes.SetString("ownerName", value);
    }

    /// <summary>The settler's colony, or empty for a player.</summary>
    public string ColonyId
    {
        get => WatchedAttributes.GetString("colonyId", "");
        set => WatchedAttributes.SetString("colonyId", value);
    }

    public int CitizenId
    {
        get => WatchedAttributes.GetInt("citizenId");
        set => WatchedAttributes.SetInt("citizenId", value);
    }

    /// <summary>A settler who was only knocked out leaves their things, not a corpse.</summary>
    public bool KnockedOut
    {
        get => WatchedAttributes.GetBool("knockedOut");
        set => WatchedAttributes.SetBool("knockedOut", value);
    }

    public bool IsSettler => ColonyId.Length > 0;

    /// <summary>Copied from the server config when the corpse is made, so the client knows them too.</summary>
    public float CollectionSeconds
    {
        get => WatchedAttributes.GetFloat("collectSeconds", 1);
        set => WatchedAttributes.SetFloat("collectSeconds", value);
    }

    public int FreeAfterHours
    {
        get => WatchedAttributes.GetInt("freeAfterHours", 240);
        set => WatchedAttributes.SetInt("freeAfterHours", value);
    }

    /// <summary>Links the corpse to its death waypoint. Saved, so the waypoint can still be found after a restart.</summary>
    public string CorpseId
    {
        get
        {
            string? id = WatchedAttributes.GetString("corpseId");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString();
                WatchedAttributes.SetString("corpseId", id);
            }
            return id;
        }
    }

    public bool IsFree
    {
        get
        {
            int hoursForFree = FreeAfterHours;
            if (hoursForFree == 0) return true;
            if (hoursForFree < 0) return false;
            return Api.World.Calendar.TotalHours - CreationTime > hoursForFree;
        }
    }

    CorpseSystem? Corpses => Api?.ModLoader.GetModSystem<CorpseSystem>();

    public override void OnEntityLoaded()
    {
        base.OnEntityLoaded();
        if (Inventory != null)
        {
            Inventory.Api = Api;
            Inventory.ResolveBlocksOrItems();
        }
    }

    public override bool ShouldReceiveDamage(DamageSource damageSource, float damage)
    {
        var config = Corpses?.Config;
        if (config == null) return false;
        if (!config.CanBurn && damageSource.Type == EnumDamageType.Fire) return false;
        if (!config.HasHealth) return false;
        return base.ShouldReceiveDamage(damageSource, damage);
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);

        if (LastInteractPassedMs > 300)
        {
            if (secondsPassed != 0 && Api is ICoreClientAPI) Corpses?.Ring?.Hide(this);
            secondsPassed = 0;
        }
        else
        {
            secondsPassed += dt;
            if (Api is ICoreClientAPI capi)
            {
                Corpses?.Ring?.Show(this, secondsPassed / Math.Max(0.05f, CollectionSeconds));
                if (secondsPassed > CollectionSeconds) capi.Network.SendEntityPacket(EntityId, HeldPacketId, new[] { (byte)Math.Min(255, secondsPassed) });
            }
        }

        // The owner sees sparks rising from their own corpse.
        if (Api is ICoreClientAPI client && OwnerUID.Length > 0 && OwnerUID == client.World.Player.PlayerUID && Api.World.Rand.NextDouble() < 0.3)
        {
            client.World.SpawnParticles(new SimpleParticleProperties
            {
                MinPos = Pos.XYZ,
                Color = SparkColor(Api.World.Rand),
                MinSize = 0.2f,
                MaxSize = 0.3f,
                MinVelocity = new Vec3f(-0.1f, 0.5f, -0.1f),
                AddVelocity = new Vec3f(0.2f, 1.5f, 0.2f),
                MinQuantity = 1,
                LifeLength = 1,
                WithTerrainCollision = false,
                LightEmission = ColorUtil.ToRgba(255, 255, 255, 255)
            });
        }
    }

    public override void OnReceivedClientPacket(IServerPlayer player, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(player, packetid, data);
        if (packetid == HeldPacketId && data?.Length > 0 && data[0] > secondsPassed) secondsPassed = data[0];
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (byEntity is EntityPlayer entityPlayer && World.PlayerByUid(entityPlayer.PlayerUID) is { } byPlayer)
        {
            if (!CanCollect(byPlayer))
            {
                if (byPlayer is IServerPlayer sp) sp.SendIngameError("", Lang.Get("createacolony:corpse-not-yours"));
            }
            else
            {
                if (Api.Side == EnumAppSide.Server)
                {
                    if ((Inventory == null || Inventory.Empty) && KeepsBody())
                    {
                        if (byPlayer is IServerPlayer sp) sp.SendIngameError("", Lang.Get("createacolony:corpse-for-undertaker"));
                    }
                    else if (Inventory == null || Inventory.Count == 0)
                    {
                        Corpses?.Log("{0} at {1} is empty and was removed, id {2}", GetName(), RelativePos(), EntityId);
                        Die();
                    }
                    else if (secondsPassed > CollectionSeconds)
                    {
                        Collect(byPlayer);
                    }
                }
                LastInteractPassedMs = World.ElapsedMilliseconds;
                return;
            }
        }
        base.OnInteract(byEntity, itemslot, hitPosition, mode);
    }

    /// <summary>
    /// The owner, anyone in creative, anyone once the corpse is free, and for a settler any member of their colony.
    /// The client cannot see colony membership, so it lets the hold start and the server decides.
    /// </summary>
    bool CanCollect(IPlayer byPlayer)
    {
        if (!byPlayer.Entity.Alive) return false;
        if (byPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative || IsFree) return true;
        if (!IsSettler) return byPlayer.PlayerUID == OwnerUID;
        if (Api.Side == EnumAppSide.Client) return true;
        var colony = Api.ModLoader.GetModSystem<ColonyManager>()?.GetById(ColonyId);
        // A disbanded colony's settlers belong to no one.
        return colony == null || colony.RoleOf(byPlayer.PlayerUID) >= ColonyRole.Member;
    }

    void Collect(IPlayer byPlayer)
    {
        if (Inventory != null)
        {
            foreach (var slot in Inventory)
            {
                if (slot.Empty) continue;
                if (!byPlayer.InventoryManager.TryGiveItemstack(slot.Itemstack))
                {
                    Api.World.SpawnItemEntity(slot.Itemstack, byPlayer.Entity.ServerPos.XYZ.AddCopy(0, 1, 0));
                }
                slot.Itemstack = null;
                slot.MarkDirty();
            }
        }
        Corpses?.Log("{0} at {1} was collected by {2}, id {3}", GetName(), RelativePos(), byPlayer.PlayerName, EntityId);
        Corpses?.OnCollected(this, byPlayer);
        // With a Graveyard the body stays where it lies, for the undertaker.
        if (KeepsBody()) return;
        Die();
    }

    /// <summary>A dead settler's body, in a colony with a Graveyard: only the undertaker takes it away (ColonyManager.Graveyard.cs).</summary>
    bool KeepsBody() => IsSettler && !KnockedOut && Api.Side == EnumAppSide.Server
        && Api.ModLoader.GetModSystem<ColonyManager>()?.KeepsBodies(ColonyId) == true;

    public override void Die(EnumDespawnReason reason = EnumDespawnReason.Death, DamageSource? damageSourceForDeath = null)
    {
        // Broken (or burnt): what it held falls out.
        if (reason == EnumDespawnReason.Death && Inventory != null)
        {
            Inventory.Api = Api;
            Inventory.DropAll(SidedPos.XYZ.AddCopy(0, 1, 0));
        }
        if (Api.Side == EnumAppSide.Server) Corpses?.Log("{0} at {1} was removed, id {2}", GetName(), RelativePos(), EntityId);
        // A settler's body gone some other way than with the undertaker: there is nothing left to bury.
        if (Api.Side == EnumAppSide.Server && IsSettler && !KnockedOut && !WatchedAttributes.GetBool(ColonyManager.UndertakenAttr))
        {
            Api.ModLoader.GetModSystem<ColonyManager>()?.OnBodyGone(ColonyId, CitizenId);
        }
        base.Die(reason, damageSourceForDeath);
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        base.OnEntityDespawn(despawn);
        if (Api is ICoreClientAPI) Corpses?.Ring?.Hide(this);
    }

    public override void ToBytes(BinaryWriter writer, bool forClient)
    {
        if (Inventory != null && Inventory.Count > 0 && WatchedAttributes != null)
        {
            WatchedAttributes.SetString("invid", Inventory.InventoryID);
            Inventory.ToTreeAttributes(WatchedAttributes);
        }
        base.ToBytes(writer, forClient);
    }

    public override void FromBytes(BinaryReader reader, bool forClient)
    {
        base.FromBytes(reader, forClient);
        if (WatchedAttributes == null) return;
        string inventoryId = WatchedAttributes.GetString("invid");
        int qslots = WatchedAttributes.GetInt("qslots", 0);
        Inventory = new InventoryGeneric(qslots, inventoryId, Api);
        Inventory.FromTreeAttributes(WatchedAttributes);
        if (Api != null) Inventory.ResolveBlocksOrItems();
    }

    public override string GetName() => Lang.Get(KnockedOut ? "createacolony:corpse-things-name" : "createacolony:corpse-name", OwnerName);

    public override string GetInfoText()
    {
        var sb = new StringBuilder();
        sb.Append(base.GetInfoText());
        sb.AppendLine(Lang.Get("createacolony:corpse-created", CreationRealDatetime));
        if (IsFree) sb.AppendLine(Lang.Get("createacolony:corpse-free"));
        else if (IsSettler) sb.AppendLine(Lang.Get("createacolony:corpse-colony"));
        return sb.ToString();
    }

    public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player)
    {
        return new[] { new WorldInteraction { ActionLangCode = "createacolony:corpse-collect", MouseButton = EnumMouseButton.Right } };
    }

    /// <summary>Where the corpse is, as the player's coordinates show it (relative to the world spawn).</summary>
    public string RelativePos()
    {
        var spawn = Api.World.DefaultSpawnPosition?.XYZ ?? new Vec3d();
        var pos = SidedPos.XYZ;
        return $"{(int)(pos.X - spawn.X)}, {(int)pos.Y}, {(int)(pos.Z - spawn.Z)}";
    }

    static int SparkColor(Random rand) => ColorUtil.ToRgba(255, rand.Next(200, 256), rand.Next(100, 156), rand.Next(0, 56));
}
