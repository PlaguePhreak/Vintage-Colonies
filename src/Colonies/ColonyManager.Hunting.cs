using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The Hunter's Hut. Its hunter goes after animals around the hut with a spear (any weapon does), skins what
/// it kills with its knife (ColonyManager.HomeCooking.cs) and brings the meat, hides and the rest to the hut,
/// where couriers pick them up. It leaves monsters to the guards, and leaves alone animals bred in pens and
/// babies. A kill it leaves behind (it had no knife, or its hands were full) is marked as the colony's, so
/// settlers without a job skin it. The Allowed to collect tab says which animals it hunts.
/// </summary>
public partial class ColonyManager
{
    /// <summary>On a carcass: the colony whose hunter killed it.</summary>
    public const string HuntedByAttr = "createacolony:huntedBy";

    /// <summary>The key of an animal on the Allowed to collect tab: "animal-" and its entity code.</summary>
    public static string AnimalKey(Entity e) => "animal-" + e.Code.Path;

    /// <summary>Every animal a hunter could hunt: creatures with something to skin that are not monsters guards fight.</summary>
    List<CollectKind> AnimalKinds()
    {
        var kinds = new List<CollectKind>();
        foreach (var type in sapi.World.EntityTypes)
        {
            if (type?.Code == null || type.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "harvestable") != true) continue;
            string path = type.Code.Path;
            if (Config.GuardTargets.Any(p => WildcardUtil.Match(p, path))) continue;
            if (path.StartsWith("player") || type.Code.Domain == "createacolony") continue;
            var item = sapi.World.GetItem(new AssetLocation(type.Code.Domain, "creature-" + path));
            var sample = item != null ? new ItemStack(item) : new ItemStack(sapi.World.GetItem(new AssetLocation("game:hide-raw-small")) ?? sapi.World.GetItem(new AssetLocation("game:stick")));
            if (sample.Collectible == null) continue;
            kinds.Add(new CollectKind("animal-" + path, sample));
        }
        return kinds.OrderBy(k => k.Key).ToList();
    }

    /// <summary>True for a living animal this hut's hunter may hunt.</summary>
    public bool IsPrey(Colony colony, Building hut, Entity e)
    {
        if (!e.Alive || e is not EntityAgent || e is EntityPlayer || e is Settlers.EntitySettler) return false;
        if (e.GetBehavior<EntityBehaviorHarvestable>() == null || IsThreat(e)) return false;
        if (!MayCollect(hut, AnimalKey(e))) return false;
        // Bred by players (generation 1 and up), tamed, or still a baby.
        if (Config.HunterSparesLivestock && IsLivestock(e)) return false;
        // Animals inside the colony's buildings are someone's livestock.
        return ZoneAt(colony, e.Pos.AsBlockPos) == null;
    }

    /// <summary>The nearest animal in the hut's work area (or within range, on a far trip) to hunt, or null.</summary>
    public Entity? FindPrey(Colony colony, Building hut, Vec3d from, System.Func<long, bool> skip, int range = 0)
    {
        int r = range > 0 ? range : GatherRadius(hut);
        var center = hut.HutPos.ToVec3d().Add(0.5, 0, 0.5);
        return sapi.World.GetEntitiesAround(from, Math.Min(r, 48), 16, e => !skip(e.EntityId) && IsPrey(colony, hut, e))
            .Where(e => e.Pos.HorDistanceTo(center) <= r)
            .OrderBy(e => e.Pos.SquareDistanceTo(from))
            .FirstOrDefault();
    }

    /// <summary>
    /// A predator in the hut's work area (a bear, a wolf, a hyena: anything guards fight). Hunters leave those to the
    /// guards (Aaron, 0.202.0), and their status says so.
    /// </summary>
    public Entity? PredatorLeftToGuards(Building hut, Vec3d from)
    {
        int r = GatherRadius(hut);
        var center = hut.HutPos.ToVec3d().Add(0.5, 0, 0.5);
        return sapi.World.GetEntitiesAround(from, Math.Min(r, 48), 16, e => IsThreat(e) && e is not Settlers.EntityBandit && RaidOf(e) == null)
            .Where(e => e.Pos.HorDistanceTo(center) <= r)
            .OrderBy(e => e.Pos.SquareDistanceTo(from))
            .FirstOrDefault();
    }

    /// <summary>A carcass this hut's hunter killed that still has things on it, nearest first.</summary>
    public Entity? HunterCarcass(Colony colony, Building hut, Vec3d from, System.Func<long, bool> skip) =>
        sapi.World.GetEntitiesAround(from, 40, 16, e => !skip(e.EntityId) && Skinnable(e) && e.WatchedAttributes.GetString(HuntedByAttr) == colony.ColonyId)
            .OrderBy(e => e.Pos.SquareDistanceTo(from))
            .FirstOrDefault();

    /// <summary>A hunter strikes an animal; returns true when it died.</summary>
    public bool HunterStrike(Colony colony, CitizenRecord citizen, Building hut, EntityAgent hunter, Entity prey)
    {
        var hand = hunter.RightHandItemSlot;
        var weapon = hand?.Itemstack is { } held && IsWeapon(held) ? held : null;
        float damage = weapon != null ? WeaponDamage(weapon) : Config.GuardFistDamage;
        damage *= ClampEffect(1f + 0.03f * UsableLevel(colony, citizen, "hunting") + 0.03f * UsableLevel(colony, citizen, Strength));
        damage += TraitAdd(citizen, "damageAdd");
        this.SafeHit(prey, new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = hunter,
            CauseEntity = hunter,
            Type = weapon != null ? EnumDamageType.PiercingAttack : EnumDamageType.BluntAttack,
            DamageTier = weapon?.Collectible.ToolTier ?? 0
        }, damage);
        if (weapon != null && hand != null) WearTool(hunter, hand);
        if (prey.Alive) return false;
        prey.WatchedAttributes.SetString(HuntedByAttr, colony.ColonyId);
        AddStat(hut, "kill:" + prey.Code.Path);
        return true;
    }

    /// <summary>The Hunter's Hut window line.</summary>
    string HunterInfo(Building hut) => Lang.Get("createacolony:hunter-progress", hut.StatTotals.Where(s => s.Key.StartsWith("kill:")).Sum(s => s.Value));
}
