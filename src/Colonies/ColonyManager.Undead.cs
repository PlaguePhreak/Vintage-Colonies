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
/// Monster races, step 1: the Restless (zombies), 0.187.0. Design doc https://claude.ai/artifact/W5h1tzifqjBvYEoRfBR73s
/// with Aaron's answers (22 Nov 2026). The Restless are a raid faction ("restless") like the others: they come
/// only at night, in bigger groups than other raids, never steal, never light fires, never run and never break
/// blocks. At sunrise the ones still standing walk off, and they come back the next night, until every one is
/// killed (Aaron). While they are away by day the raid stays open, but settlers go back to work. A bite can make
/// a settler ill: an ill settler rests at home until it wears off or the Healer treats it. Each colony can switch
/// the race off on the Town Hall Settings tab (on by default).
/// A raid in progress is still not saved: after a restart, the Restless are gone, as other raiders are.
/// </summary>
public partial class ColonyManager
{
    public const string RestlessFaction = "restless";

    /// <summary>Monster races a colony can switch off on its Town Hall Settings tab, in the order shown.</summary>
    public static readonly string[] RaidRaces = { RestlessFaction };

    /// <summary>True for a faction that leaves at sunrise and comes back each night until it is beaten.</summary>
    public static bool IsUndeadFaction(string faction) => faction == RestlessFaction;

    /// <summary>A monster race can raid this colony: on in the server config and not switched off on the Town Hall.</summary>
    public bool RaceOn(Colony colony, string race) => Config.RaidFactions.Contains(race) && !colony.RaidRacesOff.Contains(race);

    List<EntityProperties> RestlessTypes() => new[] { "male", "female" }
        .Select(sex => sapi.World.GetEntityType(new AssetLocation(CreateAColonyModSystem.ModId, "restless-" + sex))).OfType<EntityProperties>().ToList();

    /// <summary>The Restless come in bigger groups than other raiders (config RestlessSizeFactor), still capped.</summary>
    int UndeadSize(int size) => Math.Clamp((int)Math.Round(size * Config.RestlessSizeFactor), 1, Math.Max(1, Config.RaidMaxRaiders));

    /// <summary>Total hours of the next nightfall (RestlessNightHour) after now.</summary>
    double NextNightfallHours()
    {
        var calendar = sapi.World.Calendar;
        double hours = calendar.TotalHours;
        double today = hours - calendar.HourOfDay;
        double at = today + Config.RestlessNightHour;
        return at > hours ? at : at + calendar.HoursPerDay;
    }

    /// <summary>
    /// The undead part of a raid's update: at sunrise the Restless go, and the raid waits for the next night; a
    /// little before nightfall the members are warned they are coming back. True when the raid is handled for now.
    /// </summary>
    bool UpdateUndeadRaid(Colony colony, ActiveRaid raid)
    {
        if (!IsUndeadFaction(raid.Faction)) return false;
        double hours = sapi.World.Calendar.TotalHours;
        if (raid.Resting)
        {
            if (!raid.WarnedAgain && hours >= raid.AttackAtHours - Config.RaidWarningHours)
            {
                raid.WarnedAgain = true;
                TellMembers(colony, "createacolony:raid-again-" + raid.Faction, "createacolony:side-" + raid.Side, raid.Size);
            }
            if (hours < raid.AttackAtHours) return true;
            raid.Resting = false; // nightfall: UpdateRaid spawns them again
            return false;
        }
        if (!raid.Attacking || IsNightHour(sapi.World.Calendar.HourOfDay)) return false;

        // Sunrise: the ones still standing walk off, to come back tonight. They don't count as escaped.
        int left = 0;
        foreach (long id in raid.Raiders.ToList())
        {
            if (sapi.World.GetEntityById(id) is not { Alive: true } e) continue;
            left++;
            e.Die(EnumDespawnReason.Removed);
        }
        raid.Raiders.Clear();
        if (left == 0)
        {
            EndRaid(colony, raid);
            return true;
        }
        raid.Size = left;
        raid.Attacking = false;
        raid.Resting = true;
        raid.WarnedAgain = false;
        raid.AttackAtHours = NextNightfallHours();
        TellMembers(colony, "createacolony:raid-sunrise-" + raid.Faction, left);
        LogEvent(colony, "event-raid-sunrise-" + raid.Faction, left.ToString());
        return true;
    }

    /// <summary>Dresses a new one of the Restless: a rotting look (its skin parts), torn country clothes, its health and name.</summary>
    void EquipRestless(EntityRestless zombie)
    {
        var rand = sapi.World.Rand;
        if (zombie.GetBehavior<EntityBehaviorHealth>() is { } health)
        {
            health.BaseMaxHealth = Config.RestlessHealth;
            health.Health = Config.RestlessHealth;
        }
        if (zombie.Code.Path.EndsWith("female") && zombie.GetBehavior<EntityBehaviorExtraSkinnable>() is { } skin)
        {
            foreach (var part in new[] { "beard", "mustache" })
            {
                if (skin.AvailableSkinPartsByCode.TryGetValue(part, out var available) && available.VariantsByCode.ContainsKey("none")) skin.selectSkinPart(part, "none", false, false);
            }
            zombie.WatchedAttributes.MarkPathDirty("skinConfig");
        }
        if (zombie.GetBehavior<EntityBehaviorSeraphInventory>() is { Inventory: { } gear } dress)
        {
            var clothes = OutlawClothes();
            foreach (var category in new[] { "upperbody", "lowerbody", "foot" })
            {
                if (rand.NextDouble() < 0.3) continue; // the dead lose their things
                var fits = clothes.Where(c => ClothesSlotOf(c) is var slot && slot >= 0 && slot < gear.Count
                    && string.Equals(c.ItemAttributes?["clothescategory"].AsString(), category, StringComparison.OrdinalIgnoreCase)).ToList();
                if (fits.Count == 0) continue;
                var pick = fits[rand.Next(fits.Count)].Clone();
                // Torn and rotten: worn right down, if the clothes wear at all.
                if (pick.Collectible.GetMaxDurability(pick) > 1) pick.Collectible.SetDurability(pick, Math.Max(1, pick.Collectible.GetMaxDurability(pick) / 10));
                gear[ClothesSlotOf(pick)].Itemstack = pick;
            }
            dress.storeInv();
        }
        zombie.GetBehavior<EntityBehaviorNameTag>()?.SetName(Lang.Get("createacolony:restless-name"));
    }

    /// <summary>A bite from one of the Restless: a settler may fall ill, a player gets hungry.</summary>
    public void RestlessBite(Entity attacker, Entity target)
    {
        var rand = sapi.World.Rand;
        if (target is EntitySettler settler && FindCitizen(settler.ColonyId, settler.CitizenId) is { } citizen && GetById(settler.ColonyId) is { } colony)
        {
            if (rand.NextDouble() >= Config.RestlessSickChance || IsSick(citizen)) return;
            citizen.SickUntilTotalDays = sapi.World.Calendar.TotalDays + Config.RestlessSickDays;
            if (HasMood(citizen)) AddThought(citizen, "sick");
            LogEvent(colony, "event-sick", citizen.Name);
            MarkDirty(colony);
        }
        else if (target is EntityPlayer && Config.RestlessPlayerHunger > 0)
        {
            target.GetBehavior<EntityBehaviorHunger>()?.ConsumeSaturation(Config.RestlessPlayerHunger);
        }
    }

    /// <summary>Ill from a bite: rests at home until it wears off or the Healer treats it.</summary>
    public bool IsSick(CitizenRecord c)
    {
        if (c.SickUntilTotalDays <= 0) return false;
        if (c.SickUntilTotalDays > sapi.World.Calendar.TotalDays) return true;
        c.SickUntilTotalDays = 0;
        return false;
    }

    /// <summary>One of the Restless falls: now and then some rot, rarely a rusty gear it carried in life.</summary>
    void DropRestlessLoot(Vec3d at)
    {
        var rand = sapi.World.Rand;
        if (rand.NextDouble() < 0.5 && sapi.World.GetItem(new AssetLocation("game:rot")) is { } rot) sapi.World.SpawnItemEntity(new ItemStack(rot, rand.Next(1, 3)), at);
        if (rand.NextDouble() < 0.08 && sapi.World.GetItem(new AssetLocation(ColonyMoney.GearCode)) is { } gear) sapi.World.SpawnItemEntity(new ItemStack(gear), at);
    }
}
