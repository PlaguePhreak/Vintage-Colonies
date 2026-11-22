using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Blocks;
using CreateAColony.Corpses;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Graveyard and its Undertaker (Aaron's design, 0.179.0). The player puts grave plot markers on the ground
/// near the Graveyard; each level allows more graves (GravesByLevel). The undertaker walks out (up to the Work
/// area range) to the bodies of settlers who died, wraps each in a shroud and carries it home. Their belongings
/// go into the Graveyard's storage, from where couriers take them to the warehouse. It digs a grave at a free
/// plot with a shovel, lays the body in, fills it back and puts up a grave marker (wood, stone from
/// GraveyardStoneLevel) that names who lies there. Then it holds a small funeral: grief fades a little. A
/// priest still holds the full funeral at the grave afterwards (ColonyManager.Church.cs).
/// While a colony has a Graveyard, bodies are left for the undertaker (taking their things leaves the body),
/// a body lying around upsets settlers who see it, and one left unburied for days upsets everyone.
/// Without a Graveyard nothing changes.
/// </summary>
public partial class ColonyManager
{
    public const string ShroudNameAttr = "shroudName";
    const string ShroudColonyAttr = "shroudColony";
    const string ShroudCitizenAttr = "shroudCitizen";
    const string ShroudThingsAttr = "shroudThings";
    /// <summary>Set on a corpse the undertaker took away, so its going is not the body being lost.</summary>
    public const string UndertakenAttr = "undertaken";

    /// <summary>Bodies and grave plots an undertaker is on its way to, so two undertakers do not both go: corpse entity id or plot, to citizen id and until when.</summary>
    readonly Dictionary<long, (int CitizenId, long UntilMs)> bodyClaims = new();
    readonly Dictionary<BlockPos, (int CitizenId, long UntilMs)> plotClaims = new();
    readonly Dictionary<int, (long Until, List<BlockPos> Plots, int Graves)> graveyardScans = new();
    (long Until, List<EntityColonyCorpse> Bodies) loadedBodies = (0, new());
    const long ClaimMs = 120_000;

    public static bool IsShroudedBody(ItemStack? stack) => stack?.Collectible?.Code is { Domain: "createacolony", Path: "shroudedbody" };
    static bool IsGravePlot(Block block) => block.Code?.Domain == "createacolony" && block.FirstCodePart() == "graveplot";
    static bool IsGraveMarker(Block block) => block.Code?.Domain == "createacolony" && block.FirstCodePart() == "gravemarker";

    IEnumerable<Building> Graveyards(Colony colony) => colony.Buildings.Where(b => b.Type == BuildingTypes.Graveyard && !b.Removing);
    public bool HasGraveyard(Colony colony) => Graveyards(colony).Any();

    static int ByLevel(int[] values, Building hut, int fallback) =>
        values.Length == 0 ? fallback : values[Math.Clamp(Math.Max(1, hut.Level), 1, values.Length) - 1];

    /// <summary>Undertakers a Graveyard takes: 1, 1, 2, 2, 3 by level.</summary>
    public static int UndertakerSlots(Building hut) => hut.Level >= 5 ? 3 : hut.Level >= 3 ? 2 : 1;

    /// <summary>How many graves a Graveyard holds at its level.</summary>
    public int GraveLimit(Building hut) => Math.Max(1, ByLevel(Config.GravesByLevel, hut, 8));

    // ---- Looking around ----------------------------------------------------------------------

    /// <summary>The grave plot markers near the Graveyard (nearest first) and how many graves are already there.</summary>
    (List<BlockPos> Plots, int Graves) ScanGraveyard(Building hut, bool fresh = false)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (!fresh && graveyardScans.TryGetValue(hut.BuildingId, out var known) && now < known.Until) return (known.Plots, known.Graves);
        int r = Math.Max(4, Config.GraveyardPlotRadius);
        var c = hut.HutPos;
        var plots = new List<BlockPos>();
        int graves = 0;
        sapi.World.BlockAccessor.WalkBlocks(new BlockPos(c.X - r, c.Y - 6, c.Z - r), new BlockPos(c.X + r, c.Y + 6, c.Z + r), (block, x, y, z) =>
        {
            if (IsGravePlot(block)) plots.Add(new BlockPos(x, y, z));
            else if (IsGraveMarker(block)) graves++;
        }, true);
        plots = plots.OrderBy(p => p.DistanceSqTo(c.X, c.Y, c.Z)).ToList();
        graveyardScans[hut.BuildingId] = (now + 5000, plots, graves);
        return (plots, graves);
    }

    public void ForgetGraveyardScan(Building hut) => graveyardScans.Remove(hut.BuildingId);

    /// <summary>Plots still free for a new grave, within the level's limit: open graves first, then the nearest.</summary>
    List<BlockPos> FreePlots(Building hut, CitizenRecord undertaker)
    {
        var (plots, graves) = ScanGraveyard(hut);
        int room = GraveLimit(hut) - graves;
        if (room <= 0) return new();
        long now = sapi.World.ElapsedMilliseconds;
        var open = hut.OpenGraves.Select(g => g.Pos).ToList();
        return plots.Take(room)
            .Where(p => !plotClaims.TryGetValue(p, out var claim) || claim.CitizenId == undertaker.CitizenId || claim.UntilMs < now)
            .OrderByDescending(p => open.Any(o => o.Equals(p)))
            .ToList();
    }

    /// <summary>The plot to bury this settler in: an open grave, one beside their family from GraveyardFamilyLevel, or the nearest.</summary>
    public BlockPos? PlotFor(Colony colony, Building hut, CitizenRecord undertaker, int deadId)
    {
        var free = FreePlots(hut, undertaker);
        if (free.Count == 0) return null;
        var open = free.FirstOrDefault(p => hut.OpenGraves.Any(g => g.Pos.Equals(p)));
        var plot = open;
        if (plot == null && hut.Level >= Config.GraveyardFamilyLevel && colony.Citizens.Find(c => c.CitizenId == deadId) is { } dead)
        {
            var family = colony.Citizens.Where(c => c.GravePos != null && IsFamily(c, dead)).Select(c => c.GravePos!).ToList();
            if (family.Count > 0) plot = free.OrderBy(p => family.Min(g => p.DistanceSqTo(g.X, g.Y, g.Z))).First();
        }
        plot ??= free[0];
        plotClaims[plot.Copy()] = (undertaker.CitizenId, sapi.World.ElapsedMilliseconds + ClaimMs);
        return plot;
    }

    public int FreePlotCount(Building hut, CitizenRecord undertaker) => FreePlots(hut, undertaker).Count;

    static bool IsFamily(CitizenRecord a, CitizenRecord b) =>
        a.PartnerId == b.CitizenId || b.PartnerId == a.CitizenId
        || a.ParentIds.Contains(b.CitizenId) || b.ParentIds.Contains(a.CitizenId)
        || (a.ParentIds.Count > 0 && a.ParentIds.Intersect(b.ParentIds).Any());

    /// <summary>Settler bodies in loaded land (not a knocked-out settler's things). Looked up every few seconds.</summary>
    List<EntityColonyCorpse> LoadedBodies()
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (now < loadedBodies.Until) return loadedBodies.Bodies;
        var bodies = sapi.World.LoadedEntities.Values.OfType<EntityColonyCorpse>().Where(e => e.Alive && e.IsSettler && !e.KnockedOut).ToList();
        loadedBodies = (now + 5000, bodies);
        return bodies;
    }

    /// <summary>The bodies this undertaker could fetch: its colony's dead, within the Work area range of the Graveyard, longest dead first.</summary>
    public List<EntityColonyCorpse> BodiesToFetch(Colony colony, Building hut, CitizenRecord undertaker)
    {
        int range = GatherRadius(hut);
        long now = sapi.World.ElapsedMilliseconds;
        var c = hut.HutPos;
        return LoadedBodies()
            .Where(b => b.Alive && b.ColonyId == colony.ColonyId)
            .Where(b => { double dx = b.ServerPos.X - c.X, dz = b.ServerPos.Z - c.Z; return dx * dx + dz * dz <= (double)range * range; })
            .Where(b => !bodyClaims.TryGetValue(b.EntityId, out var claim) || claim.CitizenId == undertaker.CitizenId || claim.UntilMs < now)
            .Select(b => (Body: b, Dead: colony.Citizens.Find(x => x.CitizenId == b.CitizenId)))
            .Where(x => x.Dead == null || (!x.Dead.Alive && !x.Dead.Interred))
            .OrderBy(x => x.Dead?.DiedTotalDays ?? 0)
            .Select(x => x.Body)
            .ToList();
    }

    public void ClaimBody(EntityColonyCorpse body, CitizenRecord undertaker) =>
        bodyClaims[body.EntityId] = (undertaker.CitizenId, sapi.World.ElapsedMilliseconds + ClaimMs);

    // ---- The work ----------------------------------------------------------------------------

    /// <summary>
    /// Wraps a body in a shroud with its belongings and puts it in the undertaker's inventory. The corpse goes,
    /// and so does the map marker where the settler fell. False when the body is gone or there is no room.
    /// </summary>
    public bool LiftBody(Colony colony, CitizenRecord undertaker, long corpseEntityId)
    {
        bodyClaims.Remove(corpseEntityId);
        if (sapi.World.GetEntityById(corpseEntityId) is not EntityColonyCorpse { Alive: true } body) return false;
        if (PackFull(undertaker)) return false;
        var shroud = MakeShroud(colony, body);
        if (shroud == null) return false;
        AddToPack(colony, undertaker, shroud, false);
        if (shroud.StackSize > 0) return false;
        if (body.Inventory != null) foreach (var slot in body.Inventory) slot.Itemstack = null;
        body.WatchedAttributes.SetBool(UndertakenAttr, true);
        body.Die(EnumDespawnReason.Removed);
        ClearFallen(colony, body.CitizenId, body.ServerPos.XYZ);
        LogEvent(colony, "event-undertaker-lifted", undertaker.Name, body.OwnerName);
        MarkDirty(colony);
        return true;
    }

    ItemStack? MakeShroud(Colony colony, EntityColonyCorpse body)
    {
        if (sapi.World.GetItem(new AssetLocation("createacolony:shroudedbody")) is not { } item) return null;
        var shroud = new ItemStack(item);
        shroud.Attributes.SetString(ShroudNameAttr, body.OwnerName);
        shroud.Attributes.SetString(ShroudColonyAttr, colony.ColonyId);
        shroud.Attributes.SetInt(ShroudCitizenAttr, body.CitizenId);
        var things = new TreeAttribute();
        int i = 0;
        if (body.Inventory != null)
        {
            foreach (var slot in body.Inventory)
            {
                if (slot.Empty) continue;
                things.SetItemstack((i++).ToString(), slot.Itemstack.Clone());
            }
        }
        if (i > 0) shroud.Attributes[ShroudThingsAttr] = things;
        return shroud;
    }

    static bool HasBelongings(ItemStack shroud) => shroud.Attributes[ShroudThingsAttr] is ITreeAttribute { Count: > 0 };

    /// <summary>True when the undertaker carries a body whose belongings are still wrapped up with it.</summary>
    public bool CarriesBelongings(CitizenRecord undertaker) => PackItems(undertaker).Any(s => IsShroudedBody(s) && HasBelongings(s));

    /// <summary>The settler a carried body is, or 0 when it carries none.</summary>
    public int CarriedDead(CitizenRecord undertaker) =>
        PackItems(undertaker).FirstOrDefault(IsShroudedBody)?.Attributes.GetInt(ShroudCitizenAttr) ?? 0;

    public bool CarriesBody(CitizenRecord undertaker) => PackItems(undertaker).Any(IsShroudedBody);

    /// <summary>Puts the belongings of the bodies it carries into the Graveyard's storage (what does not fit lands by the hut).</summary>
    public void UnpackBelongings(Colony colony, Building hut, CitizenRecord undertaker)
    {
        var items = PackItems(undertaker);
        var things = new List<ItemStack>();
        foreach (var shroud in items.Where(IsShroudedBody))
        {
            if (shroud.Attributes[ShroudThingsAttr] is not ITreeAttribute tree) continue;
            foreach (var (_, value) in tree)
            {
                if (value is not ItemstackAttribute { value: { } stack }) continue;
                if (stack.ResolveBlockOrItem(sapi.World)) things.Add(stack);
            }
            shroud.Attributes.RemoveAttribute(ShroudThingsAttr);
        }
        SavePack(colony, undertaker, items);
        if (things.Count == 0) return;
        StoreCuts(colony, hut, things);
        AddStat(hut, "belongings", things.Sum(s => s.StackSize));
        MarkDirty(colony);
    }

    /// <summary>True when the ground under a plot is soft enough to dig a grave in.</summary>
    public bool CanDig(BlockPos plot)
    {
        var below = sapi.World.BlockAccessor.GetBlock(plot.DownCopy());
        return below.BlockMaterial is EnumBlockMaterial.Soil or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand;
    }

    public bool IsOpenGrave(Building hut, BlockPos plot) => hut.OpenGraves.Any(g => g.Pos.Equals(plot));

    /// <summary>Digs a grave under the plot marker, GraveDepth blocks of soil, gravel or sand. The blocks are kept to fill it back.</summary>
    public bool DigGrave(Colony colony, Building hut, BlockPos plot)
    {
        var ba = sapi.World.BlockAccessor;
        if (!IsGravePlot(ba.GetBlock(plot))) return false;
        if (IsOpenGrave(hut, plot)) return true;
        var grave = new OpenGrave { Pos = plot.Copy() };
        for (int d = 1; d <= Math.Max(1, Config.GraveDepth); d++)
        {
            var pos = plot.DownCopy(d);
            var block = ba.GetBlock(pos);
            if (block.BlockMaterial is not (EnumBlockMaterial.Soil or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand) || block.Code == null) break;
            grave.Dug.Add(block.Code.ToString());
            ba.SetBlock(0, pos);
        }
        if (grave.Dug.Count == 0) return false;
        hut.OpenGraves.Add(grave);
        sapi.World.PlaySoundAt(new AssetLocation("game:sounds/block/dirt1"), plot.X + 0.5, plot.Y, plot.Z + 0.5);
        MarkDirty(colony);
        return true;
    }

    /// <summary>
    /// Lays a carried body in the grave at the plot, fills the grave back, puts up a named grave marker and holds the
    /// small funeral. False when it carries no body or the plot is gone.
    /// </summary>
    public bool Bury(Colony colony, Building hut, CitizenRecord undertaker, BlockPos plot)
    {
        var ba = sapi.World.BlockAccessor;
        var plotBlock = ba.GetBlock(plot);
        plotClaims.Remove(plot);
        if (!IsGravePlot(plotBlock)) return false;
        var shroud = TakeFromPack(colony, undertaker, IsShroudedBody, 1);
        if (shroud == null) return false;

        // Fill the grave back in.
        if (hut.OpenGraves.Find(g => g.Pos.Equals(plot)) is { } open)
        {
            for (int d = open.Dug.Count; d >= 1; d--)
            {
                var pos = plot.DownCopy(d);
                if (ba.GetBlock(pos).Id != 0) continue;
                if (sapi.World.GetBlock(new AssetLocation(open.Dug[d - 1])) is { } block) ba.SetBlock(block.Id, pos);
            }
            hut.OpenGraves.Remove(open);
        }

        string side = plotBlock.Variant?["side"] ?? "north";
        string material = hut.Level >= Config.GraveyardStoneLevel ? "stone" : "wood";
        var marker = sapi.World.GetBlock(new AssetLocation($"createacolony:gravemarker-{material}-{side}"));
        int deadId = shroud.Attributes.GetInt(ShroudCitizenAttr);
        string name = shroud.Attributes.GetString(ShroudNameAttr, "");
        var dead = colony.Citizens.Find(c => c.CitizenId == deadId);
        if (marker != null)
        {
            ba.SetBlock(marker.Id, plot);
            if (ba.GetBlockEntity(plot) is BlockEntityGraveMarker be)
            {
                be.Set(colony.ColonyId, deadId, dead?.Name ?? name, dead?.JobCode ?? "", dead?.IsChild ?? false, dead?.DiedTotalDays ?? 0, dead?.DeathCause ?? "");
            }
        }
        ForgetGraveyardScan(hut);

        if (dead != null)
        {
            dead.Interred = true;
            dead.HasBody = false;
            dead.GravePos = plot.Copy();
            colony.Markers.RemoveAll(m => m.Kind == "grave" && m.CitizenId == dead.CitizenId);
            ShareMarkers(colony);
            SmallFuneral(colony, undertaker, dead);
        }
        AddStat(hut, "burials");
        LogEvent(colony, "event-undertaker-buried", undertaker.Name, dead?.Name ?? name);
        MarkDirty(colony);
        return true;
    }

    /// <summary>The undertaker's few words at the grave: grief fades a little (less than at a priest's funeral), with a little comfort.</summary>
    void SmallFuneral(Colony colony, CitizenRecord undertaker, CitizenRecord dead)
    {
        if (dead.SmallFuneral) return;
        dead.SmallFuneral = true;
        double now = sapi.World.Calendar.TotalDays;
        float factor = Math.Clamp(Config.UndertakerFuneralGriefFactor, 0, 1);
        foreach (var c in colony.Citizens.Where(c => c.Alive && c != undertaker))
        {
            bool grieving = false;
            foreach (var t in c.Thoughts.Where(t => t.Code is "death" or "familydeath" && t.UntilTotalDays > now))
            {
                t.UntilTotalDays = now + (t.UntilTotalDays - now) * factor;
                grieving = true;
            }
            if (grieving) AddThought(c, "smallfuneral");
        }
        // Nobody else waiting to be buried: the colony stops worrying about the unburied.
        if (!colony.Citizens.Any(Unburied))
        {
            foreach (var c in colony.Citizens) c.Thoughts.RemoveAll(t => t.Code is "unburied" or "unburiedfamily");
        }
    }

    /// <summary>A dead settler whose body still waits to be buried, and has for UnburiedAfterDays.</summary>
    bool Unburied(CitizenRecord c)
    {
        if (c.Alive || c.Visitor || c.Interred || !c.HasBody || c.DiedTotalDays <= 0) return false;
        double age = sapi.World.Calendar.TotalDays - c.DiedTotalDays;
        return age >= Config.UnburiedAfterDays && age <= Config.UnburiedForgetDays;
    }

    // ---- Bodies coming and going ---------------------------------------------------------------

    /// <summary>A settler's body was made where it fell (CorpseSystem.SpawnSettlerCorpse).</summary>
    public void OnBodyMade(Colony colony, CitizenRecord dead)
    {
        dead.HasBody = true;
        MarkDirty(colony);
    }

    /// <summary>A settler's body went another way than with the undertaker (emptied with no Graveyard, burnt, broken).</summary>
    public void OnBodyGone(string colonyId, int citizenId)
    {
        if (GetById(colonyId) is not { } colony || colony.Citizens.Find(c => c.CitizenId == citizenId) is not { } dead) return;
        if (!dead.HasBody || dead.Interred) return;
        dead.HasBody = false;
        MarkDirty(colony);
    }

    /// <summary>While the colony has a Graveyard, a settler's body stays when its things are taken, for the undertaker.</summary>
    public bool KeepsBodies(string colonyId) => GetById(colonyId) is { } colony && HasGraveyard(colony);

    // ---- The colony tick and the window ---------------------------------------------------------

    /// <summary>Part of the colony tick: settlers near a body are upset, and bodies left unburied upset everyone.</summary>
    void TickGraveyard(Colony colony)
    {
        if (!Config.Happiness || !HasGraveyard(colony)) return;
        float r = Math.Max(0, Config.SawBodyRadius);
        var bodies = LoadedBodies().Where(b => b.Alive && b.ColonyId == colony.ColonyId).ToList();
        if (bodies.Count > 0 && r > 0)
        {
            foreach (var c in colony.Citizens)
            {
                if (!c.Alive || c.IsDowned || c.JobCode == Jobs.Undertaker || c.Thoughts.Any(t => t.Code == "sawbody")) continue;
                if (sapi.World.GetEntityById(c.EntityId) is not { Alive: true } settler) continue;
                if (bodies.Any(b => b.ServerPos.XYZ.SquareDistanceTo(settler.Pos.XYZ) <= r * r)) AddThought(c, "sawbody");
            }
        }

        var unburied = colony.Citizens.Where(Unburied).ToList();
        if (unburied.Count == 0) return;
        foreach (var c in colony.Citizens)
        {
            if (!c.Alive) continue;
            AddThought(c, unburied.Any(d => IsFamily(c, d)) ? "unburiedfamily" : "unburied");
        }
    }

    /// <summary>The Graveyard's lines on its Main tab: graves, plots, bodies waiting and who lies here lately.</summary>
    public IEnumerable<string> GraveyardInfo(Colony colony, Building hut)
    {
        var (plots, graves) = ScanGraveyard(hut);
        int limit = GraveLimit(hut);
        yield return Lang.Get("createacolony:graveyard-graves", graves, limit);
        int free = Math.Max(0, Math.Min(plots.Count, limit - graves));
        yield return plots.Count == 0 ? Lang.Get("createacolony:graveyard-no-plots") : Lang.Get("createacolony:graveyard-plots", free);
        if (graves >= limit) yield return Lang.Get("createacolony:graveyard-full");
        int waiting = colony.Citizens.Count(c => !c.Alive && !c.Visitor && c.HasBody && !c.Interred);
        if (waiting > 0) yield return Lang.Get("createacolony:graveyard-waiting", waiting);
        yield return Lang.Get("createacolony:graveyard-level", Math.Max(1, hut.Level), UndertakerSlots(hut),
            Lang.Get(hut.Level >= Config.GraveyardStoneLevel ? "createacolony:graveyard-stone" : "createacolony:graveyard-wood"));
        var buried = colony.Citizens.Where(c => c.Interred && c.GravePos != null
                && c.GravePos.DistanceSqTo(hut.HutPos.X, hut.HutPos.Y, hut.HutPos.Z) <= (Config.GraveyardPlotRadius + 8) * (Config.GraveyardPlotRadius + 8))
            .OrderByDescending(c => c.DiedTotalDays).Take(8).ToList();
        if (buried.Count > 0) yield return Lang.Get("createacolony:graveyard-here-lie", string.Join(", ", buried.Select(c => c.Name)));
    }
}
