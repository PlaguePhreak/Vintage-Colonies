using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A beekeeper works from the Apiary (ColonyManager.Beekeeping.cs). In order: it puts down bees it carries (into an
/// empty ceramic hive, or as a skep in the Work area), scrapes full honey pots, puts honey pots on ceramic hives that
/// have none, harvests full skeps (never the last colony while Keep the last colony is on), moves a spare skep colony
/// into an empty ceramic hive, sets empty skeps out by its colonies for their swarms, and walks out to wild hives the
/// gatherer marked to catch theirs. With From Golden Combs Langstroth hives (from Apiary level 4) it also takes out
/// full and worn frames, works them at the Frame Rack, puts lined frames back and fills empty brood boxes with bees.
/// </summary>
public class AiTaskSettlerBeekeeper : AiTaskSettlerWorker
{
    enum Work { None, Harvest, Scrape, PutTop, Fill, Lift, Place, SetSwarm, SetWild, LiftWild, TakeFrames, Rack, PutFrames, Brood }

    Work work;

    public AiTaskSettlerBeekeeper(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Beekeeper;
    protected override string HutType => BuildingTypes.Apiary;
    protected override EnumTool Tool => EnumTool.Knife;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.BeekeeperSecondsPerTask;
    protected override bool WorkNeedsTool => work == Work.Scrape || work == Work.Rack && rackScrapes;

    bool rackScrapes;

    /// <summary>Keeps the skeps and honey pots it sets out, and bees it carries home.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsHivePart(stack);

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        work = Work.None;
        workRange = 2.5f;
        m.TidyWildSkeps(colony, hut);
        var scan = m.ScanApiary(hut);
        var hives = scan.Hives.Where(h => !IsUnreachable(h.Pos)).ToList();
        bool ceramic = m.ApiaryWorksCeramic(hut);
        int limit = m.ApiaryHiveLimit(hut);
        int count = scan.Hives.Count + hut.WildSkeps.Count;
        int radius = m.GatherRadius(hut);
        bool needsSkep = false;
        bool langstroth = m.ApiaryWorksLangstroth(hut);
        // Empty brood boxes that bees can go into: in Langstroth hives, or set down on their own.
        var broods = !langstroth ? new List<ColonyManager.ApiaryHive>()
            : hives.Where(h => h.Langstroth && h.EmptyBrood).Concat(scan.Broods.Where(b => !IsUnreachable(b.Pos))).ToList();

        // Bees it carries go down first: into an empty ceramic hive, or as a skep in the Work area.
        if (m.PackCount(citizen, ColonyManager.IsColonySkepStack) > 0)
        {
            if (ceramic && hives.FirstOrDefault(h => !h.Skep && !h.Langstroth && !h.Colony) is { } pot)
            {
                work = Work.Fill;
                SetStatus("beekeeper-moving");
                return GoWork(pot.Pos);
            }
            if (broods.FirstOrDefault() is { } brood)
            {
                work = Work.Brood;
                SetStatus("beekeeper-brood");
                return GoWork(brood.Pos);
            }
            if (m.SkepSpot(colony, hut.HutPos, 2, System.Math.Max(3, radius - 1), 4, 4) is { } spot && !IsUnreachable(spot))
            {
                work = Work.Place;
                SetStatus("beekeeper-placing");
                return GoWork(spot);
            }
            SetStatus("beekeeper-no-spot");
        }

        // Full honey pots on ceramic hives: scraped with the knife, the bees stay.
        if (ceramic && hives.FirstOrDefault(h => !h.Skep && !h.Langstroth && h.Full) is { } fullPot)
        {
            work = Work.Scrape;
            SetStatus("beekeeper-scraping");
            return GoWork(fullPot.Pos);
        }

        // A ceramic hive without its honey pot.
        if (ceramic && hives.FirstOrDefault(h => !h.Skep && !h.Langstroth && h.NoTop) is { } bare)
        {
            if (m.PackCount(citizen, ColonyManager.IsHoneyPot) > 0)
            {
                work = Work.PutTop;
                SetStatus("beekeeper-honeypot");
                return GoWork(bare.Pos);
            }
            if (!m.PackFull(citizen) && storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsHoneyPot(s.Itemstack))) is { } potChest)
            {
                SetStatus("beekeeper-fetching");
                return FetchSupplies(potChest.Pos, ColonyManager.IsHoneyPot, 1);
            }
        }

        // Langstroth hives: frames out, to the Frame Rack, and back in.
        string? frameNote = null;
        if (langstroth && PlanFrames(colony, citizen, hut, storage, scan, hives, ref frameNote, out bool frameWalk)) return frameWalk;

        // Full skeps: harvested, unless that would take the last colony.
        bool keptLast = false;
        if (hives.FirstOrDefault(h => h.Skep && h.Colony && h.Full) is { } fullSkep)
        {
            if (m.MayHarvestSkep(hut, scan))
            {
                work = Work.Harvest;
                SetStatus("beekeeper-harvesting");
                return GoWork(fullSkep.Pos);
            }
            keptLast = true;
        }

        // An empty ceramic hive gets a colony: one from the hut storage, or a spare skep in the Work area (one skep always stays, for swarms).
        if ((ceramic && hives.Any(h => !h.Skep && !h.Langstroth && !h.Colony) || broods.Count > 0) && !m.PackFull(citizen))
        {
            if (storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsColonySkepStack(s.Itemstack))) is { } beeChest)
            {
                SetStatus("beekeeper-moving");
                return FetchSupplies(beeChest.Pos, ColonyManager.IsColonySkepStack, 1);
            }
            var skeps = hives.Where(h => h.Skep && h.Colony && !h.Full).ToList();
            if (skeps.Count >= 2)
            {
                work = Work.Lift;
                SetStatus("beekeeper-moving");
                return GoWork(skeps[skeps.Count - 1].Pos);
            }
        }

        // Swarms: an empty skep near each colony skep, so its bees have somewhere to go.
        if (m.ApiaryCatchesSwarms(hut) && count < limit)
        {
            foreach (var hive in hives.Where(h => h.Skep && h.Colony))
            {
                if (m.EmptySkepNear(hive.Pos)) continue;
                if (m.PackCount(citizen, ColonyManager.IsEmptySkepStack) == 0)
                {
                    if (FetchEmptySkep(colony, hut, citizen, storage, out bool walks)) return walks;
                    needsSkep = true;
                    break;
                }
                if (m.SkepSpot(colony, hive.Pos, 2, 5, 3, 2) is { } spot && !IsUnreachable(spot))
                {
                    work = Work.SetSwarm;
                    SetStatus("beekeeper-swarm");
                    return GoWork(spot);
                }
            }
        }

        // Wild hives the gatherer marked: an empty skep by each, brought home once bees moved in.
        if (m.ApiaryBringsWild(hut))
        {
            foreach (var wild in m.MarkedWildHives(colony, hut))
            {
                if (IsUnreachable(wild)) continue;
                if (m.WildSkepBy(hut, wild) is { } set)
                {
                    if (!IsUnreachable(set) && !m.PackFull(citizen) && ColonyManager.IsColonySkepBlock(entity.World.BlockAccessor.GetBlock(set)))
                    {
                        work = Work.LiftWild;
                        SetStatus("beekeeper-wild-bring");
                        return GoWork(set);
                    }
                    continue;
                }
                if (count >= limit) continue;
                if (m.PackCount(citizen, ColonyManager.IsEmptySkepStack) == 0)
                {
                    if (FetchEmptySkep(colony, hut, citizen, storage, out bool walks)) return walks;
                    needsSkep = true;
                    break;
                }
                var spot = m.SkepSpot(colony, wild, 2, 5, 7, 3);
                if (spot == null || IsUnreachable(spot))
                {
                    // Too high up a tree, or nowhere to stand a skep: leave it for a while.
                    MarkUnreachable(wild);
                    continue;
                }
                work = Work.SetWild;
                SetStatus("beekeeper-wild-set");
                return GoWork(spot);
            }
        }

        SetStatus(scan.Hives.Count == 0 && hut.WildSkeps.Count == 0 ? "beekeeper-no-hives"
            : keptLast ? "beekeeper-keeping-last"
            : needsSkep ? "beekeeper-needs-skep"
            : frameNote ?? "beekeeper-waiting");
        return false;
    }

    /// <summary>
    /// The Langstroth frame work, in order: frames it carries to the Frame Rack, lined frames back into free spots,
    /// waxed twine, frame liners or lined frames from the hut when the hives need them (or asks couriers), then
    /// full, worn or empty frames out of a hive. True when there is work (walks is then the plan).
    /// </summary>
    bool PlanFrames(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, ColonyManager.ApiaryScan scan,
        List<ColonyManager.ApiaryHive> hives, ref string? note, out bool walks)
    {
        var m = manager!;
        walks = false;
        var langs = hives.Where(h => h.Langstroth && !h.LoneBrood).ToList();
        if (langs.Count == 0) return false;
        bool rack = scan.Rack != null && !IsUnreachable(scan.Rack);

        if (m.CarriesRackWork(citizen))
        {
            if (rack)
            {
                work = Work.Rack;
                rackScrapes = m.PackCount(citizen, s => ColonyManager.IsBeeFrame(s) && s.Collectible.Variant["harvestable"] == "harvestable") > 0;
                SetStatus("beekeeper-rack");
                walks = GoWork(scan.Rack!);
                return true;
            }
            note = "beekeeper-no-rack";
        }

        int lined = m.PackCount(citizen, ColonyManager.IsLinedFrame);
        if (lined > 0 && langs.FirstOrDefault(h => h.Room > 0) is { } open)
        {
            work = Work.PutFrames;
            SetStatus("beekeeper-frames-in");
            walks = GoWork(open.Pos);
            return true;
        }

        bool twine = m.PackCount(citizen, ColonyManager.IsFrameTwine) > 0, liner = m.PackCount(citizen, ColonyManager.IsFrameLiner) > 0;
        if (!m.PackFull(citizen))
        {
            int want = m.Config.BeekeeperFrameRequest;
            if (!twine && langs.Any(h => h.WornFrames > 0) && Supply(colony, hut, storage, ColonyManager.IsFrameTwine, want, out walks)) return true;
            if (!liner && langs.Any(h => h.EmptyFrames > 0) && Supply(colony, hut, storage, ColonyManager.IsFrameLiner, want, out walks)) return true;
            int room = langs.Sum(h => h.Room);
            if (room > 0 && Supply(colony, hut, storage, ColonyManager.IsLinedFrame, System.Math.Min(room, ColonyManager.FramesPerSuperCount), out walks)) return true;
        }

        if (!m.PackFull(citizen) && langs.FirstOrDefault(h => h.FullFrames > 0 || twine && h.WornFrames > 0 || liner && h.EmptyFrames > 0) is { } hive)
        {
            if (rack)
            {
                work = Work.TakeFrames;
                SetStatus("beekeeper-frames-out");
                walks = GoWork(hive.Pos);
                return true;
            }
            note = "beekeeper-no-rack";
        }
        return false;
    }

    /// <summary>Fetches something from the hut storage, or asks couriers for it when the warehouse has some. True when there is one to fetch.</summary>
    bool Supply(Colony colony, Building hut, List<BlockEntityContainer> storage, ItemMatch match, int want, out bool walks)
    {
        var m = manager!;
        walks = false;
        if (storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && match(s.Itemstack))) is { } chest)
        {
            SetStatus("beekeeper-fetching-frames");
            walks = FetchSupplies(chest.Pos, match, want);
            return true;
        }
        if (m.CodeInWarehouse(colony, match) is { } code) m.EnsureCraftRequest(colony, hut, code, want);
        return false;
    }

    /// <summary>Gets an empty skep from the hut storage, or asks couriers for some. True when there is one to fetch (walks is then the plan).</summary>
    bool FetchEmptySkep(Colony colony, Building hut, CitizenRecord citizen, List<BlockEntityContainer> storage, out bool walks)
    {
        var m = manager!;
        walks = false;
        if (!m.PackFull(citizen) && storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsEmptySkepStack(s.Itemstack))) is { } chest)
        {
            SetStatus("beekeeper-fetching");
            walks = FetchSupplies(chest.Pos, ColonyManager.IsEmptySkepStack, 2);
            return true;
        }
        if (m.CodeInWarehouse(colony, ColonyManager.IsEmptySkepStack) is { } code) m.EnsureCraftRequest(colony, hut, code, m.Config.BeekeeperSkepRequest);
        return false;
    }

    protected override string WorkAnimation(ItemSlot? hand) => work == Work.Scrape || work == Work.Rack && rackScrapes ? "knifecut"
        : SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic";

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (work)
        {
            case Work.Harvest:
                m.HarvestSkep(colony, hut, citizen, target);
                break;
            case Work.Scrape:
                if (Hand is { } hand && m.ScrapeHoneyPot(colony, hut, citizen, target, entity, hand)) SyncHand();
                break;
            case Work.PutTop:
                m.PutHoneyPot(colony, hut, citizen, target);
                break;
            case Work.Fill:
                m.FillBroodPot(colony, hut, citizen, target);
                break;
            case Work.Lift:
                m.LiftSkep(colony, citizen, hut, target);
                break;
            case Work.Place:
                if (!m.SetSkep(colony, citizen, target, true)) MarkUnreachable(target);
                m.ForgetApiaryScan(hut);
                break;
            case Work.SetSwarm:
                if (m.SetSkep(colony, citizen, target, false)) m.AddStat(hut, "swarms");
                else MarkUnreachable(target);
                m.ForgetApiaryScan(hut);
                break;
            case Work.SetWild:
                if (m.SetSkep(colony, citizen, target, false))
                {
                    hut.WildSkeps.Add(target.Copy());
                    m.MarkDirty(colony);
                }
                else MarkUnreachable(target);
                break;
            case Work.TakeFrames:
                if (m.TakeFrames(colony, hut, citizen, target) == 0) MarkUnreachable(target);
                break;
            case Work.Rack:
                if (!m.WorkFrames(colony, hut, citizen, target, entity, Hand)) MarkUnreachable(target);
                else SyncHand();
                break;
            case Work.PutFrames:
                if (m.PutFrames(colony, hut, citizen, target) == 0) MarkUnreachable(target);
                break;
            case Work.Brood:
                if (!m.FillBroodBox(colony, hut, citizen, target)) MarkUnreachable(target);
                break;
            case Work.LiftWild:
                if (m.LiftSkep(colony, citizen, hut, target))
                {
                    hut.WildSkeps.RemoveAll(p => p.Equals(target));
                    m.AddStat(hut, "wild");
                    m.MarkDirty(colony);
                }
                break;
        }
        work = Work.None;
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-beekeeper-" + (work == Work.None ? "idle" : work.ToString().ToLowerInvariant()));
}
