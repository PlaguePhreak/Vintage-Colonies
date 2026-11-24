using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CreateAColony.Colonies;
using CreateAColony.Settlers;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CreateAColony.Report;

/// <summary>Tells the client that a report was written, so it saves its own errors next to it.</summary>
[ProtoContract]
public class ReportFilePacket
{
    [ProtoMember(1)] public string BaseName = "";
    [ProtoMember(2)] public bool Auto;
}

/// <summary>
/// /colony report: writes the colony's state and the mod's recent errors to a text file the player can upload.
/// Read only: it never changes the world or the save. Also saves a report on its own after a serious mod error,
/// at most once every <see cref="AutoReportMinutes"/> minutes.
/// </summary>
public class ColonyReportSystem : ModSystem
{
    const string ChannelName = "createacolony-report";
    const int MaxErrors = 200;
    const double AutoReportMinutes = 10;

    record LogLine(DateTime Time, EnumLogType Type, string Text);

    readonly LinkedList<LogLine> errors = new();
    readonly object errorsLock = new();
    ICoreAPI? api;
    ICoreServerAPI? sapi;
    ICoreClientAPI? capi;
    IServerNetworkChannel? serverChannel;
    bool seriousPending;
    bool writing;
    DateTime lastAutoReport = DateTime.MinValue;
    DateTime lastClientAuto = DateTime.MinValue;

    public static string ReportFolder => Path.Combine(GamePaths.DataPath, "ModData", "createacolony", "reports");

    public override void Start(ICoreAPI api)
    {
        this.api = api;
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<ReportFilePacket>();
        api.Logger.EntryAdded += OnLogEntry;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network.GetChannel(ChannelName);
        // Never write a report from inside the log handler: check for a serious error every few seconds instead.
        api.Event.RegisterGameTickListener(_ => CheckAutoReport(), 5000);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Network.GetChannel(ChannelName).SetMessageHandler<ReportFilePacket>(p => WriteClientFile(p.BaseName, p.Auto));
        api.Event.RegisterGameTickListener(_ =>
        {
            if (!seriousPending || writing) return;
            seriousPending = false;
            if ((DateTime.Now - lastClientAuto).TotalMinutes < AutoReportMinutes) return;
            lastClientAuto = DateTime.Now;
            WriteClientFile("report-" + Stamp() + "-auto", true);
        }, 5000);
    }

    public override void Dispose()
    {
        if (api != null) api.Logger.EntryAdded -= OnLogEntry;
    }

    void OnLogEntry(EnumLogType logType, string message, params object[] args)
    {
        if (writing) return;
        if (logType != EnumLogType.Error && logType != EnumLogType.Fatal && logType != EnumLogType.Warning) return;
        string text;
        try { text = args == null || args.Length == 0 ? message : string.Format(message, args); }
        catch { text = message; }
        if (text == null || text.IndexOf("createacolony", StringComparison.OrdinalIgnoreCase) < 0) return;
        lock (errorsLock)
        {
            errors.AddLast(new LogLine(DateTime.Now, logType, text));
            while (errors.Count > MaxErrors) errors.RemoveFirst();
        }
        // Serious: an error with a stack trace in the mod's own code, or a fatal one.
        if (logType == EnumLogType.Fatal || (logType == EnumLogType.Error && text.Contains("CreateAColony."))) seriousPending = true;
    }

    void CheckAutoReport()
    {
        if (!seriousPending || writing || sapi == null) return;
        seriousPending = false;
        if ((DateTime.Now - lastAutoReport).TotalMinutes < AutoReportMinutes) return;
        lastAutoReport = DateTime.Now;
        try
        {
            var manager = sapi.ModLoader.GetModSystem<ColonyManager>();
            var colonies = AllColonies(manager);
            string path = WriteServerReport(null, Lang.Get("createacolony:report-auto-note"), colonies, "-auto", out string baseName);
            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (colonies.Any(c => c.Members.Any(m => m.PlayerUid == player.PlayerUID)))
                {
                    serverChannel?.SendPacket(new ReportFilePacket { BaseName = baseName, Auto = true }, player);
                    player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("createacolony:report-auto-saved", path), EnumChatType.Notification);
                }
            }
        }
        catch (Exception e)
        {
            writing = true;
            try { sapi.Logger.Warning("[Vintage Colonies report] Auto report failed: {0}", e.Message); } finally { writing = false; }
        }
    }

    /// <summary>Handles /colony report [all] [note...].</summary>
    public TextCommandResult OnReportCommand(TextCommandCallingArgs args)
    {
        if (sapi == null) return TextCommandResult.Error("Server only");
        var player = (IServerPlayer)args.Caller.Player;
        var manager = sapi.ModLoader.GetModSystem<ColonyManager>();
        string note = args.Parsers[0].IsMissing ? "" : ((string)args[0] ?? "").Trim();
        bool all = note.Equals("all", StringComparison.OrdinalIgnoreCase) || note.StartsWith("all ", StringComparison.OrdinalIgnoreCase);
        if (all) note = note.Length > 3 ? note.Substring(4).Trim() : "";

        List<Colony> colonies;
        if (all)
        {
            colonies = AllColonies(manager).Where(c => c.Members.Any(m => m.PlayerUid == player.PlayerUID) || player.HasPrivilege(Privilege.controlserver)).ToList();
        }
        else
        {
            var here = manager.GetAt(player.Entity.Pos.AsBlockPos) ?? manager.HomeColony(player.PlayerUID);
            colonies = here == null ? new List<Colony>() : new List<Colony> { here };
        }

        try
        {
            string path = WriteServerReport(player, note, colonies, "", out string baseName);
            serverChannel?.SendPacket(new ReportFilePacket { BaseName = baseName }, player);
            return TextCommandResult.Success(Lang.Get("createacolony:report-saved", path));
        }
        catch (Exception e)
        {
            return TextCommandResult.Error(Lang.Get("createacolony:report-failed", e.Message));
        }
    }

    static List<Colony> AllColonies(ColonyManager manager)
    {
        var field = typeof(ColonyManager).GetField("colonies", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(manager) is Dictionary<string, Colony> dict ? dict.Values.ToList() : new List<Colony>();
    }

    static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

    string WriteServerReport(IServerPlayer? player, string note, List<Colony> colonies, string suffix, out string baseName)
    {
        var api = sapi!;
        var manager = api.ModLoader.GetModSystem<ColonyManager>();
        var sb = new StringBuilder();
        writing = true;
        try
        {
            Section(sb, "Report", () =>
            {
                sb.AppendLine("Note: " + (string.IsNullOrEmpty(note) ? "-" : note));
                sb.AppendLine("Written: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Vintage Colonies: " + Mod.Info.Version);
                sb.AppendLine("Game: " + GameVersion.OverallVersion);
                sb.AppendLine("By: " + (player?.PlayerName ?? "(automatic)") + (player != null ? " at " + player.Entity.Pos.AsBlockPos.ToLocalPosition(api) : ""));
                sb.AppendLine("In-game date: " + api.World.Calendar.PrettyDate() + " (day " + api.World.Calendar.TotalDays.ToString("0.00") + ", season " + api.World.Calendar.GetSeason(player?.Entity.Pos.AsBlockPos ?? new BlockPos(0)) + ")");
                sb.AppendLine("Mods: " + string.Join(", ", api.ModLoader.Mods.Select(m => m.Info.ModID + " " + m.Info.Version)));
            });

            if (colonies.Count == 0) sb.AppendLine("\nNo colony here. Stand inside your colony, or use /colony report all.");
            foreach (var colony in colonies) WriteColony(sb, api, manager, colony);

            Section(sb, "Recent mod errors and warnings (this session, newest last)", () =>
            {
                List<LogLine> copy;
                lock (errorsLock) copy = errors.ToList();
                if (copy.Count == 0) sb.AppendLine("None.");
                foreach (var e in copy) sb.AppendLine($"[{e.Time:HH:mm:ss}] {e.Type}: {e.Text}");
            });
        }
        finally { writing = false; }

        Directory.CreateDirectory(ReportFolder);
        baseName = "report-" + Stamp() + suffix;
        string path = Path.Combine(ReportFolder, baseName + ".txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    static void Section(StringBuilder sb, string title, Action body)
    {
        sb.AppendLine();
        sb.AppendLine("=== " + title + " ===");
        try { body(); }
        catch (Exception e) { sb.AppendLine("(This part could not be read: " + e.GetType().Name + ": " + e.Message + ")"); }
    }

    static void WriteColony(StringBuilder sb, ICoreServerAPI api, ColonyManager manager, Colony colony)
    {
        Section(sb, "Colony " + colony.Name, () => DumpFields(sb, colony, ""));

        Section(sb, "Huts (" + colony.Buildings.Count + ")", () =>
        {
            foreach (var b in colony.Buildings)
            {
                sb.AppendLine($"- {b.Type} level {b.Level} at {b.HutPos.ToLocalPosition(api)} (id {b.BuildingId})");
                if (b.HasWorkOrder || b.Removing) sb.AppendLine("    Build: " + manager.BuildStatus.GetValueOrDefault(b.BuildingId, "queued"));
                if (manager.StuckStep.TryGetValue(b.BuildingId, out var stuckAt)) sb.AppendLine($"    Build stuck at {stuckAt.ToLocalPosition(api)}: {api.World.BlockAccessor.GetBlock(stuckAt).Code}");
                DumpFields(sb, b, "    ");
            }
        });

        Section(sb, "Settlers (" + colony.Citizens.Count + ")", () =>
        {
            foreach (var c in colony.Citizens)
            {
                sb.AppendLine($"- {c.Name} {c.Surname} (id {c.CitizenId}), job {c.JobCode ?? "none"}");
                try
                {
                    if (manager.WorkerStatus.TryGetValue(c.CitizenId, out var status)) sb.AppendLine("    Status: " + status);
                    if (api.World.GetEntityById(c.EntityId) is EntitySettler entity)
                    {
                        var health = entity.GetBehavior<EntityBehaviorHealth>();
                        sb.AppendLine($"    Loaded at {entity.Pos.AsBlockPos.ToLocalPosition(api)}, health {health?.Health:0.#}/{health?.MaxHealth:0.#}, state {entity.State}");
                        var hand = entity.RightHandItemSlot?.Itemstack;
                        if (hand != null) sb.AppendLine("    Holding: " + hand.StackSize + "x " + hand.Collectible.Code);
                        var will = entity.GetBehavior<EntityBehaviorColonyCitizen>()?.Willpower;
                        if (will != null && will.Value > 0) sb.AppendLine($"    STUCK: {will.Reason}{(will.Trapped ? ", trapped" : "")}, Willpower {(int)(will.Fraction * 100)}%");
                    }
                    else sb.AppendLine("    Not loaded");
                }
                catch (Exception e) { sb.AppendLine("    (live data failed: " + e.Message + ")"); }
                DumpFields(sb, c, "    ");
            }
        });

        Section(sb, "Courier requests (" + colony.Requests.Count + ")", () =>
        {
            foreach (var r in colony.Requests) { sb.AppendLine("-"); DumpFields(sb, r, "    "); }
        });
    }

    /// <summary>Writes every simple field of an object (numbers, text, switches, positions; lists as counts).</summary>
    static void DumpFields(StringBuilder sb, object obj, string indent)
    {
        var parts = new List<string>();
        foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v;
            try { v = f.GetValue(obj); } catch { continue; }
            string? s = v switch
            {
                null => null,
                string str => str.Length == 0 ? null : (str.Length > 120 ? str.Substring(0, 120) + "..." : str),
                bool bo => bo ? "yes" : null,
                byte[] => null,
                double d => d == 0 ? null : d.ToString("0.###"),
                float fl => fl == 0 ? null : fl.ToString("0.###"),
                int i => i == 0 ? null : i.ToString(),
                long l => l == 0 ? null : l.ToString(),
                Enum e => e.ToString(),
                BlockPos p => p.ToString(),
                Vec3d p => $"{p.X:0.#},{p.Y:0.#},{p.Z:0.#}",
                ICollection col => col.Count == 0 ? null : "[" + col.Count + "]",
                _ when v.GetType().IsPrimitive => v.ToString(),
                _ => null
            };
            if (s != null) parts.Add(f.Name + "=" + s);
        }
        // Several fields per line keeps the file short enough to read.
        for (int i = 0; i < parts.Count; i += 6) sb.AppendLine(indent + string.Join("; ", parts.Skip(i).Take(6)));
    }

    void WriteClientFile(string baseName, bool auto)
    {
        if (capi == null) return;
        writing = true;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Vintage Colonies client report" + (auto ? " (automatic, after a serious error)" : ""));
            sb.AppendLine("Written: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Vintage Colonies: " + Mod.Info.Version + ", game " + GameVersion.OverallVersion);
            sb.AppendLine("Screen " + capi.Render.FrameWidth + "x" + capi.Render.FrameHeight);
            sb.AppendLine("Mods: " + string.Join(", ", capi.ModLoader.Mods.Select(m => m.Info.ModID + " " + m.Info.Version)));
            sb.AppendLine();
            sb.AppendLine("=== Recent mod errors and warnings on your side (windows, map, quest book) ===");
            List<LogLine> copy;
            lock (errorsLock) copy = errors.ToList();
            if (copy.Count == 0) sb.AppendLine("None.");
            foreach (var e in copy) sb.AppendLine($"[{e.Time:HH:mm:ss}] {e.Type}: {e.Text}");
            Directory.CreateDirectory(ReportFolder);
            string path = Path.Combine(ReportFolder, baseName + "-client.txt");
            File.WriteAllText(path, sb.ToString());
            if (!auto) capi.ShowChatMessage(Lang.Get("createacolony:report-client-saved", path));
        }
        catch (Exception e) { capi.ShowChatMessage(Lang.Get("createacolony:report-failed", e.Message)); }
        finally { writing = false; }
    }
}
