using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// What foresters and farmers share: each works from its own hut, keeps its tool and a stock of seeds
/// in its inventory, and brings everything else it gathers to the chest at its hut, from where
/// couriers carry it to the warehouse.
/// </summary>
public partial class ColonyManager
{
    /// <summary>
    /// A hut type with a job that works from it, the tool the job needs, the seeds it keeps a stock of,
    /// and other things it keeps (a farmer's watering can and fertilizer).
    /// </summary>
    public readonly record struct WorkerHutKind(string Job, EnumTool Tool, ItemMatch Stock, ItemMatch Keeps);

    static readonly Dictionary<string, WorkerHutKind> GatherHuts = new()
    {
        [BuildingTypes.Forester] = new WorkerHutKind(Jobs.Forester, EnumTool.Axe, IsTreeSeed, _ => false),
        [BuildingTypes.Farmer] = new WorkerHutKind(Jobs.Farmer, EnumTool.Hoe, IsCropSeed, s => IsWateringCan(s) || IsFertilizer(s)),
        // The prospecting pick is matched by its class, not a tool type (see IsProspectingPick).
        [BuildingTypes.Prospector] = new WorkerHutKind(Jobs.Prospector, EnumTool.Probe, _ => false, IsProspectingPick),
        [BuildingTypes.Miner] = new WorkerHutKind(Jobs.Miner, EnumTool.Pickaxe, _ => false, _ => false),
        [BuildingTypes.Quarry] = new WorkerHutKind(Jobs.Quarrier, EnumTool.Pickaxe, _ => false, _ => false),
        // The Explorer and Gatherer need no tool; Knife is only a placeholder (see NeedsTool on their tasks).
        [BuildingTypes.Explorer] = new WorkerHutKind(Jobs.Explorer, EnumTool.Knife, _ => false, _ => false),
        [BuildingTypes.Gatherer] = new WorkerHutKind(Jobs.Gatherer, EnumTool.Knife, _ => false, _ => false),
        // The hunter hunts with a spear (any weapon does) and keeps a knife for skinning.
        [BuildingTypes.Hunter] = new WorkerHutKind(Jobs.Hunter, EnumTool.Spear, _ => false, IsKnife),
    };

    /// <summary>Huts a settler works from, whose chests are never warehouse storage.</summary>
    public static bool IsWorkerHut(string type) => JobOf(type) != null;

    /// <summary>The job of the settlers who work from a hut of this type, or null for buildings nobody works at.</summary>
    public static string? JobOf(string type) => type switch
    {
        BuildingTypes.Builder => Jobs.Builder,
        BuildingTypes.Potter => Jobs.Potter,
        BuildingTypes.Cook => Jobs.Cook,
        BuildingTypes.Smelter => Jobs.Smelter,
        BuildingTypes.Blacksmith => Jobs.Blacksmith,
        BuildingTypes.Armourer => Jobs.Armourer,
        BuildingTypes.Weaponsmith => Jobs.Weaponsmith,
        BuildingTypes.Sawmill => Jobs.Sawyer,
        BuildingTypes.Craftsman => Jobs.Craftsman,
        BuildingTypes.Tailor => Jobs.Tailor,
        BuildingTypes.Jeweller => Jobs.Jeweller,
        BuildingTypes.Healer => Jobs.Healer,
        BuildingTypes.Mint => Jobs.Minter,
        BuildingTypes.Archive => Jobs.Researcher,
        BuildingTypes.Church => Jobs.Priest,
        BuildingTypes.GuardTower => Jobs.Guard,
        BuildingTypes.Barracks => Jobs.Guard,
        _ => GatherHuts.TryGetValue(type, out var kind) ? kind.Job : null
    };

    /// <summary>How many settlers can work from this building at once.</summary>
    public static int WorkerSlots(Building hut) => IsGuardPost(hut.Type) ? GuardSlots(hut)
        : hut.Type == BuildingTypes.Prospector ? ProspectorSlots : hut.Type == BuildingTypes.Church ? ChurchSlots(hut) : hut.Type == BuildingTypes.Archive ? ArchiveSlots(hut) : JobOf(hut.Type) == null ? 0 : 1;

    /// <summary>A Prospector's Hut takes two prospectors, each finding ore or mining it (CitizenRecord.WorkRole).</summary>
    public const int ProspectorSlots = 2;

    /// <summary>
    /// Sets what a hut's worker does, at a hut whose workers each pick their work: a prospector finds ore or
    /// mines it. The settler goes back to the hut first, to swap its tools.
    /// </summary>
    public string? SetWorkRole(Colony colony, Building hut, CitizenRecord worker, int role)
    {
        if (hut.Type == BuildingTypes.Church) return SetChurchRole(colony, hut, worker, role);
        if (hut.Type != BuildingTypes.Prospector || role is not (ProspectorRoles.Find or ProspectorRoles.Mine)) return "createacolony:bad-building";
        if (!WorkersAt(colony, hut).Contains(worker)) return "createacolony:bad-citizen";
        if (worker.WorkRole == role) return null;
        worker.WorkRole = role;
        LogEvent(colony, role == ProspectorRoles.Mine ? "event-prospector-mines" : "event-prospector-finds", worker.Name);
        MarkDirty(colony);
        return null;
    }

    /// <summary>The living settlers working from this hut.</summary>
    public List<CitizenRecord> WorkersAt(Colony colony, Building hut)
    {
        string? job = JobOf(hut.Type);
        return job == null ? new List<CitizenRecord>()
            : colony.Citizens.Where(c => c.Alive && c.JobCode == job && c.WorkplacePos != null && c.WorkplacePos.Equals(hut.HutPos)).OrderBy(c => c.CitizenId).ToList();
    }

    /// <summary>Huts with their own storage (and a Storage button): the worker huts, the Tavern, where gears can be left for hiring, and the Residence.</summary>
    public static bool HasOwnStorage(string type) => IsWorkerHut(type) || type == BuildingTypes.Tavern || type == BuildingTypes.Residence;

    /// <summary>What each forester's or farmer's hut is waiting on, for the hut window. Not saved: workers refresh it.</summary>
    public readonly Dictionary<int, string> WorkStatus = new();
    /// <summary>The same for each worker, by citizen id, for huts with more than one (a Prospector's Hut).</summary>
    public readonly Dictionary<int, string> WorkerStatus = new();

    /// <summary>How many seeds a job keeps on hand.</summary>
    public int SeedReserve(string job) => job switch
    {
        Jobs.Farmer => Config.FarmerSeedReserve,
        Jobs.Potter => Config.PotterClayReserve,
        Jobs.Cook => Config.CookRawReserve,
        Jobs.Smelter => Config.SmelterOreReserve,
        Jobs.Blacksmith => 0,
        Jobs.Sawyer => 0,
        Jobs.Craftsman => 0,
        _ => Config.ForesterSeedReserve
    };

    /// <summary>The hut of this type a worker works from, assigning one with a free place if needed.</summary>
    public Building? EnsureWorkplace(Colony colony, CitizenRecord worker, string hutType)
    {
        if (worker.WorkplacePos != null && GetBuilding(colony, worker.WorkplacePos) is { } hut && hut.Type == hutType) return hut;

        var free = colony.Buildings.Where(b => b.Type == hutType && !b.Removing).OrderBy(b => b.BuildingId)
            .FirstOrDefault(b => colony.Citizens.Count(c => c.Alive && c != worker && c.JobCode == worker.JobCode
                && c.WorkplacePos != null && c.WorkplacePos.Equals(b.HutPos)) < WorkerSlots(b));
        if (free != null || worker.WorkplacePos != null)
        {
            worker.WorkplacePos = free?.HutPos.Copy();
            MarkDirty(colony);
        }
        return free;
    }

    /// <summary>The living settler working from this hut, or null.</summary>
    public CitizenRecord? WorkerAt(Colony colony, Building hut, string job) =>
        colony.Citizens.Find(c => c.Alive && c.JobCode == job && c.WorkplacePos != null && c.WorkplacePos.Equals(hut.HutPos));

    /// <summary>
    /// At a Forester's or Farmer's Hut everything is for the warehouse except the job's tools and things
    /// it keeps (watering cans, fertilizer), and its seeds while the worker is short of them, so a worker running low can pick them up there.
    /// </summary>
    System.Func<ItemStack, bool> WorkerHutJunk(Colony colony, Building hut)
    {
        // Every hut keeps what its Crafting recipes tab needs, the crafter huts too.
        var junk = OwnHutJunk(colony, hut);
        var recipeInputs = HasHutRecipes(hut.Type) ? HutRecipeInputs(colony, hut) : _ => false;
        return s => junk(s) && !recipeInputs(s);
    }

    System.Func<ItemStack, bool> OwnHutJunk(Colony colony, Building hut)
    {
        if (hut.Type == BuildingTypes.Potter) return PotterHutJunk();
        if (hut.Type == BuildingTypes.Cook) return CookHutJunk();
        if (hut.Type == BuildingTypes.Smelter) return SmelterHutJunk();
        if (hut.Type is BuildingTypes.Blacksmith or BuildingTypes.Armourer or BuildingTypes.Weaponsmith) return BlacksmithHutJunk(colony, hut);
        if (hut.Type == BuildingTypes.Sawmill) return SawmillHutJunk(colony, hut);
        if (hut.Type == BuildingTypes.Craftsman) return CraftsmanHutJunk(colony, hut);
        if (hut.Type == BuildingTypes.Tailor) return TailorHutJunk(colony, hut);
        if (hut.Type == BuildingTypes.Jeweller) return JewellerHutJunk(colony, hut);
        if (hut.Type == BuildingTypes.Healer) return HealerHutJunk(colony, hut);
        if (hut.Type == BuildingTypes.Mint) return MintHutJunk(colony, hut);
        if (hut.Type == BuildingTypes.Archive) return ArchiveJunk(colony, hut);
        if (hut.Type == BuildingTypes.Church) return ChurchJunk(colony, hut);
        if (IsGuardPost(hut.Type)) return GuardTowerJunk(hut);
        var kind = GatherHuts[hut.Type];
        var worker = WorkerAt(colony, hut, kind.Job);
        bool keepSeeds = worker != null && PackCount(worker, kind.Stock) < SeedReserve(kind.Job);
        // What its Crafting recipes tab needs stays too.
        var recipeInputs = HutRecipeInputs(colony, hut);
        return s => s.Collectible.Tool != kind.Tool && !kind.Keeps(s) && !(keepSeeds && kind.Stock(s)) && !recipeInputs(s);
    }

    /// <summary>
    /// True when a worker may change the block: it is in no land claim, or only in this colony's own.
    /// Foresters and farmers may reach past the colony border, but never into someone else's claim.
    /// </summary>
    public bool CanWorkAt(Colony colony, BlockPos pos)
    {
        var claims = sapi.World.Claims.Get(pos);
        if (claims == null || claims.Length == 0) return true;
        string mine = ClaimDescription(colony);
        return claims.All(c => c.Description == mine);
    }

    /// <summary>What a block would drop if broken by hand, or nothing if the block insists on a player.</summary>
    ItemStack[] DropsOf(Block block, BlockPos pos, float multiplier = 1)
    {
        if (block is BlockMicroBlock) return ChiseledDrops(pos, multiplier);
        try
        {
            return block.GetDrops(sapi.World, pos, null, multiplier) ?? Array.Empty<ItemStack>();
        }
        catch (Exception)
        {
            return Array.Empty<ItemStack>();
        }
    }

    /// <summary>
    /// What a chiseled block (ruins) drops: a few stones of its main rock, as the game gives a player. The game's
    /// own GetDrops throws these on the ground itself instead of returning them, so they are worked out here.
    /// </summary>
    ItemStack[] ChiseledDrops(BlockPos pos, float multiplier)
    {
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityMicroBlock be || be.BlockIds == null || be.BlockIds.Length == 0) return Array.Empty<ItemStack>();
        var material = sapi.World.Blocks[be.GetMajorityMaterialId()];
        string? rock = material.Variant["rock"];
        if (material.BlockMaterial != EnumBlockMaterial.Stone || rock == null) return Array.Empty<ItemStack>();
        int count = GameMath.RoundRandom(sapi.World.Rand, be.VolumeRel * 4 * multiplier);
        var stone = sapi.World.GetItem(new AssetLocation("game", "stone-" + rock));
        return count <= 0 || stone == null ? Array.Empty<ItemStack>() : new[] { new ItemStack(stone, count) };
    }

    /// <summary>
    /// Breaks a block without it dropping anything; the caller collects the drops itself. A chiseled block is
    /// just removed: the game's break needs a player for those and fails without one, leaving the block there,
    /// so a builder broke the same ruin block over and over (Aaron saw thousands of stones). Any other block
    /// that is still there after breaking is removed the same way.
    /// </summary>
    void BreakQuietly(BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var block = ba.GetBlock(pos);
        if (block.Id == 0) return;
        if (block is not BlockMicroBlock)
        {
            try
            {
                ba.BreakBlock(pos, null, 0);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[createacolony] Could not break {0} at {1}, removing it: {2}", block.Code, pos, e.Message);
            }
            if (ba.GetBlock(pos).Id != block.Id) return;
        }
        ba.SetBlock(0, pos);
        ba.TriggerNeighbourBlockUpdate(pos);
    }

    /// <summary>
    /// Puts what a worker gathered into its inventory. What does not fit goes straight into the hut
    /// chest, and only if that is full too does it land on the ground where it was gathered.
    /// </summary>
    public void StoreHarvest(Colony colony, Building hut, CitizenRecord worker, IEnumerable<ItemStack> drops, BlockPos at)
    {
        var storage = HutStorage(colony, hut).ToList();
        // A skilled farmer, forester, miner or potter sometimes gets one more of the biggest stack (yield perk).
        var list = drops.Where(d => d != null && d.StackSize > 0).ToList();
        if (list.Count > 0 && RollPerk(colony, worker, "yield")) list.OrderByDescending(d => d.StackSize).First().StackSize++;
        // Crop Rotation: farmers harvest more. A part of one more item is a chance of one.
        if (worker.JobCode == Jobs.Farmer && ResearchAdd(colony, "farmYield") is float more and > 0)
        {
            foreach (var d in list)
            {
                double extra = d.StackSize * more;
                d.StackSize += (int)extra + (sapi.World.Rand.NextDouble() < extra - (int)extra ? 1 : 0);
            }
        }
        drops = list;
        foreach (var drop in drops)
        {
            if (drop == null || drop.StackSize <= 0) continue;
            AddToPack(colony, worker, drop);
            foreach (var chest in storage)
            {
                if (drop.StackSize <= 0) break;
                PutItems(chest, drop);
            }
            if (drop.StackSize > 0) sapi.World.SpawnItemEntity(drop, at.ToVec3d().Add(0.5, 0.5, 0.5));
        }
    }

    /// <summary>Wears the tool in the worker's hand down by one use; a tool that breaks leaves the hand empty.</summary>
    public void WearTool(EntityAgent worker, ItemSlot hand)
    {
        if (hand.Itemstack == null) return;
        // Careful settlers skip some of the wear, Clumsy ones add some (trait toolWear).
        // A skilled builder wears tools less too (Building perk).
        float wear = worker is EntitySettler s && FindCitizen(s.ColonyId, s.CitizenId) is { } c
            ? TraitMult(c, "toolWear") * (GetById(s.ColonyId) is { } home ? SkillPerk(home, c, "toolWear") : 1f) : 1f;
        double roll = sapi.World.Rand.NextDouble();
        if (wear < 1f && roll < 1f - wear) return;
        hand.Itemstack.Collectible.DamageItem(worker.World, worker, hand);
        if (wear > 1f && roll < wear - 1f && hand.Itemstack != null) hand.Itemstack.Collectible.DamageItem(worker.World, worker, hand);
        hand.MarkDirty();
    }

    /// <summary>Lines for a Forester's or Farmer's Hut window: who works here, what they are up to, and how the job works.</summary>
    public string WorkerHutInfo(Colony colony, Building hut)
    {
        string job = JobOf(hut.Type) ?? "";
        if (job.Length == 0 || job == Jobs.Builder) return "";
        if (IsGuardPost(hut.Type)) return GuardTowerInfo(colony, hut);
        var lines = new List<string>();
        var worker = WorkerAt(colony, hut, job);
        if (hut.Type is BuildingTypes.Prospector or BuildingTypes.Church)
        {
            // Two prospectors, each with its own work and status; a Church's priests and bishop.
            var workers = WorkersAt(colony, hut);
            if (workers.Count == 0) lines.Add(Lang.Get("createacolony:hut-no-worker", Lang.Get("createacolony:job-" + job)));
            foreach (var w in workers)
            {
                string status = WorkerStatus.TryGetValue(w.CitizenId, out var ws) ? ws : "starting";
                lines.Add(Lang.Get("createacolony:prospector-worker", w.Name, Lang.Get("createacolony:" + (hut.Type == BuildingTypes.Church ? "church-role-" : "prospector-role-") + w.WorkRole),
                    Lang.Get("createacolony:workstatus-" + status)));
                if (w.WorkRole == ProspectorRoles.Mine && TripInfo(colony, hut, w.Trip) is { } trip) lines.Add(trip);
            }
        }
        else if (worker == null)
        {
            lines.Add(Lang.Get("createacolony:hut-no-worker", Lang.Get("createacolony:job-" + job)));
        }
        else
        {
            lines.Add(Lang.Get("createacolony:hut-worker-name", Lang.Get("createacolony:job-" + job), worker.Name));
            string status = WorkStatus.TryGetValue(hut.BuildingId, out var s) ? s : "starting";
            lines.Add(Lang.Get("createacolony:hut-worker-status", Lang.Get("createacolony:workstatus-" + status)));
        }
        if (hut.Type == BuildingTypes.Farmer)
        {
            int mine = worker == null ? 0 : colony.Fields.Count(f => f.FarmerCitizenId == worker.CitizenId);
            lines.Add(Lang.Get("createacolony:hut-fields", mine, colony.Fields.Count));
        }
        if (hut.Type == BuildingTypes.Potter) lines.Add(KilnInfo(colony, hut));
        if (hut.Type == BuildingTypes.Cook)
        {
            lines.Add(FirepitInfo(colony, hut));
            if (OvenInfo(colony, hut) is { } oven) lines.Add(oven);
            if (CoolStoreInfo(colony, hut) is { } cool) lines.Add(cool);
        }
        if (hut.Type == BuildingTypes.Smelter) lines.Add(SmelterInfo(colony, hut));
        if (hut.Type is BuildingTypes.Blacksmith or BuildingTypes.Armourer or BuildingTypes.Weaponsmith) lines.Add(BlacksmithInfo(hut));
        if (hut.Type == BuildingTypes.Armourer) lines.Add(ArmourerInfo(colony, hut));
        if (hut.Type == BuildingTypes.Sawmill) lines.Add(SawmillInfo(hut));
        if (hut.Type == BuildingTypes.Craftsman) lines.Add(CraftsmanInfo(hut));
        if (hut.Type == BuildingTypes.Tailor) lines.Add(TailorInfo(colony, hut));
        if (hut.Type == BuildingTypes.Healer) lines.Add(HealerInfo(colony, hut));
        if (hut.Type == BuildingTypes.Mint) lines.Add(MintInfo(colony));
        if (hut.Type == BuildingTypes.Archive) lines.Add(ArchiveInfo(colony, hut));
        if (hut.Type == BuildingTypes.Church) lines.AddRange(ChurchInfo(colony, hut));
        if (hut.IsStation) lines.Add(StationInfo(colony, hut));
        if (HasHutRecipes(hut.Type) && hut.RecipeKeep.Count > 0) lines.Add(HutRecipesInfo(colony, hut));
        if (hut.Type == BuildingTypes.Prospector) lines.Add(ProspectorInfo(colony, hut));
        if (hut.Type == BuildingTypes.Miner) lines.Add(MineInfo(colony, hut));
        if (hut.Type == BuildingTypes.Quarry) lines.Add(QuarryInfo(hut));
        if (hut.Type == BuildingTypes.Explorer) lines.Add(ExplorerInfo(colony, hut));
        if (hut.Type == BuildingTypes.Gatherer) lines.Add(GathererInfo(colony, hut));
        if (hut.Type == BuildingTypes.Hunter) lines.Add(HunterInfo(hut));
        lines.Add("");
        lines.Add(Lang.Get("createacolony:hint-" + hut.Type + (hut.IsStation ? "-station" : ""), GatherRadius(hut)));
        return string.Join("<br>", lines);
    }
}
