using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Quests;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The quest book's progress. A quest's state is worked out live from the world each time it is asked
/// for: locked while a quest it needs is unclaimed, counting while an objective is short, complete when
/// all are met, and claimed once the rewards were taken. Only accepted and claimed are saved: on the
/// colony for colony quests, on the world under the player's uid for the player's own.
/// </summary>
public partial class ColonyManager
{
    const string PlayerQuestsKeyPrefix = "createacolony:quests:";
    /// <summary>How many quests a player can track at once.</summary>
    public const int MaxTracked = 3;

    QuestLibrary? quests;
    readonly Dictionary<string, PlayerQuests> playerQuests = new();

    /// <summary>The quest book's contents, read from the quest files the first time they are needed.</summary>
    public QuestLibrary Quests => quests ??= QuestLibrary.Load(sapi);

    /// <summary>Swaps in a new quest book (the editor saved, or the files were reset) and forgets what was worked out from the old one.</summary>
    public void ReloadQuests()
    {
        quests = QuestLibrary.Load(sapi);
        knownItems.Clear();
        killTags = null;
        breakTasks = null;
        placeTasks = null;
    }


    public PlayerQuests QuestsOf(string playerUid)
    {
        if (!playerQuests.TryGetValue(playerUid, out var progress))
        {
            byte[]? data = sapi.WorldManager.SaveGame.GetData(PlayerQuestsKeyPrefix + playerUid);
            progress = null;
            if (data != null)
            {
                try { progress = SerializerUtil.Deserialize<PlayerQuests>(data); }
                catch (Exception e) { Mod.Logger.Error("Failed to load the quests of player {0}: {1}", playerUid, e.Message); }
            }
            playerQuests[playerUid] = progress ??= new PlayerQuests();
        }
        return progress;
    }

    void SavePlayerQuests(string playerUid)
    {
        if (playerQuests.TryGetValue(playerUid, out var progress))
        {
            sapi.WorldManager.SaveGame.StoreData(PlayerQuestsKeyPrefix + playerUid, SerializerUtil.Serialize(progress));
        }
    }

    /// <summary>The colony a player's quest book is about: the one they own, else the one they rank highest in.</summary>
    public Colony? HomeColony(string playerUid) => colonies.Values
        .Where(c => c.RoleOf(playerUid) >= ColonyRole.Member)
        .OrderByDescending(c => c.RoleOf(playerUid))
        .FirstOrDefault();

    /// <summary>The saved record of a quest for this player (or their colony, for colony quests); null when nothing was saved yet.</summary>
    QuestRecord? RecordOf(QuestDef q, string playerUid, Colony? colony)
    {
        var records = q.PerColony ? colony?.Quests : QuestsOf(playerUid).Quests;
        return records?.GetValueOrDefault(q.Code);
    }

    QuestRecord EnsureRecord(QuestDef q, string playerUid, Colony colony)
    {
        var records = q.PerColony ? colony.Quests : QuestsOf(playerUid).Quests;
        if (!records.TryGetValue(q.Code, out var record)) records[q.Code] = record = new QuestRecord();
        return record;
    }

    void SaveRecords(QuestDef q, string playerUid, Colony? colony)
    {
        if (q.PerColony) { if (colony != null) SaveColony(colony); }
        else SavePlayerQuests(playerUid);
    }

    /// <summary>Claimed, or for a quest that repeats: claimed at least once.</summary>
    public bool IsClaimed(QuestDef q, string playerUid, Colony? colony) => RecordOf(q, playerUid, colony) is { } r && (r.Claimed || r.Times > 0);

    /// <summary>
    /// Where a quest stands for a player, with how far along each objective is. Colony quests need a
    /// colony; without one they stay locked.
    /// </summary>
    public QuestStatus Evaluate(IServerPlayer player, Colony? colony, QuestDef q, out int[] progress, out int[] needed)
    {
        needed = q.Objectives.Select(o => Math.Max(1, o.Count)).ToArray();
        progress = new int[q.Objectives.Count];
        var record = RecordOf(q, player.PlayerUID, colony);
        if (record?.Claimed == true)
        {
            Array.Copy(needed, progress, needed.Length);
            return QuestStatus.Claimed;
        }
        if (q.Soon) return QuestStatus.Locked;
        if (q.PerColony && colony == null) return QuestStatus.Locked;
        // A quest that is still to come never holds back the ones after it.
        var needs = q.Requires.Select(r => Quests.Get(r)).Where(n => n != null && !n.Soon).ToList();
        if (needs.Count > 0)
        {
            int claimed = needs.Count(n => IsClaimed(n!, player.PlayerUID, colony));
            if (q.RequiresAny ? claimed == 0 : claimed < needs.Count) return QuestStatus.Locked;
        }
        if (q.Accept && record?.Accepted != true) return QuestStatus.Available;

        bool complete = true;
        for (int i = 0; i < q.Objectives.Count; i++)
        {
            var o = q.Objectives[i];
            // A task the game cannot understand (a wrong item name, an old "manual" task) never blocks: it counts as done.
            progress[i] = IsManual(o) ? needed[i]
                : StickyChecks.Contains(o.Type) ? Math.Min(needed[i], StickyProgress(player, q, i, o, needed[i]))
                : Math.Min(needed[i], Progress(player, colony, o));
            if (progress[i] < needed[i]) complete = false;
        }
        return complete ? QuestStatus.Complete : QuestStatus.Active;
    }

    readonly Dictionary<string, bool> knownItems = new();

    /// <summary>
    /// A task the game cannot check, so the player marks it done: the manual type, and any item task whose
    /// item code matches nothing in this game (a wrong code, or a mod that changed it) so it can never block.
    /// </summary>
    public bool IsManual(QuestObjectiveDef o)
    {
        if (o.Type == ObjectiveTypes.Manual) return true;
        if (o.Type is ObjectiveTypes.BlockNear or ObjectiveTypes.EntityNear or ObjectiveTypes.Holds || (o.Type == ObjectiveTypes.Place && o.Code.Length > 0))
        {
            string id = o.Type + ":" + o.Code;
            if (!knownItems.TryGetValue(id, out bool found))
            {
                found = o.Type == ObjectiveTypes.EntityNear
                    ? sapi.World.EntityTypes.Any(t => QuestItems.Matches(o.Code, t.Code))
                    : o.Code.Split('|').Any(part => part.Length > 0 && (sapi.World.SearchBlocks(Loc(part)).Length > 0 || sapi.World.SearchItems(Loc(part)).Length > 0));
                knownItems[id] = found;
                if (!found) Mod.Logger.Warning("Quest task {0} {1} matches nothing in the game; the task counts as done", o.Type, o.Code);
            }
            return !found;
        }
        if (o.Type is not (ObjectiveTypes.Obtain or ObjectiveTypes.Deliver or ObjectiveTypes.Stock)) return false;
        if (!knownItems.TryGetValue(o.Code, out bool known))
        {
            knownItems[o.Code] = known = QuestItems.Sample(sapi.World, o.Code) != null;
            if (!known) Mod.Logger.Warning("Quest task item {0} matches nothing in the game; the task counts as done", o.Code);
        }
        return !known;
    }

    static AssetLocation Loc(string part) => new(part.Contains(':') ? part : "game:" + part);

    /// <summary>Marks the tasks the game cannot check as done, from the quest card's button.</summary>
    public string? CheckQuest(IServerPlayer player, Colony? colony, QuestDef q)
    {
        if (Evaluate(player, colony, q, out _, out _) != QuestStatus.Active || !q.Objectives.Any(IsManual)) return "createacolony:quest-not-active";
        if (q.PerColony && (colony == null || colony.RoleOf(player.PlayerUID) < ColonyRole.Member)) return "createacolony:denied-member";
        EnsureRecord(q, player.PlayerUID, colony ?? HomeColony(player.PlayerUID)!).Checked = true;
        SaveRecords(q, player.PlayerUID, colony);
        return null;
    }

    Dictionary<string, TagSetFast>? killTags;

    /// <summary>Counts a player's kill towards every kill task whose tags the dead creature has.</summary>
    void OnQuestKill(Entity entity, DamageSource? source)
    {
        if (source == null || (source.CauseEntity ?? source.SourceEntity) is not EntityPlayer { PlayerUID: { } uid }) return;
        if (killTags == null)
        {
            killTags = new Dictionary<string, TagSetFast>();
            foreach (var o in Quests.All.SelectMany(q => q.Objectives).Where(o => o.Type == ObjectiveTypes.Kill && o.Code.Length > 0))
            {
                if (killTags.ContainsKey(o.Code)) continue;
                // Tags the game does not know are left out, so one wrong name does not spoil the rest.
                var set = TagSetFast.Empty;
                foreach (string tag in o.Code.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (sapi.EntityTagRegistry.TryCreateTagSet(out var one, new[] { tag }) == TagRegistryError.None)
                        set = sapi.EntityTagRegistry.CreateMergedTagSet(set, one);
                    else Mod.Logger.Warning("Quest kill task: the game has no entity tag {0}", tag);
                }
                killTags[o.Code] = set;
            }
        }
        if (killTags.Count == 0) return;
        var tags = entity.Tags;
        bool counted = false;
        foreach (var (code, set) in killTags)
        {
            var wanted = set;
            if (wanted.IsEmpty || !tags.Overlaps(ref wanted)) continue;
            var counters = QuestsOf(uid).Counters;
            counters[code] = counters.GetValueOrDefault(code) + 1;
            counted = true;
        }
        if (counted) SavePlayerQuests(uid);
    }

    /// <summary>How far along one objective is, from what the player carries and what the colony has.</summary>
    int Progress(IServerPlayer player, Colony? colony, QuestObjectiveDef o)
    {
        if (o.Type == ObjectiveTypes.Kill) return QuestsOf(player.PlayerUID).Counters.GetValueOrDefault(o.Code);
        if (o.Type == ObjectiveTypes.Place) return PlaceProgress(player, o);
        if (o.Type == ObjectiveTypes.Tour) return colony == null ? 0 : TourProgress(player, colony);
        if (o.Type == ObjectiveTypes.Break) return QuestsOf(player.PlayerUID).Counters.GetValueOrDefault("break:" + o.Code);
        if (o.Type is ObjectiveTypes.Research or ObjectiveTypes.Happiness or ObjectiveTypes.AverageLevel or ObjectiveTypes.WorkHuts or ObjectiveTypes.TopSkill)
            return colony == null ? 0 : ColonyNumber(colony, o);
        double now = sapi.World.Calendar.TotalDays;
        switch (o.Type)
        {
            case ObjectiveTypes.Obtain:
            case ObjectiveTypes.Deliver:
                return CountCarried(player, o.Code);
            case ObjectiveTypes.Camp:
                return HasStarterKit(player.PlayerUID) ? 1 : 0;
            case ObjectiveTypes.Colony:
                return colony != null ? 1 : 0;
        }
        if (colony == null) return 0;
        switch (o.Type)
        {
            case ObjectiveTypes.Stock:
                return CountItems(ColonyStorage(colony), Matcher(o.Code));
            case ObjectiveTypes.Building:
                return colony.Buildings.Any(b => b.Type == o.Code && b.Level >= o.Level) ? 1 : 0;
            case ObjectiveTypes.Buildings:
                return colony.Buildings.Count(b => (o.Code.Length == 0 || b.Type == o.Code) && b.Level >= o.Level);
            case ObjectiveTypes.Settlers:
                return colony.Citizens.Count(c => c.Alive && !c.Visitor);
            case ObjectiveTypes.Job:
                return colony.Citizens.Count(c => c.Alive && !c.Visitor && c.JobCode == o.Code);
            case ObjectiveTypes.Couples:
                return colony.Citizens.Count(c => c.Alive && c.PartnerId != 0 && PartnerOf(colony, c) is { Alive: true }) / 2;
            case ObjectiveTypes.Children:
                return colony.Citizens.Count(c => c.Alive && c.IsChild);
            case ObjectiveTypes.Members:
                return colony.Members.Count;
            case ObjectiveTypes.Stat:
                return ColonyStatSummary(colony).Where(s => WildcardUtil.Match(o.Code, s.Key)).Sum(s => s.Counts[^1]);
            case ObjectiveTypes.Event:
                return colony.Events.Count(e => e.Key == o.Code);
            case ObjectiveTypes.Age:
                return (int)Math.Floor(now - colony.FoundedTotalDays);
            case ObjectiveTypes.Land:
                return (int)Math.Min(int.MaxValue, ClaimedColumns(colony));
        }
        return 0;
    }

    /// <summary>The warehouse's chests and every hut's own storage.</summary>
    IEnumerable<BlockEntityContainer> ColonyStorage(Colony colony)
    {
        foreach (var c in WarehouseContainers(colony)) yield return c;
        foreach (var b in colony.Buildings.Where(b => HasOwnStorage(b.Type)))
        {
            foreach (var c in HutStorage(colony, b)) yield return c;
        }
    }

    /// <summary>The player's hotbar and bags, where quest items are looked for.</summary>
    static IEnumerable<ItemSlot> CarriedSlots(IPlayer player)
    {
        foreach (var inv in player.InventoryManager.Inventories.Values)
        {
            if (inv.ClassName != GlobalConstants.hotBarInvClassName && inv.ClassName != GlobalConstants.backpackInvClassName) continue;
            foreach (var slot in inv)
            {
                if (!slot.Empty) yield return slot;
            }
        }
    }

    public static int CountCarried(IPlayer player, string pattern) =>
        pattern == QuestItems.AnyFood
            ? CarriedSlots(player).Where(s => s.Itemstack.Collectible.NutritionProps != null).Sum(s => s.Itemstack.StackSize)
            : CarriedSlots(player).Where(s => QuestItems.Matches(pattern, s.Itemstack.Collectible.Code)).Sum(s => s.Itemstack.StackSize);

    /// <summary>Takes up to count matching items from what the player carries; returns how many were taken.</summary>
    static int TakeCarried(IPlayer player, string pattern, int count)
    {
        int taken = 0;
        foreach (var slot in CarriedSlots(player).ToList())
        {
            if (taken >= count || !QuestItems.Matches(pattern, slot.Itemstack.Collectible.Code)) continue;
            int take = Math.Min(count - taken, slot.Itemstack.StackSize);
            slot.TakeOut(take);
            slot.MarkDirty();
            taken += take;
        }
        return taken;
    }

    // ---- Actions ----------------------------------------------------------------------------

    public string? AcceptQuest(IServerPlayer player, Colony? colony, QuestDef q)
    {
        if (Evaluate(player, colony, q, out _, out _) != QuestStatus.Available) return "createacolony:quest-not-available";
        if (q.PerColony && (colony == null || colony.RoleOf(player.PlayerUID) < ColonyRole.Member)) return "createacolony:denied-member";
        var record = EnsureRecord(q, player.PlayerUID, colony!);
        record.Accepted = true;
        record.AcceptedTotalDays = sapi.World.Calendar.TotalDays;
        SaveRecords(q, player.PlayerUID, colony);
        return null;
    }

    public string? DropQuest(IServerPlayer player, Colony? colony, QuestDef q)
    {
        var record = RecordOf(q, player.PlayerUID, colony);
        if (record == null || record.Claimed || !record.Accepted) return "createacolony:quest-not-active";
        record.Accepted = false;
        record.Notified = false;
        SaveRecords(q, player.PlayerUID, colony);
        return null;
    }

    /// <summary>Hands out the rewards, takes delivered items, and marks the quest done.</summary>
    public string? ClaimQuest(IServerPlayer player, Colony? colony, QuestDef q)
    {
        if (Evaluate(player, colony, q, out _, out _) != QuestStatus.Complete) return "createacolony:quest-not-complete";
        if (q.PerColony && colony!.RoleOf(player.PlayerUID) < ColonyRole.Member) return "createacolony:denied-member";

        foreach (var o in q.Objectives.Where(o => o.Type == ObjectiveTypes.Deliver))
        {
            TakeCarried(player, o.Code, Math.Max(1, o.Count));
        }
        foreach (var reward in q.Rewards)
        {
            var stack = QuestItems.Sample(sapi.World, reward.Code);
            if (stack == null)
            {
                Mod.Logger.Warning("Quest {0} rewards unknown item {1}, skipped", q.Code, reward.Code);
                continue;
            }
            int left = Math.Max(1, reward.Count);
            while (left > 0)
            {
                var give = stack.Clone();
                give.StackSize = Math.Min(left, stack.Collectible.MaxStackSize);
                left -= give.StackSize;
                if (!player.Entity.TryGiveItemStack(give) && give.StackSize > 0)
                {
                    // Whatever does not fit in the inventory drops at the player's feet.
                    sapi.World.SpawnItemEntity(give, player.Entity.Pos.XYZ.Add(0, 0.5, 0));
                }
            }
        }

        var record = EnsureRecord(q, player.PlayerUID, colony ?? HomeColony(player.PlayerUID)!);
        record.Times++;
        record.ClaimedTotalDays = sapi.World.Calendar.TotalDays;
        if (q.Repeatable)
        {
            // Start over: the tasks count from nothing again.
            record.Notified = record.Checked = record.Accepted = false;
            var counters = QuestsOf(player.PlayerUID).Counters;
            for (int i = 0; i < q.Objectives.Count; i++)
            {
                counters.Remove(q.Code + "#" + i);
                var o = q.Objectives[i];
                if (o.Type == ObjectiveTypes.Kill) counters.Remove(o.Code);
                else if (o.Type is ObjectiveTypes.Break or ObjectiveTypes.Place) counters.Remove(o.Type + ":" + o.Code);
            }
            SavePlayerQuests(player.PlayerUID);
        }
        else record.Claimed = true;
        SaveRecords(q, player.PlayerUID, colony);

        player.SendMessage(0, Lang.GetL(player.LanguageCode, "createacolony:quest-claimed", QuestLibrary.TitleL(player.LanguageCode, q)), EnumChatType.Notification);
        if (q.PerColony && colony != null) LogEvent(colony, "event-quest-done", "@quest-" + q.Code, player.PlayerName);
        return null;
    }

    public void ToggleTracked(IServerPlayer player, QuestDef q)
    {
        var progress = QuestsOf(player.PlayerUID);
        if (!progress.Tracked.Remove(q.Code))
        {
            progress.Tracked.Add(q.Code);
            while (progress.Tracked.Count > MaxTracked) progress.Tracked.RemoveAt(0);
        }
        SavePlayerQuests(player.PlayerUID);
    }

    /// <summary>Forgets every quest of the player and of the colony they own, for testing.</summary>
    public void ResetQuests(IServerPlayer player)
    {
        playerQuests[player.PlayerUID] = new PlayerQuests();
        SavePlayerQuests(player.PlayerUID);
        if (GetOwnedBy(player.PlayerUID) is { } colony)
        {
            colony.Quests.Clear();
            SaveColony(colony);
        }
    }

    /// <summary>Marks a quest claimed without checking it, for testing; its rewards are not given.</summary>
    public void ForceClaim(IServerPlayer player, QuestDef q)
    {
        var colony = HomeColony(player.PlayerUID);
        if (q.PerColony && colony == null) return;
        var record = EnsureRecord(q, player.PlayerUID, colony!);
        record.Accepted = record.Claimed = true;
        record.ClaimedTotalDays = sapi.World.Calendar.TotalDays;
        SaveRecords(q, player.PlayerUID, colony);
    }

    /// <summary>
    /// Every few seconds, tells each online player once about each quest that has just become ready to
    /// claim, in chat. Called from the network system's tick, which also refreshes open quest books.
    /// </summary>
    public void NotifyReadyQuests(IServerPlayer player)
    {
        var colony = HomeColony(player.PlayerUID);
        foreach (var q in Quests.All)
        {
            var record = RecordOf(q, player.PlayerUID, colony);
            if (record?.Claimed == true || record?.Notified == true) continue;
            if (Evaluate(player, colony, q, out _, out _) != QuestStatus.Complete) continue;
            if (q.PerColony && colony == null) continue;
            record = EnsureRecord(q, player.PlayerUID, colony!);
            record.Notified = true;
            SaveRecords(q, player.PlayerUID, colony);
            // The same pop-up the game shows for a lore find.
            sapi.SendIngameDiscovery(player, "questcompleted", Lang.GetL(player.LanguageCode, "createacolony:quest-completed-popup", QuestLibrary.TitleL(player.LanguageCode, q)));
            player.SendMessage(0, Lang.GetL(player.LanguageCode, "createacolony:quest-ready", QuestLibrary.TitleL(player.LanguageCode, q)), EnumChatType.Notification);
        }
    }
}
