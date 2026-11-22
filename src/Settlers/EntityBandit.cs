using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// An outlaw on a raid (0.181.0). Built like a settler (the player's model with a random look, clothes slots
/// and hand slots) but it is no colony citizen: AiTaskBanditRaid makes it march on the Town Hall, fight
/// settlers and players, rob chests and light grass with its torch. ColonyManager.Raids spawns and dresses it.
/// </summary>
public class EntityBandit : EntityHumanoid
{
    EntityBehaviorVillagerInv? hands;

    public override ItemSlot? RightHandItemSlot => hands?.Inventory[0];
    public override ItemSlot? LeftHandItemSlot => hands?.Inventory[1];

    /// <summary>Carries a bow instead of a melee weapon.</summary>
    public bool Archer;
    public int ArrowsLeft;
    /// <summary>Carries a lit torch, to set grass on fire.</summary>
    public bool CarriesTorch;
    /// <summary>Took something from a chest and is running off with it.</summary>
    public bool HasLoot;

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
    }

    public override void OnEntitySpawn()
    {
        base.OnEntitySpawn();
        ShowHeldItems();
    }

    public override void OnEntityLoaded()
    {
        base.OnEntityLoaded();
        ShowHeldItems();
    }

    void ShowHeldItems()
    {
        if (World.Side == EnumAppSide.Client && Properties.Client.Renderer is EntityShapeRenderer renderer) renderer.DoRenderHeldItem = true;
    }
}
