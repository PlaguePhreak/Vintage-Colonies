using System.Text;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Items;

/// <summary>
/// The Rally Banner (0.178.0): right-click a Guard Tower or Barracks hut block with it to link that post (again
/// to unlink), then right-click the air to raise it and call every linked guard to you; once more to lower it.
/// The banner remembers its posts on the item, so it can be handed to another member of the colony.
/// </summary>
public class ItemRallyBanner : Item
{
    static bool IsHeldBy(IPlayer player) => ColonyManager.IsRallyBanner(player.InventoryManager.ActiveHotbarSlot?.Itemstack);

    /// <summary>Called by the hut block before it opens its window: links or unlinks a guard post. True when the banner handled the click.</summary>
    public static bool TryLink(IWorldAccessor world, IPlayer player, BlockPos pos)
    {
        if (!IsHeldBy(player)) return false;
        if (world.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer) return true;
        var manager = world.Api.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(pos);
        var post = colony == null ? null : manager.GetBuilding(colony, pos);
        if (colony == null || post == null || !ColonyManager.IsGuardPost(post.Type))
        {
            Say(serverPlayer, "createacolony:rally-not-post");
            return true;
        }
        if (!colony.CanManage(player.PlayerUID))
        {
            Say(serverPlayer, "createacolony:denied-officer");
            return true;
        }
        var slot = player.InventoryManager.ActiveHotbarSlot;
        string key = manager.ToggleRallyLink(slot.Itemstack!, colony, post);
        slot.MarkDirty();
        Say(serverPlayer, key, ColonyManager.BuildingName(post));
        return true;
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        // Pointing at a block or a creature does what it normally does; only a click into the air raises the banner.
        if (!firstEvent || blockSel != null || entitySel != null || byEntity is not EntityPlayer { Player: { } player })
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            return;
        }
        handling = EnumHandHandling.PreventDefault;
        if (api.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer) return;
        api.ModLoader.GetModSystem<ColonyManager>().ToggleRally(serverPlayer, slot.Itemstack!);
    }

    static void Say(IServerPlayer player, string key, params object[] args) =>
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.GetL(player.LanguageCode, key, args), EnumChatType.Notification);

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        string names = inSlot.Itemstack?.Attributes.GetString(ColonyManager.RallyNamesAttr, "") ?? "";
        dsc.AppendLine(names.Length > 0 ? Lang.Get("createacolony:rally-posts", names) : Lang.Get("createacolony:rally-no-links"));
    }
}
