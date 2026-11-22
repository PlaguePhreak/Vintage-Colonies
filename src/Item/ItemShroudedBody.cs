using System.Text;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Items;

/// <summary>
/// A dead settler wrapped in a shroud, carried by an undertaker to the Graveyard (ColonyManager.Graveyard.cs).
/// It remembers who it is and holds their belongings until they are put in the Graveyard's storage. Only
/// undertakers make these; one left in a Graveyard's storage is picked up and buried.
/// </summary>
public class ItemShroudedBody : Item
{
    public override string GetHeldItemName(ItemStack itemStack)
    {
        string name = itemStack.Attributes.GetString(ColonyManager.ShroudNameAttr, "");
        return name.Length > 0 ? Lang.Get("createacolony:shroudedbody-of", name) : base.GetHeldItemName(itemStack);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        dsc.AppendLine(Lang.Get("createacolony:shroudedbody-info"));
    }
}
