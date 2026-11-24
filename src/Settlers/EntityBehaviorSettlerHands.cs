using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// The settler's hand slots (the game's villager inventory). The game draws what a hand holds on its own, but its
/// container code also tried to hang every held tool on the body as if it were clothing, which wrote hundreds of
/// "Will not be visible" warnings in Aaron's play-test logs (bows, falxes, hoes, prospecting picks, crocks) and
/// drew nothing. Held things are not clothing, so that pass is skipped (0.198.0).
/// </summary>
public class EntityBehaviorSettlerHands : EntityBehaviorVillagerInv
{
    public EntityBehaviorSettlerHands(Entity entity) : base(entity) { }

    protected override bool ShouldAddGearToShape(ItemSlot slot) => false;
}
