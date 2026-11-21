using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace CreateAColony.Quests;

/// <summary>
/// The quest book's contents, read from assets/createacolony/config/quests/*.json on both sides: one file
/// per group, each group holding chapters, each chapter its quests. Nothing here changes while playing;
/// progress lives on the player and the colony (see ColonyManager.Quests). Text comes from the lang file:
/// questgroup-&lt;group&gt;, chapter-&lt;chapter&gt;, quest-&lt;quest&gt; and quest-&lt;quest&gt;-desc.
/// </summary>
public class QuestLibrary
{
    public const string AssetFolder = "config/quests/";

    public readonly List<QuestGroupDef> Groups = new();
    readonly Dictionary<string, QuestDef> byCode = new();

    public IReadOnlyCollection<QuestDef> All => byCode.Values;

    public QuestDef? Get(string code) => byCode.GetValueOrDefault(code);

    public static QuestLibrary Load(ICoreAPI api)
    {
        var library = new QuestLibrary();
        var hidden = new HashSet<string>();
        foreach (var asset in api.Assets.GetManyInCategory("config", "quests/", CreateAColonyModSystem.ModId))
        {
            try
            {
                var group = JsonConvert.DeserializeObject<QuestGroupDef>(asset.ToText());
                if (group == null || group.Code.Length == 0) continue;
                library.Groups.Add(group);
            }
            catch (Exception e)
            {
                api.Logger.Error("[createacolony] Bad quest file {0}: {1}", asset.Location, e.Message);
            }
        }
        library.Groups.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Code, b.Code));

        foreach (var group in library.Groups)
        {
            foreach (var chapter in group.Chapters)
            {
                chapter.Group = group;
                foreach (var quest in chapter.Quests)
                {
                    quest.Chapter = chapter;
                    if (quest.Code.Length == 0) continue;
                    if (quest.Mod.Length > 0 && !api.ModLoader.IsModEnabled(quest.Mod))
                    {
                        hidden.Add(quest.Code);
                        continue;
                    }
                    if (library.byCode.ContainsKey(quest.Code))
                    {
                        api.Logger.Warning("[createacolony] Quest {0} appears twice; the second one is skipped", quest.Code);
                        continue;
                    }
                    library.byCode[quest.Code] = quest;
                }
                chapter.Quests.RemoveAll(q => q.Code.Length == 0 || library.byCode.GetValueOrDefault(q.Code) != q);
            }
        }
        // A quest that needs one that does not exist can never open, so the bad link is dropped with a warning.
        foreach (var quest in library.byCode.Values)
        {
            // A quest of a mod that is not installed counts as done: the link to it just goes away.
            if (quest.Requires.Any(hidden.Contains))
            {
                if (quest.RequiresAny) quest.Requires.Clear();
                else quest.Requires.RemoveAll(hidden.Contains);
            }
            foreach (string need in quest.Requires.Where(r => !library.byCode.ContainsKey(r)).ToList())
            {
                api.Logger.Warning("[createacolony] Quest {0} needs unknown quest {1}; the link is ignored", quest.Code, need);
                quest.Requires.Remove(need);
            }
        }
        api.Logger.Notification("[createacolony] Loaded {0} quest groups with {1} quests", library.Groups.Count, library.byCode.Count);
        return library;
    }

    /// <summary>Lang key suffix of a quest's title; the description adds "-desc".</summary>
    public static string TitleKey(QuestDef q) => "createacolony:quest-" + q.Code;
}

/// <summary>Whose progress a quest counts: each player's own, or the colony's, shared by all its members.</summary>
public static class QuestScope
{
    public const string Player = "player";
    public const string Colony = "colony";
}

/// <summary>A tab of the quest book: the Story, the Ages, The Colony, and so on.</summary>
public class QuestGroupDef
{
    public string Code = "";
    /// <summary>Tabs are sorted by this, then by code.</summary>
    public int Order;
    /// <summary>The default scope of the group's chapters (see QuestScope).</summary>
    public string Scope = QuestScope.Player;
    /// <summary>An item or block code drawn as the group's icon.</summary>
    public string Icon = "";
    public List<QuestChapterDef> Chapters = new();
}

/// <summary>A page of a group: a map of quests.</summary>
public class QuestChapterDef
{
    public string Code = "";
    /// <summary>Overrides the group's scope; the Story prologue is per player while the rest is per colony.</summary>
    public string? Scope;
    public string Icon = "";
    public List<QuestDef> Quests = new();

    [JsonIgnore] public QuestGroupDef Group = null!;
    [JsonIgnore] public string EffectiveScope => Scope ?? Group.Scope;
    [JsonIgnore] public bool PerColony => EffectiveScope == QuestScope.Colony;
}

/// <summary>Shapes a quest can have on the map: round for most, square for story quests, star for a chapter's last, hexagon for optional.</summary>
public static class QuestShapes
{
    public const string Round = "round";
    public const string Square = "square";
    public const string Star = "star";
    public const string Hexagon = "hexagon";
}

public class QuestDef
{
    /// <summary>Unique across the whole book; the lang keys and saved progress use it.</summary>
    public string Code = "";
    /// <summary>Where it sits on the chapter's map, in grid cells.</summary>
    public double X;
    public double Y;
    /// <summary>An item or block code for its icon (wildcards allowed: the first match is drawn). Empty uses the first objective's.</summary>
    public string Icon = "";
    public string Shape = QuestShapes.Round;
    /// <summary>Quests that must be claimed before this one opens; the map draws a line from each.</summary>
    public List<string> Requires = new();
    /// <summary>The quest must be accepted before it counts (story quests); others track on their own.</summary>
    public bool Accept;
    /// <summary>Hidden until its requirements are met.</summary>
    public bool Secret;
    /// <summary>One claimed quest of Requires is enough to open this one, instead of all of them.</summary>
    public bool RequiresAny;
    /// <summary>A mod id: the quest only exists while that mod is installed. Quests that need it open without it.</summary>
    public string Mod = "";
    /// <summary>Coming later: shown on the map but locked, for huts and features the mod does not have yet.</summary>
    public bool Soon;
    public List<QuestObjectiveDef> Objectives = new();
    public List<QuestRewardDef> Rewards = new();

    [JsonIgnore] public QuestChapterDef Chapter = null!;
    [JsonIgnore] public bool PerColony => Chapter.PerColony;
}

/// <summary>What a quest asks for. Every type checks something the game or the colony already tracks.</summary>
public static class ObjectiveTypes
{
    /// <summary>Have Count of the item (Code, wildcards allowed) in your inventory; nothing is taken.</summary>
    public const string Obtain = "obtain";
    /// <summary>Have Count of the item in your inventory; they are taken when the quest is claimed.</summary>
    public const string Deliver = "deliver";
    /// <summary>The colony holds Count of the item in its warehouse and hut storages.</summary>
    public const string Stock = "stock";
    /// <summary>The colony has a building of type Code at Level or above (level 0: placed).</summary>
    public const string Building = "building";
    /// <summary>The colony has Count buildings of type Code (empty: any) at Level or above.</summary>
    public const string Buildings = "buildings";
    /// <summary>The colony has Count living settlers (wanderers not counted).</summary>
    public const string Settlers = "settlers";
    /// <summary>The colony has Count settlers with the job Code.</summary>
    public const string Job = "job";
    /// <summary>The colony has Count couples.</summary>
    public const string Couples = "couples";
    /// <summary>The colony has Count children.</summary>
    public const string Children = "children";
    /// <summary>The colony has Count members (players).</summary>
    public const string Members = "members";
    /// <summary>The colony's huts' all-time statistics for Code (wildcards allowed, e.g. "kill:*") add up to Count.</summary>
    public const string Stat = "stat";
    /// <summary>The colony's event log holds Count events with the key Code (only the last 100 events are kept).</summary>
    public const string Event = "event";
    /// <summary>The colony is Count in-game days old.</summary>
    public const string Age = "age";
    /// <summary>The colony's land covers Count block columns.</summary>
    public const string Land = "land";
    /// <summary>The player has had the supply camp's starter kit (placed a Supply Camp).</summary>
    public const string Camp = "camp";
    /// <summary>The player belongs to a colony.</summary>
    public const string Colony = "colony";
    /// <summary>Something the game cannot check: the player presses "Mark as done" on the quest's card.</summary>
    public const string Manual = "manual";
}

public class QuestObjectiveDef
{
    public string Type = ObjectiveTypes.Obtain;
    /// <summary>An item, building type, job, stat key or event key, by Type.</summary>
    public string Code = "";
    public int Count = 1;
    /// <summary>For building objectives: the level the building must have reached.</summary>
    public int Level;
    /// <summary>Lang key suffix for the line in the quest card; empty makes one from the type.</summary>
    public string Text = "";
}

/// <summary>Items only, for now: Count of the item Code goes to the player who claims the quest.</summary>
public class QuestRewardDef
{
    public string Code = "";
    public int Count = 1;
}

/// <summary>Where a quest stands for one player or colony. Locked, Available, Active and Complete are worked out live; Claimed is saved.</summary>
public enum QuestStatus
{
    /// <summary>A quest it needs is not claimed yet.</summary>
    Locked = 0,
    /// <summary>Open, waiting to be accepted (story quests only).</summary>
    Available = 1,
    /// <summary>Counting; some objective is not met yet.</summary>
    Active = 2,
    /// <summary>Every objective is met; the rewards wait to be claimed.</summary>
    Complete = 3,
    /// <summary>Done and rewarded.</summary>
    Claimed = 4
}

/// <summary>Matches an item code pattern such as "game:plank-*" or "game:flint|game:stone-*".</summary>
public static class QuestItems
{
    public static bool Matches(string pattern, AssetLocation code)
    {
        foreach (string part in pattern.Split('|'))
        {
            if (part.Length == 0) continue;
            var loc = new AssetLocation(part.Contains(':') ? part : "game:" + part);
            if (WildcardUtil.Match(loc, code)) return true;
        }
        return false;
    }

    /// <summary>One item or block matching the pattern, for icons and rewards; null when the game has none.</summary>
    public static ItemStack? Sample(IWorldAccessor world, string pattern)
    {
        foreach (string part in pattern.Split('|'))
        {
            if (part.Length == 0) continue;
            var loc = new AssetLocation(part.Contains(':') ? part : "game:" + part);
            if (!part.Contains('*'))
            {
                if (world.GetItem(loc) is { } item) return new ItemStack(item);
                if (world.GetBlock(loc) is { } block) return new ItemStack(block);
                continue;
            }
            var items = world.SearchItems(loc);
            if (items.Length > 0) return new ItemStack(items[0]);
            var blocks = world.SearchBlocks(loc);
            if (blocks.Length > 0) return new ItemStack(blocks[0]);
        }
        return null;
    }
}
