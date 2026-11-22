using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace CreateAColony.Colonies;

/// <summary>
/// Enter and leave messages (0.185.0). A player walking into a colony's land sees "You enter (name)" on screen, and
/// "You leave (name)" on the way out, or the colony's own welcome and goodbye text. An officer can turn the messages off
/// on the Town Hall's Settings tab, and have members told in chat when an outsider walks in. A player who does not want
/// them turns them off for themselves with /colony messages off.
/// </summary>
public partial class ColonyManager
{
    const int EnterTickMs = 1000;
    const string NoMessagesKey = "createacolony:nomessages";

    /// <summary>The colony each online player stood in at the last check ("" for none).</summary>
    readonly Dictionary<string, string> playerIn = new();

    void StartEnterMessages()
    {
        sapi.Event.RegisterGameTickListener(OnEnterTick, EnterTickMs, 2000);
        sapi.Event.PlayerDisconnect += player => playerIn.Remove(player.PlayerUID);
    }

    /// <summary>Whether this player turned the messages off for themselves.</summary>
    public static bool MessagesOff(IServerPlayer player) =>
        player.ServerData?.CustomPlayerData != null && player.ServerData.CustomPlayerData.TryGetValue(NoMessagesKey, out var v) && v == "1";

    public static void SetMessagesOff(IServerPlayer player, bool off)
    {
        if (player.ServerData?.CustomPlayerData == null) return;
        if (off) player.ServerData.CustomPlayerData[NoMessagesKey] = "1";
        else player.ServerData.CustomPlayerData.Remove(NoMessagesKey);
    }

    void OnEnterTick(float dt)
    {
        foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (player.ConnectionState != EnumClientState.Playing || player.Entity == null) continue;
            var here = GetAt(player.Entity.Pos.AsBlockPos);
            string now = here?.ColonyId ?? "";
            if (!playerIn.TryGetValue(player.PlayerUID, out var before))
            {
                // Just joined the game: no message for where they logged in.
                playerIn[player.PlayerUID] = now;
                continue;
            }
            if (before == now) continue;
            playerIn[player.PlayerUID] = now;

            if (before.Length > 0 && GetById(before) is { } left && left.EnterMessages) Leaving(player, left);
            if (here != null && here.EnterMessages) Entering(player, here);
        }
    }

    void Entering(IServerPlayer player, Colony colony)
    {
        if (!MessagesOff(player))
        {
            string text = colony.EnterText.Length > 0 ? colony.EnterText : Lang.GetL(player.LanguageCode, "createacolony:enter-colony", colony.Name);
            sapi.SendIngameDiscovery(player, "createacolony-enter", text);
        }
        if (colony.TellOutsiders && colony.RoleOf(player.PlayerUID) < ColonyRole.Member)
            TellMembers(colony, "createacolony:outsider-entered", player.PlayerName, colony.Name);
    }

    void Leaving(IServerPlayer player, Colony colony)
    {
        if (MessagesOff(player)) return;
        string text = colony.LeaveText.Length > 0 ? colony.LeaveText : Lang.GetL(player.LanguageCode, "createacolony:leave-colony", colony.Name);
        sapi.SendIngameDiscovery(player, "createacolony-leave", text);
    }
}
