using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Colonies;

/// <summary>
/// Who may break the Town Hall and hut blocks. Breaking the Town Hall ends the colony and breaking a hut
/// forgets its building (level, workers, settings), so neither happens by a stray click: only the owner
/// takes the Town Hall down, officers take huts away, and both need sneaking when something would be lost.
/// Server admins may always break them.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Why this player may not break the colony block at pos now (a lang key), or null when they may.</summary>
    public string? BreakRefusal(IPlayer? player, BlockPos pos, bool townHall)
    {
        var colony = GetAt(pos);
        // Outside any colony (a block put down by hand for blueprints) it is only a block.
        if (colony == null) return null;
        var building = GetBuilding(colony, pos);
        if (townHall ? !colony.TownHallPos.Equals(pos) : building == null) return null;
        // Nobody broke it (an explosion, another mod): it stays.
        if (player == null) return "createacolony:break-protected";
        bool admin = player is IServerPlayer sp && sp.HasPrivilege(Privilege.controlserver);
        bool sneaking = player.Entity?.Controls.ShiftKey == true;
        if (townHall)
        {
            if (!admin && colony.RoleOf(player.PlayerUID) < ColonyRole.Owner) return "createacolony:break-townhall-owner";
            return sneaking ? null : "createacolony:break-townhall-sneak";
        }
        if (!admin && RoleFor(colony, player.PlayerUID, RankPerm.Build) < ColonyRole.Officer) return "createacolony:break-hut-officer";
        bool somethingToLose = building!.Level > 0 || building.HasWorkOrder;
        return somethingToLose && !sneaking ? "createacolony:break-hut-sneak" : null;
    }
}
