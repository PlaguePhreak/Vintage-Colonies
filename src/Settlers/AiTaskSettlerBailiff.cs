using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// The bailiff makes rounds of the estate: from the Town Hall out to a field or a hut where someone works, a
/// look at how it goes, and back to the Town Hall, where wanderers come to ask to join. What a bailiff does
/// for the colony (answering wanderers, giving out jobs, taxes and wages) happens in ColonyManager.Bailiff.cs, whether
/// or not they are on a round. The Mayor (0.199.0) makes the same rounds; their planning is in ColonyManager.Mayor.cs.
/// </summary>
public class AiTaskSettlerBailiff : SettlerTaskBase
{
    const float ArriveRange = 3;
    /// <summary>How long the bailiff stands and looks at a place, in seconds.</summary>
    const float LookSeconds = 12;
    /// <summary>Rest between rounds, so they also stand about at the Town Hall.</summary>
    const long RestMs = 25_000;
    const long RetryAfterMs = 15_000;

    long nextTryMs;
    bool toTownHall = true;
    BlockPos? place;
    string placeName = "";
    float looked;
    bool looking;

    public AiTaskSettlerBailiff(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextTryMs) return false;
        nextTryMs = entity.World.ElapsedMilliseconds + RetryAfterMs;
        if (Colony is not { } colony || Citizen is not { JobCode: Jobs.Bailiff or Jobs.Mayor, IsDowned: false } citizen || manager.IsBrokenDown(citizen)) return false;
        PickPlace(colony);
        return place != null;
    }

    /// <summary>Every other round goes back to the Town Hall; the others to a random field or a hut someone works at.</summary>
    void PickPlace(Colony colony)
    {
        toTownHall = !toTownHall;
        place = null;
        if (!toTownHall)
        {
            var rand = entity.World.Rand;
            var huts = colony.Buildings.Where(b => b.Level >= 1 && !b.Removing && manager!.WorkersAt(colony, b).Count > 0).ToList();
            int choices = huts.Count + colony.Fields.Count;
            if (choices > 0)
            {
                int pick = rand.Next(choices);
                if (pick < huts.Count)
                {
                    place = huts[pick].HutPos.Copy();
                    placeName = ColonyManager.BuildingName(huts[pick]);
                }
                else
                {
                    place = colony.Fields[pick - huts.Count].Pos.Copy();
                    placeName = Lang.Get("createacolony:bailiff-a-field");
                }
            }
        }
        if (place == null)
        {
            toTownHall = true;
            place = colony.TownHallPos.Copy();
            placeName = Lang.Get("createacolony:building-townhall");
        }
        // Already standing there: nothing to walk to this round.
        if (entity.Pos.SquareDistanceTo(place.ToVec3d().Add(0.5, 1, 0.5)) <= (ArriveRange + 2) * (ArriveRange + 2)) place = null;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        looking = false;
        looked = 0;
        WalkTo(place!.ToVec3d().Add(0.5, 1, 0.5), ArriveRange);
    }

    public override bool ContinueExecute(float dt)
    {
        if (place == null || Citizen is not { JobCode: Jobs.Bailiff or Jobs.Mayor }) return false;
        if (looking)
        {
            looked += dt;
            if (looked < LookSeconds) return true;
            nextTryMs = entity.World.ElapsedMilliseconds + RestMs;
            return false;
        }
        if (Arrived)
        {
            StopWalking();
            looking = true;
            return true;
        }
        if (PathFailed)
        {
            StopWalking();
            nextTryMs = entity.World.ElapsedMilliseconds + RetryAfterMs;
            return false;
        }
        KeepWalking();
        return true;
    }

    public override string Describe(Colony colony, CitizenRecord citizen) =>
        Lang.Get(looking ? "createacolony:activity-bailiff-looking" : "createacolony:activity-bailiff-walking", placeName);
}
