using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The blacksmith of a Blacksmith's Hut makes tool heads, tools and other metal pieces from ingots. It
/// heats the ingots in the game's forge by the hut, with charcoal, then works them on the anvil by the hut
/// with a hammer. The game's smithing recipes say what can be made and how many ingots each takes; the
/// blacksmith does not move voxels one by one like a player but strikes a number of times that grows with
/// the size of the piece. When the game has a crafting recipe that puts the piece on a stick (a pickaxe
/// head and a stick make a pickaxe), the blacksmith does that too and makes the finished tool. The anvil
/// must be of a high enough tier for the metal, as for a player. The Smithing tab says how many to keep.
/// </summary>
public partial class ColonyManager
{
    /// <summary>One thing the blacksmith can make: the smithing recipe, the ingots it takes, and the crafting step that finishes it, if any.</summary>
    public sealed class SmithKind
    {
        /// <summary>The finished thing's code: the tool when the piece is put on a handle, else the piece.</summary>
        public required string Key;
        public required ItemStack Sample;
        /// <summary>The game's smithing recipe; null for a bloom hammered into an ingot by the config's list.</summary>
        public SmithingRecipe? Recipe;
        /// <summary>Hammered from a bloom (iron bloom into an iron ingot), not from ingots.</summary>
        public bool FromBloom;
        public required AssetLocation Ingot;
        public required int Ingots;
        public required int Strikes;
        public required int Tier;
        /// <summary>What comes off the anvil.</summary>
        public required ItemStack Piece;
        /// <summary>The crafting recipe that puts the piece on a handle, and the handle it needs; null when the piece is the finished thing.</summary>
        public GridRecipe? Assembly;
        public CraftingRecipeIngredient? Handle;

        public bool IsIngot(ItemStack s) => s.Collectible.Code.Equals(Ingot);
        public bool IsMine(ItemStack s) => s.Collectible.Code.ToString() == Key;
        public bool IsHandle(ItemStack s) => Handle != null && Handle.SatisfiesAsIngredient(s, false);
        public string Metal => Ingot.Path.StartsWith("ingot-") ? Ingot.Path["ingot-".Length..] : Ingot.Path;
    }

    List<SmithKind>? smithKinds;

    /// <summary>Everything the game's smithing recipes make from a whole number of ingots.</summary>
    public List<SmithKind> SmithKinds()
    {
        if (smithKinds != null) return smithKinds;
        var kinds = new Dictionary<string, SmithKind>();
        var assemblies = sapi.World.GridRecipes.Where(r => r.Output?.ResolvedItemStack != null && r.ResolvedIngredients != null
            && r.ResolvedIngredients.Count(i => i != null) == 2).ToList();
        foreach (var item in sapi.World.Items)
        {
            if (item?.Code == null || item is not IAnvilWorkable workable || item.FirstCodePart() != "ingot") continue;
            var ingot = new ItemStack(item);
            List<SmithingRecipe> recipes;
            int perIngot, tier;
            try
            {
                recipes = workable.GetMatchingRecipes(ingot);
                perIngot = Math.Max(1, workable.VoxelCountForHandbook(ingot));
                tier = workable.GetRequiredAnvilTier(ingot);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var recipe in recipes)
            {
                var piece = recipe.Output?.ResolvedItemstack;
                if (piece == null || recipe.Voxels == null) continue;
                int voxels = recipe.Voxels.Cast<bool>().Count(v => v);
                int ingots = (int)Math.Ceiling(voxels / (double)perIngot);
                if (ingots <= 0 || ingots > Config.BlacksmithMaxIngots) continue;
                var (assembly, handle) = FindAssembly(assemblies, piece);
                var finished = assembly?.Output?.ResolvedItemStack ?? piece;
                string key = finished.Collectible.Code.ToString();
                if (kinds.TryGetValue(key, out var known) && known.Ingots <= ingots) continue;
                var sample = finished.Clone();
                sample.StackSize = Math.Max(1, finished.StackSize);
                kinds[key] = new SmithKind
                {
                    Key = key,
                    Sample = sample,
                    Recipe = recipe,
                    Ingot = item.Code,
                    Ingots = ingots,
                    Strikes = Math.Max(1, (int)Math.Ceiling(voxels / (double)Math.Max(1, Config.BlacksmithVoxelsPerStrike))),
                    Tier = tier,
                    Piece = piece.Clone(),
                    Assembly = assembly,
                    Handle = handle
                };
            }
        }
        foreach (var bloom in BloomSmithKinds())
        {
            if (!kinds.ContainsKey(bloom.Key)) kinds[bloom.Key] = bloom;
        }
        return smithKinds = kinds.Values.OrderBy(k => k.Metal).ThenBy(k => k.Key).ToList();
    }

    /// <summary>
    /// Which smith makes a thing (Aaron, TRADERS_DRAFT.md): "weapon" for the Weaponsmith (swords, spears, maces,
    /// shields, arrowheads and blades), "armor" for the Armourer (chains, scales, plates and lamellae for
    /// armor), "tools" for the Blacksmith (tools and everything else).
    /// </summary>
    public string SmithCategory(SmithKind kind)
    {
        foreach (var stack in new[] { kind.Sample, kind.Piece })
        {
            string part = FirstPart(stack.Collectible.Code);
            if (Config.ArmourerParts.Contains(part) || IsArmor(stack)) return "armor";
            if (Config.WeaponsmithParts.Contains(part) || stack.Collectible.Tool is { } tool && Config.WeaponTools.Contains(tool.ToString())) return "weapon";
        }
        return "tools";
    }

    static string SmithCategoryOf(string hutType) => hutType switch
    {
        BuildingTypes.Armourer => "armor",
        BuildingTypes.Weaponsmith => "weapon",
        _ => "tools"
    };

    /// <summary>
    /// What a smith's hut makes: its own kind of thing. Until the colony has built an Armourer or a Weaponsmith,
    /// the Blacksmith still makes their things too.
    /// </summary>
    public List<SmithKind> SmithKindsFor(Colony colony, string hutType)
    {
        string mine = SmithCategoryOf(hutType);
        if (mine != "tools") return SmithKinds().Where(k => SmithCategory(k) == mine).ToList();
        bool armourer = HasBuilt(colony, BuildingTypes.Armourer), weaponsmith = HasBuilt(colony, BuildingTypes.Weaponsmith);
        return SmithKinds().Where(k => SmithCategory(k) switch
        {
            "armor" => !armourer,
            "weapon" => !weaponsmith,
            _ => true
        }).ToList();
    }

    bool HasBuilt(Colony colony, string type) => colony.Buildings.Any(b => b.Type == type && b.Level > 0 && !b.Removing);

    /// <summary>
    /// A new Armourer or Weaponsmith was built: what the colony's Blacksmiths kept of its kind of thing moves
    /// over to it, so armor and weapons are made there from now on.
    /// </summary>
    public void TakeOverSmithing(Colony colony, Building hut)
    {
        string mine = SmithCategoryOf(hut.Type);
        if (mine == "tools" || hut.CraftChosen) return;
        var keys = SmithKinds().Where(k => SmithCategory(k) == mine).Select(k => k.Key).ToHashSet();
        var moved = new Dictionary<string, int>();
        foreach (var smith in colony.Buildings.Where(b => b.Type == BuildingTypes.Blacksmith && b.CraftChosen))
        {
            foreach (var (key, keep) in smith.CraftKeep.Where(e => keys.Contains(e.Key)).ToList())
            {
                moved[key] = Math.Max(moved.GetValueOrDefault(key), keep);
                smith.CraftKeep.Remove(key);
            }
        }
        if (moved.Count == 0) return;
        hut.CraftKeep = moved;
        hut.CraftChosen = true;
        MarkDirty(colony);
    }

    /// <summary>A two-ingredient crafting recipe that takes this piece and one other thing (a stick), and that other thing.</summary>
    static (GridRecipe?, CraftingRecipeIngredient?) FindAssembly(List<GridRecipe> recipes, ItemStack piece)
    {
        foreach (var recipe in recipes)
        {
            var parts = recipe.ResolvedIngredients!.Where(i => i != null).Select(i => i!).ToList();
            var forPiece = parts.FirstOrDefault(i => i.SatisfiesAsIngredient(piece, false));
            if (forPiece == null) continue;
            var other = parts.FirstOrDefault(i => i != forPiece);
            if (other == null || other.SatisfiesAsIngredient(piece, false) || other.IsTool) continue;
            var output = recipe.Output!.ResolvedItemStack!;
            if (output.Collectible.Code.Equals(piece.Collectible.Code) || output.Collectible.FirstCodePart() == "ingot") continue;
            return (recipe, other);
        }
        return (null, null);
    }

    /// <summary>True for any metal ingot.</summary>
    public static bool IsIngot(ItemStack s) => s.Collectible.FirstCodePart() == "ingot";

    /// <summary>True when the ingot is hot enough to work on the anvil.</summary>
    public static bool IsWorkable(ItemStack s) => s.Collectible is IAnvilWorkable w && w.CanWork(s);

    /// <summary>Fuel a forge takes: charcoal or coal.</summary>
    public static bool IsForgeFuel(ItemStack s) => s.Collectible.CombustibleProps is { BurnTemperature: > 1000, BurnDuration: > 0 } && s.Block == null;

    /// <summary>Which forge fuel a smith takes first (Aaron, 0.142.0): charcoal, then coke, black coal and brown coal.</summary>
    public static int ForgeFuelRank(ItemStack s) => s.Collectible.Code.Path switch
    {
        "charcoal" => 0,
        "coke" => 1,
        var p when p.Contains("anthracite") || p.Contains("bituminouscoal") => 2,
        var p when p.Contains("lignite") => 3,
        _ => 4
    };

    public BlockEntityForge? SmithForge(Building hut) => NearHut<BlockEntityForge>(hut, Config.BlacksmithRadius);

    /// <summary>The best anvil by the hut that is free (no player's work on it).</summary>
    public BlockEntityAnvil? SmithAnvil(Building hut) =>
        AllNear<BlockEntityAnvil>(hut.HutPos, Config.BlacksmithRadius).Where(a => a.WorkItemStack == null).OrderByDescending(a => a.OwnMetalTier).FirstOrDefault();

    /// <summary>How many of a thing are ready: at the hut, in the warehouse and with the blacksmith.</summary>
    public int SmithStock(Colony colony, Building hut, SmithKind kind, CitizenRecord? smith)
    {
        ItemMatch mine = kind.IsMine;
        int count = CountItems(WarehouseContainers(colony), mine) + CountItems(HutStorage(colony, hut), mine);
        if (smith != null) count += PackCount(smith, mine);
        return count;
    }

    /// <summary>The things there are too few of that the anvil by the hut can make, most wanted first.</summary>
    public List<SmithKind> WantedSmithing(Colony colony, Building hut, CitizenRecord? smith, int anvilTier) => ByPriority(hut, SmithKindsFor(colony, hut.Type)
        .Where(k => k.Tier <= anvilTier)
        .Select(k => (Kind: k, Want: CraftWant(colony, hut, k.Key)))
        .Where(k => k.Want > 0)
        .Select(k => (k.Kind, k.Kind.Key, Short: k.Want - SmithStock(colony, hut, k.Kind, smith)))
        .Where(k => k.Short > 0))
        .ToList();

    /// <summary>
    /// The rows of the Smithing tab: everything kept in stock, and everything that can be made from the
    /// metals the colony has ingots of, that the anvil (if there is one) can work.
    /// </summary>
    public List<SmithKind> SmithRows(Colony colony, Building hut, CitizenRecord? smith)
    {
        var metals = new HashSet<AssetLocation>();
        void Add(IEnumerable<ItemStack> stacks)
        {
            foreach (var s in stacks) if (IsIngot(s) || IsBloom(s)) metals.Add(s.Collectible.Code);
        }
        Add(WarehouseContainers(colony).SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack));
        Add(HutStorage(colony, hut).SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack));
        if (smith != null) Add(PackItems(smith));
        int tier = SmithAnvil(hut)?.OwnMetalTier ?? int.MaxValue;
        return SmithKindsFor(colony, hut.Type).Where(k => CraftKeep(hut, k.Key) > 0 || ((k.FromBloom || metals.Contains(k.Ingot)) && k.Tier <= tier)).ToList();
    }

    /// <summary>Puts the ingots for a piece in the forge, adds fuel if it has little, and lights it.</summary>
    public bool HeatIngots(Colony colony, CitizenRecord smith, BlockEntityForge forge, SmithKind kind)
    {
        if (forge.WorkItemSlot.Empty)
        {
            var ingots = TakeFromPack(colony, smith, kind.IsIngot, kind.Ingots);
            if (ingots == null) return false;
            forge.WorkItemSlot.Itemstack = ingots;
            forge.WorkItemSlot.MarkDirty();
        }
        FuelForge(colony, smith, forge);
        return true;
    }

    /// <summary>Tops the forge up with charcoal and lights it.</summary>
    public void FuelForge(Colony colony, CitizenRecord smith, BlockEntityForge forge)
    {
        int want = Config.BlacksmithFuel - forge.FuelSlot.StackSize;
        // The best fuel the smith carries that the forge can take (it holds one kind at a time).
        var pick = PackItems(smith).Where(s => IsForgeFuel(s) && (forge.FuelSlot.Empty || s.Collectible.Code.Equals(forge.FuelSlot.Itemstack.Collectible.Code)))
            .OrderBy(ForgeFuelRank).FirstOrDefault();
        if (want > 0 && pick != null && TakeFromPack(colony, smith, s => s.Collectible.Code.Equals(pick.Collectible.Code), want) is { } fuel)
        {
            if (forge.FuelSlot.Empty) forge.FuelSlot.Itemstack = fuel;
            else forge.FuelSlot.Itemstack.StackSize += fuel.StackSize;
            forge.FuelSlot.MarkDirty();
            SaveFuel(colony, smith, fuel); // Efficient Smelting
        }
        if (!forge.IsBurning && forge.FuelLevel > 0) forge.TryIgnite();
        forge.MarkDirty(true);
    }

    /// <summary>Takes the hot ingots out of the forge.</summary>
    public void TakeFromForge(Colony colony, CitizenRecord smith, BlockEntityForge forge)
    {
        var stack = forge.WorkItemSlot.Itemstack;
        if (stack == null) return;
        forge.WorkItemSlot.Itemstack = null;
        forge.WorkItemSlot.MarkDirty();
        AddToPack(colony, smith, stack);
        if (stack.StackSize > 0)
        {
            forge.WorkItemSlot.Itemstack = stack;
            forge.WorkItemSlot.MarkDirty();
        }
        forge.MarkDirty(true);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), forge.Pos, 0);
    }

    /// <summary>
    /// The last strike: the hot ingots become the piece, and the piece goes on its handle if the blacksmith
    /// has one. False if the ingots are not there.
    /// </summary>
    public bool FinishPiece(Colony colony, Building hut, CitizenRecord smith, SmithKind kind)
    {
        if (PackCount(smith, kind.IsIngot) < kind.Ingots) return false;
        TakeFromPack(colony, smith, kind.IsIngot, kind.Ingots);
        var made = kind.Piece.Clone();
        if (kind.Assembly != null && kind.Handle != null && TakeFromPack(colony, smith, kind.IsHandle, Math.Max(1, kind.Handle.Quantity)) != null)
        {
            made = kind.Assembly.Output!.ResolvedItemStack!.Clone();
        }
        int count = made.StackSize;
        AddToPack(colony, smith, made);
        if (made.StackSize > 0) StoreHarvest(colony, hut, smith, new[] { made }, hut.HutPos);
        AddStat(hut, kind.FromBloom ? "blooms-hammered" : "smithed", count);
        return true;
    }

    /// <summary>
    /// At a Blacksmith's, Armourer's or Weaponsmith's hut ingots, handles and forge fuel stay, and at an
    /// Armourer's what its armor recipes take and worn metal armor; what was made goes to the warehouse.
    /// </summary>
    ItemMatch BlacksmithHutJunk(Colony colony, Building hut)
    {
        var kinds = SmithKindsFor(colony, hut.Type);
        var recipeInputs = HasHutRecipes(hut.Type) ? HutRecipeInputs(colony, hut) : _ => false;
        bool armourer = hut.Type == BuildingTypes.Armourer;
        return s => !IsIngot(s) && !IsBloom(s) && !IsForgeFuel(s) && !kinds.Any(k => k.IsHandle(s)) && !recipeInputs(s)
            && !(armourer && (ArmourerMendLeft(s) > 1 - Config.ArmourerMendBelow || IsArmourPart(s)));
    }

    /// <summary>A line about the forge and the anvil for the hut window.</summary>
    public string BlacksmithInfo(Building hut)
    {
        var forge = SmithForge(hut);
        var anvil = SmithAnvil(hut);
        string forgeLine = forge == null ? Lang.Get("createacolony:smith-no-forge", Config.BlacksmithRadius)
            : Lang.Get(forge.IsBurning ? "createacolony:smith-forge-lit" : "createacolony:smith-forge-cold", Math.Round(forge.FuelLevel, 1));
        string anvilLine = anvil == null ? Lang.Get("createacolony:smith-no-anvil", Config.BlacksmithRadius)
            : Lang.Get("createacolony:smith-anvil", anvil.OwnMetalTier);
        return forgeLine + "<br>" + anvilLine;
    }
}
