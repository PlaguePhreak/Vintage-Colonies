using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// A colony settler. It is built like a player: the player's seraph model with a look from the player
/// character creator ("extraskinnable"), the player's clothing and armor slots ("seraphinventory", see
/// ColonyManager.Gear.cs), and hand slots from the "villagerinventory" behavior. SettlerLooks fills in
/// the player's model, textures and skin parts when the game loads.
/// </summary>
public class EntitySettler : EntityHumanoid
{
    EntityBehaviorVillagerInv? hands;
    Colonies.ColonyManager? manager;

    /// <summary>Holding torches like a player, and the idle fidgets (see SettlerBody).</summary>
    public SettlerBody? Body { get; private set; }

    /// <summary>The tool or weapon the settler works with.</summary>
    public override ItemSlot? RightHandItemSlot => hands?.Inventory[0];
    /// <summary>The offhand item from the tool bar, or a torch in the dark.</summary>
    public override ItemSlot? LeftHandItemSlot => hands?.Inventory[1];

    /// <summary>
    /// A torch or lantern in either hand lights up the ground around the settler, as it does for a player
    /// (EntityPlayer.LightHsv). Burning, it glows as any entity on fire does.
    /// </summary>
    public override byte[]? LightHsv
    {
        get => ColorUtil.MergeLightHSV(HeldLight(RightHandItemSlot), HeldLight(LeftHandItemSlot)) ?? base.LightHsv;
        set => base.LightHsv = value;
    }

    byte[]? HeldLight(ItemSlot? slot)
    {
        if (slot?.Itemstack is not { } stack || World == null) return null;
        var hsv = stack.Collectible.GetLightHsv(World.BlockAccessor, null, stack);
        return hsv != null && hsv[2] > 0 ? hsv : null;
    }

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        hands = GetBehavior<EntityBehaviorVillagerInv>();
        if (api.Side == EnumAppSide.Server)
        {
            manager = api.ModLoader.GetModSystem<Colonies.ColonyManager>();
            Body = new SettlerBody(this, manager);
        }
    }

    /// <summary>The game calls this every tick on both sides; settlers do their hand poses and fidgets on the server.</summary>
    protected override void HandleHandAnimations(float dt)
    {
        base.HandleHandAnimations(dt);
        Body?.Tick(dt, manager?.Config);
        ResendAnimations(dt);
    }

    float resendTimer;

    /// <summary>
    /// Sends the running animations to players again every few seconds. A player who was too far away misses the
    /// changes, and a settler coming back into view would otherwise glide or stand stiff until its next change.
    /// </summary>
    void ResendAnimations(float dt)
    {
        if (World.Side != EnumAppSide.Server || AnimManager == null) return;
        resendTimer += dt;
        if (resendTimer < 3f) return;
        resendTimer = 0;
        if (AnimManager.ActiveAnimationsByAnimCode.Count > 0) AnimManager.AnimationsDirty = true;
    }

    public override void OnEntitySpawn()
    {
        base.OnEntitySpawn();
        ShowHeldItems();
    }

    public override void OnEntityLoaded()
    {
        base.OnEntityLoaded();
        // Unloaded in the middle of a meal: the food copy goes and the tool comes back.
        if (Api.Side == EnumAppSide.Server) AiTaskSettlerEat.PutDownFood(this);
        ShowHeldItems();
    }

    /// <summary>The plain shape renderer only draws held items when told to, as the game does for its player-like bots.</summary>
    void ShowHeldItems()
    {
        if (World.Side == EnumAppSide.Client && Properties.Client.Renderer is EntityShapeRenderer renderer) renderer.DoRenderHeldItem = true;
    }

    public const string ColonyIdAttr = "createacolony:colonyId";
    public const string CitizenIdAttr = "createacolony:citizenId";

    public string? ColonyId
    {
        get => WatchedAttributes.GetString(ColonyIdAttr);
        set { WatchedAttributes.SetString(ColonyIdAttr, value); WatchedAttributes.MarkPathDirty(ColonyIdAttr); }
    }

    public int CitizenId
    {
        get => WatchedAttributes.GetInt(CitizenIdAttr);
        set { WatchedAttributes.SetInt(CitizenIdAttr, value); WatchedAttributes.MarkPathDirty(CitizenIdAttr); }
    }

    /// <summary>Armor takes the blow first, and a monster striking a settler calls the guards (see ColonyManager.OnSettlerHurt).</summary>
    public override bool ReceiveDamage(DamageSource damageSource, float damage)
    {
        if (Api.Side == EnumAppSide.Server && Alive && damageSource.Type != EnumDamageType.Heal)
        {
            // Test setting (0.196.0): the colony's settlers can't be hurt.
            if (Api.ModLoader.GetModSystem<Colonies.ColonyManager>().SettlerUnhurt(this)) return false;
            damage = Api.ModLoader.GetModSystem<Colonies.ColonyManager>().OnSettlerHurt(this, damageSource, damage);
        }
        return base.ReceiveDamage(damageSource, damage);
    }

    /// <summary>Walking speed from the Fitness and Hauling skills; EntityBehaviorColonyCitizen refreshes it every few seconds.</summary>
    public float SkillWalkFactor = 1f;

    /// <summary>Walking on a Pathblock (a road) is faster, by config PathSpeedMultiplier; fit settlers walk faster everywhere.</summary>
    public override double GetWalkSpeedMultiplier(double groundDragFactor = 0.3)
    {
        double multiplier = base.GetWalkSpeedMultiplier(groundDragFactor) * SkillWalkFactor;
        return PathBlocks.SpeedMultiplier != 1f && PathBlocks.StandsOnPath(this) ? multiplier * PathBlocks.SpeedMultiplier : multiplier;
    }
}
