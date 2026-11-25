using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// What a settler does with nothing else to do: now and then it strolls to a spot near its home, using the
/// pathfinder like every other walk, and otherwise stands about.
///
/// This replaces the game's "wander" task, which settlers used to have. That task walks in a straight line
/// with no pathfinding, so settlers walked into walls and fences and stood pressed against them. It also
/// pulls an entity back to where it first spawned (often the Town Hall or the colony border) whenever it is
/// more than a few blocks away, and after two minutes away with no player near, it teleports it there. Workers
/// whose hut was further away kept marching off between jobs and popping back to the spawn point.
///
/// Before strolling, an idle settler tidies up: a guard drops off what it picked up at its tower, anyone
/// walks over to items lying nearby to pick them up, a settler without a torch fetches one from its
/// hut or the warehouse for the dark, one in rags or cold changes into better clothes from the warehouse, and
/// one without first aid takes a bandage or poultice, or makes a poultice from horsetail (ColonyManager.Healing.cs).
/// One without a bowl takes one, and one carrying a bowl of rotten food washes it out in water (ColonyManager.Crocks.cs).
///
/// Now and then, instead of strolling, it sits down for a while as a player does: on a free chair, stool or
/// bench near its home if there is one, else on the floor where it stands (config SitWhenIdle).
/// </summary>
public class AiTaskSettlerIdle : SettlerTaskBase
{
    /// <summary>How long a settler stands about between strolls, at least and at most.</summary>
    const long MinPauseMs = 8_000;
    const long MaxPauseMs = 25_000;
    /// <summary>A stroll that has not arrived by now is cut short.</summary>
    const long MaxStrollMs = 30_000;
    /// <summary>Further than this from home, the next stroll heads back there.</summary>
    const double FarFromHome = 12;
    /// <summary>Chance that a break is spent sitting rather than strolling.</summary>
    const double SitChance = 0.5;
    /// <summary>How long a settler stays sitting, at least and at most.</summary>
    const float MinSitSeconds = 20f;
    const float MaxSitSeconds = 60f;
    /// <summary>How far from home (or from where it stands) a settler looks for a seat.</summary>
    const int SeatRange = 8;

    readonly int strollRadius;
    long nextStrollMs;
    long startedMs;
    Vec3d? spot;
    enum Errand { Stroll, PickUp, Deposit, Sit, Dress, FirstAid, MakeRemedy, Bowl, Wash }
    Errand errand;
    BlockPos? chestPos;
    BlockPos? seat;
    bool sitting;
    float sitLeft;

    public AiTaskSettlerIdle(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig)
    {
        strollRadius = System.Math.Max(1, taskConfig["strollRadius"].AsInt(6));
    }

    public override bool ShouldExecute()
    {
        if (manager == null) return false;
        long now = entity.World.ElapsedMilliseconds;
        if (now < nextStrollMs) return false;
        nextStrollMs = now + MinPauseMs + (long)(entity.World.Rand.NextDouble() * (MaxPauseMs - MinPauseMs));
        if (Colony is not { } colony || Citizen is not { } citizen || citizen.IsDowned) return false;
        if (PlanErrand(colony, citizen)) return true;
        var home = Home(colony, citizen);
        if (PlanSit(home)) return true;
        errand = Errand.Stroll;

        // Back towards home when far from it, else anywhere within the stroll radius of it.
        bool far = entity.Pos.XYZ.HorizontalSquareDistanceTo(home.X + 0.5, home.Z + 0.5) > FarFromHome * FarFromHome;
        int r = far ? 2 : strollRadius;
        var rand = entity.World.Rand;
        var around = home.AddCopy(rand.Next(-r, r + 1), 0, rand.Next(-r, r + 1));
        // A block of its own (0.200.0): not the one another settler already stands on.
        spot = manager.OwnSpotNear(entity, around, 2) ?? manager.FreeSpotNear(entity, around, 2);
        return spot != null && spot.SquareDistanceTo(entity.Pos.XYZ) > 2 * 2;
    }

    /// <summary>Sometimes a break is spent sitting: on a seat near home, else on the floor here. True when it is.</summary>
    bool PlanSit(BlockPos home)
    {
        if (!manager!.Config.SitWhenIdle || entity.World.Rand.NextDouble() > SitChance) return false;
        seat = SettlerSeats.FindSeat(entity, home, SeatRange) ?? SettlerSeats.FindSeat(entity, entity.Pos.AsBlockPos, SeatRange);
        if (seat == null && !SettlerSeats.CanSitHere(entity)) return false;
        errand = Errand.Sit;
        spot = seat == null ? null : SettlerSeats.SeatSpot(seat);
        sitLeft = MinSitSeconds + (float)entity.World.Rand.NextDouble() * (MaxSitSeconds - MinSitSeconds);
        return true;
    }

    /// <summary>Picks a chore to do before strolling. True when there is one; spot (and chestPos) say where.</summary>
    bool PlanErrand(Colony colony, CitizenRecord citizen)
    {
        if (citizen.Visitor || citizen.WaitingForRoom || citizen.IsChild) return false;
        chestPos = null;

        // A guard's things go to its tower; other jobs drop theirs off themselves, and couriers take loose loads to the warehouse.
        if (citizen.JobCode == Jobs.Guard && !manager!.PackEmpty(citizen) && citizen.WorkplacePos != null
            && manager.GetBuilding(colony, citizen.WorkplacePos) is { } tower && manager.HutStorage(colony, tower).FirstOrDefault() is { } chest)
        {
            errand = Errand.Deposit;
            chestPos = chest.Pos.Copy();
            spot = chestPos.ToVec3d().Add(0.5, 0, 0.5);
            return true;
        }

        if (manager!.Config.PickUpRange > 0 && manager.GroundItemNear(colony, citizen, entity, manager.Config.PickUpRange) is { } item)
        {
            errand = Errand.PickUp;
            spot = item.Pos.XYZ.Clone();
            return true;
        }

        // A bowl to take a serving of a crock or pot meal along, and rotten food washed out of one (ColonyManager.Crocks.cs).
        if (manager.BowlChest(colony, citizen) is { } bowlChest)
        {
            errand = Errand.Bowl;
            chestPos = bowlChest.Pos.Copy();
            spot = chestPos.ToVec3d().Add(0.5, 0, 0.5);
            return true;
        }
        if (manager.CarriesRotten(citizen) && manager.WashWater(colony, entity.Pos.AsBlockPos) is { } water)
        {
            errand = Errand.Wash;
            chestPos = water;
            spot = manager.FreeSpotNear(entity, water, 2) ?? water.ToVec3d().Add(0.5, 0, 0.5);
            return true;
        }

        // In rags or cold, or without jewellery: better clothes from the warehouse (ColonyManager.Clothing.cs).
        if (manager.ClothesChest(colony, citizen) is { } clothesChest)
        {
            errand = Errand.Dress;
            chestPos = clothesChest.Pos.Copy();
            spot = chestPos.ToVec3d().Add(0.5, 0, 0.5);
            return true;
        }

        // No bandage or poultice: take one, or make a poultice where the herbs are (ColonyManager.Healing.cs).
        if (manager.FirstAidErrand(colony, citizen) is { } aid)
        {
            errand = aid.Make ? Errand.MakeRemedy : Errand.FirstAid;
            chestPos = aid.Chest.Pos.Copy();
            spot = chestPos.ToVec3d().Add(0.5, 0, 0.5);
            return true;
        }
        return false;
    }

    /// <summary>On arrival: does the chore.</summary>
    void DoErrand()
    {
        if (Colony is not { } colony || Citizen is not { } citizen) return;
        switch (errand)
        {
            case Errand.PickUp:
                manager!.PickUpNearby(colony, citizen, entity, 2f);
                break;
            case Errand.Bowl:
                if (entity.World.BlockAccessor.GetBlockEntity(chestPos!) is Vintagestory.GameContent.BlockEntityContainer bowlChest) manager!.TakeBowl(colony, citizen, bowlChest);
                break;
            case Errand.Wash:
                manager!.WashCarried(colony, citizen, entity.Pos.XYZ);
                break;
            case Errand.Dress:
                if (entity.World.BlockAccessor.GetBlockEntity(chestPos!) is Vintagestory.GameContent.BlockEntityContainer clothesChest) manager!.ChangeClothesAt(colony, citizen, clothesChest);
                break;
            case Errand.FirstAid:
                if (entity.World.BlockAccessor.GetBlockEntity(chestPos!) is Vintagestory.GameContent.BlockEntityContainer aidChest) manager!.TakeFirstAid(colony, citizen, aidChest);
                break;
            case Errand.MakeRemedy:
                manager!.MakeRemedy(colony, citizen);
                break;
            case Errand.Deposit:
                if (citizen.WorkplacePos != null && manager!.GetBuilding(colony, citizen.WorkplacePos) is { } tower)
                {
                    manager.UnloadPack(colony, citizen, manager.HutStorage(colony, tower));
                }
                break;
        }
    }

    /// <summary>
    /// Where a settler hangs about: its workplace if it has one, else the spot the colony gave it (a child's home,
    /// where a visitor waits), else its bed, else the Town Hall.
    /// </summary>
    BlockPos Home(Colony colony, CitizenRecord citizen)
    {
        if (citizen.JobCode != null && citizen.WorkplacePos != null) return citizen.WorkplacePos;
        var attrs = entity.Attributes;
        if (attrs.HasAttribute("spawnX"))
        {
            return new BlockPos((int)System.Math.Floor(attrs.GetDouble("spawnX")), (int)System.Math.Floor(attrs.GetDouble("spawnY")),
                (int)System.Math.Floor(attrs.GetDouble("spawnZ")), entity.Pos.Dimension);
        }
        return citizen.HomeBedPos ?? colony.TownHallPos;
    }

    protected override bool CountsForWillpower => false;

    public override void StartExecute()
    {
        base.StartExecute();
        startedMs = entity.World.ElapsedMilliseconds;
        sitting = false;
        if (errand == Errand.Sit && spot == null)
        {
            // Sits down where it stands: the walk animation the task started is not wanted.
            StopWalking();
            SettlerSeats.SitOnFloor(entity);
            sitting = true;
            return;
        }
        WalkTo(spot!, errand switch { Errand.Stroll => 1f, Errand.Sit => 1.2f, _ => 1.5f });
    }

    public override bool ContinueExecute(float dt)
    {
        if (errand == Errand.Sit) return ContinueSitting(dt);

        // A stroll is given up at the first failure: it is not worth retrying, nor building Willpower for.
        if (Arrived || PathFailedOnce || entity.World.ElapsedMilliseconds - startedMs > MaxStrollMs)
        {
            StopWalking();
            if (Arrived || entity.Pos.XYZ.DistanceTo(spot!) < 2.5) DoErrand();
            if (errand != Errand.Stroll) nextStrollMs = entity.World.ElapsedMilliseconds + 1000; // straight on to the next chore
            return false;
        }
        KeepWalking();
        return true;
    }

    bool ContinueSitting(float dt)
    {
        if (sitting)
        {
            sitLeft -= dt;
            return sitLeft > 0;
        }
        if (!Arrived && !PathFailedOnce && entity.World.ElapsedMilliseconds - startedMs <= MaxStrollMs)
        {
            KeepWalking();
            return true;
        }
        // At the seat; or it could not be reached, and the settler sits where it is if it can.
        StopWalking();
        if (Arrived && seat != null && SettlerSeats.SitOn(entity, seat)) sitting = true;
        else if (SettlerSeats.CanSitHere(entity))
        {
            SettlerSeats.SitOnFloor(entity);
            sitting = true;
        }
        return sitting;
    }

    public override void FinishExecute(bool cancelled)
    {
        if (sitting) SettlerSeats.StandUp(entity);
        sitting = false;
        seat = null;
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen) => errand switch
    {
        Errand.PickUp => Lang.Get("createacolony:activity-picking-up"),
        Errand.Deposit => Lang.Get("createacolony:activity-dropping-off"),
        Errand.Dress => Lang.Get("createacolony:activity-changing-clothes"),
        Errand.Bowl => Lang.Get("createacolony:activity-fetching-bowl"),
        Errand.Wash => Lang.Get("createacolony:activity-washing"),
        Errand.FirstAid => Lang.Get("createacolony:activity-fetching-first-aid"),
        Errand.MakeRemedy => Lang.Get("createacolony:activity-making-poultice"),
        _ => Lang.Get(citizen.Visitor ? "createacolony:activity-visitor-waiting" : "createacolony:activity-idle")
    };
}
