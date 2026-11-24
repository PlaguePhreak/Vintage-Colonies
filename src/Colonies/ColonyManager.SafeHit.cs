using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Hits that never break a settler's turn (0.198.0). In Aaron's play test every guard hit on some creature during a
/// temporal storm threw an error inside the game's "rip harvestable" code (patched by another mod), which cut the
/// guard's turn short and made the mod save an automatic report. The hit is now caught; the target still takes the
/// damage straight off its health, and the log names the creature once.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Creature types whose hits already threw once, so the log names each only once.</summary>
    readonly HashSet<string> badHitTargets = new();

    /// <summary>ReceiveDamage, but an error inside another mod's code still lands the hit. True when the target was hurt.</summary>
    public bool SafeHit(Entity target, DamageSource source, float damage)
    {
        try
        {
            return target.ReceiveDamage(source, damage);
        }
        catch (Exception e)
        {
            string code = target.Code?.ToString() ?? "?";
            if (badHitTargets.Add(code))
                Mod.Logger.Warning("[createacolony] Hitting {0} threw inside the game or another mod ({1}: {2}); its health is lowered directly instead.", code, e.GetType().Name, e.Message);
            if (!target.Alive) return false;
            var health = target.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return false;
            health.Health = Math.Max(0, health.Health - damage);
            if (health.Health <= 0) target.Die(EnumDespawnReason.Death, source);
            return true;
        }
    }
}
