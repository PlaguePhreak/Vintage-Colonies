using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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

    // ---- The quest editor's changes (client side, before the book is saved to the server) ----

    public void Add(QuestChapterDef chapter, QuestDef quest)
    {
        quest.Chapter = chapter;
        chapter.Quests.Add(quest);
        byCode[quest.Code] = quest;
    }

    public void Remove(QuestDef quest)
    {
        quest.Chapter.Quests.Remove(quest);
        byCode.Remove(quest.Code);
        foreach (var other in byCode.Values)
        {
            other.Requires.Remove(quest.Code);
            other.NoLine.Remove(quest.Code);
        }
    }

    /// <summary>A code no quest has yet, from a wanted start such as "farm-new".</summary>
    public string FreeCode(string start)
    {
        string code = start;
        for (int n = 2; byCode.ContainsKey(code); n++) code = start + n;
        return code;
    }

    /// <summary>Writes every name and description into the quests themselves, so an edited book carries its own text.</summary>
    public void BakeText()
    {
        foreach (var g in Groups)
        {
            g.Title = Title(g);
            foreach (var c in g.Chapters)
            {
                c.Title = Title(c);
                foreach (var q in c.Quests)
                {
                    string desc = Desc(q);
                    q.Title = Title(q);
                    q.Desc = desc;
                }
            }
        }
    }

    /// <summary>Where a server keeps a quest book that was edited in game; while it holds files they replace the mod's own.</summary>
    public static string OverrideFolder(ICoreAPI api) => System.IO.Path.Combine(api.GetOrCreateDataPath("ModConfig"), "createacolony", "quests");

    /// <summary>The server's book: the edited one when there is one, else the mod's own. The client starts with the mod's own and is sent the server's.</summary>
    public static QuestLibrary Load(ICoreAPI api)
    {
        if (api.Side == EnumAppSide.Server)
        {
            try
            {
                string folder = OverrideFolder(api);
                if (System.IO.Directory.Exists(folder))
                {
                    var files = System.IO.Directory.GetFiles(folder, "*.json");
                    if (files.Length > 0)
                    {
                        var edited = FromJson(api, files.Select(System.IO.File.ReadAllText));
                        edited.Edited = true;
                        return edited;
                    }
                }
            }
            catch (Exception e)
            {
                api.Logger.Error("[createacolony] Could not read the edited quest book, using the mod's own: {0}", e.Message);
            }
        }
        return FromJson(api, api.Assets.GetManyInCategory("config", "quests/", CreateAColonyModSystem.ModId).Select(a => a.ToText()));
    }

    /// <summary>True when this book came from the server's edited files.</summary>
    public bool Edited;

    public List<string> ToJson() => Groups.Select(g => JsonConvert.SerializeObject(g, Formatting.Indented,
        new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore, NullValueHandling = NullValueHandling.Ignore })).ToList();

    public static QuestLibrary FromJson(ICoreAPI api, IEnumerable<string> jsons)
    {
        var library = new QuestLibrary();
        var hidden = new HashSet<string>();
        foreach (string json in jsons)
        {
            try
            {
                var group = JsonConvert.DeserializeObject<QuestGroupDef>(json);
                if (group == null || group.Code.Length == 0) continue;
                library.Groups.Add(group);
            }
            catch (Exception e)
            {
                api.Logger.Error("[createacolony] Bad quest file: {0}", e.Message);
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

    // Names: the edited text when there is one, else the lang file.
    public static string Title(QuestDef q) => q.Title.Length > 0 ? q.Title : Lang.Get(TitleKey(q));
    public static string TitleL(string language, QuestDef q) => q.Title.Length > 0 ? q.Title : Lang.GetL(language, TitleKey(q));
    public static string Desc(QuestDef q)
    {
        if (q.Desc.Length > 0 || q.Title.Length > 0) return q.Desc;
        string text = Lang.Get(TitleKey(q) + "-desc");
        return text.EndsWith("-desc") ? "" : text;
    }
    public static string Title(QuestChapterDef c) => c.Title.Length > 0 ? c.Title : Lang.Get("createacolony:chapter-" + c.Code);
    public static string Title(QuestGroupDef g) => g.Title.Length > 0 ? g.Title : Lang.Get("createacolony:questgroup-" + g.Code);

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
    /// <summary>The group's name when the book was edited in game; empty uses the lang file (questgroup-code).</summary>
    public string Title = "";
    public List<QuestChapterDef> Chapters = new();
}

/// <summary>A page of a group: a map of quests.</summary>
public class QuestChapterDef
{
    public string Code = "";
    /// <summary>Overrides the group's scope; the Story prologue is per player while the rest is per colony.</summary>
    public string? Scope;
    public string Icon = "";
    /// <summary>The chapter's name when edited in game; empty uses the lang file (chapter-code).</summary>
    public string Title = "";
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
    /// <summary>Optional: an x on the map where lines from quests to the left of it turn, so they reach this quest along one shared rail.</summary>
    public double? Rail;
    /// <summary>Requirements that get no line on the map: a quest from far across the chapter, named on the card instead.</summary>
    public List<string> NoLine = new();
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
    /// <summary>Can be done again: claiming it gives the rewards and starts it over. Quests after it open at the first claim.</summary>
    public bool Repeatable;
    /// <summary>A settler request: only open while it is on the colony's board (ColonyManager.Requests.cs).</summary>
    public bool Request;
    /// <summary>For a request, "itemcode&lt;count": it is posted first while the colony's stores hold fewer than count.</summary>
    public string Need = "";
    /// <summary>Name and description when edited in game; empty uses the lang file (quest-code, quest-code-desc).</summary>
    public string Title = "";
    public string Desc = "";
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
    /// <summary>The player killed Count creatures that have any of the entity tags in Code ("animal|huntable|predator").</summary>
    public const string Kill = "kill";
    /// <summary>A container the player carries, or the block they look at, holds Code (a liquid in a bucket or barrel, food on a shelf).</summary>
    public const string Holds = "holds";
    /// <summary>The player carries a cooked meal of recipe Code with Count servings; Level 1 asks for a sealed crock.</summary>
    public const string Meal = "meal";
    /// <summary>Count blocks matching Code stand within a few blocks of the player.</summary>
    public const string BlockNear = "blocknear";
    /// <summary>Count creatures matching Code are near the player; Level is the lowest generation that counts.</summary>
    public const string EntityNear = "entitynear";
    /// <summary>The player stands in a closed room; Code "cellar" or "greenhouse" asks for that kind.</summary>
    public const string Room = "room";
    /// <summary>The player rides a creature matching Code (any when empty).</summary>
    public const string Mounted = "mounted";
    /// <summary>The player is Count blocks below sea level.</summary>
    public const string Depth = "depth";
    /// <summary>The player broke Count blocks; Code is "tool@block", either side may be empty.</summary>
    public const string Break = "break";
    /// <summary>The player placed Count blocks matching Code (any block when empty).</summary>
    public const string Place = "place";
    /// <summary>The player used (right-clicked) Count blocks; Code is "held item@block", either side may be empty.</summary>
    public const string Use = "use";
    /// <summary>Count saplings the player planted have grown into trees.</summary>
    public const string Grown = "grown";
    /// <summary>The player has stood beside Count different kinds of the colony's huts.</summary>
    public const string Tour = "tour";
    /// <summary>Count pieces of research are finished; with a Code, that piece of research is finished (Count 1).</summary>
    public const string Research = "research";
    /// <summary>The colony's average happiness (0 to 100) is at least Count.</summary>
    public const string Happiness = "happiness";
    /// <summary>Huts of type Code average Count / 10 levels.</summary>
    public const string AverageLevel = "averagelevel";
    /// <summary>Count different kinds of work hut are at Level or above.</summary>
    public const string WorkHuts = "workhuts";
    /// <summary>Count settlers have a skill at Level or above.</summary>
    public const string TopSkill = "topskill";
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
    /// <summary>
    /// A narrower check, "key=value": for carried items an item attribute (material=copper on a lantern); for blocks
    /// near the player "moist=75" (farmland at least that moist) or "kinds=2" (that many different kinds among them).
    /// </summary>
    public string Filter = "";
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
    /// <summary>A task code that means anything that can be eaten, whatever mod it comes from.</summary>
    public const string AnyFood = "@food";

    public static ItemStack? Sample(IWorldAccessor world, string pattern)
    {
        if (pattern == AnyFood) pattern = "game:fruit-*|game:bread-*|game:vegetable-*";
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
