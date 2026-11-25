using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The potter of a Potter's Hut works with the game's own pottery. It shapes clay on the game's clay
/// forming block, one layer at a time, following the same clay forming recipes a player picks from,
/// and fires what it made in pit kilns it digs beside the hut: it lays the pieces in a pit, adds
/// the dry grass, sticks and firewood the game's pit kiln asks for, lights it, and takes the fired
/// pottery out once the game has fired it. The hut window says how many of each kind to keep in stock.
/// </summary>
public partial class ColonyManager
{
    /// <summary>
    /// One kind of pottery, whatever clay it is made from: the clay forming recipes that make it, the
    /// raw piece's code with the clay type as * (also its key), and the codes of the fired pieces.
    /// </summary>
    public sealed class PotteryKind
    {
        public required string Key;
        public required AssetLocation RawPattern;
        public readonly List<ClayFormingRecipe> Recipes = new();
        public readonly HashSet<AssetLocation> Fired = new();
        /// <summary>A fired piece, for the hut window.</summary>
        public required ItemStack Sample;

        public bool IsRaw(ItemStack s) => WildcardUtil.Match(RawPattern, s.Collectible.Code);
        public bool IsMine(ItemStack s) => IsRaw(s) || Fired.Contains(s.Collectible.Code);
    }

    public enum KilnState { NoPit, Empty, Building, Ready, Lit, Fired }

    static readonly FieldInfo? KilnStageField = typeof(BlockEntityPitKiln).GetField("currentBuildStage", BindingFlags.Instance | BindingFlags.NonPublic);

    List<PotteryKind>? potteryKinds;
    HashSet<AssetLocation>? kilnMaterials;

    /// <summary>Every kind of pottery the game's clay forming recipes make that can be fired in a pit kiln.</summary>
    public List<PotteryKind> PotteryKinds()
    {
        if (potteryKinds != null) return potteryKinds;
        var clayTypes = sapi.World.Items.Where(i => i is ItemClay && i.Variant?["type"] != null).Select(i => i.Variant["type"]).ToHashSet();
        var kinds = new Dictionary<string, PotteryKind>();
        foreach (var recipe in sapi.GetClayformingRecipes())
        {
            var raw = recipe.Output?.ResolvedItemstack;
            if (raw == null || !CanPitFire(raw, out var fired)) continue;
            // The same piece in blue, fire or red clay is one kind.
            string path = string.Join("-", raw.Collectible.Code.Path.Split('-').Select(part => clayTypes.Contains(part) ? "*" : part));
            string key = raw.Collectible.Code.Domain + ":" + path;
            if (!kinds.TryGetValue(key, out var kind))
            {
                kinds[key] = kind = new PotteryKind { Key = key, RawPattern = new AssetLocation(key), Sample = fired.Clone() };
            }
            kind.Recipes.Add(recipe);
            kind.Fired.Add(fired.Collectible.Code);
        }
        return potteryKinds = kinds.Values.OrderBy(k => k.Key).ToList();
    }

    /// <summary>True when a piece can go in a pit kiln: it stands on the ground and fires into something.</summary>
    bool CanPitFire(ItemStack raw, out ItemStack fired)
    {
        fired = null!;
        var props = raw.Collectible.GetCombustibleProperties(sapi.World, raw, null);
        if (props?.SmeltingType != EnumSmeltType.Fire || props.SmeltedStack?.ResolvedItemstack is not { } result) return false;
        if (raw.Collectible.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps == null) return false;
        fired = result;
        return true;
    }

    public PotteryKind? PotteryKindOf(ItemStack stack) => PotteryKinds().FirstOrDefault(k => k.IsRaw(stack));

    public static bool IsClay(ItemStack stack) => stack.Collectible is ItemClay;

    public bool IsRawPottery(ItemStack stack) => !IsClay(stack) && PotteryKindOf(stack) != null;

    /// <summary>Clay a recipe takes, worked out as the game's handbook does.</summary>
    public static int ClayCost(ClayFormingRecipe recipe) =>
        (int)Math.Ceiling(Math.Max(1, (recipe.Voxels.Cast<bool>().Count(v => v) - 64) / 25f));

    /// <summary>How many of a kind of pottery the hut keeps in stock.</summary>
    public int PotteryKeep(Building hut, PotteryKind kind)
    {
        if (hut.PotteryChosen) return hut.PotteryKeep.GetValueOrDefault(kind.Key);
        foreach (var (pattern, keep) in Config.PotteryDefaultKeep)
        {
            if (WildcardUtil.Match(pattern, kind.Key)) return keep;
        }
        return 0;
    }

    public void SetPotteryKeep(Colony colony, Building hut, string key, int keep)
    {
        if (!hut.PotteryChosen)
        {
            // The defaults become the hut's own list, so changing one kind keeps the others.
            hut.PotteryKeep = PotteryKinds().Select(k => (k.Key, Keep: PotteryKeep(hut, k))).Where(k => k.Keep > 0).ToDictionary(k => k.Key, k => k.Keep);
            hut.PotteryChosen = true;
        }
        if (keep > 0) hut.PotteryKeep[key] = keep;
        else hut.PotteryKeep.Remove(key);
        MarkDirty(colony);
    }

    /// <summary>How many of a kind the colony has, raw or fired: in the warehouse, at the hut, with the potter and in its kiln.</summary>
    public int PotteryStock(Colony colony, Building hut, PotteryKind kind, CitizenRecord? potter)
    {
        ItemMatch mine = kind.IsMine;
        int count = CountItems(WarehouseContainers(colony), mine) + CountItems(HutStorage(colony, hut), mine);
        if (potter != null) count += PackCount(potter, mine);
        count += InKilns(hut, mine);
        if (FormingRecipe(hut) is { } forming && kind.Recipes.Contains(forming)) count += forming.Output!.ResolvedItemstack!.StackSize;
        return count;
    }

    /// <summary>Pieces in all of the hut's pit kilns, raw or fired, that match.</summary>
    int InKilns(Building hut, ItemMatch match)
    {
        int count = 0;
        foreach (var pos in KilnsOf(hut))
        {
            if (sapi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityGroundStorage kiln)
            {
                count += kiln.Inventory.Take(4).Where(s => !s.Empty && match(s.Itemstack)).Sum(s => s.StackSize);
            }
        }
        return count;
    }

    /// <summary>
    /// The next piece to make: the first kind with fewer in stock than the hut keeps, and a recipe for it
    /// that takes clay the potter can get. Null when everything is stocked or there is no clay for it.
    /// </summary>
    public (PotteryKind Kind, ClayFormingRecipe Recipe)? NextPottery(Colony colony, Building hut, CitizenRecord potter, List<ItemStack> clay)
    {
        // What another hut has asked for comes first (0.202.0): the colony waited for a crucible while bowls were made.
        foreach (var kind in PotteryKinds().OrderByDescending(k => PotteryAskedShort(colony, hut, k, potter)).ThenBy(k => PriorityIndex(hut.Priorities, k.Key)))
        {
            int keep = PotteryKeep(hut, kind);
            // Crocks a cook or bowls settlers are short of are made on top of what the tab keeps (ColonyManager.Crocks.cs).
            if ((keep <= 0 || PotteryStock(colony, hut, kind, potter) >= keep) && !PotteryOrderOpen(colony, hut, kind, potter)
                && !PotteryAskedShort(colony, hut, kind, potter)) continue;
            foreach (var recipe in kind.Recipes)
            {
                int cost = ClayCost(recipe);
                if (clay.Any(c => recipe.Ingredient!.SatisfiesAsIngredient(c, false) && clay.Where(o => SameItem(o, c)).Sum(o => o.StackSize) >= cost)) return (kind, recipe);
            }
        }
        return null;
    }

    /// <summary>True when some kind is short and wants clay, so the potter should ask for some.</summary>
    public bool WantsPottery(Colony colony, Building hut, CitizenRecord potter) =>
        PotteryKinds().Any(k => PotteryKeep(hut, k) is > 0 and var keep && keep > PotteryStock(colony, hut, k, potter) || PotteryOrderOpen(colony, hut, k, potter)
            || PotteryAskedShort(colony, hut, k, potter));

    readonly Dictionary<string, (long Ms, Dictionary<string, int> Asked)> potteryAsked = new();

    /// <summary>
    /// How many fired pieces of a kind other huts have asked the couriers for (a crucible for the Smelter, moulds,
    /// a storage vessel for a build). Looked up every five seconds.
    /// </summary>
    public int PotteryAsked(Colony colony, Building hut, PotteryKind kind)
    {
        long ms = sapi.World.ElapsedMilliseconds;
        if (!potteryAsked.TryGetValue(colony.ColonyId, out var book) || ms - book.Ms > 5000)
        {
            var asked = new Dictionary<string, int>();
            var open = colony.Requests.Where(r => r.Remaining > 0 && ParseToolCode(r.ItemCode) == null).ToList();
            if (open.Count > 0)
            {
                foreach (var k in PotteryKinds())
                {
                    var fired = k.Fired.Select(code => (CollectibleObject?)sapi.World.GetItem(code) ?? sapi.World.GetBlock(code)).Where(c => c is { Id: > 0 })
                        .Select(c => new ItemStack(c!)).ToList();
                    if (fired.Count == 0) continue;
                    int n = open.Where(r => { var match = Matcher(r.ItemCode); return fired.Any(f => match(f)); }).Sum(r => r.Remaining);
                    if (n > 0) asked[k.Key] = n;
                }
            }
            potteryAsked[colony.ColonyId] = book = (ms, asked);
        }
        return book.Asked.GetValueOrDefault(kind.Key);
    }

    /// <summary>True when others have asked for more of a kind than the colony has, raw or fired.</summary>
    public bool PotteryAskedShort(Colony colony, Building hut, PotteryKind kind, CitizenRecord? potter) =>
        PotteryAsked(colony, hut, kind) is > 0 and var asked && asked > PotteryStock(colony, hut, kind, potter);

    public bool SameItem(ItemStack a, ItemStack b) => a.Equals(sapi.World, b, GlobalConstants.IgnoredStackAttributes);

    // ---- Clay forming -----------------------------------------------------------------------

    /// <summary>The recipe being shaped at the potter's clay forming spot, or null.</summary>
    public ClayFormingRecipe? FormingRecipe(Building hut) =>
        hut.FormPos != null && sapi.World.BlockAccessor.GetBlockEntity(hut.FormPos) is BlockEntityClayForm form ? form.SelectedRecipe : null;

    /// <summary>A spot near the hut to shape clay: free, with floor under it, and clear of buildings, fields and the kiln.</summary>
    public BlockPos? FindFormSpot(Colony colony, Building hut)
    {
        var ba = sapi.World.BlockAccessor;
        var clayform = sapi.World.GetBlock(new AssetLocation("clayform"));
        if (clayform == null) return null;
        return FindPotterySpot(colony, hut, ground =>
        {
            var pos = ground.UpCopy();
            if (KilnsOf(hut).Any(k => pos.Equals(k) || pos.Equals(k.UpCopy()))) return null;
            if (!ba.GetBlock(pos).IsReplacableBy(clayform) || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) return null;
            if (!ba.GetBlock(ground).CanAttachBlockAt(ba, clayform, ground, BlockFacing.UP)) return null;
            return pos;
        });
    }

    /// <summary>Puts down the game's clay forming block with the clay for a recipe in it, the recipe chosen.</summary>
    public bool StartForming(Colony colony, Building hut, CitizenRecord potter, ClayFormingRecipe recipe, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var clayform = sapi.World.GetBlock(new AssetLocation("clayform"));
        if (clayform == null || !ba.GetBlock(pos).IsReplacableBy(clayform)) return false;
        int cost = ClayCost(recipe);
        var clay = TakeFromPack(colony, potter, s => IsClay(s) && recipe.Ingredient!.SatisfiesAsIngredient(s, false), cost);
        if (clay == null) return false;
        var work = sapi.World.GetItem(AssetLocation.Create("clayworkitem-" + clay.Collectible.Variant["type"], clay.Collectible.Code.Domain));
        if (clay.StackSize < cost || work == null)
        {
            AddToPack(colony, potter, clay);
            return false;
        }

        ba.SetBlock(clayform.BlockId, pos);
        if (ba.GetBlockEntity(pos) is not BlockEntityClayForm form) return false;
        // The same state a player's clay forming block has after picking the recipe.
        var tree = new TreeAttribute();
        form.Voxels = new bool[16, 16, 16];
        form.ToTreeAttributes(tree);
        var baseMaterial = clay.Clone();
        baseMaterial.StackSize = 1;
        tree.SetItemstack("workItemStack", new ItemStack(work));
        tree.SetItemstack("baseMaterial", baseMaterial);
        tree.SetInt("availableVoxels", 25 * cost);
        tree.SetInt("selectedRecipeId", recipe.RecipeId);
        form.FromTreeAttributes(tree, sapi.World);
        form.MarkDirty(true);
        if (clayform.Sounds?.Place != null) sapi.World.PlaySoundAt(clayform.Sounds.Place, pos, -0.5);
        hut.FormPos = pos.Copy();
        MarkDirty(colony);
        return true;
    }

    /// <summary>
    /// Shapes the next layer of the piece on the clay forming block to match its recipe. When the last
    /// layer matches, the raw piece goes into the potter's inventory, as it would into a player's.
    /// </summary>
    public bool FormLayer(Colony colony, Building hut, CitizenRecord potter)
    {
        var ba = sapi.World.BlockAccessor;
        if (hut.FormPos == null || ba.GetBlockEntity(hut.FormPos) is not BlockEntityClayForm form || form.SelectedRecipe is not { } recipe) return false;
        var target = recipe.Voxels;
        int layer = 0;
        while (layer < 16 && LayerMatches(form.Voxels, target, layer)) layer++;
        if (layer < 16)
        {
            int added = 0;
            for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
            {
                if (form.Voxels[x, layer, z] == target[x, layer, z]) continue;
                added += target[x, layer, z] ? 1 : -1;
                form.Voxels[x, layer, z] = target[x, layer, z];
            }
            form.AvailableVoxels = Math.Max(0, form.AvailableVoxels - added);
            form.MarkDirty(true);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/player/clayform.ogg"), hut.FormPos.X + 0.5, hut.FormPos.Y + 0.5, hut.FormPos.Z + 0.5, null, true, 12);
            while (layer < 16 && LayerMatches(form.Voxels, target, layer)) layer++;
            if (layer < 16) return true;
        }

        var piece = recipe.Output!.ResolvedItemstack!.Clone();
        ba.SetBlock(0, hut.FormPos);
        StoreHarvest(colony, hut, potter, new[] { piece }, hut.FormPos);
        AddStat(hut, "pottery-shaped");
        return true;
    }

    static bool LayerMatches(bool[,,] voxels, bool[,,] target, int layer)
    {
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        {
            if (voxels[x, layer, z] != target[x, layer, z]) return false;
        }
        return true;
    }

    // ---- The pit kiln -----------------------------------------------------------------------

    BlockPitkiln? PitKilnBlock => sapi.World.GetBlock(new AssetLocation("pitkiln")) as BlockPitkiln;

    /// <summary>What the dry grass, sticks and firewood of a pit kiln are, from the game's pit kiln.</summary>
    public bool IsKilnMaterial(ItemStack stack)
    {
        kilnMaterials ??= PitKilnBlock?.BuildStagesByBlock.Values.SelectMany(stages => stages).SelectMany(st => st.Materials)
            .Where(m => m?.ItemStack != null).Select(m => m.ItemStack.Collectible.Code).ToHashSet() ?? new HashSet<AssetLocation>();
        return kilnMaterials.Contains(stack.Collectible.Code);
    }

    /// <summary>Every pit the potter dug: the first one and any more beside it.</summary>
    public IEnumerable<BlockPos> KilnsOf(Building hut)
    {
        if (hut.KilnPos != null) yield return hut.KilnPos;
        foreach (var pos in hut.MoreKilns) yield return pos;
    }

    /// <summary>
    /// How many pit kilns the potter uses at once (Aaron, 0.155.0): one more than the hut's level,
    /// up to PotterMaxKilns, so it can fire one load while it fills the next.
    /// </summary>
    public int KilnsAllowed(Building hut) => Math.Max(1, Math.Min(Config.PotterMaxKilns, Math.Max(1, hut.Level) + 1));

    readonly Dictionary<(string Colony, int Hut), long> lastKilnAdopt = new();

    /// <summary>
    /// Takes over pit kilns near the hut that no Potter's Hut has (0.202.0): a hut that was placed again forgot its old
    /// pits, dug a new one and left the others standing, lit or full of fired pots. Looked for twice a minute.
    /// </summary>
    void AdoptKilns(Colony colony, Building hut)
    {
        if (!Config.PotterAdoptsKilns) return;
        long ms = sapi.World.ElapsedMilliseconds;
        if (lastKilnAdopt.TryGetValue((colony.ColonyId, hut.BuildingId), out long last) && ms - last < 30_000) return;
        lastKilnAdopt[(colony.ColonyId, hut.BuildingId)] = ms;
        var ba = sapi.World.BlockAccessor;
        var taken = colony.Buildings.Where(b => b.Type == BuildingTypes.Potter).SelectMany(KilnsOf).ToList();
        int radius = Math.Max(2, Config.PotterSpotRadius) + 1;
        var pos = new BlockPos(hut.HutPos.dimension);
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        for (int dy = -5; dy <= 3; dy++)
        {
            pos.Set(hut.HutPos.X + dx, hut.HutPos.Y + dy, hut.HutPos.Z + dz);
            var be = ba.GetBlockEntity(pos);
            bool kiln = be is BlockEntityPitKiln;
            // A fired kiln is a pile of pottery in a pit.
            bool pile = !kiln && be is BlockEntityGroundStorage storage && storage.Inventory.Any(s => !s.Empty && PotteryKinds().Any(k => k.IsMine(s.Itemstack)))
                && KilnWallsOk(pos);
            if (!kiln && !pile) continue;
            if (taken.Any(k => k.Equals(pos)) || GetAt(pos) != colony) continue;
            var found = pos.Copy();
            if (hut.KilnPos == null) hut.KilnPos = found;
            else hut.MoreKilns.Add(found);
            taken.Add(found);
            LogEvent(colony, "event-kiln-adopted", BuildingName(hut));
            MarkDirty(colony);
        }
    }

    /// <summary>Each of the hut's pits and its state. Pits that were filled in or lost their walls are forgotten.</summary>
    public List<(BlockPos Pos, KilnState State)> Kilns(Colony colony, Building hut)
    {
        AdoptKilns(colony, hut);
        var list = new List<(BlockPos, KilnState)>();
        foreach (var pos in KilnsOf(hut).ToList())
        {
            var state = Kiln(colony, hut, pos);
            if (state != KilnState.NoPit) list.Add((pos, state));
        }
        return list;
    }

    public KilnState Kiln(Colony colony, Building hut, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        switch (ba.GetBlockEntity(pos))
        {
            case BlockEntityPitKiln pitKiln:
                return pitKiln.Lit ? KilnState.Lit : pitKiln.IsComplete ? KilnState.Ready : KilnState.Building;
            case BlockEntityGroundStorage pile:
                // A fired kiln turns back into a pile of pottery.
                if (pile.Inventory.Any(s => !s.Empty)) return KilnState.Fired;
                ba.SetBlock(0, pos);
                break;
        }
        if (IsKilnPit(pos)) return KilnState.Empty;
        // Filled in, or the walls are gone: dig a new one.
        if (hut.KilnPos != null && pos.Equals(hut.KilnPos))
        {
            hut.KilnPos = hut.MoreKilns.FirstOrDefault();
            if (hut.KilnPos != null) hut.MoreKilns.RemoveAt(0);
        }
        else hut.MoreKilns.RemoveAll(k => k.Equals(pos));
        MarkDirty(colony);
        return KilnState.NoPit;
    }

    /// <summary>A hole the game accepts as a pit kiln: solid, non-flammable sides and floor, open above.</summary>
    bool IsKilnPit(BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var block = ba.GetBlock(pos);
        if (block.Replaceable < 6000 && !(block is BlockPitkiln) || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) return false;
        return KilnWallsOk(pos);
    }

    bool KilnWallsOk(BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var kiln = PitKilnBlock;
        if (kiln == null) return false;
        foreach (var face in BlockFacing.HORIZONTALS.Append(BlockFacing.DOWN))
        {
            var npos = pos.AddCopy(face);
            var block = ba.GetBlock(npos);
            if (!block.CanAttachBlockAt(ba, kiln, npos, face.Opposite) || block.GetCombustibleProperties(sapi.World, null, npos) != null) return false;
        }
        return ba.GetBlock(pos.UpCopy()).Replaceable >= 6000;
    }

    /// <summary>Ground near the hut the potter can dig one block of to make a pit kiln.</summary>
    public BlockPos? FindKilnSpot(Colony colony, Building hut)
    {
        var ba = sapi.World.BlockAccessor;
        return FindPotterySpot(colony, hut, ground =>
        {
            var block = ba.GetBlock(ground);
            if (block.BlockMaterial is not (EnumBlockMaterial.Soil or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand)) return null;
            if (hut.FormPos != null && (ground.Equals(hut.FormPos) || ground.UpCopy().Equals(hut.FormPos))) return null;
            // Never in, under or in the walls of a pit already dug.
            if (KilnsOf(hut).Any(k => ground.ManhattenDistance(k) <= 1)) return null;
            return KilnWallsOk(ground) ? ground : null;
        });
    }

    /// <summary>Digs the pit; the soil goes with the potter's harvest to the hut.</summary>
    public bool DigKiln(Colony colony, Building hut, CitizenRecord potter, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var block = ba.GetBlock(pos);
        if (!KilnWallsOk(pos)) return false;
        if (block.Id != 0)
        {
            StoreHarvest(colony, hut, potter, DropsOf(block, pos), pos);
            BreakQuietly(pos);
            if (block.Sounds?.Break != null) sapi.World.PlaySoundAt(block.Sounds.Break, pos, 0);
        }
        if (hut.KilnPos == null) hut.KilnPos = pos.Copy();
        else if (!KilnsOf(hut).Any(k => k.Equals(pos))) hut.MoreKilns.Add(pos.Copy());
        MarkDirty(colony);
        return true;
    }

    /// <summary>
    /// How many raw pieces like this one go into one pit kiln, as the game allows: four to a block
    /// (bowls, crocks), two (ingot molds), or a stack up to what the game fires at once (bricks, shingles).
    /// </summary>
    public static int KilnLoad(ItemStack raw)
    {
        var props = raw.Collectible.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;
        if (props == null) return 0;
        return props.Layout switch
        {
            EnumGroundStorageLayout.Quadrants => 4,
            EnumGroundStorageLayout.Halves => 2,
            EnumGroundStorageLayout.Stacking => Math.Max(1, Math.Min(props.StackingCapacity, props.MaxFireable)),
            _ => 1
        };
    }

    /// <summary>
    /// Lays raw pieces in the pit as a player does with sneak + right-click, and makes it a pit kiln
    /// waiting for its fuel. Pieces that lie four to a block may be different pieces; stacked ones are all the same.
    /// </summary>
    public bool LoadKiln(Colony colony, Building hut, CitizenRecord potter, BlockPos pos)
    {
        var kilnBlock = PitKilnBlock;
        if (kilnBlock == null || !KilnsOf(hut).Any(k => k.Equals(pos)) || !IsKilnPit(pos)) return false;
        var first = PackItems(potter).FirstOrDefault(IsRawPottery);
        var props = first?.Collectible.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;
        if (first == null || props == null) return false;

        var pieces = new List<ItemStack>();
        if (props.Layout == EnumGroundStorageLayout.Stacking)
        {
            if (TakeFromPack(colony, potter, s => SameItem(s, first), KilnLoad(first)) is { } pile) pieces.Add(pile);
        }
        else
        {
            for (int i = 0; i < KilnLoad(first); i++)
            {
                var layout = props.Layout;
                var piece = TakeFromPack(colony, potter, s => IsRawPottery(s)
                    && s.Collectible.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps?.Layout == layout, 1);
                if (piece == null) break;
                pieces.Add(piece);
            }
        }
        if (pieces.Count == 0) return false;

        var ba = sapi.World.BlockAccessor;
        ba.SetBlock(kilnBlock.BlockId, pos);
        if (ba.GetBlockEntity(pos) is not BlockEntityPitKiln kiln)
        {
            foreach (var piece in pieces) AddToPack(colony, potter, piece);
            return false;
        }
        for (int i = 0; i < pieces.Count; i++)
        {
            kiln.Inventory[i].Itemstack = pieces[i];
            kiln.Inventory[i].MarkDirty();
        }
        kiln.DetermineBuildStages();
        kiln.DetermineStorageProperties(null);
        kiln.MarkDirty(true);
        // A player makes a pit kiln by adding its first fuel, so it never stands without any.
        AddKilnMaterial(colony, hut, potter, pos);
        if (props.PlaceRemoveSound != null) sapi.World.PlaySoundAt(props.PlaceRemoveSound.Clone().WithPathPrefixOnce("sounds/"), pos, 0);
        return true;
    }

    /// <summary>What a pit kiln of these pieces takes first (the game's first build stage, usually dry grass).</summary>
    public List<ItemStack> FirstKilnMaterials(ItemStack raw)
    {
        var kiln = PitKilnBlock;
        if (kiln == null) return new List<ItemStack>();
        var stages = kiln.BuildStagesByBlock.FirstOrDefault(kv => kv.Key != "*" && WildcardUtil.Match(new AssetLocation(kv.Key), raw.Collectible.Code)).Value
            ?? kiln.BuildStagesByBlock.GetValueOrDefault("*");
        if (stages == null || stages.Length == 0) return new List<ItemStack>();
        return stages[0].Materials.Where(m => m?.ItemStack != null).Select(m => m.ItemStack).ToList();
    }

    /// <summary>The fuel the kiln wants next and how many, preferring what the potter or the hut already has.</summary>
    public ItemStack? NextKilnMaterial(Colony colony, Building hut, CitizenRecord potter, BlockPos pos)
    {
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityPitKiln kiln || kiln.IsComplete) return null;
        var choices = KilnChoices(kiln);
        var storage = HutStorage(colony, hut).ToList();
        if (Config.KilnPrefersBestFuel && choices.Count > 1)
        {
            // The best fuel there is enough of with the potter, in the hut and in the warehouse (KilnChoices is best first).
            var warehouse = WarehouseContainers(colony).ToList();
            var best = choices.FirstOrDefault(m => PackCount(potter, s => SameItem(s, m)) + CountItems(storage, s => SameItem(s, m))
                + CountItems(warehouse, s => SameItem(s, m)) >= m.StackSize);
            if (best != null) return best;
        }
        return choices.FirstOrDefault(m => PackCount(potter, s => SameItem(s, m)) >= m.StackSize)
            ?? choices.FirstOrDefault(m => CountItems(storage, s => SameItem(s, m)) > 0)
            ?? choices.FirstOrDefault();
    }

    /// <summary>The materials the kiln's next build stage takes, keeping to the one used before for the same layer kind.</summary>
    List<ItemStack> KilnChoices(BlockEntityPitKiln kiln)
    {
        var stage = kiln.NextBuildStage;
        var used = stage.Materials.FirstOrDefault(m => m?.ItemStack != null && kiln.Inventory.Skip(4).Any(s => !s.Empty && SameItem(s.Itemstack, m.ItemStack)));
        var choices = stage.Materials.Where(m => m?.ItemStack != null && (used == null || m == used)).Select(m => m.ItemStack);
        return (Config.KilnPrefersBestFuel ? choices.OrderBy(KilnFuelRank) : choices).ToList();
    }

    /// <summary>Which pit kiln fuel a potter takes first (Aaron, 0.142.0): coal, then charcoal, peat and firewood.</summary>
    public static int KilnFuelRank(ItemStack s) => s.Collectible.Code.Path switch
    {
        var p when p.Contains("anthracite") || p.Contains("bituminouscoal") || p == "coke" => 0,
        var p when p.Contains("lignite") => 1,
        "charcoal" => 2,
        var p when p.Contains("peat") => 3,
        var p when p.StartsWith("firewood") => 4,
        _ => 5
    };

    /// <summary>Adds the next layer of fuel from the potter's inventory, as a player does by hand.</summary>
    public bool AddKilnMaterial(Colony colony, Building hut, CitizenRecord potter, BlockPos pos)
    {
        if (KilnStageField == null || sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityPitKiln kiln || kiln.IsComplete) return false;
        foreach (var material in KilnChoices(kiln))
        {
            if (PackCount(potter, s => SameItem(s, material)) < material.StackSize) continue;
            var fuel = TakeFromPack(colony, potter, s => SameItem(s, material), material.StackSize);
            if (fuel == null) continue;
            for (int i = 4; i < kiln.Inventory.Count && fuel.StackSize > 0; i++)
            {
                var slot = kiln.Inventory[i];
                if (slot.Empty)
                {
                    slot.Itemstack = fuel.Clone();
                    fuel.StackSize = 0;
                }
                else if (SameItem(slot.Itemstack, fuel))
                {
                    int n = Math.Min(fuel.StackSize, slot.Itemstack.Collectible.MaxStackSize - slot.StackSize);
                    slot.Itemstack.StackSize += n;
                    fuel.StackSize -= n;
                }
                slot.MarkDirty();
            }
            if (fuel.StackSize > 0) AddToPack(colony, potter, fuel);
            KilnStageField.SetValue(kiln, (int)KilnStageField.GetValue(kiln)! + 1);
            kiln.DetermineBuildStages(); // also picks up how long this fuel burns
            kiln.DetermineStorageProperties(null);
            kiln.MarkDirty(true);
            if (material.Collectible.Attributes?["placeSound"].AsString() is { } sound)
            {
                sapi.World.PlaySoundAt(AssetLocation.Create(sound, material.Collectible.Code.Domain).WithPathPrefixOnce("sounds/"), pos, -0.4);
            }
            return true;
        }
        return false;
    }

    // ---- Fuel for every kiln in one go, and the spare (0.202.0) -------------------------------

    /// <summary>What one firing of one pit kiln takes: every build stage of the game's pit kiln, in the plainest fuel of each (dry grass, sticks, firewood).</summary>
    public List<ItemStack> KilnFiringFuel()
    {
        var fuel = new List<ItemStack>();
        var kiln = PitKilnBlock;
        var stages = kiln?.BuildStagesByBlock.GetValueOrDefault("*") ?? kiln?.BuildStagesByBlock.Values.FirstOrDefault();
        if (stages == null) return fuel;
        foreach (var stage in stages)
        {
            var pick = stage.Materials.Where(m => m?.ItemStack != null).Select(m => m.ItemStack).OrderByDescending(KilnFuelRank).FirstOrDefault();
            if (pick == null) continue;
            if (fuel.FirstOrDefault(f => SameItem(f, pick)) is { } same) same.StackSize += pick.StackSize;
            else fuel.Add(pick.Clone());
        }
        return fuel;
    }

    /// <summary>
    /// Asks for kiln fuel in one go: dry grass, sticks and firewood for every pit kiln the potter may use, and with
    /// Keep spare fuel on, one more firing on top. Before, the potter asked for the next layer of one kiln at a time.
    /// firing: a kiln is waiting for fuel now. Without it only the spare is kept up.
    /// </summary>
    public void EnsureKilnFuel(Colony colony, Building hut, CitizenRecord potter, bool firing)
    {
        if (!firing && !hut.KeepSpareFuel) return;
        int kilns = Math.Max(KilnsAllowed(hut), KilnsOf(hut).Count());
        var storage = HutStorage(colony, hut).ToList();
        foreach (var fuel in KilnFiringFuel())
        {
            int per = fuel.StackSize;
            int target = per * (kilns + (hut.KeepSpareFuel ? 1 : 0));
            int have = PackCount(potter, s => SameItem(s, fuel)) + CountItems(storage, s => SameItem(s, fuel));
            // Less than one firing short: not worth a trip, unless a kiln stands waiting for it.
            if (have > target - per && !(firing && have < per)) continue;
            if (target > have) EnsurePotterRequest(colony, hut, fuel.Collectible.Code.ToString(), target - have);
        }
    }

    // ---- The cover against rain (0.202.0) -------------------------------------------------------

    public bool IsKilnCover(ItemStack stack) =>
        stack.Block != null && (Config.KilnCoverBlocks ?? Array.Empty<string>()).Any(p => WildcardUtil.Match(new AssetLocation(p), stack.Collectible.Code));

    /// <summary>The request code for a kiln cover: any of the cover blocks.</summary>
    public string KilnCoverCode => string.Join("|", Config.KilnCoverBlocks ?? Array.Empty<string>());

    /// <summary>True when rain can't fall on the pit: something that keeps rain out is above it.</summary>
    public bool KilnCovered(BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        if (ba.GetRainMapHeightAt(pos.X, pos.Z) > pos.Y) return true;
        for (int dy = 1; dy <= 8; dy++)
        {
            var block = ba.GetBlock(pos.UpCopy(dy));
            if (block.Id != 0 && !block.RainPermeable && block.Replaceable < 6000) return true;
        }
        return false;
    }

    /// <summary>True when a cover block can go two above the pit: the fire keeps the air right above it.</summary>
    public bool CanCoverKiln(BlockPos pos) => sapi.World.BlockAccessor.GetBlock(pos.UpCopy(2)).Replaceable >= 6000;

    readonly Dictionary<BlockPos, double> kilnCoverWait = new();

    /// <summary>
    /// True when the potter should hold off lighting this kiln for its cover: covers are on, it has none, one can be
    /// put there, and the wait (KilnCoverWaitHours) is not over. After the wait it is lit as it is, unless it rains.
    /// </summary>
    public bool KilnWaitsForCover(BlockPos pos)
    {
        if (!Config.KilnCover || KilnCovered(pos) || !CanCoverKiln(pos))
        {
            kilnCoverWait.Remove(pos);
            return false;
        }
        double hours = sapi.World.Calendar.TotalHours;
        if (!kilnCoverWait.TryGetValue(pos, out double since)) kilnCoverWait[pos.Copy()] = since = hours;
        if (hours - since < Math.Max(0, Config.KilnCoverWaitHours)) return true;
        return RainingAt(pos);
    }

    bool RainingAt(BlockPos pos)
    {
        var climate = sapi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        return climate != null && climate.Rainfall > 0.05f;
    }

    /// <summary>Puts a cover block from the potter's pack two above the pit. It stays there.</summary>
    public bool CoverKiln(Colony colony, Building hut, CitizenRecord potter, BlockPos pos)
    {
        if (!CanCoverKiln(pos) || KilnCovered(pos)) return false;
        ItemStack? cover = null;
        // The first choice in the list that the potter carries.
        foreach (var pattern in Config.KilnCoverBlocks ?? Array.Empty<string>())
        {
            var loc = new AssetLocation(pattern);
            cover = TakeFromPack(colony, potter, s => s.Block != null && WildcardUtil.Match(loc, s.Collectible.Code), 1);
            if (cover != null) break;
        }
        if (cover?.Block == null) return false;
        var at = pos.UpCopy(2);
        sapi.World.BlockAccessor.SetBlock(cover.Block.BlockId, at);
        if (cover.Block.Sounds?.Place != null) sapi.World.PlaySoundAt(cover.Block.Sounds.Place, at, 0);
        kilnCoverWait.Remove(pos);
        return true;
    }

    public bool LightKiln(Colony colony, Building hut, CitizenRecord potter, BlockPos pos)
    {
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityPitKiln kiln || !kiln.CanIgnite) return false;
        // Nothing for the fire to spread through (ColonyManager.Fires.cs).
        ClearAroundFire(colony, potter, pos);
        kiln.TryIgnite(null);
        return true;
    }

    /// <summary>Hours until the lit kiln is done, or 0.</summary>
    public double KilnHoursLeft(BlockPos pos) =>
        sapi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityPitKiln { Lit: true } kiln
            ? Math.Max(0, kiln.BurningUntilTotalHours - sapi.World.Calendar.TotalHours) : 0;

    /// <summary>Takes the fired pottery out of the pit, leaving the pit ready for the next load.</summary>
    public void EmptyKiln(Colony colony, Building hut, CitizenRecord potter, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        if (!KilnsOf(hut).Any(k => k.Equals(pos)) || ba.GetBlockEntity(pos) is not BlockEntityGroundStorage pile || pile is BlockEntityPitKiln) return;
        var stacks = pile.Inventory.Where(s => !s.Empty).Select(s => s.Itemstack!.Clone()).ToList();
        ba.SetBlock(0, pos);
        StoreHarvest(colony, hut, potter, stacks, pos);
        AddStat(hut, "pottery-fired", stacks.Sum(s => s.StackSize));
    }

    // ---- Finding spots ----------------------------------------------------------------------

    /// <summary>
    /// The spot nearest the hut, within PotterSpotRadius, that the test accepts. The test gets the top
    /// block of the ground in each column and returns the spot or null. Spots inside buildings (at their
    /// highest level), fields and other claims are skipped.
    /// </summary>
    BlockPos? FindPotterySpot(Colony colony, Building hut, System.Func<BlockPos, BlockPos?> test)
    {
        var ba = sapi.World.BlockAccessor;
        var footprints = colony.Buildings.Select(Footprint).ToList();
        int radius = Math.Max(2, Config.PotterSpotRadius);
        for (int r = 1; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                for (int dy = 3; dy >= -4; dy--)
                {
                    var ground = hut.HutPos.AddCopy(dx, dy, dz);
                    if (!ba.GetBlock(ground).SideSolid[BlockFacing.UP.Index] || ba.GetBlock(ground.UpCopy()).Replaceable < 6000) continue;
                    if (footprints.Any(f => f.Contains(ground.X, ground.Y + 1, ground.Z) || f.Contains(ground.X, ground.Y, ground.Z))) break;
                    if (colony.Fields.Any(f => Math.Abs(f.Pos.X - ground.X) <= Config.FieldRadius && Math.Abs(f.Pos.Z - ground.Z) <= Config.FieldRadius)) break;
                    if (GetAt(ground) != colony || !CanWorkAt(colony, ground)) break;
                    if (test(ground) is { } spot) return spot;
                    break; // only the top of the ground in each column
                }
            }
        }
        return null;
    }

    /// <summary>The space a building takes at its highest level, or just around the hut block when it has no blueprint.</summary>
    Cuboidi Footprint(Building building)
    {
        int level = Math.Max(1, Blueprints.MaxLevel(building));
        var blueprint = Blueprints.Get(building, level);
        var p = building.HutPos;
        if (blueprint == null || blueprint.Steps.Count == 0) return new Cuboidi(p.X - 1, p.Y - 1, p.Z - 1, p.X + 1, p.Y + 2, p.Z + 1);
        var box = new Cuboidi(int.MaxValue, int.MaxValue, int.MaxValue, int.MinValue, int.MinValue, int.MinValue);
        foreach (var step in blueprint.Steps)
        {
            var pos = StepPos(building, step);
            box.X1 = Math.Min(box.X1, pos.X); box.Y1 = Math.Min(box.Y1, pos.Y); box.Z1 = Math.Min(box.Z1, pos.Z);
            box.X2 = Math.Max(box.X2, pos.X); box.Y2 = Math.Max(box.Y2, pos.Y); box.Z2 = Math.Max(box.Z2, pos.Z);
        }
        return box;
    }

    // ---- The hut ----------------------------------------------------------------------------

    /// <summary>At a Potter's Hut the clay, the kiln fuel and raw pieces stay; fired pottery and dug soil go to the warehouse.</summary>
    System.Func<ItemStack, bool> PotterHutJunk() => s => !IsClay(s) && !IsKilnMaterial(s) && !IsRawPottery(s) && !(Config.KilnCover && IsKilnCover(s));

    /// <summary>Asks couriers to bring something to the Potter's Hut, once at a time.</summary>
    public void EnsurePotterRequest(Colony colony, Building hut, string code, int quantity)
    {
        var chest = HutStorage(colony, hut).FirstOrDefault();
        if (chest == null) return;
        string tag = RequestTag(hut);
        if (!colony.Requests.Any(r => r.RequestedBy == tag && r.ItemCode == code)) AddRequest(colony, code, quantity, chest.Pos, tag);
    }

    /// <summary>A line about the kilns for the hut window.</summary>
    public string KilnInfo(Colony colony, Building hut)
    {
        var kilns = Kilns(colony, hut);
        if (kilns.Count == 0) return Lang.Get("createacolony:kiln-nopit");
        if (kilns.Count == 1)
        {
            var (pos, state) = kilns[0];
            return state == KilnState.Lit
                ? Lang.Get("createacolony:kiln-lit", Math.Ceiling(KilnHoursLeft(pos)))
                : Lang.Get("createacolony:kiln-" + state.ToString().ToLowerInvariant());
        }
        var parts = kilns.GroupBy(k => k.State).OrderBy(g => g.Key).Select(g => g.Key == KilnState.Lit
            ? Lang.Get("createacolony:kilns-lit", g.Count(), Math.Ceiling(g.Min(k => KilnHoursLeft(k.Pos))))
            : Lang.Get("createacolony:kilns-" + g.Key.ToString().ToLowerInvariant(), g.Count()));
        return Lang.Get("createacolony:kilns-info", kilns.Count, string.Join(", ", parts));
    }
}
