using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace CreateAColony.Colonies;

/// <summary>
/// The Rally Banner (0.178.0, design doc https://claude.ai/artifact/X1rem76HRLvrBPbsY3UWZp): a player links
/// Guard Towers and Barracks to their banner, then raises it to call every guard of those posts to them. The
/// guards follow and fight around the player (as on the Follow task) until the banner is lowered, or the player
/// logs off or dies. Raised banners are not saved: after a restart every banner is down.
/// </summary>
public partial class ColonyManager
{
    public const string RallyColonyAttr = "rallyColony";
    public const string RallyPostsAttr = "rallyPosts";
    public const string RallyNamesAttr = "rallyNames";

    /// <summary>A raised banner: whose colony, and which guard posts it calls.</summary>
    public record RallyCall(string ColonyId, HashSet<int> Posts);

    /// <summary>Raised banners by player uid.</summary>
    readonly Dictionary<string, RallyCall> rallies = new();

    bool rallyEventsHooked;

    void HookRallyEvents()
    {
        if (rallyEventsHooked) return;
        rallyEventsHooked = true;
        sapi.Event.PlayerDisconnect += player => LowerRally(player, quiet: true);
        sapi.Event.PlayerDeath += (player, _) => LowerRally(player, quiet: false);
    }

    public static bool IsRallyBanner(ItemStack? stack) => stack?.Collectible is Items.ItemRallyBanner;

    public static int[] RallyPosts(ItemStack stack) => (stack.Attributes[RallyPostsAttr] as IntArrayAttribute)?.value ?? System.Array.Empty<int>();

    /// <summary>Links the guard post to the banner, or unlinks it if it was linked. A banner serves one colony. Returns the chat message key.</summary>
    public string ToggleRallyLink(ItemStack banner, Colony colony, Building post)
    {
        var attrs = banner.Attributes;
        var posts = attrs.GetString(RallyColonyAttr, "") == colony.ColonyId ? RallyPosts(banner).ToList() : new List<int>();
        bool linked = posts.Remove(post.BuildingId);
        if (!linked) posts.Add(post.BuildingId);
        attrs.SetString(RallyColonyAttr, colony.ColonyId);
        attrs[RallyPostsAttr] = new IntArrayAttribute(posts.ToArray());
        attrs.SetString(RallyNamesAttr, string.Join(", ", posts.Select(id => colony.Buildings.Find(b => b.BuildingId == id)).OfType<Building>().Select(BuildingName)));
        return linked ? "createacolony:rally-unlinked" : "createacolony:rally-linked";
    }

    /// <summary>Raises the banner, or lowers it if this player's banner is up.</summary>
    public void ToggleRally(IServerPlayer player, ItemStack banner)
    {
        HookRallyEvents();
        if (rallies.ContainsKey(player.PlayerUID))
        {
            LowerRally(player, quiet: false);
            return;
        }
        var colony = GetById(banner.Attributes.GetString(RallyColonyAttr, ""));
        var posts = colony == null ? new HashSet<int>() : RallyPosts(banner).Where(id => colony.Buildings.Any(b => b.BuildingId == id && IsGuardPost(b.Type))).ToHashSet();
        if (colony == null || posts.Count == 0)
        {
            TellRally(player, "createacolony:rally-no-posts");
            return;
        }
        if (colony.RoleOf(player.PlayerUID) < ColonyRole.Member)
        {
            TellRally(player, "createacolony:rally-not-member");
            return;
        }
        rallies[player.PlayerUID] = new RallyCall(colony.ColonyId, posts);
        int guards = colony.Citizens.Count(c => c.Alive && c.JobCode == Jobs.Guard && TowerOf(colony, c) is { } t && posts.Contains(t.BuildingId));
        TellRally(player, "createacolony:rally-raised", guards);
        LogEvent(colony, "event-rally-raised", player.PlayerName);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/effect/swoosh"), player.Entity, null, false, 16f, 1f);
    }

    void LowerRally(IServerPlayer player, bool quiet)
    {
        if (!rallies.Remove(player.PlayerUID, out var call)) return;
        if (!quiet) TellRally(player, "createacolony:rally-lowered");
        if (GetById(call.ColonyId) is { } colony) LogEvent(colony, "event-rally-lowered", player.PlayerName);
    }

    /// <summary>
    /// The player a guard of this post rallies to: one whose raised banner calls the post, online, alive and
    /// within RallyRange. Null when none.
    /// </summary>
    public EntityPlayer? RallyFor(Colony colony, Building? post, Entity guard)
    {
        if (post == null || rallies.Count == 0) return null;
        foreach (var (uid, call) in rallies)
        {
            if (call.ColonyId != colony.ColonyId || !call.Posts.Contains(post.BuildingId)) continue;
            if (sapi.World.PlayerByUid(uid)?.Entity is not { Alive: true } player) continue;
            if (player.Pos.Dimension == guard.Pos.Dimension && player.Pos.SquareDistanceTo(guard.Pos.XYZ) > Config.RallyRange * Config.RallyRange) continue;
            return player;
        }
        return null;
    }

    /// <summary>The players rallying this colony's guards right now, for the Town Hall.</summary>
    public List<string> RalliedBy(Colony colony) => rallies.Where(r => r.Value.ColonyId == colony.ColonyId)
        .Select(r => sapi.World.PlayerByUid(r.Key)?.PlayerName).OfType<string>().ToList();

    static void TellRally(IServerPlayer player, string key, params object[] args) =>
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.GetL(player.LanguageCode, key, args), EnumChatType.Notification);
}
