using System.Text;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Items;

/// <summary>
/// A dead animal picked up whole, for the butcher (ColonyManager.Butchery.cs). It holds what the animal gives
/// when skinned, and spoils like meat. Only settlers make these; a player can carry one to a Butcher's Hut.
/// </summary>
public class ItemCarcass : Item
{
    public override string GetHeldItemName(ItemStack itemStack) =>
        Lang.Get("createacolony:carcass-of", ColonyManager.CarcassAnimalName(itemStack));

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        dsc.AppendLine(Lang.Get("createacolony:carcass-info"));
    }
}
