using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// What a settler wears and keeps at hand, apart from its inventory:
/// - the tool bar: one weapon, three tools and an offhand item (a shield shows in the left hand);
/// - clothes in the player's twelve clothing slots, starting with the Commoner class clothes a new player gets;
/// - the look from the player character creator (skin, eyes, hair...), kept on the record so a settler
///   who is respawned looks the same.
/// The record is the truth; DressEntity copies clothes, armor and the offhand onto the settler's model.
/// </summary>
public partial class ColonyManager
{
    public const int ToolBarSlots = 5;
    public const int WeaponSlot = 0;
    public const int FirstToolSlot = 1;
    public const int ToolSlots = 3;
    public const int OffhandSlot = 4;
    /// <summary>The player's clothing slots (EnumCharacterDressType 0-11); 12-14 are armor.</summary>
    public const int ClothesSlots = 12;

    public static bool IsShield(ItemStack stack) => stack.ItemAttributes?["shield"].Exists == true;

    /// <summary>What a tool bar slot takes: a weapon, any tool, or anything in the offhand.</summary>
    public static bool FitsToolBar(ItemStack stack, int slot) => slot switch
    {
        WeaponSlot => IsWeapon(stack),
        >= FirstToolSlot and < FirstToolSlot + ToolSlots => stack.Collectible.Tool != null,
        OffhandSlot => true,
        _ => false
    };

    static List<byte[]> ToBytes(IEnumerable<ItemStack?> stacks) =>
        stacks.Select(s => s == null || s.StackSize <= 0 ? Array.Empty<byte>() : s.ToBytes()).ToList();

    ItemStack?[] Slots(List<byte[]> data, int count)
    {
        var slots = new ItemStack?[count];
        for (int i = 0; i < data.Count && i < count; i++)
        {
            if (data[i].Length == 0) continue;
            var stack = new ItemStack(data[i]);
            if (stack.ResolveBlockOrItem(sapi.World) && stack.StackSize > 0) slots[i] = stack;
        }
        return slots;
    }

    /// <summary>
    /// The tool bar by slot. Settlers from before the tool bar carried their tools in the inventory; the
    /// first time it is read those move onto it.
    /// </summary>
    public ItemStack?[] ToolBarItems(CitizenRecord citizen)
    {
        if (citizen.ToolBar.Count == ToolBarSlots) return Slots(citizen.ToolBar, ToolBarSlots);

        var bar = Slots(citizen.ToolBar, ToolBarSlots);
        var pack = Resolve(citizen.Pack);
        foreach (var stack in pack.ToList())
        {
            if (stack.StackSize != 1 || ToolBarSlotFor(bar, stack) is not int slot) continue;
            bar[slot] = stack;
            pack.Remove(stack);
        }
        citizen.ToolBar = ToBytes(bar);
        citizen.Pack = pack.Select(s => s.ToBytes()).ToList();
        return bar;
    }

    /// <summary>The free slot a single item would go to on its own, or null: weapons, then tools, then shields.</summary>
    static int? ToolBarSlotFor(ItemStack?[] bar, ItemStack stack)
    {
        if (IsWeapon(stack) && bar[WeaponSlot] == null) return WeaponSlot;
        if (stack.Collectible.Tool != null)
        {
            for (int i = FirstToolSlot; i < FirstToolSlot + ToolSlots; i++) if (bar[i] == null) return i;
        }
        // A shield takes the offhand from a carried light (ColonyManager.HandLights.cs), which goes in the inventory.
        if (IsShield(stack) && (bar[OffhandSlot] == null || IsHandLight(bar[OffhandSlot]))) return OffhandSlot;
        return null;
    }

    public void SaveToolBar(Colony colony, CitizenRecord citizen, ItemStack?[] bar)
    {
        citizen.ToolBar = ToBytes(bar);
        MarkDirty(colony);
        DressEntity(citizen);
    }

    /// <summary>Puts one item of the stack on its free tool bar slot. False if it has none.</summary>
    bool PutOnToolBar(Colony colony, CitizenRecord citizen, ItemStack stack)
    {
        var bar = ToolBarItems(citizen);
        if (ToolBarSlotFor(bar, stack) is not int slot) return false;
        var one = stack.Clone();
        one.StackSize = 1;
        stack.StackSize--;
        var light = bar[slot];
        bar[slot] = one;
        SaveToolBar(colony, citizen, bar);
        if (light != null) AddToPack(colony, citizen, light, false);
        return true;
    }

    /// <summary>Takes the first matching item off the tool bar, or null.</summary>
    ItemStack? TakeFromToolBar(Colony colony, CitizenRecord citizen, ItemMatch match)
    {
        var bar = ToolBarItems(citizen);
        for (int i = 0; i < bar.Length; i++)
        {
            if (bar[i] is not { } stack || !match(stack)) continue;
            bar[i] = null;
            SaveToolBar(colony, citizen, bar);
            return stack;
        }
        return null;
    }

    /// <summary>0-11 for a piece of clothing, by the game's dress slot; -1 for armor or anything else.</summary>
    public static int ClothesSlotOf(ItemStack stack)
    {
        var dress = EnumCharacterDressType.Unknown;
        try
        {
            if (stack.Collectible.GetCollectibleInterface<IWearableStatsSupplier>() is { } wearable) dress = wearable.GetDressType(new DummySlot(stack));
            else Enum.TryParse(stack.ItemAttributes?["clothescategory"].AsString(), true, out dress);
        }
        catch (Exception)
        {
            return -1;
        }
        int slot = (int)dress;
        return slot >= 0 && slot < ClothesSlots ? slot : -1;
    }

    public ItemStack?[] WornClothes(CitizenRecord citizen) => Slots(citizen.Clothes, ClothesSlots);

    public void SaveClothes(Colony colony, CitizenRecord citizen, ItemStack?[] clothes)
    {
        citizen.Clothes = ToBytes(clothes);
        MarkDirty(colony);
        DressEntity(citizen);
    }

    /// <summary>Takes all clothes off, for dropping where a settler died for good.</summary>
    public List<ItemStack> TakeOffClothes(Colony colony, CitizenRecord citizen)
    {
        var all = WornClothes(citizen).OfType<ItemStack>().ToList();
        citizen.Clothes.Clear();
        MarkDirty(colony);
        DressEntity(citizen);
        return all;
    }

    /// <summary>
    /// Dresses a settler who has not been dressed yet in the starting clothes of the config's character
    /// class (Commoner by default), the clothes a new player starts with.
    /// </summary>
    public void GiveStartingClothes(Colony colony, CitizenRecord citizen)
    {
        if (citizen.Dressed) return;
        citizen.Dressed = true;
        var clothes = WornClothes(citizen);
        var characters = sapi.ModLoader.GetModSystem<CharacterSystem>();
        if (characters != null && characters.characterClassesByCode.TryGetValue(Config.SettlerClothesClass, out var charClass))
        {
            foreach (var jstack in charClass.Gear ?? Array.Empty<JsonItemStack>())
            {
                if (jstack.ResolvedItemstack == null && !jstack.Resolve(sapi.World, "settler clothes", false)) continue;
                var stack = jstack.ResolvedItemstack!.Clone();
                int slot = ClothesSlotOf(stack);
                if (slot >= 0 && clothes[slot] == null) clothes[slot] = stack;
            }
        }
        else
        {
            Mod.Logger.Warning("Character class {0} not found; settlers start without clothes.", Config.SettlerClothesClass);
        }
        SaveClothes(colony, citizen, clothes);
    }

    /// <summary>Copies the record's clothes, armor and offhand item onto the settler's model, if it is loaded.</summary>
    public void DressEntity(CitizenRecord citizen)
    {
        if (sapi.World.GetEntityById(citizen.EntityId) is not EntitySettler settler) return;

        if (settler.GetBehavior<EntityBehaviorSeraphInventory>() is { Inventory: { } gear } gearBehavior)
        {
            var clothes = WornClothes(citizen);
            var armor = WornArmor(citizen);
            bool changed = false;
            for (int i = 0; i < ClothesSlots + ArmorSlots && i < gear.Count; i++)
            {
                var want = i < ClothesSlots ? clothes[i] : armor[i - ClothesSlots];
                changed |= SetIfDifferent(gear[i], want);
            }
            if (changed) gearBehavior.storeInv();
        }

        // The offhand item shows in the left hand, or a torch in the dark; a shield raised in a fight shows even then.
        var offhand = ToolBarItems(citizen)[OffhandSlot];
        var left = offhand != null && IsShield(offhand) && ShieldIsUp(settler) ? offhand
            : (Config.CarryTorches ? TorchToHold(citizen, settler) : null) ?? offhand;
        if (left?.Block is BlockLantern && !left.Attributes.HasAttribute("material")) left = WithLanternParts(left.Clone());
        if (settler.LeftHandItemSlot is { } leftHand && SetIfDifferent(leftHand, left))
        {
            settler.GetBehavior<EntityBehaviorVillagerInv>()?.storeInv();
        }
    }

    bool SetIfDifferent(ItemSlot slot, ItemStack? want)
    {
        var have = slot.Itemstack;
        if (have == null && want == null) return false;
        // Same item counts as unchanged even when worn down a little, so a hit on armor does not rebuild the model.
        if (have != null && want != null && have.StackSize == want.StackSize && have.Collectible.Code.Equals(want.Collectible.Code)) return false;
        slot.Itemstack = want?.Clone();
        slot.MarkDirty();
        return true;
    }

    /// <summary>
    /// Gives the settler its look from the player character creator. A new settler keeps the look the game
    /// rolled for it (women without a beard), which is stored on the record; a respawned one gets its
    /// stored look back.
    /// </summary>
    public void ApplySkin(Colony colony, CitizenRecord citizen, EntitySettler settler)
    {
        if (settler.GetBehavior<EntityBehaviorExtraSkinnable>() is not { } skin) return;
        var applied = skin.AppliedSkinParts.ToDictionary(p => p.PartCode, p => p.Code);
        bool changed = false;

        if (citizen.Skin.Count == 0)
        {
            if (citizen.Sex == "female")
            {
                foreach (var part in new[] { "beard", "mustache" })
                {
                    if (!skin.AvailableSkinPartsByCode.TryGetValue(part, out var available) || !available.VariantsByCode.ContainsKey("none")) continue;
                    if (applied.TryGetValue(part, out var code) && code == "none") continue;
                    skin.selectSkinPart(part, "none", false, false);
                    applied[part] = "none";
                    changed = true;
                }
            }
            citizen.Skin = applied;
            MarkDirty(colony);
        }
        else
        {
            foreach (var (part, code) in citizen.Skin)
            {
                if (applied.TryGetValue(part, out var have) && have == code) continue;
                if (!skin.AvailableSkinPartsByCode.TryGetValue(part, out var available) || !available.VariantsByCode.ContainsKey(code)) continue;
                skin.selectSkinPart(part, code, false, false);
                changed = true;
            }
        }
        if (changed) settler.WatchedAttributes.MarkPathDirty("skinConfig");
    }

    /// <summary>
    /// Writes a stored look into a settler entity that is about to spawn, so it never shows the random one first.
    /// </summary>
    public static void PresetSkin(CitizenRecord citizen, Entity entity)
    {
        if (citizen.Skin.Count == 0) return;
        var tree = new TreeAttribute();
        var parts = new TreeAttribute();
        foreach (var (part, code) in citizen.Skin) parts.SetString(part, code);
        tree["appliedParts"] = parts;
        entity.WatchedAttributes["skinConfig"] = tree;
    }

    /// <summary>
    /// Once an entity is up (spawned or loaded): its look, its starting clothes and what it wears.
    /// </summary>
    public void PrepareSettler(Colony colony, CitizenRecord citizen, EntitySettler settler)
    {
        ApplySkin(colony, citizen, settler);
        GiveStartingClothes(colony, citizen);
        DressEntity(citizen);
    }

    /// <summary>
    /// Picks up items lying within reach, like a player walking over them. Workers take them to their hut;
    /// settlers without a job carry them until they take a load to the warehouse (AiTaskSettlerCourier). Returns how many items.
    /// </summary>
    public int PickUpNearby(Colony colony, CitizenRecord citizen, EntityAgent settler, float range)
    {
        if (!CanCollectItems(colony, citizen)) return 0;
        int picked = 0;
        foreach (var item in sapi.World.GetEntitiesAround(settler.Pos.XYZ, range, range, e => e is EntityItem).OfType<EntityItem>())
        {
            if (!Collectible(item)) continue;
            var stack = item.Itemstack;
            int moved = AddToPack(colony, citizen, stack);
            if (moved <= 0) continue;
            picked += moved;
            if (stack.StackSize <= 0) item.Die(EnumDespawnReason.PickedUp);
            else item.Slot.MarkDirty();
            sapi.World.PlaySoundAt(new AssetLocation("sounds/player/collect"), settler);
            if (PackFull(citizen)) break;
        }
        return picked;
    }

    /// <summary>The nearest item on the ground worth walking to, within range, inside the colony; or null.</summary>
    public EntityItem? GroundItemNear(Colony colony, CitizenRecord citizen, EntityAgent settler, float range)
    {
        if (!CanCollectItems(colony, citizen) || PackFull(citizen)) return null;
        return sapi.World.GetEntitiesAround(settler.Pos.XYZ, range, 4, e => e is EntityItem).OfType<EntityItem>()
            .Where(i => Collectible(i) && GetAt(i.Pos.AsBlockPos) == colony)
            .OrderBy(i => i.Pos.SquareDistanceTo(settler.Pos.XYZ))
            .FirstOrDefault();
    }

    bool CanCollectItems(Colony colony, CitizenRecord citizen) =>
        Config.SettlersPickUpItems && citizen.Alive && !citizen.Visitor && !citizen.IsChild && !citizen.IsDowned;

    /// <summary>Items a player dropped are left alone for a few seconds, so a settler does not snatch what was just thrown.</summary>
    bool Collectible(EntityItem item) =>
        item.Alive && item.Itemstack != null && item.Itemstack.StackSize > 0
        && sapi.World.ElapsedMilliseconds - item.itemSpawnedMilliseconds > Config.PickUpDelaySeconds * 1000;
}
