using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Items;

/// <summary>
/// Field notes (0.180.0): a book an Explorer writes on a field trip (ColonyManager.FieldTrips.cs). Its attributes
/// say what place it is about and what it gives when handed in at an Archive.
/// </summary>
public class ItemFieldNotes : Item
{
    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var attrs = inSlot.Itemstack?.Attributes;
        int points = attrs?.GetInt("fieldPoints") ?? 0;
        int tokens = attrs?.GetInt("fieldTokens") ?? 0;
        string title = attrs?.GetString("fieldTitle") ?? "";
        if (points <= 0 && tokens <= 0) dsc.AppendLine(Lang.Get("createacolony:fieldnotes-info-blank"));
        else dsc.AppendLine(Lang.Get(tokens > 0 ? "createacolony:fieldnotes-info-tokens" : "createacolony:fieldnotes-info", title, points));
    }
}
