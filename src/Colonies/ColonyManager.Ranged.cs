using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Bows and slings (0.178.0, design doc https://claude.ai/artifact/X1rem76HRLvrBPbsY3UWZp). A guard set to
/// Ranged on the Colony militia tab fights with the best bow or sling it has, shooting real arrows and stones
/// the way the game's own bows and slings do (the same projectile entities, damage and break chance), aimed
/// on an arc at where the target will be. After a fight it picks up the arrows the colony's settlers shot.
/// Other armed settlers use a bow or sling only when they have no melee weapon (Aaron: spears and shields first).
/// </summary>
public partial class ColonyManager
{
    /// <summary>A bow or sling.</summary>
    public static bool IsRangedWeapon(ItemStack? stack) => stack?.Collectible is ItemBow or ItemSling;

    public static bool IsSling(ItemStack? stack) => stack?.Collectible is ItemSling;

    public static bool IsArrow(ItemStack? stack) => stack?.Collectible.Code.PathStartsWith("arrow-") == true;

    /// <summary>Something a bow or sling shoots: arrows, or stones for a sling.</summary>
    public static bool IsAmmo(ItemStack? stack) => IsArrow(stack) || stack?.Collectible is ItemStone;

    /// <summary>What this weapon shoots: arrows for a bow, stones for a sling (as the game's ItemBow and ItemSling look for).</summary>
    public static bool IsAmmoFor(ItemStack weapon, ItemStack ammo) => IsSling(weapon) ? ammo.Collectible is ItemStone : IsArrow(ammo);

    static float AttrDamage(ItemStack stack) => stack.Collectible.Attributes?["damage"].AsFloat(0) ?? 0;

    /// <summary>How good a bow or sling is: its damage, a little for its tier, less when worn.</summary>
    public static float RangedScore(ItemStack stack) =>
        (AttrDamage(stack) + 1 + (stack.Collectible.Attributes?["damageTier"].AsInt(0) ?? 0) * 0.5f) * WearFactor(stack);

    /// <summary>How many shots this weapon has in the settler's inventory.</summary>
    public int AmmoCount(CitizenRecord citizen, ItemStack weapon) => PackCount(citizen, s => IsAmmoFor(weapon, s));

    /// <summary>The best bow or sling the settler carries or holds, with or without anything to shoot.</summary>
    public ItemStack? BestRanged(CitizenRecord citizen, ItemStack? held) =>
        CarriedItems(citizen).Append(held).OfType<ItemStack>().Where(IsRangedWeapon).OrderByDescending(RangedScore).FirstOrDefault();

    /// <summary>The best bow or sling the settler has something to shoot with, or null.</summary>
    public ItemStack? ReadyRanged(CitizenRecord citizen, ItemStack? held) =>
        CarriedItems(citizen).Append(held).OfType<ItemStack>().Where(IsRangedWeapon).Where(w => AmmoCount(citizen, w) > 0)
            .OrderByDescending(RangedScore).FirstOrDefault();

    /// <summary>
    /// The weapon an armed settler who is not a guard fights with (Aaron, 0.178.0): a spear first, then the
    /// hardest-hitting other melee weapon, and a bow or sling with something to shoot only as the last choice.
    /// </summary>
    public ItemStack? CivilianWeapon(CitizenRecord citizen, ItemStack? held)
    {
        var all = CarriedItems(citizen).Append(held).OfType<ItemStack>().ToList();
        var melee = all.Where(IsWeapon).OrderByDescending(s => s.Collectible.Tool == EnumTool.Spear).ThenByDescending(WeaponDamage).FirstOrDefault();
        return melee ?? all.Where(IsRangedWeapon).Where(w => AmmoCount(citizen, w) > 0).OrderByDescending(RangedScore).FirstOrDefault();
    }

    /// <summary>What the Colony militia tab shows as a settler's weapon: a ranged guard's bow and its arrows, else the weapon it fights with.</summary>
    public string WeaponText(CitizenRecord citizen)
    {
        var held = sapi.World.GetEntityById(citizen.EntityId) is EntityAgent agent ? AiTaskSettlerEat.ToolInHand(agent) : null;
        ItemStack? weapon = citizen.JobCode == Jobs.Guard
            ? (citizen.GuardRanged ? BestRanged(citizen, held) : null) ?? BestWeapon(citizen)
            : CivilianWeapon(citizen, held);
        if (weapon == null) return "";
        if (!IsRangedWeapon(weapon)) return weapon.GetName();
        return Lang.Get(IsSling(weapon) ? "createacolony:militia-weapon-stones" : "createacolony:militia-weapon-arrows", weapon.GetName(), AmmoCount(citizen, weapon));
    }

    // ---- Shooting -----------------------------------------------------------------------------

    /// <summary>How far this weapon reaches.</summary>
    public float RangeOf(ItemStack weapon) => IsSling(weapon) ? Config.SlingRange : Config.BowRange;

    /// <summary>Seconds to draw this weapon before a shot.</summary>
    public float DrawSecondsOf(ItemStack weapon) => IsSling(weapon) ? Config.SlingDrawSeconds : Config.BowDrawSeconds;

    /// <summary>The animation a settler plays while it draws: the one the weapon sets for players.</summary>
    public static string AimAnimation(ItemStack weapon) =>
        IsSling(weapon) ? "slingaimbalearic" : weapon.Collectible.Attributes?["aimAnimation"].AsString("bowaim") ?? "bowaim";

    public static string ShotAnimation(ItemStack weapon) => IsSling(weapon) ? "slingthrowbalearic" : "bowhit";

    static Vec3d Eye(Entity e) => e.Pos.XYZ.Add(0, e is EntityAgent a ? a.LocalEyePos.Y : e.CollisionBox.Y2 * 0.8, 0);

    /// <summary>Where a shot aims at a creature: the middle of its body.</summary>
    static Vec3d Body(Entity e) => e.Pos.XYZ.Add(0, Math.Max(0.3, e.CollisionBox.Y2 * 0.55), 0);

    /// <summary>
    /// True when nothing is in the way of a shot: no solid block on the straight line, and no player, settler
    /// or animal that is not a monster close to it (Aaron: no shooting into a crowd).
    /// </summary>
    public bool ClearShot(EntityAgent shooter, Entity target)
    {
        var from = Eye(shooter);
        var to = Body(target);
        double length = from.DistanceTo(to);
        if (length < 1) return true;
        var dir = (to - from) * (1 / length);
        var ba = sapi.World.BlockAccessor;
        var pos = new BlockPos(shooter.Pos.Dimension);
        for (double t = 0.8; t < length - 0.6; t += 0.4)
        {
            double x = from.X + dir.X * t, y = from.Y + dir.Y * t, z = from.Z + dir.Z * t;
            pos.Set((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
            var boxes = ba.GetBlock(pos).GetCollisionBoxes(ba, pos);
            if (boxes == null) continue;
            foreach (var box in boxes)
            {
                if (box.Contains(x - pos.X, y - pos.Y, z - pos.Z)) return false;
            }
        }

        var middle = from.AddCopy(dir * (length / 2));
        float around = (float)(length / 2 + 2);
        var friends = sapi.World.GetEntitiesAround(middle, around, around, e =>
            e.EntityId != shooter.EntityId && e.EntityId != target.EntityId && e.Alive && e.IsInteractable
            && (e is EntityPlayer || e is EntitySettler || e is EntityAgent && !IsThreat(e)));
        foreach (var friend in friends)
        {
            var p = Body(friend);
            double along = Math.Clamp((p - from).Dot(dir), 0, length);
            var closest = from.AddCopy(dir * along);
            if (closest.SquareDistanceTo(p) < 1.0) return false;
        }
        return true;
    }

    /// <summary>
    /// Shoots the bow or sling in the settler's hand at the target, as a player would: one arrow (or stone) from
    /// its inventory, the best it has, flying on an arc to where the target is going. Bow and arrow damage add up
    /// as in the game, times the settler's Fighting and Strength. False when it had nothing to shoot.
    /// </summary>
    public bool Shoot(Colony colony, CitizenRecord citizen, EntityAgent shooter, ItemSlot hand, Entity target)
    {
        var weapon = hand.Itemstack;
        if (weapon == null || !IsRangedWeapon(weapon)) return false;
        var best = CarriedItems(citizen).Where(s => IsAmmoFor(weapon, s)).OrderByDescending(AttrDamage).FirstOrDefault();
        if (best == null) return false;
        var ammo = TakeFromPack(colony, citizen, s => s.Collectible.Code.Equals(best.Collectible.Code), 1);
        if (ammo == null) return false;

        bool sling = IsSling(weapon);
        string code = sling
            ? "thrownstone-" + ammo.Collectible.Variant["rock"]
            : ammo.ItemAttributes?["arrowEntityCode"].AsString(null) ?? "arrow-" + ammo.Collectible.Variant["material"];
        var type = sapi.World.GetEntityType(new AssetLocation(code));
        var entity = type == null ? null : sapi.World.ClassRegistry.CreateEntity(type);
        if (entity is not IProjectile projectile)
        {
            AddToPack(colony, citizen, ammo, false);
            return false;
        }

        projectile.FiredBy = shooter;
        projectile.Damage = (AttrDamage(weapon) + AttrDamage(ammo)) * DamageFactor(colony, citizen);
        projectile.DamageTier = sling ? 0 : weapon.Collectible.Attributes?["damageTier"].AsInt(0) ?? 0;
        projectile.ProjectileStack = ammo;
        projectile.WeaponStack = weapon;
        projectile.IgnoreInvFrames = weapon.Collectible.Attributes?["ignoreInvFrames"].AsBool(false) ?? false;
        if (!sling) projectile.DropOnImpactChance = 1f - (ammo.ItemAttributes?["breakChanceOnImpact"].AsFloat(0.5f) ?? 0.5f);

        // Aim on an arc at where the target will be when the shot gets there (as the game's own archers aim).
        var from = Eye(shooter);
        var to = Body(target);
        double speed = (sling ? Config.SlingStoneSpeed : Config.ArrowSpeed) / 60.0;
        double gravity = GlobalConstants.GravityPerSecond / 60.0;
        to.Add(target.Pos.Motion.X * from.DistanceTo(to) / speed, 0, target.Pos.Motion.Z * from.DistanceTo(to) / speed);
        var velocity = AimArc(from, to, speed, gravity);

        // Better fighters aim truer (the Fighting skill's swing speed stands for their skill here).
        double spread = Config.RangedSpreadDegrees / Math.Max(0.5, SwingFactor(colony, citizen)) * GameMath.DEG2RAD;
        var rand = sapi.World.Rand;
        double yaw = Math.Atan2(velocity.X, velocity.Z) + (rand.NextDouble() + rand.NextDouble() - 1) * spread;
        double flat = Math.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        double pitch = Math.Atan2(velocity.Y, flat) + (rand.NextDouble() + rand.NextDouble() - 1) * spread;
        double length = velocity.Length();
        velocity = new Vec3d(Math.Sin(yaw) * Math.Cos(pitch) * length, Math.Sin(pitch) * length, Math.Cos(yaw) * Math.Cos(pitch) * length);

        shooter.Pos.Yaw = (float)Math.Atan2(to.X - shooter.Pos.X, to.Z - shooter.Pos.Z);
        var start = from.AddCopy(velocity.Clone().Normalize() * 0.5);
        entity.Pos.SetFrom(shooter.Pos);
        entity.Pos.SetPos(start);
        entity.Pos.Motion.Set(velocity);
        entity.World = sapi.World;
        projectile.PreInitialize();
        sapi.World.SpawnPriorityEntity(entity);
        sapi.World.PlaySoundAt(new AssetLocation(sling ? "sounds/tool/sling1" : "sounds/bow-release"), shooter, null, false, 8f, sling ? 0.25f : 1f);
        WearTool(shooter, hand);
        return true;
    }

    /// <summary>The lower of the two arcs that reach the target at this speed; a straight shot when it is out of reach.</summary>
    static Vec3d AimArc(Vec3d from, Vec3d to, double speed, double gravity)
    {
        for (int i = 0; i < 20; i++, speed *= 1.1)
        {
            double dx = to.X - from.X, dz = to.Z - from.Z, dy = to.Y - from.Y;
            double flat = Math.Sqrt(dx * dx + dz * dz);
            double s2 = speed * speed;
            double root = s2 * s2 - gravity * (gravity * flat * flat + 2 * dy * s2);
            if (root < 0 || flat < 0.01) continue;
            double angle = Math.Atan2(s2 - Math.Sqrt(root), gravity * flat);
            double up = speed * Math.Sin(angle), along = speed * Math.Cos(angle);
            return new Vec3d(dx / flat * along, up, dz / flat * along);
        }
        return (to - from).Normalize() * speed;
    }

    /// <summary>A creature killed by a settler's arrow or stone: the kill counts for the shooter as a blow would (Veteran, Fighting XP, tower statistics).</summary>
    void OnShotKill(Entity dead, DamageSource? source)
    {
        if (source?.SourceEntity is not IProjectile || source.CauseEntity is not EntitySettler { ColonyId: { } id } shooter) return;
        if (GetById(id) is not { } colony || colony.Citizens.Find(c => c.CitizenId == shooter.CitizenId) is not { } citizen) return;
        CountKill(colony, citizen);
        GainWorkXp(colony, citizen, Jobs.Guard, 5);
        if (citizen.JobCode == Jobs.Guard && TowerOf(colony, citizen) is { } tower) AddStat(tower, "kill:" + dead.Code.Path);
        else if (citizen.JobCode != Jobs.Guard) LogEvent(colony, "event-fought-off", citizen.Name, dead.GetName());
    }

    // ---- Picking arrows up --------------------------------------------------------------------

    /// <summary>
    /// The nearest arrow or stone lying still within the radius that a settler of this colony shot, or an arrow
    /// lying loose on the colony's land. Ones in skip could not be reached.
    /// </summary>
    public Entity? LooseAmmoNear(Colony colony, EntityAgent guard, float radius, HashSet<long> skip)
    {
        var near = sapi.World.GetEntitiesAround(guard.Pos.XYZ, radius, 8, e => e.Alive && !skip.Contains(e.EntityId) && IsColonyAmmo(colony, guard, e));
        return near.OrderBy(e => e.Pos.SquareDistanceTo(guard.Pos.XYZ)).FirstOrDefault();
    }

    bool IsColonyAmmo(Colony colony, EntityAgent guard, Entity e)
    {
        if (e is EntityProjectileBase shot)
        {
            if (shot.ProjectileStack == null || !shot.CanCollect(guard)) return false;
            var by = shot.FiredBy ?? sapi.World.GetEntityById(shot.WatchedAttributes.GetLong("firedBy"));
            return by is EntitySettler s ? s.ColonyId == colony.ColonyId : by == null && GetAt(e.Pos.AsBlockPos) == colony;
        }
        return e is EntityItem item && IsArrow(item.Itemstack) && GetAt(e.Pos.AsBlockPos) == colony;
    }

    /// <summary>Puts a loose arrow or stone in the guard's inventory. False when there was nothing to take.</summary>
    public bool PickUpAmmo(Colony colony, CitizenRecord citizen, EntityAgent guard, Entity loose)
    {
        if (!loose.Alive) return false;
        var stack = loose is EntityProjectileBase shot ? shot.OnCollected(guard)?.Clone() : (loose as EntityItem)?.Itemstack?.Clone();
        if (stack == null || stack.StackSize <= 0) return false;
        loose.Die(EnumDespawnReason.PickedUp);
        AddToPack(colony, citizen, stack, false);
        if (stack.StackSize > 0) sapi.World.SpawnItemEntity(stack, guard.Pos.XYZ);
        return true;
    }

    // ---- Gear ---------------------------------------------------------------------------------

    /// <summary>True when a ranged guard wants a better bow or sling, or more to shoot, from this stack.</summary>
    bool RangedGearWanted(CitizenRecord citizen, ItemStack? held, ItemStack stack)
    {
        if (!citizen.GuardRanged) return false;
        var have = BestRanged(citizen, held);
        if (IsRangedWeapon(stack)) return Beats(RangedScore(stack), have == null ? 0 : RangedScore(have));
        return have != null && IsAmmoFor(have, stack) && AmmoCount(citizen, have) < Config.GuardAmmoLow && !PackFull(citizen);
    }

    /// <summary>At the chest: a ranged guard swaps in a better bow or sling (the old one stays there) and fills up on arrows or stones.</summary>
    public void TakeRangedGear(Colony colony, CitizenRecord citizen, BlockEntityContainer chest, ItemSlot hand)
    {
        if (!citizen.GuardRanged) return;
        var best = chest.Inventory.Where(s => !s.Empty && IsRangedWeapon(s.Itemstack)).OrderByDescending(s => RangedScore(s.Itemstack!)).FirstOrDefault();
        var have = BestRanged(citizen, hand.Itemstack);
        if (best != null && Beats(RangedScore(best.Itemstack!), have == null ? 0 : RangedScore(have)))
        {
            var weapon = best.TakeOut(1);
            best.MarkDirty();
            if (have != null)
            {
                ItemStack? old;
                if (hand.Itemstack == have)
                {
                    old = hand.Itemstack;
                    hand.Itemstack = null;
                    hand.MarkDirty();
                }
                else old = TakeFromPack(colony, citizen, s => s.Collectible.Code.Equals(have.Collectible.Code), 1);
                if (old != null) GiveBack(colony, citizen, chest, old, true);
            }
            AddToPack(colony, citizen, weapon);
            if (weapon.StackSize > 0) sapi.World.SpawnItemEntity(weapon, chest.Pos.ToVec3d().Add(0.5, 1, 0.5));
            if (have == null) LogEvent(colony, "event-archer-armed", citizen.Name, weapon.GetName());
            have = BestRanged(citizen, hand.Itemstack);
        }
        if (have == null) return;
        int need = Config.GuardAmmoCarried - AmmoCount(citizen, have);
        foreach (var slot in chest.Inventory.Where(s => !s.Empty && IsAmmoFor(have, s.Itemstack)).OrderByDescending(s => AttrDamage(s.Itemstack!)).ToList())
        {
            if (need <= 0) break;
            var taken = slot.TakeOut(Math.Min(need, slot.StackSize));
            slot.MarkDirty();
            int size = taken.StackSize;
            AddToPack(colony, citizen, taken, false);
            need -= size - taken.StackSize;
            if (taken.StackSize > 0)
            {
                PutItems(chest, taken);
                break;
            }
        }
        chest.MarkDirty(true);
    }

    /// <summary>A ranged guard out of arrows (or stones) asks couriers to bring some to its tower, once at a time.</summary>
    public void EnsureAmmoRequest(Colony colony, Building? tower, ItemStack weapon)
    {
        if (tower == null) return;
        var chest = HutStorage(colony, tower).FirstOrDefault();
        if (chest == null) return;
        string code = IsSling(weapon) ? "game:stone-*" : "game:arrow-*";
        string tag = RequestTag(tower);
        if (!colony.Requests.Any(r => r.RequestedBy == tag && r.ItemCode == code)) AddRequest(colony, code, Math.Max(1, Config.GuardAmmoCarried), chest.Pos, tag);
    }
}
