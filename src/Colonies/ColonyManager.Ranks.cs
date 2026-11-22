using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Network;
using Vintagestory.API.Config;

namespace CreateAColony.Colonies;

/// <summary>What a custom rank may do; each is a tick box on the Permissions tab's Ranks page.</summary>
[Flags]
public enum RankPerm
{
    None = 0,
    /// <summary>Place and remove huts, order builds, upgrades and repairs.</summary>
    Build = 1,
    /// <summary>Open hut storage, give from their own inventory, fill requests.</summary>
    Storage = 2,
    /// <summary>Give settlers jobs, choose workers and residents, hire at the Tavern, recall settlers.</summary>
    Workers = 4,
    /// <summary>Change a hut's tabs: work area, stock, recipes, mines, markers and the like.</summary>
    HutSettings = 8,
    /// <summary>Change the Town Hall's settings: Bailiff rules, Market, tithe, militia, raids, banners, research.</summary>
    TownSettings = 16,
    /// <summary>Welcome or turn away wanderers.</summary>
    Visitors = 32,
    /// <summary>Invite players into the colony (/colony invite).</summary>
    Invite = 64,
    /// <summary>Take guard scepters and change the guards' tasks.</summary>
    Guards = 128,
}

/// <summary>
/// Custom ranks and the permission log (0.185.0). The owner makes up to MaxRanks ranks on the Permissions tab, names
/// them and ticks what each may do. A player with a rank is a Member who may also do the ticked things, as an officer
/// would. Member and Officer stay as they were. Every guarded thing a non-owner does goes into the colony's permission
/// log (the last PermissionLogSize lines), shown on the same page.
/// </summary>
public partial class ColonyManager
{
    public static readonly RankPerm[] AllPerms = { RankPerm.Build, RankPerm.Storage, RankPerm.Workers, RankPerm.HutSettings, RankPerm.TownSettings, RankPerm.Visitors, RankPerm.Invite, RankPerm.Guards };

    /// <summary>The permission an action needs beyond being a member; None for actions every member may do or only the owner may.</summary>
    public static RankPerm PermFor(ColonyAction a) => a switch
    {
        ColonyAction.Build or ColonyAction.CancelBuild or ColonyAction.Rotate or ColonyAction.Repair or ColonyAction.ForceUpgrade
            or ColonyAction.RemoveHut or ColonyAction.CancelTeardown or ColonyAction.MovePriority
            or (>= ColonyAction.OpenPlacement and <= ColonyAction.ReopenPlacement) or ColonyAction.PlacementLevel or ColonyAction.PlacementVariant => RankPerm.Build,
        ColonyAction.OpenStorage or ColonyAction.GiveResource or ColonyAction.GiveAllResources or ColonyAction.FillRequests or ColonyAction.PickupNow
            or ColonyAction.SetPickupPriority or ColonyAction.AddMinStock or ColonyAction.SetMinStock => RankPerm.Storage,
        ColonyAction.SetJob or ColonyAction.SetWorkRole or ColonyAction.RecallCitizen or ColonyAction.HireRecruit or ColonyAction.AssignWorker
            or ColonyAction.UnassignWorker or ColonyAction.RecallWorkers or ColonyAction.MoveInResidence or ColonyAction.MoveOutResidence
            or ColonyAction.SetHutAutoFlag => RankPerm.Workers,
        ColonyAction.SetWorkRadius or ColonyAction.SetEscort or ColonyAction.SetCollectBerries or ColonyAction.SetKeepLastColony or ColonyAction.SetPotteryKeep
            or ColonyAction.SetKitchenKeep or ColonyAction.SetCraftKeep or ColonyAction.SetRecipeKeep or ColonyAction.TeachRecipe or ColonyAction.ForgetRecipe
            or ColonyAction.SetMineOre or ColonyAction.SetMineProspected or ColonyAction.NewMine or (>= ColonyAction.SetMineLevel and <= ColonyAction.UnassignMineGuard)
            or ColonyAction.SetMarkerGroup or ColonyAction.RemoveMarkers or ColonyAction.RemoveMarker or ColonyAction.MarkRemovedAgain or ColonyAction.SetGatherMode
            or ColonyAction.SetCollect or ColonyAction.RenameBuilding or ColonyAction.LinkMineHut or ColonyAction.UnlinkMineHut => RankPerm.HutSettings,
        ColonyAction.Rename or ColonyAction.SetAnyWoodOrStone or ColonyAction.SetColonyBanner or ColonyAction.ReplaceBanners or ColonyAction.SetBannerOnNew
            or ColonyAction.SetSelfDefence or ColonyAction.SetSupportAllies or ColonyAction.SetRaidMode or ColonyAction.SetRaidSize or ColonyAction.StartRaid
            or ColonyAction.SetBailiffRule or ColonyAction.SetTithe or ColonyAction.SetMarketRule or ColonyAction.StartResearch or ColonyAction.PauseResearch
            or ColonyAction.SetAutoHiring or ColonyAction.SetAutoHousing or ColonyAction.SetEnterMessages or ColonyAction.SetWinterPrep => RankPerm.TownSettings,
        ColonyAction.WelcomeVisitor or ColonyAction.TurnAwayVisitor => RankPerm.Visitors,
        ColonyAction.GetGuardScepter or ColonyAction.ClearGuardPoints or ColonyAction.SetHostile or ColonyAction.SetGuardTask or ColonyAction.SetRetreat
            or ColonyAction.SetPatrolManual or ColonyAction.SetFollowTight or ColonyAction.SetGuardRanged => RankPerm.Guards,
        _ => RankPerm.None
    };

    /// <summary>
    /// The role a player counts as for something needing this permission: a Member whose rank has it counts as an
    /// Officer for it; everyone else keeps their own role.
    /// </summary>
    public static ColonyRole RoleFor(Colony colony, string playerUid, RankPerm perm)
    {
        var member = colony.Members.Find(m => m.PlayerUid == playerUid);
        if (member == null) return ColonyRole.Visitor;
        if (member.Role != ColonyRole.Member || perm == RankPerm.None || member.RankId == 0) return member.Role;
        var rank = colony.Ranks.Find(r => r.Id == member.RankId);
        return rank != null && ((RankPerm)rank.Perms & perm) != 0 ? ColonyRole.Officer : ColonyRole.Member;
    }

    /// <summary>The name of a player's rank: their custom rank, else Member, Officer or Owner.</summary>
    public static string RankName(Colony colony, ColonyMember m) =>
        m.Role == ColonyRole.Member && m.RankId != 0 && colony.Ranks.Find(r => r.Id == m.RankId) is { } rank ? rank.Name
            : Lang.Get("createacolony:role-" + m.Role.ToString().ToLowerInvariant());

    /// <summary>Notes a guarded thing a non-owner did. The same thing by the same player within the hour is noted once.</summary>
    public void LogPermission(Colony colony, string playerUid, string playerName, RankPerm perm, string? building)
    {
        if (perm == RankPerm.None || colony.RoleOf(playerUid) == ColonyRole.Owner) return;
        string what = Lang.Get("createacolony:permlog-" + perm.ToString().ToLowerInvariant()) + (string.IsNullOrEmpty(building) ? "" : " (" + building + ")");
        double now = sapi.World.Calendar.TotalHours;
        var last = colony.PermissionLog.LastOrDefault(e => e.Player == playerName);
        if (last != null && last.What == what && now - last.TotalHours < 1) return;
        colony.PermissionLog.Add(new PermissionLogEntry { Player = playerName, What = what, TotalHours = now });
        int over = colony.PermissionLog.Count - Math.Max(1, Config.PermissionLogSize);
        if (over > 0) colony.PermissionLog.RemoveRange(0, over);
    }

    /// <summary>
    /// The owner's changes on the Ranks page. Text: "add|name", "rename|id|name", "delete|id", "perm|id|bit" (toggles that
    /// permission), "assign|playerUid|id" (0 takes the rank away). Returns a lang key on failure.
    /// </summary>
    public string? RankChange(Colony colony, string text)
    {
        var parts = text.Split('|');
        if (parts.Length < 2) return "createacolony:bad-request";
        switch (parts[0])
        {
            case "add":
            {
                if (colony.Ranks.Count >= Config.MaxRanks) return "createacolony:ranks-full";
                string name = CleanRankName(parts[1]);
                if (name.Length == 0) return "createacolony:rank-no-name";
                colony.Ranks.Add(new ColonyRank { Id = colony.NextRankId++, Name = name });
                break;
            }
            case "rename" when parts.Length >= 3 && int.TryParse(parts[1], out int id):
            {
                var rank = colony.Ranks.Find(r => r.Id == id);
                string name = CleanRankName(parts[2]);
                if (rank == null) return "createacolony:bad-request";
                if (name.Length == 0) return "createacolony:rank-no-name";
                rank.Name = name;
                break;
            }
            case "delete" when int.TryParse(parts[1], out int id):
                colony.Ranks.RemoveAll(r => r.Id == id);
                foreach (var m in colony.Members.Where(m => m.RankId == id)) m.RankId = 0;
                break;
            case "perm" when parts.Length >= 3 && int.TryParse(parts[1], out int id) && int.TryParse(parts[2], out int bit):
            {
                var rank = colony.Ranks.Find(r => r.Id == id);
                if (rank == null || !AllPerms.Contains((RankPerm)bit)) return "createacolony:bad-request";
                rank.Perms ^= bit;
                break;
            }
            case "assign" when parts.Length >= 3 && int.TryParse(parts[2], out int id):
            {
                var member = colony.Members.Find(m => m.PlayerUid == parts[1]);
                if (member == null || member.Role == ColonyRole.Owner) return "createacolony:bad-request";
                if (id != 0 && colony.Ranks.All(r => r.Id != id)) return "createacolony:bad-request";
                // A rank is a kind of Member.
                member.Role = ColonyRole.Member;
                member.RankId = id;
                break;
            }
            default:
                return "createacolony:bad-request";
        }
        MarkDirty(colony);
        return null;
    }

    static string CleanRankName(string name)
    {
        name = name.Replace("|", "").Trim();
        return name.Length > 24 ? name[..24] : name;
    }
}
