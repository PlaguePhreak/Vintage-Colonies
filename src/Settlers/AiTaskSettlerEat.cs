using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// When hungry, a settler walks to a container in the colony that holds food and eats one item, one
/// serving of a meal or one slice of pie (see ColonyManager.Food.cs). Food comes from the same chests,
/// shelves and crocks the players use, so feeding the colony is part of the game. Settlers prefer food
/// groups they have not eaten lately. With no food anywhere they try again later and eventually starve.
/// Settlers carrying packed food eat that on the go instead (ColonyManager.Lunch.cs) and only come here once
/// it is gone and they are hungry; here they pack food for later.
/// To eat, a settler sits down as a player does: on a chair, stool or bench near the food if there is a free
/// one, else on the floor (config SitToEat).
/// The food is held in the hand while it is eaten, as a player holds it (config HoldFoodToEat). The tool in the hand
/// is kept with the food and goes back in the hand afterwards.
/// </summary>
public class AiTaskSettlerEat : SettlerTaskBase
{
    const float EatSeconds = 3f;
    /// <summary>Sitting down, a settler takes its time over the meal.</summary>
    const float SitEatSeconds = 10f;
    /// <summary>How far from the food a settler looks for a seat.</summary>
    const int SeatRange = 6;
    const long NoFoodRetryMs = 30_000;
    /// <summary>After failing to reach food, the settler waits this long before looking again.</summary>
    const long UnreachableRetryMs = 5_000;
    /// <summary>Food that could not be reached is left alone for this long, so the settler tries other food.</summary>
    const long UnreachableForgetMs = 120_000;
    /// <summary>Marks the food a settler holds while eating: a copy for show, not food it can eat again.</summary>
    const string HeldFoodAttr = "createacolony:heldFood";
    /// <summary>On the held food: the tool that was in the hand before, to give back after eating.</summary>
    const string ToolAttr = "createacolony:handTool";

    BlockPos? containerPos;
    float eatTime;
    bool eating;
    BlockPos? seat;
    bool sitting;
    long nextSearchMs;
    string? eatingName;
    readonly Dictionary<BlockPos, long> unreachable = new();

    /// <summary>Never given up, however often the walk fails (0.200.0).</summary>
    protected override bool CanGiveUp => false;

    public AiTaskSettlerEat(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextSearchMs) return false;
        var citizen = Citizen;
        var needs = entity.GetBehavior<EntityBehaviorSettlerNeeds>();
        if (citizen == null || needs == null || CalledToArms || Colony is not { } colony) return false;
        bool hungry = needs.IsHungry(manager.Config, citizen);
        // At a meal time a settler that would eat comes in to the Cook's Hut tables (ColonyManager.Serving.cs).
        bool toTable = !hungry && manager.ComesToTable(colony, citizen);
        if (!hungry && !toTable) return false;
        // With packed food left the settler eats on the go (EntityBehaviorColonyCitizen) and keeps working.
        if (!toTable && manager.HasLunch(citizen, entity)) return false;

        var food = toTable ? manager.ServedMealFor(colony, citizen, entity, IsUnreachable) : manager.FindFood(colony, citizen, entity, IsUnreachable);
        if (food == null)
        {
            nextSearchMs = entity.World.ElapsedMilliseconds + NoFoodRetryMs;
            return false;
        }
        containerPos = food.Container.Pos.Copy();
        return true;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        eating = false;
        eatingName = null;
        eatTime = 0;
        seat = null;
        sitting = false;
        WalkTo(containerPos!.ToVec3d().Add(0.5, 0, 0.5), 1.5f);
    }

    public override bool ContinueExecute(float dt)
    {
        if (seat != null)
        {
            // Food in hand, on the way to a seat. If it cannot be reached, the settler sits where it is.
            if (!Arrived && !PathFailed)
            {
                KeepWalking();
                return true;
            }
            StopWalking();
            bool onSeat = Arrived && SettlerSeats.SitOn(entity, seat);
            seat = null;
            SitDown(onSeat);
            return true;
        }
        if (PathFailed)
        {
            // A hungry settler that cannot reach the nearest food used to try the same food again straight away,
            // forever, and never got back to work. Try other food (or work) first.
            long now = entity.World.ElapsedMilliseconds;
            unreachable[containerPos!.Copy()] = now + UnreachableForgetMs;
            nextSearchMs = now + UnreachableRetryMs;
            return false;
        }
        if (!eating && CalledToArms) return false;

        if (!eating)
        {
            if (!Arrived)
            {
                KeepWalking();
                return true;
            }
            StopWalking();
            if (!TakeFood()) return false;
            eating = true;
            if (manager!.Config.SitToEat && (seat = SettlerSeats.FindSeat(entity, containerPos!, SeatRange)) != null)
            {
                WalkTo(SettlerSeats.SeatSpot(seat), 1.2f);
                return true;
            }
            SitDown(false);
        }

        eatTime += dt;
        return eatTime < (sitting ? SitEatSeconds : EatSeconds);
    }

    /// <summary>Starts eating: on the seat it was just put on, else on the floor if it can sit there, else standing.</summary>
    void SitDown(bool onSeat)
    {
        sitting = onSeat;
        if (!onSeat && manager!.Config.SitToEat && SettlerSeats.CanSitHere(entity))
        {
            SettlerSeats.SitOnFloor(entity);
            sitting = true;
        }
        PlayAnimation("eat");
    }

    bool IsUnreachable(BlockPos pos)
    {
        if (!unreachable.TryGetValue(pos, out long until)) return false;
        if (entity.World.ElapsedMilliseconds < until) return true;
        unreachable.Remove(pos);
        return false;
    }

    bool TakeFood()
    {
        var citizen = Citizen;
        if (citizen == null || entity.World.BlockAccessor.GetBlockEntity(containerPos!) is not BlockEntityContainer container) return false;
        bool atTable = manager!.IsServedMeal(Colony!, container);
        eatingName = manager.EatFood(Colony!, citizen, entity, container, out var eaten);
        if (eatingName != null) manager.NoteMeal(Colony!, citizen, atTable);
        if (eatingName != null && eaten != null && manager.Config.HoldFoodToEat) HoldFood(entity, eaten);
        // Packs food for later while at the store, so the next meals are eaten on the go.
        if (eatingName != null) manager.PackLunch(Colony!, citizen, entity, container);
        return eatingName != null;
    }

    public override void FinishExecute(bool cancelled)
    {
        StopAnimation("eat");
        PutDownFood(entity);
        if (sitting) SettlerSeats.StandUp(entity);
        sitting = false;
        seat = null;
        eating = false;
        eatingName = null;
        base.FinishExecute(cancelled);
    }

    /// <summary>Puts a copy of the food in the right hand; the tool that was there is kept on it.</summary>
    static void HoldFood(EntityAgent entity, ItemStack food)
    {
        var hand = entity.RightHandItemSlot;
        if (hand == null) return;
        PutDownFood(entity);
        var held = food.Clone();
        held.StackSize = 1;
        // One slice of a pie, not the whole pie.
        if (held.Block is BlockPie) held.Attributes.SetInt("pieSize", 1);
        held.Attributes.SetBool(HeldFoodAttr, true);
        if (hand.Itemstack is { } tool) held.Attributes.SetItemstack(ToolAttr, tool);
        hand.Itemstack = held;
        SyncHand(entity, hand);
    }

    /// <summary>
    /// Takes the food out of the hand and gives back the tool. Also called when a settler dies or is loaded, so a
    /// meal cut short never leaves the copy in the hand or loses the tool.
    /// </summary>
    public static void PutDownFood(EntityAgent entity)
    {
        var hand = entity.RightHandItemSlot;
        if (hand?.Itemstack is not { } held || !held.Attributes.GetBool(HeldFoodAttr)) return;
        var tool = held.Attributes.GetItemstack(ToolAttr);
        if (tool != null && !tool.ResolveBlockOrItem(entity.World)) tool = null;
        hand.Itemstack = tool;
        SyncHand(entity, hand);
    }

    /// <summary>The tool or weapon in the settler's hand, even while it holds its food to eat.</summary>
    public static ItemStack? ToolInHand(EntityAgent entity)
    {
        var held = entity.RightHandItemSlot?.Itemstack;
        if (held == null || !held.Attributes.GetBool(HeldFoodAttr)) return held;
        var tool = held.Attributes.GetItemstack(ToolAttr);
        return tool != null && tool.ResolveBlockOrItem(entity.World) ? tool : null;
    }

    static void SyncHand(EntityAgent entity, ItemSlot hand)
    {
        hand.MarkDirty();
        entity.GetBehavior<EntityBehaviorVillagerInv>()?.storeInv();
    }

    public override string Describe(Colony colony, CitizenRecord citizen) => eatingName != null
        ? Vintagestory.API.Config.Lang.Get("createacolony:activity-eating-food", eatingName)
        : Vintagestory.API.Config.Lang.Get("createacolony:activity-eating");
}
