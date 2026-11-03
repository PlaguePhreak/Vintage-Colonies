using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Healing (Aaron, 2026-10-24). A badly hurt settler goes home to bed to recover (AiTaskSettlerRecover), heals
/// faster while resting, and uses the bandage or poultice it carries. Settlers keep one of these as first aid:
/// they take one from the warehouse, a Healer's Hut or a chest at home, and with none about they make the
/// weaker horsetail poultice themselves from the game's recipe. The Healer (AiTaskSettlerHealer) makes the
/// game's bandages on its Crafting recipes tab and treats settlers recovering in bed. The Gatherer picks the
/// herbs these recipes take (horsetail), so they show on its Allowed to collect tab.
/// </summary>
public partial class ColonyManager
{
    /// <summary>When each settler last looked for first aid, so it does not look all the time. Not saved.</summary>
    readonly Dictionary<int, long> nextFirstAidCheck = new();
    const long FirstAidCheckMs = 60_000;

    /// <summary>Settlers resting in bed to recover, by citizen id: the bed, and whether the Healer has treated them yet. Not saved.</summary>
    public readonly Dictionary<int, (BlockPos Bed, bool Treated)> Recovering = new();

    List<SawKind>? remedyKinds;
    List<CraftingRecipeIngredient>? healingInputs;
    readonly Dictionary<int, bool> herbBlocks = new();
    readonly Dictionary<(int Hut, string Key), (long Ms, bool Stocked)> herbStock = new();

    // ---- What heals --------------------------------------------------------------------------

    /// <summary>True for a bandage, poultice or anything else the game lets a player heal with.</summary>
    public bool IsHealingItem(ItemStack? stack) => stack != null && HealPower(stack) > 0;

    /// <summary>How much health it gives back, as the game says: the healing behavior's Health, or the old "health" attribute.</summary>
    public static float HealPower(ItemStack stack)
    {
        var collectible = stack.Collectible;
        if (collectible == null || collectible.GetCollectibleInterface<ICanHealCreature>() == null) return 0;
        if (collectible.GetBehavior<CollectibleBehaviorHealingItem>() is { } healing) return healing.Health;
        return collectible.Attributes?["health"].AsFloat(0) ?? 0;
    }

    /// <summary>Gives the health back to the settler as the game does for a player: over a few seconds for bandages, at once for the old poultices.</summary>
    public void ApplyHealing(Entity target, ItemStack item, float bonus = 1f)
    {
        float health = HealPower(item) * bonus;
        if (health <= 0) return;
        var source = new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Heal };
        if (item.Collectible.GetBehavior<CollectibleBehaviorHealingItem>() is { } healing)
        {
            source.Duration = TimeSpan.FromSeconds(healing.EffectDurationSec);
            source.TicksPerDuration = healing.Ticks;
            if (healing.AppliedSound != null) sapi.World.PlaySoundAt(healing.AppliedSound, target, null, true, healing.SoundRange);
        }
        target.ReceiveDamage(source, health);
    }

    /// <summary>The settler's share of its health, 0 to 1; 1 when it has no health to speak of.</summary>
    public static float HealthShare(Entity entity) =>
        entity.GetBehavior<EntityBehaviorHealth>() is { MaxHealth: > 0 } hp ? hp.Health / hp.MaxHealth : 1f;

    // ---- First aid -----------------------------------------------------------------------------

    public ItemStack? FirstAidOf(CitizenRecord c)
    {
        if (c.FirstAid == null) return null;
        var stack = new ItemStack(c.FirstAid);
        return stack.ResolveBlockOrItem(sapi.World) && stack.StackSize > 0 ? stack : null;
    }

    void SetFirstAid(Colony colony, CitizenRecord c, ItemStack? stack)
    {
        c.FirstAid = stack?.ToBytes();
        MarkDirty(colony);
    }

    /// <summary>Where a settler could take first aid from: a chest at home, a Healer's Hut, the warehouse.</summary>
    IEnumerable<BlockEntityContainer> FirstAidPlaces(Colony colony, CitizenRecord c)
    {
        var home = HomeOf(colony, c);
        var places = home == null ? Enumerable.Empty<BlockEntityContainer>() : HomeChests(home);
        foreach (var hut in colony.Buildings.Where(b => b.Type == BuildingTypes.Healer)) places = places.Concat(HutStorage(colony, hut));
        return places.Concat(WarehouseContainers(colony));
    }

    /// <summary>
    /// True when the settler should go and get first aid: it carries none, and it has not looked lately.
    /// Children, visitors and settlers waiting for room do not.
    /// </summary>
    bool WantsFirstAid(CitizenRecord c)
    {
        if (!Config.SettlersCarryFirstAid || c.IsChild || c.Visitor || c.WaitingForRoom || FirstAidOf(c) != null) return false;
        long now = sapi.World.ElapsedMilliseconds;
        if (nextFirstAidCheck.TryGetValue(c.CitizenId, out long next) && next > now) return false;
        nextFirstAidCheck[c.CitizenId] = now + FirstAidCheckMs;
        return true;
    }

    /// <summary>
    /// For an idle settler with no first aid: the chest to get a bandage or poultice from, or else (Make true)
    /// a chest with what a poultice takes, to make one there. Null when there is neither, or it looked lately.
    /// </summary>
    public (BlockEntityContainer Chest, bool Make)? FirstAidErrand(Colony colony, CitizenRecord c)
    {
        if (!WantsFirstAid(c)) return null;
        if (FirstAidPlaces(colony, c).FirstOrDefault(chest => chest.Inventory.Any(s => IsHealingItem(s.Itemstack))) is { } found) return (found, false);
        if (RemedyFrom(colony, c) is { } make) return (make.Chests[0], true);
        return null;
    }

    /// <summary>Takes the best bandage or poultice in the chest as the settler's first aid. False when there was none.</summary>
    public bool TakeFirstAid(Colony colony, CitizenRecord c, BlockEntityContainer chest)
    {
        if (FirstAidOf(c) != null) return true;
        var slot = chest.Inventory.Where(s => IsHealingItem(s.Itemstack)).OrderByDescending(s => HealPower(s.Itemstack!)).FirstOrDefault();
        if (slot == null) return false;
        var aid = slot.TakeOut(1);
        slot.MarkDirty();
        chest.MarkDirty(true);
        SetFirstAid(colony, c, aid);
        return true;
    }

    /// <summary>The settler uses its first aid on itself. True when it had some.</summary>
    public bool UseFirstAid(Colony colony, CitizenRecord c, Entity settler)
    {
        if (FirstAidOf(c) is not { } aid) return false;
        SetFirstAid(colony, c, null);
        ApplyHealing(settler, aid);
        LogEvent(colony, "event-first-aid", c.Name, aid.GetName());
        return true;
    }

    /// <summary>When each settler may next use a healing item it carries, so one bandage finishes before the next. Not saved.</summary>
    readonly Dictionary<long, long> nextSelfHeal = new();

    /// <summary>
    /// A hurt settler (below config UseHealingBelow) uses a bandage or poultice it carries, wherever it is (Aaron,
    /// 2026-10-25): its first aid first, then any in its inventory. It picks the smallest one that covers what it
    /// is missing, else the strongest, and waits for one to finish healing before it uses the next.
    /// </summary>
    public void HealSelfIfHurt(Colony colony, CitizenRecord c, EntityAgent settler)
    {
        if (!Config.UseHealingWhenHurt || c.IsDowned || settler.GetBehavior<EntityBehaviorHealth>() is not { MaxHealth: > 0 } hp) return;
        if (hp.Health >= hp.MaxHealth * Config.UseHealingBelow) return;
        long now = sapi.World.ElapsedMilliseconds;
        if (nextSelfHeal.TryGetValue(settler.EntityId, out long next) && next > now) return;

        float missing = hp.MaxHealth - hp.Health;
        var aid = FirstAidOf(c);
        var carried = CarriedItems(c).Where(IsHealingItem).ToList();
        var all = (aid == null ? carried : carried.Prepend(aid)).ToList();
        if (all.Count == 0) return;
        var pick = all.Where(s => HealPower(s) >= missing).OrderBy(HealPower).FirstOrDefault() ?? all.OrderByDescending(HealPower).First();

        ItemStack? used;
        if (aid != null && ReferenceEquals(pick, aid))
        {
            used = aid;
            SetFirstAid(colony, c, null);
        }
        else used = TakeFromPack(colony, c, s => s.Collectible.Code.Equals(pick.Collectible.Code), 1);
        if (used == null) return;

        ApplyHealing(settler, used);
        float seconds = used.Collectible.GetBehavior<CollectibleBehaviorHealingItem>()?.EffectDurationSec ?? 0;
        nextSelfHeal[settler.EntityId] = now + (long)(Math.Max(5f, seconds + 2f) * 1000);
        LogEvent(colony, "event-first-aid", c.Name, used.GetName());
    }

    // ---- Making a poultice ---------------------------------------------------------------------

    /// <summary>The game's recipes any settler may make first aid with (config HomemadeRemedies): the horsetail poultice.</summary>
    public List<SawKind> RemedyKinds() => remedyKinds ??= ScanGridRecipes(_ => true,
        tool => Config.HomemadeRemedyTools.Contains(FirstPart(tool.Code)),
        output => IsHealingItem(output) && Config.HomemadeRemedies.Contains(FirstPart(output.Collectible.Code)),
        Config.HutRecipeMaxInputs);

    /// <summary>
    /// A poultice recipe the settler can make from one place (its home chests, or the warehouse), and those
    /// chests, nearest first. It must carry the recipe's tool. Null when there is none.
    /// </summary>
    (SawRecipe Recipe, List<BlockEntityContainer> Chests)? RemedyFrom(Colony colony, CitizenRecord c)
    {
        if (!Config.SettlersMakeRemedies) return null;
        var recipes = RemedyKinds().SelectMany(k => k.Recipes).Where(r => CarriesTool(c, r.Tool, null) && CarriesTool(c, r.Tool2, null)).ToList();
        if (recipes.Count == 0) return null;
        var home = HomeOf(colony, c);
        var groups = new List<List<BlockEntityContainer>>();
        if (home != null) groups.Add(HomeChests(home));
        groups.Add(WarehouseContainers(colony).ToList());
        foreach (var chests in groups)
        {
            if (chests.Count == 0) continue;
            foreach (var recipe in recipes)
            {
                if (!recipe.Inputs.All(i => CountItems(chests, s => i.Part.SatisfiesAsIngredient(s, false)) >= i.Quantity)) continue;
                // Start with the chest that holds the first thing it takes, so the settler walks there.
                var first = chests.First(ch => ch.Inventory.Any(s => !s.Empty && recipe.Inputs[0].Part.SatisfiesAsIngredient(s.Itemstack, false)));
                return (recipe, chests.OrderBy(ch => ch == first ? 0 : 1).ToList());
            }
        }
        return null;
    }

    /// <summary>
    /// At the chest: makes a poultice from what is there, keeps one as first aid and leaves the rest in the
    /// chest. False when it could not (someone took the herbs meanwhile).
    /// </summary>
    public bool MakeRemedy(Colony colony, CitizenRecord c)
    {
        if (RemedyFrom(colony, c) is not { } plan) return false;
        foreach (var (part, quantity) in plan.Recipe.Inputs)
        {
            int left = quantity;
            while (left > 0 && TakeItems(plan.Chests, s => part.SatisfiesAsIngredient(s, false), left) is { } taken) left -= taken.StackSize;
        }
        var made = plan.Recipe.Output.Clone();
        var one = made.Clone();
        one.StackSize = 1;
        made.StackSize -= 1;
        if (FirstAidOf(c) == null) SetFirstAid(colony, c, one);
        else made.StackSize += 1;
        foreach (var chest in plan.Chests)
        {
            if (made.StackSize <= 0) break;
            PutItems(chest, made);
        }
        if (made.StackSize > 0) sapi.World.SpawnItemEntity(made, plan.Chests[0].Pos.ToVec3d().Add(0.5, 1, 0.5));
        LogEvent(colony, "event-made-remedy", c.Name, one.GetName());
        return true;
    }

    // ---- Recovering ----------------------------------------------------------------------------

    /// <summary>True when a settler should go home to recover: badly hurt, not a guard, not knocked out, not running or fighting.</summary>
    public bool NeedsRest(CitizenRecord c, EntityAgent settler)
    {
        if (!Config.RecoverAtHome || c.IsDowned || c.Visitor || c.JobCode == Jobs.Guard) return false;
        return HealthShare(settler) < Config.RecoverBelowHealth;
    }

    /// <summary>True once a resting settler is well enough to get up.</summary>
    public bool RestedEnough(EntityAgent settler) => HealthShare(settler) >= Math.Max(Config.RecoverBelowHealth, Config.RecoverUntilHealth);

    /// <summary>
    /// The settler lies down to rest: it uses its first aid, else takes one from a chest at home and uses it,
    /// else makes a poultice there if it can. Then it waits for the Healer.
    /// </summary>
    public void StartRest(Colony colony, CitizenRecord c, EntityAgent settler, BlockPos bed)
    {
        Recovering[c.CitizenId] = (bed.Copy(), false);
        if (FirstAidOf(c) == null && HomeOf(colony, c) is { } home)
        {
            var chest = HomeChests(home).FirstOrDefault(ch => ch.Inventory.Any(s => IsHealingItem(s.Itemstack)));
            if (chest != null) TakeFirstAid(colony, c, chest);
            else if (RemedyFrom(colony, c) is { } plan && plan.Chests.All(ch => HomeChests(home).Contains(ch))) MakeRemedy(colony, c);
        }
        UseFirstAid(colony, c, settler);
    }

    /// <summary>Health back for this much rest in bed (config RestHealPerHour).</summary>
    public void Rest(Colony colony, EntityAgent settler, double hours)
    {
        if (hours <= 0 || settler.GetBehavior<EntityBehaviorHealth>() is not { } hp) return;
        // Herbal Remedies heals faster (ColonyManager.Research.cs).
        hp.Health = Math.Min(hp.MaxHealth, hp.Health + (float)(Config.RestHealPerHour * hours * ResearchMult(colony, "restHeal")));
    }

    public void EndRest(CitizenRecord c) => Recovering.Remove(c.CitizenId);

    // ---- The Healer ----------------------------------------------------------------------------

    /// <summary>A new Healer's Hut keeps config HealerStartKeep of each bandage, so the Healer starts making them at once.</summary>
    void KeepBandagesAtStart(Building hut)
    {
        if (Config.HealerStartKeep <= 0) return;
        foreach (var kind in HutRecipeKinds(BuildingTypes.Healer).Where(k => FirstPart(k.Sample.Collectible.Code) == "bandage"))
        {
            hut.RecipeKeep[kind.Key] = Config.HealerStartKeep;
        }
    }

    /// <summary>A new Gatherer's Hut keeps config GathererStartPoultices of each poultice, made from the horsetail and reed tops it picks.</summary>
    void KeepPoulticesAtStart(Building hut)
    {
        if (Config.GathererStartPoultices <= 0) return;
        foreach (var kind in HutRecipeKinds(BuildingTypes.Gatherer).Where(k => FirstPart(k.Sample.Collectible.Code) == "poultice"))
        {
            hut.RecipeKeep[kind.Key] = Config.GathererStartPoultices;
        }
    }

    /// <summary>The next settler in bed for the Healer to treat: not treated yet, nearest the hut first. Null for none.</summary>
    public CitizenRecord? NextPatient(Colony colony, Building hut, CitizenRecord healer)
    {
        if (!Config.HealerTreatsPatients) return null;
        return colony.Citizens
            .Where(c => c.Alive && c != healer && Recovering.TryGetValue(c.CitizenId, out var r) && !r.Treated && CanWorkAt(colony, r.Bed))
            .OrderBy(c => Recovering[c.CitizenId].Bed.DistanceSqTo(hut.HutPos.X, hut.HutPos.Y, hut.HutPos.Z))
            .FirstOrDefault();
    }

    /// <summary>The Healer treats the patient with the best thing it carries, healing HealerHealBonus times as much. False when it could not.</summary>
    public bool TreatPatient(Colony colony, Building hut, CitizenRecord healer, CitizenRecord patient)
    {
        if (!Recovering.TryGetValue(patient.CitizenId, out var r) || r.Treated) return false;
        if (sapi.World.GetEntityById(patient.EntityId) is not EntityAgent { Alive: true } body) return false;
        var best = PackItems(healer).Where(IsHealingItem).OrderByDescending(HealPower).FirstOrDefault();
        if (best == null || TakeFromPack(colony, healer, s => s.Collectible.Code.Equals(best.Collectible.Code), 1) is not { } item) return false;
        ApplyHealing(body, item, Config.HealerHealBonus);
        Recovering[patient.CitizenId] = (r.Bed, true);
        AddStat(hut, "treated");
        LogEvent(colony, "event-treated", healer.Name, patient.Name, item.GetName());
        return true;
    }

    /// <summary>At a Healer's Hut, its tools, bandages and poultices and what its recipes take stay; the rest goes to the warehouse.</summary>
    ItemMatch HealerHutJunk(Colony colony, Building hut)
    {
        var recipeInputs = HutRecipeInputs(colony, hut);
        var tools = Config.HutRecipeTools.GetValueOrDefault(BuildingTypes.Healer) ?? Array.Empty<string>();
        return s => !IsHealingItem(s) && !tools.Contains(FirstPart(s.Collectible.Code)) && !recipeInputs(s);
    }

    /// <summary>A line about the Healer's patients, for the hut window.</summary>
    public string HealerInfo(Colony colony, Building hut) =>
        Lang.Get("createacolony:healer-patients", colony.Citizens.Count(c => c.Alive && Recovering.ContainsKey(c.CitizenId)),
            CountItems(HutStorage(colony, hut), IsHealingItem));

    // ---- Herbs for the Gatherer --------------------------------------------------------------

    /// <summary>What the game's healing recipes (bandages, poultices) take.</summary>
    List<CraftingRecipeIngredient> HealingInputs()
    {
        if (healingInputs != null) return healingInputs;
        var parts = new List<CraftingRecipeIngredient>();
        foreach (var recipe in sapi.World.GridRecipes)
        {
            var output = recipe?.Output?.ResolvedItemStack;
            if (recipe?.ResolvedIngredients == null || output == null || !IsHealingItem(output)) continue;
            parts.AddRange(recipe.ResolvedIngredients.Where(i => i != null && i.Consume && !i.IsTool)!);
        }
        return healingInputs = parts;
    }

    /// <summary>A plant the Gatherer picks for healing: one a bandage or poultice recipe takes (itself or what it drops), or one in config GathererHerbs.</summary>
    bool IsHerb(Block block) => Config.GathererPicksHerbs && IsHerbPlant(block);

    /// <summary>A plant a bandage or poultice recipe takes (itself or what it drops), or one in config GathererHerbs, whether or not gatherers pick herbs.</summary>
    bool IsHerbPlant(Block block)
    {
        if (block.Code == null) return false;
        if (herbBlocks.TryGetValue(block.Id, out bool known)) return known;
        bool herb = false;
        if (block is not (BlockCrop or BlockReeds) && (block.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves || block is BlockPlant))
        {
            herb = Config.GathererHerbs.Any(h => WildcardUtil.Match(new AssetLocation(h), block.Code));
            if (!herb && sapi.World.GridRecipes.Count > 0)
            {
                // The plant itself, or what it drops when picked, goes into a bandage or poultice.
                var samples = new List<ItemStack> { new ItemStack(block) };
                foreach (var drop in block.Drops ?? Array.Empty<BlockDropItemStack>())
                {
                    if (drop.ResolvedItemstack == null) drop.Resolve(sapi.World, "herb", block.Code);
                    if (drop.ResolvedItemstack != null) samples.Add(drop.ResolvedItemstack);
                }
                herb = samples.Any(sample => HealingInputs().Any(i => i.SatisfiesAsIngredient(sample, false)));
            }
            else if (sapi.World.GridRecipes.Count == 0) return herb; // too early to know; ask again later
        }
        return herbBlocks[block.Id] = herb;
    }

    /// <summary>The key of a herb on the Allowed to collect tab: "herb-horsetail".</summary>
    static string HerbKey(Block block) => "herb-" + (block.FirstCodePart(1) ?? block.FirstCodePart());

    /// <summary>True when the colony has config GathererHerbKeep of this herb already (warehouse and hut), so it is left to grow. Checked every 30 seconds.</summary>
    bool HerbStocked(Colony colony, Building hut, Block block)
    {
        if (Config.GathererHerbKeep <= 0) return false;
        string key = HerbKey(block);
        long now = sapi.World.ElapsedMilliseconds;
        if (herbStock.TryGetValue((hut.BuildingId, key), out var cached) && now - cached.Ms < 30_000) return cached.Stocked;
        string prefix = block.FirstCodePart() + "-" + block.FirstCodePart(1);
        ItemMatch same = s => s.Collectible.Code.Path.StartsWith(prefix);
        bool stocked = CountItems(WarehouseContainers(colony), same) + CountItems(HutStorage(colony, hut), same) >= Config.GathererHerbKeep;
        herbStock[(hut.BuildingId, key)] = (now, stocked);
        return stocked;
    }
}
