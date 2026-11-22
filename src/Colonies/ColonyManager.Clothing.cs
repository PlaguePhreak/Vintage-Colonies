using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// What settlers wear (Aaron, TRADERS_DRAFT.md). Clothes wear down as settlers go about their day, as a
/// player's do. A settler in rags, or cold (the air plus the warmth of its clothes is below what the world
/// lets a body bear), is a little unhappy; one wearing jewellery is happier. When idle, a settler in rags or
/// cold changes into better clothes from the warehouse and leaves the old ones there, and takes a piece of
/// jewellery to wear if it has none. The Tailor mends worn clothes, and leather and linen armor, with what the
/// game mends them with; never metal armor. The Jeweller makes jewellery on its Crafting recipes tab.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The Tailor's mending, as a row of its Priorities tab.</summary>
    public const string MendKey = "mend";
    /// <summary>How often an idle settler looks in the warehouse for something better to wear.</summary>
    const long DressCheckMs = 60_000;
    /// <summary>How long whether a settler is cold is remembered, so the windows do not look up the weather each time.</summary>
    const long ColdCheckMs = 20_000;

    readonly Dictionary<int, long> nextDressCheck = new();
    readonly Dictionary<int, (long Until, bool Cold)> coldCache = new();

    /// <summary>True for a necklace, bracelet, brooch, ring and the like (config JewelleryWords and JewelleryDomains).</summary>
    public bool IsJewellery(ItemStack stack)
    {
        var code = stack.Collectible.Code;
        if (code == null) return false;
        if (Config.JewelleryDomains.Contains(code.Domain)) return true;
        if (!code.Path.StartsWith("clothes-")) return false;
        foreach (var part in code.Path.Split('-').Skip(1))
        {
            foreach (var word in Config.JewelleryWords)
            {
                // golden-necklace, large-silver-bracelets, hornedbracelet, nazar1
                if (part == word || part == word + "s" || word.Length >= 5 && part.EndsWith(word)
                    || part.StartsWith(word) && part.Length > word.Length && part[word.Length..].All(char.IsDigit)) return true;
            }
        }
        return false;
    }

    /// <summary>True for armor of leather or linen, which the Tailor may mend (gambesons, jerkins, leather armor).</summary>
    public bool IsClothArmor(ItemStack stack) => IsArmor(stack) && ArmorMaterial(stack) != null;

    /// <summary>The key of config ArmorMendWith the armor's code ends with (armor-body-sewn-linen is linen), or null.</summary>
    string? ArmorMaterial(ItemStack stack)
    {
        string path = stack.Collectible.Code?.Path ?? "";
        return Config.ArmorMendWith.Keys.FirstOrDefault(m => path.EndsWith("-" + m));
    }

    static IWearable? WearableOf(ItemStack stack) => stack.Collectible.GetCollectibleInterface<IWearable>();

    /// <summary>A piece of clothing's condition, 0 (ruined) to 1 (new).</summary>
    public static float Condition(ItemStack stack) => stack.Attributes.GetFloat("condition", 1);

    /// <summary>How warm a piece keeps its wearer now; worn clothes keep less warm, as for players.</summary>
    static float WarmthOf(ItemStack stack)
    {
        if (WearableOf(stack) is not { } wearable) return 0;
        try
        {
            return wearable.GetWarmth(new DummySlot(stack));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>True for clothes that wear down: anything with warmth or a condition, but not jewellery or armor.</summary>
    bool WearsDown(ItemStack stack) => WearableOf(stack) != null && !IsJewellery(stack) && !IsArmor(stack);

    public bool IsRagged(ItemStack stack) => WearsDown(stack) && Condition(stack) < Config.ClothesRaggedBelow;

    /// <summary>
    /// Part of the mood tick: worn clothes lose condition for the hours that passed. The game wears a player's
    /// clothes only while they move; settlers are on their feet most of the day, so theirs wear all the time.
    /// </summary>
    void WearClothes(Colony colony, double hours)
    {
        float loss = (float)(Math.Max(0, Config.ClothesWearPerDay) * hours / 24.0);
        if (loss <= 0) return;
        foreach (var c in colony.Citizens)
        {
            if (!c.Alive || c.Visitor || c.WaitingForRoom || c.Clothes.Count == 0) continue;
            var clothes = WornClothes(c);
            bool changed = false;
            for (int i = 0; i < clothes.Length; i++)
            {
                if (clothes[i] is not { } piece || !WearsDown(piece)) continue;
                var slot = new DummySlot(piece);
                WearableOf(piece)!.ChangeCondition(slot, -loss);
                clothes[i] = slot.Itemstack;
                changed = true;
            }
            if (changed) SaveClothes(colony, c, clothes);
        }
    }

    /// <summary>How many pieces of jewellery the settler wears.</summary>
    public int JewelleryWorn(CitizenRecord c) => WornClothes(c).Count(s => s != null && IsJewellery(s));

    public bool InRags(CitizenRecord c) => WornClothes(c).Any(s => s != null && IsRagged(s));

    /// <summary>
    /// True when the settler is cold: the air where it stands plus the warmth of what it wears is below the
    /// world's body temperature limit, as for a player. A settler that is not loaded is never cold.
    /// </summary>
    public bool IsCold(CitizenRecord c)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (coldCache.TryGetValue(c.CitizenId, out var known) && known.Until > now) return known.Cold;
        bool cold = false;
        if (sapi.World.GetEntityById(c.EntityId) is Entity { Alive: true } settler
            && sapi.World.BlockAccessor.GetClimateAt(settler.Pos.AsBlockPos, EnumGetClimateMode.NowValues) is { } climate)
        {
            float warmth = WornClothes(c).Sum(s => s == null ? 0 : WarmthOf(s));
            // Indoors, out of the wind (an enclosed room), is a little warmer.
            if (sapi.ModLoader.GetModSystem<RoomRegistry>()?.GetRoomForPosition(settler.Pos.AsBlockPos) is { ExitCount: 0 }) warmth += Config.RoomWarmth;
            cold = climate.Temperature + warmth < BodyTemperatureResistance();
        }
        coldCache[c.CitizenId] = (now + ColdCheckMs, cold);
        return cold;
    }

    float BodyTemperatureResistance() => sapi.World.Config.GetString("bodyTemperatureResistance", "0").ToFloat(0);

    /// <summary>The standing thoughts about clothes, for ThoughtsOf: in rags, cold, wearing jewellery.</summary>
    void ClothesThoughts(CitizenRecord c, Action<string, int> standing)
    {
        if (InRags(c)) standing("ragged", Config.RaggedMood);
        if (IsCold(c)) standing("cold", Config.ColdMood);
        int jewellery = Math.Min(JewelleryWorn(c), Math.Max(0, Config.JewelleryMoodMax));
        standing("jewellery", jewellery * Config.JewelleryMood);
    }

    // ---- Changing clothes ------------------------------------------------------------------

    /// <summary>
    /// A warehouse container with something the settler would rather wear: better clothes for ones in rags,
    /// warmer ones when cold, or jewellery when it wears less than it would like. Null when there is nothing,
    /// or it looked a short while ago.
    /// </summary>
    public BlockEntityContainer? ClothesChest(Colony colony, CitizenRecord c)
    {
        if (!Config.SettlersChangeClothes && !Config.SettlersWearJewellery) return null;
        long now = sapi.World.ElapsedMilliseconds;
        if (nextDressCheck.TryGetValue(c.CitizenId, out long next) && next > now) return null;
        nextDressCheck[c.CitizenId] = now + DressCheckMs;

        var worn = WornClothes(c);
        bool cold = Config.SettlersChangeClothes && IsCold(c);
        bool rags = Config.SettlersChangeClothes && worn.Any(s => s != null && IsRagged(s));
        bool wantsJewellery = Config.SettlersWearJewellery && JewelleryWorn(c) < Config.JewelleryMoodMax;
        // Settlers with spare gears buy new clothes at the Market when theirs are worn (ColonyManager.Market.cs).
        bool wantsNew = Config.SettlersChangeClothes && WantsNewClothes(worn);
        if ((cold || rags || wantsNew) && MarketClothesChest(colony, c, worn, cold) is { } market) return market;
        if (wantsNew && MarketOpen(colony) && MarketSells(colony, "clothing") && !CanBuy(colony, c, "clothing")) CantAfford(c);
        if (!cold && !rags && !wantsJewellery) return null;
        return WarehouseContainers(colony).FirstOrDefault(chest => chest.Inventory.Any(s => !s.Empty && BetterPiece(worn, s.Itemstack, cold, wantsJewellery) >= 0));
    }

    /// <summary>The clothes slot this piece would go in because it beats what is worn there, or -1.</summary>
    int BetterPiece(ItemStack?[] worn, ItemStack piece, bool cold, bool wantsJewellery)
    {
        int slot = ClothesSlotOf(piece);
        if (slot < 0 || slot >= worn.Length) return -1;
        var have = worn[slot];
        if (IsJewellery(piece)) return wantsJewellery && have == null ? slot : -1;
        if (!WearsDown(piece) || IsRagged(piece) || Condition(piece) < Config.ClothesRaggedBelow * 2) return -1;
        if (have != null && IsJewellery(have)) return -1;
        if (have != null && IsRagged(have) && Condition(piece) > Condition(have)) return slot;
        if (cold && WarmthOf(piece) > (have == null ? 0 : WarmthOf(have)) + 0.5f) return slot;
        return -1;
    }

    /// <summary>
    /// At the chest: puts on the best of what it holds, one piece per slot, and leaves what it wore there in
    /// its place (for the Tailor to mend). True when it changed anything.
    /// </summary>
    public bool ChangeClothesAt(Colony colony, CitizenRecord c, BlockEntityContainer chest)
    {
        if (IsMarketStore(colony, chest)) return BuyClothesAt(colony, c, chest);
        var worn = WornClothes(c);
        bool cold = Config.SettlersChangeClothes && IsCold(c);
        bool changed = false;
        foreach (var slot in chest.Inventory)
        {
            if (slot.Empty) continue;
            bool wantsJewellery = Config.SettlersWearJewellery && worn.Count(s => s != null && IsJewellery(s)) < Config.JewelleryMoodMax;
            int to = BetterPiece(worn, slot.Itemstack, cold, wantsJewellery);
            if (to < 0) continue;
            var piece = slot.TakeOut(1);
            slot.MarkDirty();
            var old = worn[to];
            worn[to] = piece;
            changed = true;
            if (old == null) continue;
            PutItems(chest, old);
            if (old.StackSize > 0) AddToPack(colony, c, old, false);
            if (old.StackSize > 0) sapi.World.SpawnItemEntity(old, chest.Pos.ToVec3d().Add(0.5, 1, 0.5));
        }
        if (!changed) return false;
        chest.MarkDirty(true);
        SaveClothes(colony, c, worn);
        coldCache.Remove(c.CitizenId);
        return true;
    }

    // ---- Mending -----------------------------------------------------------------------------

    /// <summary>True for clothes or leather and linen armor the Tailor would mend: worn below config TailorMendBelow.</summary>
    public bool NeedsMending(ItemStack stack) => MendLeft(stack) > 1 - Config.TailorMendBelow;

    /// <summary>How much mending a piece could still take, 0 to 1; 0 for things the Tailor does not mend (jewellery, metal armor).</summary>
    public float MendLeft(ItemStack stack)
    {
        if (IsClothArmor(stack))
        {
            int max = stack.Collectible.GetMaxDurability(stack);
            return max <= 0 ? 0 : 1 - (float)stack.Collectible.GetRemainingDurability(stack) / max;
        }
        return WearsDown(stack) ? 1 - Condition(stack) : 0;
    }

    /// <summary>True when this mends that piece: twine, cloth or a sewing kit for clothes; linen or leather for armor of it.</summary>
    public bool Mends(ItemStack material, ItemStack piece)
    {
        if (IsClothArmor(piece))
        {
            string? kind = ArmorMaterial(piece);
            return kind != null && Config.ArmorMendWith.TryGetValue(kind, out var with)
                && WildcardUtil.Match(new AssetLocation(with), material.Collectible.Code);
        }
        return RepairStrength(material) > 0;
    }

    static float RepairStrength(ItemStack material) => material.ItemAttributes?["clothingRepairStrength"].AsFloat(0) ?? 0;

    /// <summary>
    /// One stitch: mends the most worn piece the Tailor carries with one of what mends it, from its inventory.
    /// Clothes get back what the game gives for it (twine a little, a sewing kit a lot); armor ArmorMendShare
    /// of its durability. False when there was nothing to mend or nothing to mend it with.
    /// </summary>
    public bool MendOnce(Colony colony, Building hut, CitizenRecord tailor)
    {
        var piece = PackItems(tailor).Where(s => MendLeft(s) > 0.01f).OrderByDescending(MendLeft).FirstOrDefault();
        if (piece == null) return false;
        // The least that does the job, so sewing kits are not spent on a loose thread.
        var materials = PackItems(tailor).Where(s => Mends(s, piece)).ToList();
        if (materials.Count == 0) return false;
        float left = MendLeft(piece);
        var pick = materials.OrderBy(s => RepairStrength(s) >= left ? 0 : 1).ThenBy(s => Math.Abs(RepairStrength(s) - left)).First();
        if (TakeFromPack(colony, tailor, s => s.Equals(sapi.World, pick, GlobalConstants.IgnoredStackAttributes), 1) == null) return false;

        var items = PackItems(tailor);
        var mended = items.FirstOrDefault(s => s.Collectible.Code.Equals(piece.Collectible.Code) && Math.Abs(MendLeft(s) - left) < 0.0001f);
        if (mended == null) return false;
        if (IsClothArmor(mended))
        {
            int max = mended.Collectible.GetMaxDurability(mended);
            int now = mended.Collectible.GetRemainingDurability(mended);
            mended.Attributes.SetInt("durability", Math.Min(max, now + (int)Math.Ceiling(max * Config.ArmorMendShare)));
        }
        else
        {
            var slot = new DummySlot(mended);
            WearableOf(mended)!.ChangeCondition(slot, RepairStrength(pick));
        }
        SavePack(colony, tailor, items);
        AddStat(hut, "mended");
        return true;
    }

    /// <summary>How many worn pieces wait for the Tailor: in the warehouse, at the hut and with the Tailor.</summary>
    public int MendingCount(Colony colony, Building hut, CitizenRecord? tailor)
    {
        ItemMatch worn = NeedsMending;
        int count = CountItems(WarehouseContainers(colony), worn) + CountItems(HutStorage(colony, hut), worn);
        if (tailor != null) count += PackItems(tailor).Count(s => MendLeft(s) > 0.01f);
        return count;
    }

    /// <summary>
    /// At a Tailor's Hut, worn pieces, what mends them, its tools and what its recipes take stay; mended and
    /// new clothes go to the warehouse, where settlers find them.
    /// </summary>
    ItemMatch TailorHutJunk(Colony colony, Building hut)
    {
        var recipeInputs = HutRecipeInputs(colony, hut);
        var tools = Config.HutRecipeTools.GetValueOrDefault(BuildingTypes.Tailor) ?? Array.Empty<string>();
        return s => !NeedsMending(s) && RepairStrength(s) <= 0 && !Config.ArmorMendWith.Values.Any(w => WildcardUtil.Match(new AssetLocation(w), s.Collectible.Code))
            && !tools.Contains(FirstPart(s.Collectible.Code)) && !recipeInputs(s);
    }

    /// <summary>At a Jeweller's Workshop, its tools and the metal and gems for what it keeps stay; the jewellery goes to the warehouse.</summary>
    ItemMatch JewellerHutJunk(Colony colony, Building hut)
    {
        var recipeInputs = HutRecipeInputs(colony, hut);
        var tools = Config.HutRecipeTools.GetValueOrDefault(BuildingTypes.Jeweller) ?? Array.Empty<string>();
        return s => !tools.Contains(FirstPart(s.Collectible.Code)) && !recipeInputs(s);
    }

    /// <summary>A line about the Tailor's mending, for the hut window.</summary>
    public string TailorInfo(Colony colony, Building hut) =>
        Lang.Get("createacolony:tailor-mending-count", MendingCount(colony, hut, WorkerAt(colony, hut, Jobs.Tailor)));
}
