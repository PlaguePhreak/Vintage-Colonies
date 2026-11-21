using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using CreateAColony.Gui;
using CreateAColony.Quests;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace CreateAColony.Network;

/// <summary>
/// The quest book: the client asks for it (hotkey, Colony Ledger, the inventory button or the Town
/// Hall's Quests tab), the server answers with where every quest stands for that player, and every
/// change comes back as a fresh snapshot. Open books refresh every few seconds, since most quests
/// complete without anyone clicking.
/// </summary>
public partial class ColonyNetwork
{
    public const string QuestHotkey = "createacolony-questbook";

    // Who has the quest book open.
    readonly HashSet<string> questViewers = new();
    GuiDialogQuestBook? questDialog;
    GuiDialogQuestButton? questButton;
    GuiDialogQuestTracker? questTracker;
    QuestLibrary? clientQuests;

    /// <summary>The quest book's contents on the client, read from the same files as the server's.</summary>
    public QuestLibrary ClientQuests => clientQuests ??= QuestLibrary.Load(capi!);

    // ---- Server ---------------------------------------------------------------------------

    void StartQuestsServer(ICoreServerAPI api)
    {
        // A book edited in game is sent to each player as they join; the mod's own book they already have.
        api.Event.PlayerNowPlaying += player =>
        {
            if (Manager.Quests.Edited) serverChannel!.SendPacket(new QuestLibraryPacket { Groups = Manager.Quests.ToJson() }, player);
        };
        api.Event.RegisterGameTickListener(_ =>
        {
            foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.ConnectionState != EnumClientState.Playing) continue;
                Manager.NotifyReadyQuests(player);
                // Open books refresh, and so does the tracked-quests panel on screen.
                if (questViewers.Contains(player.PlayerUID) || Manager.QuestsOf(player.PlayerUID).Tracked.Count > 0)
                {
                    SendQuestBook(player, openWindow: false, periodic: true);
                }
            }
        }, 4000, 2000);
    }

    void OnQuestBookOpen(IServerPlayer player, QuestBookOpenRequest req) => OpenQuestBook(player);

    /// <summary>Opens the quest book for a player, wherever they are (the Colony Ledger uses this).</summary>
    public void OpenQuestBook(IServerPlayer player)
    {
        questViewers.Add(player.PlayerUID);
        SendQuestBook(player, openWindow: true, periodic: false);
    }

    void SendQuestBook(IServerPlayer player, bool openWindow, bool periodic)
    {
        var colony = Manager.HomeColony(player.PlayerUID);
        var snapshot = new QuestBookSnapshot
        {
            ColonyId = colony?.ColonyId ?? "",
            ColonyName = colony?.Name ?? "",
            ViewerRole = colony == null ? 0 : (int)colony.RoleOf(player.PlayerUID),
            OpenWindow = openWindow,
            CanEdit = player.HasPrivilege(Privilege.controlserver),
            Periodic = periodic,
            Quests = QuestStates(player, colony)
        };
        serverChannel!.SendPacket(snapshot, player);
    }

    /// <summary>Where every quest stands for a player, as the quest book and the Town Hall's Quests tab show it.</summary>
    List<QuestStateView> QuestStates(IServerPlayer player, Colony? colony)
    {
        var tracked = Manager.QuestsOf(player.PlayerUID).Tracked;
        var states = new List<QuestStateView>();
        foreach (var q in Manager.Quests.All)
        {
            var status = Manager.Evaluate(player, colony, q, out var progress, out var needed);
            states.Add(new QuestStateView
            {
                Code = q.Code,
                Status = (int)status,
                Progress = progress.ToList(),
                Needed = needed.ToList(),
                Tracked = tracked.Contains(q.Code)
            });
        }
        return states;
    }

    void OnQuestAction(IServerPlayer player, QuestActionPacket packet)
    {
        if (packet.Action == QuestAction.CloseWindow)
        {
            questViewers.Remove(player.PlayerUID);
            return;
        }
        var colony = Manager.HomeColony(player.PlayerUID);
        var quest = Manager.Quests.Get(packet.Code);
        string? error = packet.Action switch
        {
            QuestAction.Accept when quest != null => Manager.AcceptQuest(player, colony, quest),
            QuestAction.Claim when quest != null => Manager.ClaimQuest(player, colony, quest),
            QuestAction.Drop when quest != null => Manager.DropQuest(player, colony, quest),
            QuestAction.Check when quest != null => Manager.CheckQuest(player, colony, quest),
            QuestAction.Track when quest != null => Track(player, quest),
            QuestAction.Refresh => null,
            _ => "createacolony:quest-unknown"
        };
        if (error != null) player.SendIngameError("createacolony-quest", Lang.GetL(player.LanguageCode, error));
        if (questViewers.Contains(player.PlayerUID) || packet.Action == QuestAction.Track) SendQuestBook(player, openWindow: false, periodic: false);
        // Colony quests show on the Town Hall's Quests tab too.
        if (colony != null && quest?.PerColony == true) RefreshViewers(colony);
    }

    /// <summary>The quest editor saved the book, or asked for the mod's own back. Admins only.</summary>
    void OnQuestEdit(IServerPlayer player, QuestEditPacket packet)
    {
        if (!player.HasPrivilege(Privilege.controlserver))
        {
            player.SendIngameError("createacolony-quest", Lang.GetL(player.LanguageCode, "createacolony:questedit-denied"));
            return;
        }
        try
        {
            string folder = QuestLibrary.OverrideFolder(sapi!);
            if (!packet.Reset)
            {
                var check = QuestLibrary.FromJson(sapi!, packet.Groups);
                if (check.Groups.Count == 0 || check.Groups.Count != packet.Groups.Count) throw new Exception("the book could not be read");
            }
            if (System.IO.Directory.Exists(folder))
            {
                foreach (string old in System.IO.Directory.GetFiles(folder, "*.json")) System.IO.File.Delete(old);
            }
            if (!packet.Reset)
            {
                System.IO.Directory.CreateDirectory(folder);
                var saved = QuestLibrary.FromJson(sapi!, packet.Groups);
                var texts = saved.ToJson();
                for (int i = 0; i < saved.Groups.Count; i++)
                {
                    string name = new string(saved.Groups[i].Code.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
                    System.IO.File.WriteAllText(System.IO.Path.Combine(folder, (name.Length > 0 ? name : "group" + i) + ".json"), texts[i]);
                }
            }
        }
        catch (Exception e)
        {
            sapi!.Logger.Error("[createacolony] Saving the quest book failed: {0}", e);
            player.SendIngameError("createacolony-quest", Lang.GetL(player.LanguageCode, "createacolony:questedit-failed", e.Message));
            return;
        }
        Manager.ReloadQuests();
        var book = new QuestLibraryPacket { Groups = Manager.Quests.ToJson() };
        foreach (var online in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (online.ConnectionState != EnumClientState.Playing) continue;
            serverChannel!.SendPacket(book, online);
            if (questViewers.Contains(online.PlayerUID)) SendQuestBook(online, openWindow: false, periodic: false);
        }
        player.SendMessage(0, Lang.GetL(player.LanguageCode, packet.Reset ? "createacolony:questedit-reset-done" : "createacolony:questedit-saved"), EnumChatType.Notification);
    }

    string? Track(IServerPlayer player, QuestDef quest)
    {
        Manager.ToggleTracked(player, quest);
        return null;
    }

    // ---- Client ---------------------------------------------------------------------------

    void StartQuestsClient(ICoreClientAPI api)
    {
        api.Input.RegisterHotKey(QuestHotkey, Lang.Get("createacolony:hotkey-questbook"), GlKeys.L, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(QuestHotkey, _ =>
        {
            if (questDialog != null && questDialog.IsOpened()) questDialog.TryClose();
            else RequestQuestBook();
            return true;
        });
        // A small book button beside the inventory window while it is open.
        api.Event.RegisterGameTickListener(_ => KeepQuestButton(api), 250);
    }

    /// <summary>Asks the server for the quest book; it answers with a snapshot that opens the window.</summary>
    public void RequestQuestBook() => clientChannel?.SendPacket(new QuestBookOpenRequest());

    public void SendQuestAction(string code, QuestAction action) => clientChannel?.SendPacket(new QuestActionPacket { Code = code, Action = action });

    /// <summary>The server's quest book replaces the one read from the mod's files.</summary>
    void OnQuestLibrary(QuestLibraryPacket packet)
    {
        clientQuests = QuestLibrary.FromJson(capi!, packet.Groups);
        if (questDialog != null && questDialog.IsOpened())
        {
            questDialog.TryClose();
            questDialog = null;
            RequestQuestBook();
        }
    }

    /// <summary>The editor threw its changes away: back to the book as it was.</summary>
    public void SetClientQuests(List<string> groups) => OnQuestLibrary(new QuestLibraryPacket { Groups = groups });

    public void SendQuestEdit(List<string> groups, bool reset) => clientChannel!.SendPacket(new QuestEditPacket { Groups = groups, Reset = reset });

    void OnQuestBookSnapshot(QuestBookSnapshot snapshot)
    {
        questTracker ??= new GuiDialogQuestTracker(capi!, this);
        questTracker.Update(snapshot);
        if (questDialog != null && questDialog.IsOpened())
        {
            questDialog.Update(snapshot);
            return;
        }
        if (!snapshot.OpenWindow) return;
        questDialog = new GuiDialogQuestBook(capi!, this, snapshot);
        questDialog.TryOpen();
    }

    /// <summary>Shows the quest book button while the player's inventory is open, and hides it after.</summary>
    void KeepQuestButton(ICoreClientAPI api)
    {
        bool inventoryOpen = api.Gui.LoadedGuis.Any(d => d.ToggleKeyCombinationCode == "inventorydialog" && d.IsOpened());
        bool shown = questButton != null && questButton.IsOpened();
        if (inventoryOpen && !shown)
        {
            questButton ??= new GuiDialogQuestButton(api, this);
            questButton.TryOpen();
        }
        else if (!inventoryOpen && shown)
        {
            questButton!.TryClose();
        }
    }
}
