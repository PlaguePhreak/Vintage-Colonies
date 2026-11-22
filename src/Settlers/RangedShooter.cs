using System;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// One settler's bow or sling in a fight (0.178.0): draws for as long as the game asks of a player, holds
/// the shot while something is in the way, then shoots (ColonyManager.Shoot) and waits before the next.
/// Used by guards (AiTaskSettlerGuard) and by armed settlers fighting back (AiTaskSettlerDefend).
/// </summary>
public class RangedShooter
{
    float drawLeft = -1;
    float drawTotal;
    string? aimAnim;
    string? shotAnim;
    float shotLeft;
    float cooldown;
    /// <summary>A drawn shot held while something is in the way looks again after this many seconds.</summary>
    float holdLook;

    public bool Drawing => drawLeft >= 0;

    /// <summary>Counts down the wait between shots and ends the release animation. Call every tick.</summary>
    public void Tick(EntityAgent entity, float dt)
    {
        cooldown -= dt;
        if (shotAnim != null && (shotLeft -= dt) <= 0)
        {
            entity.AnimManager.StopAnimation(shotAnim);
            shotAnim = null;
        }
    }

    /// <summary>
    /// Aims at the target and shoots when the draw is done and the way is clear. Call every tick while the
    /// settler stands in range with its bow or sling in hand. True on the tick it shot.
    /// </summary>
    public bool Update(ColonyManager m, Colony colony, CitizenRecord citizen, EntityAgent entity, ItemSlot hand, Entity target, float dt, Action syncHand)
    {
        var weapon = hand.Itemstack;
        if (weapon == null || !ColonyManager.IsRangedWeapon(weapon))
        {
            Cancel(entity, hand, syncHand);
            return false;
        }
        entity.Pos.Yaw = (float)Math.Atan2(target.Pos.X - entity.Pos.X, target.Pos.Z - entity.Pos.Z);

        if (drawLeft < 0)
        {
            if (cooldown > 0 || (holdLook -= dt) > 0) return false;
            if (!m.ClearShot(entity, target))
            {
                holdLook = 0.3f;
                return false;
            }
            drawTotal = drawLeft = m.DrawSecondsOf(weapon);
            aimAnim = ColonyManager.AimAnimation(weapon);
            Play(entity, aimAnim);
            if (!ColonyManager.IsSling(weapon)) entity.World.PlaySoundAt(new AssetLocation("sounds/bow-draw"), entity, null, false, 8f, 1f);
            return false;
        }

        drawLeft -= dt;
        // The bow bends as it is drawn, as it does for players (the bow's render variants 1 to 3).
        int variant = GameMath.Clamp((int)Math.Ceiling((drawTotal - Math.Max(0, drawLeft)) / Math.Max(0.1f, drawTotal) * 3), 1, 3);
        if (weapon.Attributes.GetInt("renderVariant") != variant)
        {
            weapon.Attributes.SetInt("renderVariant", variant);
            syncHand();
        }
        if (drawLeft > 0) return false;

        // Fully drawn: hold it while someone is in the way.
        if ((holdLook -= dt) > 0) return false;
        if (!m.ClearShot(entity, target))
        {
            holdLook = 0.3f;
            return false;
        }
        StopAim(entity, hand, syncHand);
        if (!m.Shoot(colony, citizen, entity, hand, target)) return false;
        syncHand();
        shotAnim = ColonyManager.ShotAnimation(weapon);
        Play(entity, shotAnim);
        shotLeft = 0.4f;
        cooldown = m.Config.RangedSecondsPerShot * m.WorkTimeFactor(colony, citizen) / m.SwingFactor(colony, citizen);
        return true;
    }

    /// <summary>Lets the draw go without shooting (the target moved off, the settler had to walk).</summary>
    public void Cancel(EntityAgent entity, ItemSlot? hand, Action syncHand)
    {
        if (drawLeft < 0 && aimAnim == null) return;
        StopAim(entity, hand, syncHand);
    }

    void StopAim(EntityAgent entity, ItemSlot? hand, Action syncHand)
    {
        drawLeft = -1;
        if (aimAnim != null) entity.AnimManager.StopAnimation(aimAnim);
        aimAnim = null;
        if (hand?.Itemstack is { } held && held.Attributes.GetInt("renderVariant") != 0)
        {
            held.Attributes.SetInt("renderVariant", 0);
            syncHand();
        }
    }

    /// <summary>Ends every animation it started, when the task finishes.</summary>
    public void Stop(EntityAgent entity, ItemSlot? hand, Action syncHand)
    {
        StopAim(entity, hand, syncHand);
        if (shotAnim != null) entity.AnimManager.StopAnimation(shotAnim);
        shotAnim = null;
    }

    static void Play(EntityAgent entity, string code)
    {
        if (!SettlerAnimations.PlayerAnimations.Contains(code) || entity.AnimManager.IsAnimationActive(code)) return;
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Code = code,
            Animation = code,
            Weight = 5,
            BlendMode = EnumAnimationBlendMode.Average
        }.Init());
    }
}
