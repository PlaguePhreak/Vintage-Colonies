using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Blocks;
using CreateAColony.Colonies;
using CreateAColony.Import;
using CreateAColony.Settlers;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CreateAColony;

/// <summary>
/// Entry point: registers classes on both sides and the server-side debug commands.
/// Gameplay state lives in ColonyManager; this system only wires things up.
/// </summary>
public class CreateAColonyModSystem : ModSystem
{
    public const string ModId = "createacolony";

    /// <summary>Mods that replace the same gameplay loop and are not supported alongside this one.</summary>
    static readonly string[] IncompatibleModIds = { "vsvillage" };

    ICoreServerAPI? sapi;

    // Pending invites, invitee uid -> colony id. Kept in memory only: an invite lapses on restart.
    readonly Dictionary<string, string> pendingInvites = new();

    public override void StartPre(ICoreAPI api)
    {
        // modinfo.json has no incompatibility field, so refuse to start with a clear message instead.
        foreach (string modId in IncompatibleModIds)
        {
            if (api.ModLoader.IsModEnabled(modId))
            {
                throw new InvalidOperationException($"Vintage Colonies cannot run together with '{modId}'. Disable one of the two mods.");
            }
        }
    }

    public override void Start(ICoreAPI api)
    {
        api.RegisterEntity("EntitySettler", typeof(EntitySettler));
        api.RegisterEntityBehaviorClass("colonycitizen", typeof(EntityBehaviorColonyCitizen));
        api.RegisterBlockClass("BlockTownHall", typeof(BlockTownHall));
        api.RegisterBlockEntityClass("TownHall", typeof(BlockEntityTownHall));
        api.RegisterBlockClass("BlockSupplyCamp", typeof(BlockSupplyCamp));
        api.RegisterEntityBehaviorClass("settlerneeds", typeof(EntityBehaviorSettlerNeeds));
        api.RegisterBlockClass("BlockWarehouse", typeof(BlockWarehouse));
        api.RegisterBlockClass("BlockDropOff", typeof(BlockDropOff));
        api.RegisterBlockClass("BlockColonyHut", typeof(BlockColonyHut));
        api.RegisterBlockClass("BlockWorkStation", typeof(BlockWorkStation));
        api.RegisterBlockEntityClass("ColonyHut", typeof(BlockEntityColonyHut));
        api.RegisterBlockClass("BlockFieldMarker", typeof(BlockFieldMarker));
        api.RegisterBlockClass("BlockPainting", typeof(BlockPainting));
        api.RegisterItemClass("ItemBuildersWand", typeof(Items.ItemBuildersWand));
        api.RegisterItemClass("ItemGuardScepter", typeof(Items.ItemGuardScepter));
        api.RegisterItemClass("ItemColonyLedger", typeof(Items.ItemColonyLedger));

        AiTaskRegistry.Register<AiTaskSettlerFlee>("settlerflee");
        AiTaskRegistry.Register<AiTaskSettlerDefend>("settlerdefend");
        AiTaskRegistry.Register<AiTaskSettlerShelter>("settlershelter");
        AiTaskRegistry.Register<AiTaskSettlerSleep>("settlersleep");
        AiTaskRegistry.Register<AiTaskSettlerEat>("settlereat");
        AiTaskRegistry.Register<AiTaskSettlerCourier>("settlercourier");
        AiTaskRegistry.Register<AiTaskSettlerBuilder>("settlerbuilder");
        AiTaskRegistry.Register<AiTaskSettlerForester>("settlerforester");
        AiTaskRegistry.Register<AiTaskSettlerFarmer>("settlerfarmer");
        AiTaskRegistry.Register<AiTaskSettlerGuard>("settlerguard");
        AiTaskRegistry.Register<AiTaskSettlerPotter>("settlerpotter");
        AiTaskRegistry.Register<AiTaskSettlerSmelter>("settlersmelter");
        AiTaskRegistry.Register<AiTaskSettlerBlacksmith>("settlerblacksmith");
        AiTaskRegistry.Register<AiTaskSettlerArmourer>("settlerarmourer");
        AiTaskRegistry.Register<AiTaskSettlerWeaponsmith>("settlerweaponsmith");
        AiTaskRegistry.Register<AiTaskSettlerSawyer>("settlersawyer");
        AiTaskRegistry.Register<AiTaskSettlerCraftsman>("settlercraftsman");
        AiTaskRegistry.Register<AiTaskSettlerTailor>("settlertailor");
        AiTaskRegistry.Register<AiTaskSettlerJeweller>("settlerjeweller");
        AiTaskRegistry.Register<AiTaskSettlerMinter>("settlerminter");
        AiTaskRegistry.Register<AiTaskSettlerResearcher>("settlerresearcher");
        AiTaskRegistry.Register<AiTaskSettlerHealer>("settlerhealer");
        AiTaskRegistry.Register<AiTaskSettlerPriest>("settlerpriest");
        AiTaskRegistry.Register<AiTaskSettlerWorship>("settlerworship");
        AiTaskRegistry.Register<AiTaskSettlerRecover>("settlerrecover");
        AiTaskRegistry.Register<AiTaskSettlerFirefight>("settlerfirefight");
        AiTaskRegistry.Register<AiTaskSettlerLight>("settlerlight");
        AiTaskRegistry.Register<AiTaskSettlerExplorer>("settlerexplorer");
        AiTaskRegistry.Register<AiTaskSettlerGatherer>("settlergatherer");
        AiTaskRegistry.Register<AiTaskSettlerHunter>("settlerhunter");
        AiTaskRegistry.Register<AiTaskSettlerCook>("settlercook");
        AiTaskRegistry.Register<AiTaskSettlerProspector>("settlerprospector");
        AiTaskRegistry.Register<AiTaskSettlerMiner>("settlerminer");
        AiTaskRegistry.Register<AiTaskSettlerProspectorMiner>("settlerprospectorminer");
        AiTaskRegistry.Register<AiTaskSettlerQuarrier>("settlerquarrier");
        AiTaskRegistry.Register<AiTaskSettlerVisitor>("settlervisitor");
        AiTaskRegistry.Register<AiTaskSettlerBailiff>("settlerbailiff");
        AiTaskRegistry.Register<AiTaskSettlerChores>("settlerchores");
        AiTaskRegistry.Register<AiTaskSettlerDecorate>("settlerdecorate");
        AiTaskRegistry.Register<AiTaskSettlerIdle>("settleridle");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        var parsers = api.ChatCommands.Parsers;

        api.ChatCommands.Create("colony")
            .WithDescription("Vintage Colonies commands")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("info")
                .WithDescription("Show the colony you are standing in")
                .HandleWith(OnInfo)
            .EndSubCommand()
            .BeginSubCommand("citizens")
                .WithDescription("List the settlers of the colony you are standing in")
                .HandleWith(OnCitizens)
            .EndSubCommand()
            .BeginSubCommand("recruit")
                .WithDescription("Spawn a settler for your colony (MVP test command)")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalWord("name"))
                .HandleWith(OnRecruit)
            .EndSubCommand()
            .BeginSubCommand("wanderer")
                .WithDescription("Have a wanderer come to your colony now and ask to join (for testing)")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(OnWanderer)
            .EndSubCommand()
            .BeginSubCommand("family")
                .WithDescription("Families, for testing: couple two settlers, give a couple a child, or make a child grow up")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.WordRange("action", "couple", "child", "grow"), parsers.Word("settler"), parsers.OptionalWord("partner"))
                .HandleWith(OnFamily)
            .EndSubCommand()
            .BeginSubCommand("skills")
                .WithDescription("Skills, for testing: show a settler's skills, or set one skill to a level")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("settler"), parsers.OptionalWord("skill"), parsers.OptionalInt("level", 0))
                .HandleWith(OnSkills)
            .EndSubCommand()
            .BeginSubCommand("mood")
                .WithDescription("Happiness, for testing: show a settler's happiness and thoughts, or set it (0-100)")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("settler"), parsers.OptionalInt("happiness", -1))
                .HandleWith(OnMood)
            .EndSubCommand()
            .BeginSubCommand("traits")
                .WithDescription("Traits, for testing: show a settler's traits, add or remove one, or set its level")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("settler"), parsers.OptionalWordRange("action", "add", "remove", "level"), parsers.OptionalWord("trait"), parsers.OptionalInt("level", 0))
                .HandleWith(OnTraits)
            .EndSubCommand()
            .BeginSubCommand("invite")
                .WithDescription("Invite an online player to join your colony")
                .WithArgs(parsers.OnlinePlayer("player"))
                .HandleWith(OnInvite)
            .EndSubCommand()
            .BeginSubCommand("join")
                .WithDescription("Accept your pending colony invite")
                .HandleWith(OnJoin)
            .EndSubCommand()
            .BeginSubCommand("leave")
                .WithDescription("Leave the colony you are standing in")
                .HandleWith(OnLeave)
            .EndSubCommand()
            .BeginSubCommand("request")
                .WithDescription("Ask couriers to bring items from the warehouse to the chest you are looking at")
                .WithArgs(parsers.OptionalWord("item"), parsers.OptionalInt("amount", 1))
                .HandleWith(OnRequest)
            .EndSubCommand()
            .BeginSubCommand("requests")
                .WithDescription("List the open delivery requests of the colony you are standing in")
                .HandleWith(OnRequests)
            .EndSubCommand()
            .BeginSubCommand("reloadblueprints")
                .WithDescription("Re-read blueprint files after editing them, without restarting")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ =>
                {
                    sapi!.ModLoader.GetModSystem<ColonyManager>().Blueprints.Reload();
                    return TextCommandResult.Success(Lang.Get("createacolony:blueprints-reloaded", BlueprintLibrary.UserFolder));
                })
            .EndSubCommand()
            .BeginSubCommand("importmc")
                .WithDescription("Convert a MineColonies .blueprint from ModConfig/createacolony/import into a building level")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("file"), parsers.Word("buildingtype"), parsers.IntRange("level", 1, BlueprintLibrary.MaxLevels))
                .HandleWith(OnImportMc)
            .EndSubCommand()
            .BeginSubCommand("findblock")
                .WithDescription("List installed block codes matching a pattern, e.g. /colony findblock *door*oak*")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.Word("pattern"))
                .HandleWith(args =>
                {
                    string pattern = (string)args[0];
                    if (!pattern.Contains(':')) pattern = "game:" + pattern;
                    var codes = sapi!.World.SearchBlocks(new AssetLocation(pattern)).Select(b => b.Code.ToString()).OrderBy(c => c).ToList();
                    string list = codes.Count == 0 ? "-" : string.Join("\n", codes.Take(30)) + (codes.Count > 30 ? $"\n... {codes.Count - 30} more" : "");
                    sapi.Logger.Notification("[createacolony] findblock {0}: {1}", pattern, string.Join(", ", codes));
                    return TextCommandResult.Success(list);
                })
            .EndSubCommand()
            .BeginSubCommand("stuck")
                .WithDescription("List the settlers of the colony you are standing in that are stuck and building Willpower")
                .HandleWith(OnStuck)
            .EndSubCommand()
            .BeginSubCommand("pathdebug")
                .WithDescription("Show Willpower sparks over stuck settlers and log pathing in the server log (on/off, until restart)")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalBool("on"))
                .HandleWith(args =>
                {
                    var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
                    manager.PathDebug = args.Parsers[0].IsMissing ? !manager.PathDebug : (bool)args[0];
                    return TextCommandResult.Success(Lang.Get(manager.PathDebug ? "createacolony:pathdebug-on" : "createacolony:pathdebug-off"));
                })
            .EndSubCommand()
            .BeginSubCommand("handplace")
                .WithDescription("Let the Town Hall, huts and the Warehouse be placed by hand anywhere, for making blueprints (on/off, saved in the config)")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.OptionalBool("on"))
                .HandleWith(args =>
                {
                    var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
                    manager.Config.PlaceHutsByHand = args.Parsers[0].IsMissing ? !manager.Config.PlaceHutsByHand : (bool)args[0];
                    sapi.StoreModConfig(manager.Config, ColonyConfig.FileName);
                    sapi.ModLoader.GetModSystem<Network.ColonyNetwork>().SendHandPlace();
                    return TextCommandResult.Success(Lang.Get(manager.Config.PlaceHutsByHand ? "createacolony:handplace-on" : "createacolony:handplace-off"));
                })
            .EndSubCommand()
            .BeginSubCommand("claims")
                .WithDescription("List the land each building of the colony you are standing in claims; 'show' marks the edge, 'hide' takes the marks away")
                .WithArgs(parsers.OptionalWordRange("mode", "list", "show", "hide"))
                .HandleWith(OnClaims)
            .EndSubCommand()
            .BeginSubCommand("cancelrequest")
                .WithDescription("Cancel a delivery request by its number")
                .WithArgs(parsers.Int("number"))
                .HandleWith(OnCancelRequest)
            .EndSubCommand()
            .BeginSubCommand("quest")
                .WithDescription("Quest tools: list, complete <code> (marks it done, no rewards), reset (forgets your quests), reload (re-reads the quest files)")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(parsers.WordRange("action", "list", "complete", "reset", "reload"), parsers.OptionalWord("code"))
                .HandleWith(OnQuest)
            .EndSubCommand();
    }

    /// <summary>Highlight slot for the claim edge marks; the placement outline uses its own.</summary>
    const int ClaimMarkSlot = 7411;
    const int ClaimMarkMax = 6000;

    TextCommandResult OnClaims(TextCommandCallingArgs args)
    {
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var player = (IServerPlayer)args.Caller.Player;
        string mode = args.Parsers[0].IsMissing ? "list" : (string)args[0];
        if (mode == "hide")
        {
            sapi.World.HighlightBlocks(player, ClaimMarkSlot, new List<BlockPos>(), new List<int>());
            return TextCommandResult.Success(Lang.Get("createacolony:claims-hidden"));
        }

        var colony = manager.GetAt(args.Caller.Entity.Pos.AsBlockPos);
        if (colony == null) return TextCommandResult.Success(Lang.Get("createacolony:not-in-colony"));
        var lines = manager.ClaimLines(colony);
        string header = Lang.Get("createacolony:claims-header", colony.Name, manager.ClaimedColumns(colony).ToString("N0"), manager.ClaimAreas(colony).Count);
        if (mode != "show") return TextCommandResult.Success(header + "\n" + string.Join("\n", lines));

        var border = manager.ClaimBorder(colony, ClaimMarkMax);
        int blue = ColorUtil.ColorFromRgba(90, 170, 255, 120);
        sapi.World.HighlightBlocks(player, ClaimMarkSlot, border, border.Select(_ => blue).ToList(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        return TextCommandResult.Success(Lang.Get(border.Count >= ClaimMarkMax ? "createacolony:claims-shown-capped" : "createacolony:claims-shown", border.Count));
    }

    TextCommandResult OnStuck(TextCommandCallingArgs args)
    {
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(args.Caller.Entity.Pos.AsBlockPos);
        if (colony == null) return TextCommandResult.Success(Lang.Get("createacolony:not-in-colony"));
        var lines = new System.Collections.Generic.List<string>();
        foreach (var citizen in colony.Citizens)
        {
            if (!citizen.Alive || sapi.World.GetEntityById(citizen.EntityId) is not EntitySettler entity) continue;
            var will = entity.GetBehavior<EntityBehaviorColonyCitizen>()?.Willpower;
            if (will == null || will.Value <= 0) continue;
            string why = Lang.Get("createacolony:stuck-reason-" + will.Reason.ToString().ToLowerInvariant()) + (will.Trapped ? ", " + Lang.Get("createacolony:stuck-trapped") : "");
            lines.Add(Lang.Get("createacolony:stuck-line", citizen.Name, entity.Pos.AsBlockPos.ToLocalPosition(sapi), why, (int)(will.Fraction * 100)));
        }
        return TextCommandResult.Success(lines.Count == 0 ? Lang.Get("createacolony:stuck-none") : string.Join("\n", lines));
    }

    TextCommandResult OnQuest(TextCommandCallingArgs args)
    {
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var player = (IServerPlayer)args.Caller.Player;
        var colony = manager.HomeColony(player.PlayerUID);
        switch ((string)args[0])
        {
            case "list":
                var lines = manager.Quests.All.Select(q => q.Code + ": " + manager.Evaluate(player, colony, q, out _, out _));
                return TextCommandResult.Success(string.Join("\n", lines));
            case "complete":
                var quest = args.Parsers[1].IsMissing ? null : manager.Quests.Get((string)args[1]);
                if (quest == null) return TextCommandResult.Error("Unknown quest code");
                manager.ForceClaim(player, quest);
                return TextCommandResult.Success("Marked " + quest.Code + " as done");
            case "reset":
                manager.ResetQuests(player);
                return TextCommandResult.Success("Your quests, and your own colony's, are forgotten");
            case "reload":
                manager.ReloadQuests();
                return TextCommandResult.Success("Quest files re-read: " + manager.Quests.All.Count + " quests (players re-join to see changes)");
        }
        return TextCommandResult.Error("Unknown action");
    }

    TextCommandResult OnInfo(TextCommandCallingArgs args)
    {
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(args.Caller.Entity.Pos.AsBlockPos);
        if (colony == null) return TextCommandResult.Success(Lang.Get("createacolony:not-in-colony"));
        return TextCommandResult.Success(Lang.Get("createacolony:summary", colony.Name, colony.Level, colony.Citizens.Count(c => c.Alive && !c.Visitor), colony.Members.Count));
    }

    TextCommandResult OnCitizens(TextCommandCallingArgs args)
    {
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(args.Caller.Entity.Pos.AsBlockPos);
        if (colony == null) return TextCommandResult.Success(Lang.Get("createacolony:not-in-colony"));

        var lines = new List<string>();
        foreach (var c in colony.Citizens)
        {
            string state = !c.Alive ? Lang.Get("createacolony:state-dead")
                : c.IsDowned ? Lang.Get("createacolony:state-downed")
                : c.Visitor ? Lang.Get("createacolony:state-visitor")
                : c.Saturation >= 0 && c.Saturation < manager.Config.SaturationMax * manager.Config.EatBelowFraction ? Lang.Get("createacolony:state-hungry")
                : Lang.Get("createacolony:state-ok");
            int food = c.Saturation < 0 ? 100 : (int)(100 * c.Saturation / manager.Config.SaturationMax);
            string bed = c.HomeBedPos != null ? Lang.Get("createacolony:has-bed") : Lang.Get("createacolony:no-bed");
            lines.Add(Lang.Get("createacolony:citizen-line", c.Name, state, food, bed));
        }
        return TextCommandResult.Success(lines.Count == 0 ? Lang.Get("createacolony:no-citizens") : string.Join("\n", lines));
    }

    TextCommandResult OnRecruit(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetOwnedBy(player.PlayerUID);
        if (colony == null) return TextCommandResult.Error(Lang.Get("createacolony:no-colony"));

        var record = manager.SpawnSettler(colony, args[0] as string);
        if (record == null) return TextCommandResult.Error("Settler entity type not found, see server log");

        return TextCommandResult.Success(Lang.Get("createacolony:recruited", record.Name));
    }

    /// <summary>/colony mood Jon: Jon's happiness and thoughts; /colony mood Jon 10: sets it to 10 (a breakdown follows soon).</summary>
    TextCommandResult OnMood(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetOwnedBy(player.PlayerUID);
        if (colony == null) return TextCommandResult.Error(Lang.Get("createacolony:no-colony"));
        if (!manager.Config.Happiness) return TextCommandResult.Error(Lang.Get("createacolony:mood-cmd-off"));

        string name = (string)args[0];
        var settler = colony.Citizens.Find(c => c.Alive && !c.Visitor && !c.IsChild
            && (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (settler == null) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-notfound", name));
        int set = (int)args[1];
        if (set >= 0)
        {
            settler.Happiness = Math.Clamp(set, 0, 100);
            manager.MarkDirty(colony);
        }
        float mood = manager.HappinessOf(settler);
        var thoughts = manager.ThoughtsOf(colony, settler).OrderByDescending(t => t.Mood).Select(manager.ThoughtText);
        return TextCommandResult.Success(Lang.Get("createacolony:mood-cmd-line", settler.Name, (int)Math.Round(mood), Lang.Get("createacolony:mood-" + manager.MoodOf(mood)),
            (int)Math.Round(manager.MoodTarget(colony, settler)), string.Join(", ", thoughts)));
    }

    /// <summary>/colony skills Jon: lists Jon's skills; /colony skills Jon farming 7: sets Farming to level 7.</summary>
    TextCommandResult OnSkills(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetOwnedBy(player.PlayerUID);
        if (colony == null) return TextCommandResult.Error(Lang.Get("createacolony:no-colony"));
        if (!manager.Config.Skills) return TextCommandResult.Error(Lang.Get("createacolony:skills-cmd-off"));

        string name = (string)args[0];
        var settler = colony.Citizens.Find(c => c.Alive && !c.Visitor
            && (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (settler == null) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-notfound", name));

        if (args[1] is string code && code.Length > 0)
        {
            var skill = manager.Skill(code.ToLowerInvariant());
            if (skill == null) return TextCommandResult.Error(Lang.Get("createacolony:skills-cmd-badskill", code, string.Join(", ", manager.Skills().Select(s => s.Code))));
            int level = Math.Clamp((int)args[2], 0, manager.MaxLevel);
            settler.SkillXp[skill.Code] = manager.XpForLevel(level);
            manager.MarkDirty(colony);
            return TextCommandResult.Success(Lang.Get("createacolony:skills-cmd-set", settler.Name, Lang.Get("createacolony:skill-" + skill.Code), level));
        }

        var lines = manager.SkillInfos(colony, settler).Where(s => s.Level > 0)
            .Select(s => Lang.Get("createacolony:skill-level", Lang.Get("createacolony:skill-" + s.Code), s.Usable < s.Level ? Lang.Get("createacolony:skill-using", s.Level, s.Usable) : s.Level.ToString()));
        return TextCommandResult.Success(lines.Any() ? Lang.Get("createacolony:skills-cmd-line", settler.Name, string.Join(", ", lines)) : Lang.Get("createacolony:skills-cmd-none", settler.Name));
    }

    /// <summary>/colony traits Jon: lists Jon's traits; /colony traits Jon add brave 5, remove lazy, level tough 10.</summary>
    TextCommandResult OnTraits(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetOwnedBy(player.PlayerUID);
        if (colony == null) return TextCommandResult.Error(Lang.Get("createacolony:no-colony"));
        if (!manager.Config.Traits) return TextCommandResult.Error(Lang.Get("createacolony:traits-cmd-off"));

        string name = (string)args[0];
        var settler = colony.Citizens.Find(c => c.Alive && !c.Visitor
            && (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (settler == null) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-notfound", name));

        if (args[1] is string action && action.Length > 0)
        {
            string code = (args[2] as string ?? "").ToLowerInvariant();
            var trait = manager.Trait(code);
            if (trait == null) return TextCommandResult.Error(Lang.Get("createacolony:traits-cmd-badtrait", code, string.Join(", ", manager.TraitDefs().Select(t => t.Code))));
            int level = Math.Clamp((int)args[3], 0, manager.TraitLevelMax);
            string traitName = Lang.Get("createacolony:trait-" + trait.Code);
            if (action == "remove")
            {
                return manager.RemoveTrait(colony, settler, trait.Code) ? TextCommandResult.Success(Lang.Get("createacolony:traits-cmd-removed", settler.Name, traitName))
                    : TextCommandResult.Error(Lang.Get("createacolony:traits-cmd-missing", settler.Name, traitName));
            }
            if (action == "level" && manager.TraitOf(settler, trait.Code) is { } have)
            {
                have.Level = level;
                have.Xp = 0;
                manager.MarkDirty(colony);
            }
            else manager.AddTrait(colony, settler, trait.Code, level, announce: false);
            return TextCommandResult.Success(Lang.Get("createacolony:traits-cmd-set", settler.Name, traitName, level));
        }

        if (settler.Traits.Count == 0) return TextCommandResult.Success(Lang.Get("createacolony:traits-cmd-none", settler.Name));
        string score = (manager.TraitScore(settler) >= 0 ? "+" : "") + manager.TraitScore(settler);
        return TextCommandResult.Success(Lang.Get("createacolony:traits-cmd-line", settler.Name, score, manager.TraitsText(settler)));
    }

    /// <summary>
    /// /colony family couple A B, /colony family child A (A and their partner have a child), /colony family grow A:
    /// family events at once, for testing. Settlers are found by first or full name.
    /// </summary>
    TextCommandResult OnFamily(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetOwnedBy(player.PlayerUID);
        if (colony == null) return TextCommandResult.Error(Lang.Get("createacolony:no-colony"));

        CitizenRecord? Find(string? name) => name == null ? null : colony.Citizens.Find(c => c.Alive && !c.Visitor
            && (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase)));
        string action = (string)args[0];
        var a = Find(args[1] as string);
        if (a == null) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-notfound", args[1]));
        double now = sapi.World.Calendar.TotalDays;

        switch (action)
        {
            case "couple":
                var b = Find(args[2] as string);
                if (b == null) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-notfound", args[2] ?? ""));
                if (a == b || a.IsChild || b.IsChild || manager.PartnerOf(colony, a) is { Alive: true } || manager.PartnerOf(colony, b) is { Alive: true }
                    || a.ParentIds.Contains(b.CitizenId) || b.ParentIds.Contains(a.CitizenId) || a.ParentIds.Intersect(b.ParentIds).Any())
                {
                    return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-bad-couple"));
                }
                manager.MakeCouple(colony, a, b);
                return TextCommandResult.Success(Lang.Get("createacolony:family-cmd-couple", a.Name, b.Name));
            case "child":
                if (manager.PartnerOf(colony, a) is not { Alive: true } partner) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-cant", Lang.Get("createacolony:citizen-no-family")));
                // The test command skips the waiting time and the dice, but not the real limits.
                a.LastChildTotalDays = partner.LastChildTotalDays = now - manager.Config.FamilyChildCooldownDays - 1;
                if (manager.ChildBlocker(colony, a, partner, now) is { } why) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-cant", Lang.Get(why)));
                var mother = a.Sex == "female" ? a : partner;
                var child = manager.HaveChild(colony, mother, mother == a ? partner : a);
                return child == null ? TextCommandResult.Error("Settler entity type not found, see server log") : TextCommandResult.Success(Lang.Get("createacolony:family-cmd-child", child.Name));
            default:
                if (!a.IsChild) return TextCommandResult.Error(Lang.Get("createacolony:family-cmd-nochild", a.Name));
                manager.GrowUp(colony, a);
                return TextCommandResult.Success(Lang.Get("createacolony:family-cmd-grow", a.Name));
        }
    }

    TextCommandResult OnWanderer(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetOwnedBy(player.PlayerUID);
        if (colony == null) return TextCommandResult.Error(Lang.Get("createacolony:no-colony"));
        var record = manager.SpawnWanderer(colony);
        return record == null ? TextCommandResult.Error(Lang.Get("createacolony:wanderer-no-spot")) : TextCommandResult.Success();
    }

    TextCommandResult OnInvite(TextCommandCallingArgs args)
    {
        var inviter = (IServerPlayer)args.Caller.Player;
        var invitee = (IServerPlayer)args[0];
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();

        // Invite into the colony you stand in if you manage it, else into the one you own.
        var colony = manager.GetAt(inviter.Entity.Pos.AsBlockPos) is { } here && here.CanManage(inviter.PlayerUID)
            ? here
            : manager.GetOwnedBy(inviter.PlayerUID);
        if (colony == null) return TextCommandResult.Error(Lang.Get("createacolony:no-colony"));
        if (colony.RoleOf(invitee.PlayerUID) >= ColonyRole.Member) return TextCommandResult.Error(Lang.Get("createacolony:already-member", invitee.PlayerName));

        pendingInvites[invitee.PlayerUID] = colony.ColonyId;
        invitee.SendMessage(0, Lang.Get("createacolony:invited", inviter.PlayerName, colony.Name), EnumChatType.Notification);
        return TextCommandResult.Success(Lang.Get("createacolony:invite-sent", invitee.PlayerName));
    }

    TextCommandResult OnJoin(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        if (!pendingInvites.Remove(player.PlayerUID, out var colonyId) || manager.GetById(colonyId) is not { } colony)
        {
            return TextCommandResult.Error(Lang.Get("createacolony:no-invite"));
        }

        manager.SetMemberRole(colony, player.PlayerUID, player.PlayerName, ColonyRole.Member);
        return TextCommandResult.Success(Lang.Get("createacolony:joined", colony.Name));
    }

    TextCommandResult OnLeave(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(player.Entity.Pos.AsBlockPos);
        if (colony == null || colony.RoleOf(player.PlayerUID) == ColonyRole.Visitor) return TextCommandResult.Error(Lang.Get("createacolony:not-member"));
        if (colony.OwnerUid == player.PlayerUID) return TextCommandResult.Error(Lang.Get("createacolony:owner-cannot-leave"));

        manager.SetMemberRole(colony, player.PlayerUID, player.PlayerName, ColonyRole.Visitor);
        return TextCommandResult.Success(Lang.Get("createacolony:left", colony.Name));
    }

    TextCommandResult OnRequest(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        if (args[0] is not string code || code.Length == 0) return TextCommandResult.Error(Lang.Get("createacolony:request-usage"));
        int amount = Math.Max(1, (int)args[1]);

        var sel = player.CurrentBlockSelection;
        var colony = sel == null ? null : manager.GetAt(sel.Position);
        if (sel == null || colony == null || sapi.World.BlockAccessor.GetBlockEntity(sel.Position) is not BlockEntityContainer)
        {
            return TextCommandResult.Error(Lang.Get("createacolony:request-not-container"));
        }
        if (!colony.CanManage(player.PlayerUID)) return TextCommandResult.Error(Lang.Get("createacolony:denied-officer"));

        if (!code.Contains(':')) code = "game:" + code;
        var request = manager.AddRequest(colony, code, amount, sel.Position, player.PlayerName);
        return TextCommandResult.Success(Lang.Get("createacolony:request-created", request.RequestId, amount, code));
    }

    TextCommandResult OnRequests(TextCommandCallingArgs args)
    {
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(args.Caller.Entity.Pos.AsBlockPos);
        if (colony == null) return TextCommandResult.Success(Lang.Get("createacolony:not-in-colony"));
        if (colony.Requests.Count == 0) return TextCommandResult.Success(Lang.Get("createacolony:request-none"));

        var lines = colony.Requests.Select(r => Lang.Get("createacolony:request-line", r.RequestId, r.ItemCode, r.Delivered, r.Quantity,
            r.LeasedByCitizenId != 0 ? Lang.Get("createacolony:request-leased") : ""));
        return TextCommandResult.Success(string.Join("\n", lines));
    }

    static bool SafeName(string name) =>
        name.Length > 0 && !name.Contains("..") && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
        && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;

    TextCommandResult OnImportMc(TextCommandCallingArgs args)
    {
        string file = (string)args[0];
        string type = ((string)args[1]).ToLowerInvariant();
        int level = (int)args[2];

        // Only plain names: the file must be in the import folder and the type a folder name, nothing like "../".
        if (!SafeName(file) || !SafeName(type)) return TextCommandResult.Error(Lang.Get("createacolony:import-badname"));
        if (!file.EndsWith(".blueprint")) file += ".blueprint";
        string input = System.IO.Path.Combine(MineColoniesImporter.ImportFolder, file);
        if (!System.IO.File.Exists(input)) return TextCommandResult.Error(Lang.Get("createacolony:import-missing", input));
        string output = System.IO.Path.Combine(BlueprintLibrary.UserFolder, type, $"level{level}.json");

        MineColoniesImporter.Result result;
        try
        {
            result = new MineColoniesImporter(sapi!, MineColoniesImporter.LoadConfig(sapi!)).Convert(input, type, output);
        }
        catch (Exception e)
        {
            sapi!.Logger.Error("[createacolony] Import of {0} failed: {1}", input, e);
            return TextCommandResult.Error(Lang.Get("createacolony:import-failed", e.Message));
        }

        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        manager.Blueprints.Reload();
        string unmatched = result.Unmatched.Count == 0
            ? Lang.Get("createacolony:import-all-matched")
            : Lang.Get("createacolony:import-unmatched", string.Join(", ", result.Unmatched.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")));
        sapi.Logger.Notification("[createacolony] Imported {0} as {1} level {2}: {3} blocks. {4}", file, type, level, result.Blocks, unmatched);
        return TextCommandResult.Success(Lang.Get("createacolony:import-done", file, Lang.Get("createacolony:building-" + type), level, result.Blocks) + "\n" + unmatched);
    }

    TextCommandResult OnCancelRequest(TextCommandCallingArgs args)
    {
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi!.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(player.Entity.Pos.AsBlockPos);
        if (colony == null) return TextCommandResult.Success(Lang.Get("createacolony:not-in-colony"));
        if (!colony.CanManage(player.PlayerUID)) return TextCommandResult.Error(Lang.Get("createacolony:denied-officer"));

        int id = (int)args[0];
        var request = colony.Requests.Find(r => r.RequestId == id);
        if (request == null) return TextCommandResult.Error(Lang.Get("createacolony:request-bad-id"));
        manager.CancelRequest(colony, request);
        return TextCommandResult.Success(Lang.Get("createacolony:request-cancelled", id));
    }
}
