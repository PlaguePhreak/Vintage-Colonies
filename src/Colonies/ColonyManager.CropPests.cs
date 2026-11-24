using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Farmers protect their crops (Aaron, 0.147.0). In the game, animals whose diet takes crops walk to farmland and
/// eat what grows there, leaving a dead crop. The game tells us when an animal picks a crop to eat and when it eats
/// one (CropWatch); such an animal on a colony's farmland becomes a crop pest. With a hunter in the colony, the
/// farmer calls the hunter, who goes after it first (AiTaskSettlerHunter); if the animal is still at the crops a
/// while later, or there is no hunter, the farmer goes after it with its hoe and kills it (AiTaskSettlerFarmer).
/// Livestock (bred, tamed or a baby) is only chased off, never killed. An animal that runs well away from the
/// fields counts as chased off. Kills are the colony's, so the hunter or settlers without a job skin them.
/// </summary>
public partial class ColonyManager
{
    const int CropWatchTickMs = 2000;
    /// <summary>An animal that only looked at crops counts once it is this many blocks from them.</summary>
    const int PestLookRange = 6;
    /// <summary>A crop eater nobody saw near the crops for this long is forgotten.</summary>
    const long PestForgetMs = 300_000;
    /// <summary>A hunter goes this far for a crop eater, from wherever it is.</summary>
    const int HunterPestRange = 128;
    /// <summary>"Something is eating the crops" goes into the Town Hall's log at most this often for each colony.</summary>
    const long PestAlarmEveryMs = 120_000;

    sealed class CropPest
    {
        public string ColonyId = "";
        public BlockPos Pos = null!;
        public long FirstMs;
        public long LastMs;
        public bool Livestock;
    }

    /// <summary>Animals at the colonies' crops, by entity id.</summary>
    readonly Dictionary<long, CropPest> cropPests = new();
    readonly Dictionary<string, long> pestAlarmMs = new();
    Harmony? cropHarmony;

    void StartCropWatch()
    {
        CropWatch.Seen.Clear();
        if (!Config.FarmerGuardsCrops) return;
        cropHarmony = new Harmony("createacolony.crops");
        try
        {
            CropWatch.Patch(cropHarmony);
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Could not watch for animals eating crops, so farmers will not chase them: {0}", e.Message);
            return;
        }
        sapi.Event.RegisterGameTickListener(OnCropWatchTick, CropWatchTickMs);
    }

    void StopCropWatch()
    {
        cropHarmony?.UnpatchAll("createacolony.crops");
        cropHarmony = null;
        CropWatch.Seen.Clear();
    }

    void OnCropWatchTick(float dt)
    {
        long now = sapi.World.ElapsedMilliseconds;
        foreach (long id in CropWatch.Seen.Keys.ToList())
        {
            if (!CropWatch.Seen.TryRemove(id, out var seen)) continue;
            var e = sapi.World.GetEntityById(id);
            if (e is not EntityAgent || !e.Alive || e is EntityPlayer || e is Settlers.EntitySettler) continue;
            // Only looking from afar: not yet a pest.
            if (!seen.Ate && e.Pos.HorDistanceTo(seen.Pos.ToVec3d().Add(0.5, 0, 0.5)) > PestLookRange) continue;
            if (ColonyForCrops(seen.Pos) is not { } colony) continue;

            if (cropPests.TryGetValue(id, out var pest) && pest.ColonyId == colony.ColonyId)
            {
                pest.Pos = seen.Pos;
                pest.LastMs = now;
                continue;
            }
            pest = new CropPest { ColonyId = colony.ColonyId, Pos = seen.Pos, FirstMs = now, LastMs = now, Livestock = IsLivestock(e) };
            cropPests[id] = pest;
            if (now - pestAlarmMs.GetValueOrDefault(colony.ColonyId, long.MinValue / 2) >= PestAlarmEveryMs)
            {
                pestAlarmMs[colony.ColonyId] = now;
                bool hunter = !pest.Livestock && HasHunter(colony);
                LogEvent(colony, hunter ? "event-crop-pest-hunter" : "event-crop-pest", e.GetName(), NearestPlaceName(colony, seen.Pos));
            }
        }

        // Dead, gone, chased well away from the crops, or not seen for a long time: no longer a pest.
        foreach (var (id, pest) in cropPests.ToList())
        {
            var e = sapi.World.GetEntityById(id);
            bool gone = e == null || !e.Alive || now - pest.LastMs > PestForgetMs
                || e.Pos.HorDistanceTo(pest.Pos.ToVec3d().Add(0.5, 0, 0.5)) > Config.FarmerChaseDistance + Config.FieldRadius;
            if (gone) cropPests.Remove(id);
        }
    }

    /// <summary>The colony whose crops these are: the colony's land, or one of its fields.</summary>
    Colony? ColonyForCrops(BlockPos pos)
    {
        if (GetAt(pos) is { } colony) return colony;
        int r = Config.FieldRadius + 1;
        return colonies.Values.FirstOrDefault(c => c.Fields.Any(f => Math.Abs(f.Pos.X - pos.X) <= r && Math.Abs(f.Pos.Z - pos.Z) <= r && Math.Abs(f.Pos.Y - pos.Y) <= 4));
    }

    /// <summary>Bred by players (generation 1 and up), tamed, or a baby: someone's livestock.</summary>
    public static bool IsLivestock(Entity e) =>
        e.WatchedAttributes.GetInt("generation") > 0 || e.WatchedAttributes.HasAttribute("domesticationstatus")
        || e.Code.Path.Contains("-baby") || e.Code.Path.EndsWith("-calf") || e.Code.Path.Contains("-piglet")
        || e.Code.Path.Contains("-lamb") || e.Code.Path.Contains("-chick");

    /// <summary>The colony has a hunter working from a Hunter's Hut.</summary>
    bool HasHunter(Colony colony) => colony.Citizens.Any(c => c.Alive && c.JobCode == Jobs.Hunter && c.WorkplacePos != null);

    /// <summary>A wild animal eating this colony's crops, which its hunter should kill.</summary>
    public bool IsCropPest(Colony colony, Entity e) =>
        e.Alive && cropPests.TryGetValue(e.EntityId, out var p) && p.ColonyId == colony.ColonyId && !p.Livestock;

    /// <summary>The nearest wild animal eating this colony's crops, for its hunter.</summary>
    public Entity? CropPestForHunter(Colony colony, Vec3d from, System.Func<long, bool> skip) => cropPests
        .Where(p => p.Value.ColonyId == colony.ColonyId && !p.Value.Livestock && !skip(p.Key))
        .Select(p => sapi.World.GetEntityById(p.Key))
        .Where(e => e != null && e.Alive && e.Pos.SquareDistanceTo(from) <= HunterPestRange * HunterPestRange)
        .OrderBy(e => e!.Pos.SquareDistanceTo(from))
        .FirstOrDefault();

    /// <summary>
    /// True when this farmer should go after this crop eater now: it is at the crops near the farmer's fields or hut
    /// (or the colony has no other farmer), and it is livestock to chase off, or no hunter came for it in time.
    /// </summary>
    public bool FarmerShouldChase(Colony colony, CitizenRecord farmer, Building hut, Entity e)
    {
        if (!e.Alive || !cropPests.TryGetValue(e.EntityId, out var p) || p.ColonyId != colony.ColonyId) return false;
        int r = Config.FieldRadius + PestLookRange;
        int near = Math.Max(32, Config.FarmerOrchardRadius);
        bool mine = colony.Fields.Any(f => f.FarmerCitizenId == farmer.CitizenId && Math.Abs(f.Pos.X - p.Pos.X) <= r && Math.Abs(f.Pos.Z - p.Pos.Z) <= r)
            || hut.HutPos.HorDistanceSqTo(p.Pos.X, p.Pos.Z) <= near * near
            || !colony.Citizens.Any(c => c.Alive && c.JobCode == Jobs.Farmer && c.CitizenId != farmer.CitizenId);
        if (!mine) return false;
        if (p.Livestock || !HasHunter(colony)) return true;
        return sapi.World.ElapsedMilliseconds - p.FirstMs >= Config.FarmerWaitsForHunterSeconds * 1000;
    }

    /// <summary>The nearest crop eater this farmer should go after now, or null.</summary>
    public Entity? CropPestForFarmer(Colony colony, CitizenRecord farmer, Building hut, Vec3d from, System.Func<long, bool> skip) => cropPests
        .Where(p => p.Value.ColonyId == colony.ColonyId && !skip(p.Key))
        .Select(p => sapi.World.GetEntityById(p.Key))
        .Where(e => e != null && FarmerShouldChase(colony, farmer, hut, e))
        .OrderBy(e => e!.Pos.SquareDistanceTo(from))
        .FirstOrDefault();

    /// <summary>
    /// A farmer hits a crop eater with what it holds (its hoe), as hard as that tool hits for a player. Livestock is
    /// only pushed away. A wild animal that dies is the colony's kill, for the hunter or jobless settlers to skin.
    /// </summary>
    public void FarmerStrike(Colony colony, CitizenRecord citizen, Building hut, EntityAgent farmer, Entity pest)
    {
        if (!cropPests.TryGetValue(pest.EntityId, out var record)) return;
        if (record.Livestock)
        {
            var away = pest.Pos.XYZ.Sub(farmer.Pos.XYZ);
            double length = Math.Max(0.01, Math.Sqrt(away.X * away.X + away.Z * away.Z));
            pest.Pos.Motion.Add(away.X / length * 0.4, 0.15, away.Z / length * 0.4);
            return;
        }
        var hand = farmer.RightHandItemSlot;
        var held = hand?.Itemstack;
        float damage = Math.Max(Config.GuardFistDamage, held?.Collectible.GetAttackPower(held) ?? 0);
        damage *= DamageFactor(colony, citizen);
        damage += TraitAdd(citizen, "damageAdd");
        this.SafeHit(pest, new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = farmer,
            CauseEntity = farmer,
            Type = EnumDamageType.BluntAttack,
            DamageTier = held?.Collectible.ToolTier ?? 0
        }, damage);
        if (held != null && hand != null) WearTool(farmer, hand);
        if (pest.Alive) return;
        pest.WatchedAttributes.SetString(HuntedByAttr, colony.ColonyId);
        cropPests.Remove(pest.EntityId);
        CountKill(colony, citizen);
        AddStat(hut, "pests-killed");
        LogEvent(colony, "event-pest-killed", citizen.Name, pest.GetName());
    }
}

/// <summary>Hooks into the game's farmland to see animals choosing crops to eat and eating them.</summary>
static class CropWatch
{
    /// <summary>Animals seen at crops since the last tick, by entity id: where, and whether they ate. Only the server side adds to it.</summary>
    public static readonly ConcurrentDictionary<long, (BlockPos Pos, bool Ate)> Seen = new();

    public static void Patch(Harmony harmony)
    {
        var suitable = AccessTools.Method(typeof(BlockEntityFarmland), nameof(BlockEntityFarmland.IsSuitableFor))
            ?? throw new MissingMethodException(nameof(BlockEntityFarmland), nameof(BlockEntityFarmland.IsSuitableFor));
        var eat = AccessTools.Method(typeof(BlockEntityFarmland), nameof(BlockEntityFarmland.ConsumeOnePortion))
            ?? throw new MissingMethodException(nameof(BlockEntityFarmland), nameof(BlockEntityFarmland.ConsumeOnePortion));
        harmony.Patch(suitable, postfix: new HarmonyMethod(typeof(CropWatch), nameof(Wanted)));
        harmony.Patch(eat, prefix: new HarmonyMethod(typeof(CropWatch), nameof(Eaten)));
    }

    static void Wanted(BlockEntityFarmland __instance, Entity entity, bool __result)
    {
        if (!__result || entity == null || __instance.Api?.Side != EnumAppSide.Server) return;
        Seen.AddOrUpdate(entity.EntityId, (__instance.Pos.Copy(), false), (_, old) => old.Ate ? old : (__instance.Pos.Copy(), false));
    }

    static void Eaten(BlockEntityFarmland __instance, Entity entity)
    {
        if (entity == null || __instance.Api?.Side != EnumAppSide.Server || __instance.GetCrop() == null) return;
        Seen[entity.EntityId] = (__instance.Pos.Copy(), true);
    }
}
