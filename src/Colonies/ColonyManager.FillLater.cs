using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace CreateAColony.Colonies;

/// <summary>
/// Fill in later (0.205.0, Aaron): a build leaves out the blocks the colony can't get yet and goes up without them, so
/// the building can be used straight away. Two kinds wait:
/// 1. boards and everything made from them (config NeedsSawPatterns) while the colony has no saw: a saw needs copper;
/// 2. things no hut in the colony makes or gathers, once a request for them has waited MayorShortHours.
/// The building is marked (Building.LaterBlocks), and the Mayor sends a builder back (a repair) once they can be had.
/// </summary>
public partial class ColonyManager
{
    readonly Dictionary<string, (long Ms, bool Saw, HashSet<string> Codes)> laterCodes = new();

    /// <summary>True when the colony has a saw (or a saw blade) in a chest or with a settler.</summary>
    public bool ColonyHasSaw(Colony colony)
    {
        bool IsSaw(ItemStack s) => (Config.SawPatterns ?? Array.Empty<string>()).Any(p => WildcardUtil.Match(new AssetLocation(p), s.Collectible.Code));
        if (CountItems(ColonyStores(colony), IsSaw) > 0) return true;
        return colony.Citizens.Any(c => c.Alive && !c.Visitor && PackCount(c, IsSaw) > 0);
    }

    /// <summary>True for a material that needs a saw (config NeedsSawPatterns); code may be a pattern such as game:plank-*.</summary>
    public bool NeedsSaw(string code) =>
        (Config.NeedsSawPatterns ?? Array.Empty<string>()).Any(p => WildcardUtil.Match(p, code) || WildcardUtil.Match(p, code.Replace("{", "").Replace("}", "")));

    /// <summary>The material codes builds of this colony leave for later right now. Worked out at most every 10 seconds.</summary>
    (bool Saw, HashSet<string> Codes) LaterCodes(Colony colony)
    {
        long ms = sapi.World.ElapsedMilliseconds;
        if (laterCodes.TryGetValue(colony.ColonyId, out var known) && ms - known.Ms < 10_000) return (known.Saw, known.Codes);
        bool saw = ColonyHasSaw(colony);
        var codes = known.Codes == null ? new HashSet<string>() : new HashSet<string>(known.Codes);
        var stores = ColonyStores(colony).ToList();
        bool Stored(string code) => CountItems(stores, Matcher(code)) > 0;
        // What can be had now comes off the list.
        codes.RemoveWhere(code => NeedsSaw(code) ? saw || Stored(code) : MakerTypeFor(colony, code) != null || Stored(code));
        // Builds' requests nobody can fill go on it.
        double hours = sapi.World.Calendar.TotalHours;
        foreach (var r in colony.Requests)
        {
            if (r.Remaining <= 0 || codes.Contains(r.ItemCode) || !RequestHoldsUpBuild(colony, r)) continue;
            if (NeedsSaw(r.ItemCode))
            {
                if (!saw && !Stored(r.ItemCode)) codes.Add(r.ItemCode);
                continue;
            }
            if (colony.MayorShortSince.TryGetValue(r.RequestId, out double since) && hours - since >= Config.MayorShortHours
                && MakerTypeFor(colony, r.ItemCode) == null && !Stored(r.ItemCode))
            {
                codes.Add(r.ItemCode);
            }
        }
        laterCodes[colony.ColonyId] = (ms, saw, codes);
        return (saw, codes);
    }

    /// <summary>True when a build step waits for later: what it takes is on the colony's later list, or needs a saw nobody has.</summary>
    public bool IsLaterStep(Colony colony, BuildStep step, BlockPos pos)
    {
        if (!Config.BuildersFillInLater || !Config.BuildersNeedMaterials || step.BlockId == 0) return false;
        var (saw, codes) = LaterCodes(colony);
        foreach (var cost in RemainingCost(colony, step, pos))
        {
            if (codes.Contains(cost.Code)) return true;
            if (!saw && NeedsSaw(cost.Code) && !codes.Contains(cost.Code))
            {
                // Boards somebody brought anyway are used.
                if (CountItems(ColonyStores(colony), Matcher(cost.Code)) >= cost.Count) continue;
                codes.Add(cost.Code);
                return true;
            }
        }
        return false;
    }

    /// <summary>Marks a building as having blocks left for later.</summary>
    void MarkLater(Colony colony, Building building)
    {
        if (building.LaterBlocks) return;
        building.LaterBlocks = true;
        MarkDirty(colony);
    }

    /// <summary>
    /// The Mayor's fill-in step: a finished building with blocks left for later, now that the colony can get at least
    /// one of them, gets a builder back (a repair, which puts in every missing block). Null when there is none.
    /// </summary>
    MayorPlan? FillInStep(Colony colony, bool room)
    {
        if (!room || !Config.BuildersFillInLater) return null;
        foreach (var b in colony.Buildings.Where(b => b.LaterBlocks && b.Level >= 1 && !b.HasWorkOrder && !b.Removing).OrderBy(b => b.BuildingId))
        {
            var blueprint = Blueprints.Get(b, b.Level);
            if (blueprint == null)
            {
                b.LaterBlocks = false;
                continue;
            }
            bool left = false, ready = false;
            foreach (var step in blueprint.Steps)
            {
                if (step.BlockId == 0) continue;
                var pos = StepPos(b, step);
                if (IsStepDone(colony, step, pos, b)) continue;
                left = true;
                if (!IsLaterStep(colony, step, pos))
                {
                    ready = true;
                    break;
                }
            }
            if (!left)
            {
                b.LaterBlocks = false;
                MarkDirty(colony);
                continue;
            }
            if (!ready) continue;
            return UpgradePlan(b, "mayor-why-fillin", new[] { BuildingName(b) }, b.Level);
        }
        return null;
    }

    /// <summary>How many buildings have blocks left for later, for the report and the Mayor tab.</summary>
    public int BuildingsWithLaterBlocks(Colony colony) => colony.Buildings.Count(b => b.LaterBlocks && !b.Removing);
}
