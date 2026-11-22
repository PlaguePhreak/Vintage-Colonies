using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Items;

/// <summary>
/// The Guard Scepter, handed out by a Guard Tower's Selection tools tab. It remembers its tower. On the
/// Guard task, right-clicking a block makes it the guards' spot; on the Patrol task, each block clicked
/// becomes the next patrol point (taking a new scepter starts the route over).
/// </summary>
public class ItemGuardScepter : Item
{
    public const string ColonyAttr = "colonyId";
    public const string TowerAttr = "towerId";

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (!firstEvent || blockSel == null || byEntity is not EntityPlayer { Player: { } player })
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            return;
        }
        handling = EnumHandHandling.PreventDefault;
        if (api.Side != EnumAppSide.Server || player is not IServerPlayer serverPlayer) return;

        var manager = api.ModLoader.GetModSystem<ColonyManager>();
        var attrs = slot.Itemstack.Attributes;
        var colony = manager.GetById(attrs.GetString(ColonyAttr, ""));
        var tower = colony?.Buildings.Find(b => b.BuildingId == attrs.GetInt(TowerAttr) && ColonyManager.IsGuardPost(b.Type));
        if (colony == null || tower == null)
        {
            Tell(serverPlayer, "createacolony:scepter-no-tower");
            return;
        }
        if (ColonyManager.RoleFor(colony, player.PlayerUID, RankPerm.Guards) < ColonyRole.Officer)
        {
            Tell(serverPlayer, "createacolony:denied-officer");
            return;
        }

        // The spot to stand on is the one above the block clicked.
        var spot = blockSel.Position.AddCopy(blockSel.Face);
        string name = ColonyManager.BuildingName(tower);
        if (tower.GuardTask == GuardTasks.Patrol)
        {
            if (tower.PatrolPoints.Count >= manager.Config.GuardMaxPatrolPoints)
            {
                Tell(serverPlayer, "createacolony:scepter-patrol-full", manager.Config.GuardMaxPatrolPoints);
                return;
            }
            tower.PatrolPoints.Add(spot);
            tower.PatrolManual = true;
            Tell(serverPlayer, "createacolony:scepter-patrol-added", tower.PatrolPoints.Count, name);
        }
        else
        {
            tower.GuardSpot = spot;
            Tell(serverPlayer, "createacolony:scepter-guard-set", name);
        }
        manager.MarkDirty(colony);
        api.ModLoader.GetModSystem<Network.ColonyNetwork>().RefreshViewers(colony);
    }

    static void Tell(IServerPlayer player, string key, params object[] args) =>
        player.SendMessage(0, Lang.GetL(player.LanguageCode, key, args), EnumChatType.Notification);

    public override void GetHeldItemInfo(ItemSlot inSlot, System.Text.StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        string tower = inSlot.Itemstack.Attributes.GetString("towerName", "");
        if (tower.Length > 0) dsc.AppendLine(Lang.Get("createacolony:scepter-for", tower));
    }
}
