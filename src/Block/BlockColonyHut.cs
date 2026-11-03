using CreateAColony.Colonies;
using CreateAColony.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Blocks;

/// <summary>
/// A hut block: the seed of a building. Put it down inside the colony with the Builder's Wand, then
/// right-click it to order level 1, and a builder grows the building around it from its blueprint.
/// The building type comes from the block's "buildingType" attribute, so new huts are JSON only.
/// </summary>
public class BlockColonyHut : Block, IWandPlaceable
{
    public string BuildingType => Attributes?["buildingType"].AsString() ?? "";

    /// <summary>Huts are put down with the Builder's Wand, which shows the building first.</summary>
    public override bool DoPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack)
    {
        // With PlaceHutsByHand on it is only a block, for making blueprints: no colony, no building.
        if (WandOnly.HandPlacing(world))
        {
            if (!base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack)) return false;
            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityColonyHut be) be.SetRotation(WandOnly.HandRotation(byPlayer));
            return true;
        }
        WandOnly.Tell(world, byPlayer);
        return false;
    }

    /// <summary>The game's own placing, without the hut's "use the Builder's Wand" (for work stations, BlockWorkStation).</summary>
    protected bool PlacePlainBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ItemStack byItemStack) =>
        base.DoPlaceBlock(world, byPlayer, blockSel, byItemStack);

    public string? PlaceByWand(IServerPlayer player, BlockPos pos, int rotation)
    {
        var world = player.Entity.World;
        var manager = world.Api.ModLoader.GetModSystem<ColonyManager>();
        var colony = manager.GetAt(pos);
        if (colony == null || colony.RoleOf(player.PlayerUID) < ColonyRole.Member) return "createacolony:marker-outside";
        if (!world.BlockAccessor.GetBlock(pos).IsReplacableBy(this)) return "createacolony:wand-spot-taken";
        // The Archive needs a Town Hall of config ArchiveTownHallLevel (ColonyManager.Research.cs).
        if (BuildingType == BuildingTypes.Archive && !manager.ArchiveAllowed(colony)) return "createacolony:archive-needs-townhall";

        world.BlockAccessor.SetBlock(BlockId, pos);
        manager.AddBuilding(colony, BuildingType, pos, rotation);
        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityColonyHut hut) hut.SetRotation(rotation);
        string name = Lang.GetL(player.LanguageCode, "createacolony:building-" + BuildingType);
        string message = BuildingType == BuildingTypes.Tavern
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-tavern", name)
            : BuildingType == BuildingTypes.Potter
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-potter", name)
            : BuildingType == BuildingTypes.Cook
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-cook", name)
            : BuildingType == BuildingTypes.Residence
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-residence", name)
            : BuildingType == BuildingTypes.GuardTower
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-guardtower", name)
            : BuildingType == BuildingTypes.Barracks
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-barracks", name)
            : BuildingType is BuildingTypes.Prospector or BuildingTypes.Miner or BuildingTypes.Quarry or BuildingTypes.Smelter or BuildingTypes.Blacksmith or BuildingTypes.Armourer or BuildingTypes.Weaponsmith or BuildingTypes.Sawmill or BuildingTypes.Craftsman or BuildingTypes.Tailor or BuildingTypes.Jeweller or BuildingTypes.Healer or BuildingTypes.Mint or BuildingTypes.Archive
                or BuildingTypes.Explorer or BuildingTypes.Gatherer or BuildingTypes.Hunter or BuildingTypes.MineshaftEntrance
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-" + BuildingType, name)
            : BuildingType is BuildingTypes.Forester or BuildingTypes.Farmer
            ? Lang.GetL(player.LanguageCode, "createacolony:hut-placed-worker", name, Lang.GetL(player.LanguageCode, "createacolony:job-" + BuildingType))
            : Lang.GetL(player.LanguageCode, "createacolony:hut-placed", name);
        player.SendMessage(0, message, EnumChatType.Notification);
        return null;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // With the Builder's Wand in hand the placement window opens instead.
        if (Items.ItemBuildersWand.TryUse(world, byPlayer, blockSel.Position)) return true;
        // Sneaking opens the hut's own storage, like a chest; the server sends the inventory window.
        if (byPlayer.Entity.Controls.ShiftKey)
        {
            if (world.Side == EnumAppSide.Server && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityColonyHut storage)
            {
                storage.OnPlayerRightClick(byPlayer, blockSel);
            }
            return true;
        }
        // The client asks the server for the hut window; the server decides what to show.
        if (world.Side == EnumAppSide.Client)
        {
            world.Api.ModLoader.GetModSystem<ColonyNetwork>().RequestOpenHut(blockSel.Position);
        }
        return true;
    }

    /// <summary>Only some players may break it, and only when they mean to (see ColonyManager.BreakRefusal).</summary>
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (!ColonyBlockGuard.MayBreak(world, pos, byPlayer, townHall: false)) return;
        base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        if (world.Side == EnumAppSide.Server)
        {
            var manager = world.Api.ModLoader.GetModSystem<ColonyManager>();
            var colony = manager.GetAt(pos);
            if (colony != null) manager.RemoveBuilding(colony, pos);
        }
        base.OnBlockRemoved(world, pos);
    }
}
