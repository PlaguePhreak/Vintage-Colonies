using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// More research (0.180.0): handing in books, lore, relics, temporal gears and Field notes at the Archive, the
/// Stored points they become, and failed experiments and lucky finds while researchers study. Hand-ins give at most
/// HandInPointsPerDay a day per colony; points go to the oldest research running at that Archive, or are stored for
/// the next one. Rusty gears are money, so they are not taken.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The Field notes item (createacolony:fieldnotes); its "fieldPoints", "fieldTokens" and "fieldTitle" attributes say what it is worth.</summary>
    public const string FieldNotesCode = "fieldnotes";

    public static bool IsFieldNotes(ItemStack? s) => s?.Collectible?.Code is { } c && c.Domain == CreateAColonyModSystem.ModId && c.Path == FieldNotesCode;

    /// <summary>What one of this item gives as a hand-in (points, tokens), or null when the Archive has no use for it.</summary>
    public (int Points, int Tokens)? HandInValue(ItemStack? s)
    {
        if (s?.Collectible?.Code == null) return null;
        if (IsFieldNotes(s))
        {
            int p = s.Attributes.GetInt("fieldPoints");
            int t = s.Attributes.GetInt("fieldTokens");
            return p > 0 || t > 0 ? (p, t) : null;
        }
        foreach (var def in Config.ResearchHandIns)
        {
            if (def.Code.Length > 0 && WildcardUtil.Match(new AssetLocation(def.Code), s.Collectible.Code)) return (def.Points, def.Tokens);
        }
        return null;
    }

    /// <summary>Points hand-ins may still give today, keeping the colony's day count up to date.</summary>
    int HandInRoomToday(Colony colony)
    {
        int today = (int)sapi.World.Calendar.TotalDays;
        if (colony.HandInDay != today)
        {
            colony.HandInDay = today;
            colony.HandInPointsToday = 0;
        }
        return Config.HandInPointsPerDay <= 0 ? int.MaxValue : Math.Max(0, Config.HandInPointsPerDay - colony.HandInPointsToday);
    }

    /// <summary>The points hand-ins gave today and the day's cap (0 = none), for the Research tab.</summary>
    public (int Today, int Cap) HandInToday(Colony colony)
    {
        HandInRoomToday(colony);
        return (colony.HandInPointsToday, Math.Max(0, Config.HandInPointsPerDay));
    }

    /// <summary>
    /// Takes a stack as a hand-in at an Archive: all of it, or as many as today's cap still allows (an item that
    /// gives only tokens is always taken). Returns what was taken and what it gave; Taken 0 when nothing was.
    /// </summary>
    public (int Taken, int Points, int Tokens) HandIn(Colony colony, Building archive, ItemStack stack)
    {
        if (HandInValue(stack) is not { } value) return (0, 0, 0);
        int room = HandInRoomToday(colony);
        int count = stack.StackSize;
        if (value.Points > 0) count = Math.Min(count, room / value.Points);
        if (count <= 0) return (0, 0, 0);
        int points = value.Points * count;
        int tokens = value.Tokens * count;
        colony.HandInPointsToday += points;
        colony.KnowledgeTokens += tokens;
        AddResearchPoints(colony, archive, points);
        MarkDirty(colony);
        return (count, points, tokens);
    }

    /// <summary>A player hands in what they hold at the Archive. Returns a lang key for an error, or null (the message is sent).</summary>
    public string? HandInFromPlayer(Colony colony, Building archive, IServerPlayer player)
    {
        if (archive.Type != BuildingTypes.Archive) return "createacolony:bad-building";
        var slot = player.InventoryManager.ActiveHotbarSlot;
        if (slot?.Itemstack == null) return "createacolony:research-handin-nothing";
        if (HandInValue(slot.Itemstack) == null) return "createacolony:research-handin-unwanted";
        string name = slot.Itemstack.GetName();
        var (taken, points, tokens) = HandIn(colony, archive, slot.Itemstack);
        if (taken == 0) return "createacolony:research-handin-capped";
        slot.TakeOut(taken);
        slot.MarkDirty();
        LogEvent(colony, "event-research-handin", player.PlayerName, (taken > 1 ? taken + " " : "") + name);
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("createacolony:research-handin-done", (taken > 1 ? taken + " " : "") + name, points, tokens), EnumChatType.Notification);
        return null;
    }

    readonly System.Collections.Generic.Dictionary<int, int> archiveHandInDay = new();

    /// <summary>Once a day per Archive, researchers take hand-in items left in its storage.</summary>
    void TakeStoredHandIns(Colony colony, Building archive)
    {
        int today = (int)sapi.World.Calendar.TotalDays;
        if (archiveHandInDay.TryGetValue(archive.BuildingId, out int day) && day == today) return;
        archiveHandInDay[archive.BuildingId] = today;
        foreach (var chest in HutStorage(colony, archive))
        {
            foreach (var slot in chest.Inventory)
            {
                if (slot.Empty || HandInValue(slot.Itemstack) == null) continue;
                string name = slot.Itemstack.GetName();
                var (taken, _, _) = HandIn(colony, archive, slot.Itemstack);
                if (taken == 0) continue;
                slot.TakeOut(taken);
                slot.MarkDirty();
                LogEvent(colony, "event-research-handin", Lang.Get("createacolony:building-archive"), (taken > 1 ? taken + " " : "") + name);
            }
        }
    }

    /// <summary>Points for the oldest paid research running at this Archive; stored for later when there is none.</summary>
    public void AddResearchPoints(Colony colony, Building archive, double points)
    {
        if (points <= 0) return;
        colony.StoredResearchPoints += points;
        UseStoredPoints(colony, archive);
    }

    /// <summary>Moves Stored points into the research running at this Archive, oldest first, until they run out.</summary>
    void UseStoredPoints(Colony colony, Building archive)
    {
        if (colony.StoredResearchPoints <= 0) return;
        foreach (var progress in Running(colony, archive).Where(p => p.Paid))
        {
            if (colony.StoredResearchPoints <= 0) break;
            if (ResearchDef(progress.Code) is not { } def) continue;
            double give = Math.Min(colony.StoredResearchPoints, Math.Max(0, def.Points - progress.Points));
            if (give <= 0) continue;
            progress.Points += give;
            colony.StoredResearchPoints -= give;
            FinishIfDone(colony, progress);
        }
        MarkDirty(colony);
    }

    /// <summary>
    /// What one piece of a researcher's work adds: its points, or a failed experiment (it loses
    /// ResearchFailWorksLost works' points, a log line and a sad thought), or a lucky find (ResearchLuckyWorks works).
    /// </summary>
    double ExperimentOutcome(Colony colony, Building archive, CitizenRecord researcher, ResearchProgress progress, double gain)
    {
        TakeStoredHandIns(colony, archive);
        if (!Config.ResearchFailures) return gain;
        var room = RoomOf(colony, archive);
        double fail = Config.ResearchFailChance - Config.ResearchFailPerSkillLevel * UsableLevel(colony, researcher, "research")
            - (room.Table && room.Shelf && room.Light ? Config.ResearchFailGoodRoomCut : 0);
        double roll = sapi.World.Rand.NextDouble();
        string name = Lang.Get("createacolony:research-" + progress.Code);
        if (roll < Math.Max(0, fail))
        {
            LogEvent(colony, "event-research-fail", researcher.Name, name);
            AddThought(researcher, "researchfail");
            return -gain * Math.Max(0, Config.ResearchFailWorksLost - 1);
        }
        if (roll < Math.Max(0, fail) + Math.Max(0, Config.ResearchLuckyChance))
        {
            LogEvent(colony, "event-research-lucky", researcher.Name, name);
            AddThought(researcher, "researchlucky");
            return gain * Math.Max(1, Config.ResearchLuckyWorks);
        }
        return gain;
    }
}
