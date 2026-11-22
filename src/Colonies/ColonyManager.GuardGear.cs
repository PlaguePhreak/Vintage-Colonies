using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Guards keep their gear up to date: in a quiet moment they look in their tower's storage and the warehouse
/// for a better weapon, better armor or a better shield, take it, and leave what they had in its place (for
/// the Weaponsmith or Armourer to mend). Something only counts as better when it beats what the guard has by
/// GearUpgradeMargin, so a guard does not walk back for every scratch on its sword.
///
/// Shields: a shield in the offhand blocks blows from the front the way a player's does, with its passive
/// chance, or its active (raised) chance while a guard is fighting. At night a guard with a shield holds a
/// torch from its inventory instead and only raises the shield when it fights.
/// </summary>
public partial class ColonyManager
{
    /// <summary>A worn item counts for less, down to half at no durability left; a broken one for nothing.</summary>
    static float WearFactor(ItemStack stack)
    {
        int max = stack.Collectible.GetMaxDurability(stack);
        if (max <= 0) return 1f;
        int left = stack.Collectible.GetRemainingDurability(stack);
        return left <= 0 ? 0 : 0.5f + 0.5f * left / max;
    }

    /// <summary>How good a weapon is, to pick the better of two: how hard it hits, a little for its tier, less when worn.</summary>
    public static float WeaponScore(ItemStack stack) => (WeaponDamage(stack) + stack.Collectible.ToolTier * 0.5f) * WearFactor(stack);

    /// <summary>How good a shield is: its chance to block times what a block takes off, raised and not, less when worn.</summary>
    public static float ShieldScore(ItemStack stack)
    {
        if (!IsShield(stack)) return 0;
        var shield = stack.ItemAttributes["shield"];
        float score = shield["protectionChance"]["passive"].AsFloat(0) * shield["damageAbsorption"]["passive"].AsFloat(0)
            + shield["protectionChance"]["active"].AsFloat(0) * shield["damageAbsorption"]["active"].AsFloat(0);
        // A shield without the numbers still beats no shield.
        return Math.Max(0.01f, score) * WearFactor(stack);
    }

    /// <summary>True when the new score beats the old one by the upgrade margin (anything usable beats nothing).</summary>
    bool Beats(float score, float have) => score > 0 && (have <= 0 || score > have * (1 + Config.GearUpgradeMargin));

    /// <summary>The guard's best weapon: in its hand or carried.</summary>
    float BestWeaponScore(CitizenRecord citizen, ItemStack? held)
    {
        float best = held != null && IsWeapon(held) ? WeaponScore(held) : 0;
        foreach (var stack in CarriedItems(citizen).Where(IsWeapon)) best = Math.Max(best, WeaponScore(stack));
        return best;
    }

    ItemStack? ShieldOf(CitizenRecord citizen) => ToolBarItems(citizen)[OffhandSlot] is { } offhand && IsShield(offhand) ? offhand : null;

    float ShieldScoreOf(CitizenRecord citizen) => ShieldOf(citizen) is { } shield ? ShieldScore(shield) : 0;

    /// <summary>
    /// The first container (tower storage, then warehouse) with a weapon, armor piece or shield better than what
    /// the guard has, or null.
    /// </summary>
    public BlockPos? GearChest(Colony colony, Building? tower, CitizenRecord citizen, ItemStack? held)
    {
        float weapon = BestWeaponScore(citizen, held);
        float shield = Config.GuardsUseShields ? ShieldScoreOf(citizen) : -1;
        var worn = Config.GuardsWearArmor ? WornArmor(citizen) : null;
        foreach (var container in GearContainers(colony, tower))
        {
            foreach (var slot in container.Inventory)
            {
                if (slot.Empty) continue;
                var stack = slot.Itemstack;
                if (IsWeapon(stack) && Beats(WeaponScore(stack), weapon)) return container.Pos;
                if (shield >= 0 && IsShield(stack) && Beats(ShieldScore(stack), shield)) return container.Pos;
                if (worn != null && Beats(stack, worn)) return container.Pos;
                if (RangedGearWanted(citizen, held, stack)) return container.Pos;
            }
        }
        return null;
    }

    /// <summary>
    /// At the chest: takes the best weapon there into the hand if it beats the guard's, and puts the weapon it
    /// held into the chest. True when the hand changed.
    /// </summary>
    public bool TakeBetterWeapon(Colony colony, CitizenRecord citizen, BlockEntityContainer chest, ItemSlot hand)
    {
        var best = chest.Inventory.Where(s => !s.Empty && IsWeapon(s.Itemstack)).OrderByDescending(s => WeaponScore(s.Itemstack!)).FirstOrDefault();
        if (best == null || !Beats(WeaponScore(best.Itemstack!), BestWeaponScore(citizen, hand.Itemstack))) return false;
        var weapon = best.TakeOut(1);
        best.MarkDirty();
        var old = hand.Itemstack;
        hand.Itemstack = weapon;
        if (old != null) GiveBack(colony, citizen, chest, old, IsWeapon(old));
        chest.MarkDirty(true);
        return true;
    }

    /// <summary>At the chest: swaps the guard's shield for the best one there, if that is better. The old shield goes in the chest.</summary>
    public void TakeBetterShield(Colony colony, CitizenRecord citizen, BlockEntityContainer chest)
    {
        if (!Config.GuardsUseShields) return;
        var best = chest.Inventory.Where(s => !s.Empty && IsShield(s.Itemstack)).OrderByDescending(s => ShieldScore(s.Itemstack!)).FirstOrDefault();
        if (best == null || !Beats(ShieldScore(best.Itemstack!), ShieldScoreOf(citizen))) return;
        var shield = best.TakeOut(1);
        best.MarkDirty();
        var bar = ToolBarItems(citizen);
        var old = bar[OffhandSlot];
        bar[OffhandSlot] = shield;
        SaveToolBar(colony, citizen, bar);
        // A light it held goes in its inventory, for the dark; an old shield goes back.
        if (old != null)
        {
            if (IsShield(old)) GiveBack(colony, citizen, chest, old, true);
            else AddToPack(colony, citizen, old, false);
            if (old.StackSize > 0) sapi.World.SpawnItemEntity(old, chest.Pos.ToVec3d().Add(0.5, 1, 0.5));
        }
        chest.MarkDirty(true);
    }

    /// <summary>Puts old gear in the chest; if it is full, into the inventory (not the tool bar), else on the ground.</summary>
    void GiveBack(Colony colony, CitizenRecord citizen, BlockEntityContainer chest, ItemStack old, bool intoChest)
    {
        if (intoChest) PutItems(chest, old);
        if (old.StackSize > 0) AddToPack(colony, citizen, old, false);
        if (old.StackSize > 0) sapi.World.SpawnItemEntity(old, chest.Pos.ToVec3d().Add(0.5, 1, 0.5));
    }

    // ---- Shields in a fight -----------------------------------------------------------------

    /// <summary>Settlers (by entity id) holding their shield up: guards while they fight.</summary>
    readonly HashSet<long> shieldsUp = new();

    /// <summary>Raises or lowers the settler's shield. A raised shield shows in the left hand even in the dark.</summary>
    public void SetShieldUp(CitizenRecord citizen, Entity settler, bool up)
    {
        if (up ? !shieldsUp.Add(settler.EntityId) : !shieldsUp.Remove(settler.EntityId)) return;
        if (ShieldOf(citizen) != null) DressEntity(citizen);
    }

    bool ShieldIsUp(Entity settler) => shieldsUp.Contains(settler.EntityId);

    /// <summary>
    /// The game's shield rules for a settler: a shield shown in the left hand may block a blow from the front,
    /// with its active chance when raised, otherwise its passive one, and takes off what it absorbs.
    /// </summary>
    float ShieldBlock(Colony colony, CitizenRecord citizen, EntitySettler settler, DamageSource source, float damage)
    {
        if (damage <= 0 || Array.IndexOf(ArmorStops, source.Type) < 0) return damage;
        if (settler.LeftHandItemSlot?.Itemstack is not { } shown || !IsShield(shown)) return damage;
        var bar = ToolBarItems(citizen);
        if (bar[OffhandSlot] is not { } shield || !IsShield(shield) || WearFactor(shield) <= 0) return damage;
        var from = source.SourceEntity ?? source.CauseEntity;
        if (from == null) return damage;

        // Only blows from the front: within about 70 degrees of where the settler faces.
        double dx = from.Pos.X - settler.Pos.X, dz = from.Pos.Z - settler.Pos.Z;
        double length = Math.Sqrt(dx * dx + dz * dz);
        if (length > 0.01 && (dx * Math.Sin(settler.Pos.Yaw) + dz * Math.Cos(settler.Pos.Yaw)) / length < 0.34) return damage;

        bool raised = ShieldIsUp(settler);
        bool projectile = source.SourceEntity != null && source.CauseEntity != null && source.SourceEntity != source.CauseEntity;
        string how = raised ? "active" : "passive";
        var attrs = shield.ItemAttributes["shield"];
        if (sapi.World.Rand.NextDouble() >= attrs["protectionChance"][projectile ? how + "-projectile" : how].AsFloat(0)) return damage;
        JsonObject absorbs = projectile && attrs["projectileDamageAbsorption"].Exists ? attrs["projectileDamageAbsorption"] : attrs["damageAbsorption"];
        float absorb = absorbs[how].Exists ? absorbs[how].AsFloat(0) : absorbs.AsFloat(0);

        var dummy = new DummySlot(shield);
        shield.Collectible.DamageItem(sapi.World, settler, dummy, 1);
        bar[OffhandSlot] = dummy.Empty ? null : dummy.Itemstack;
        if (dummy.Empty) sapi.World.PlaySoundAt(new AssetLocation("sounds/effect/toolbreak"), settler);
        SaveToolBar(colony, citizen, bar);
        return Math.Max(0, damage - absorb);
    }
}
