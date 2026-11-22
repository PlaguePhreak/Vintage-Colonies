using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace CreateAColony.Settlers;

/// <summary>
/// Added to a wolf or drifter raider (ColonyManager.AddRaidMarch, 0.181.0): while the raid is on, it walks on
/// the Town Hall. Its own hunting and fighting tasks rank above this, so it goes for settlers it meets on the way.
/// </summary>
public class AiTaskRaidMarch : SettlerTaskBase
{
    const float Near = 10f;
    long nextTryMs;

    public AiTaskRaidMarch(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override bool CountsForWillpower => false;

    ColonyManager.ActiveRaid? Raid => manager?.RaidOf(entity) is { Attacking: true, Fleeing: false } raid ? raid : null;

    public override bool ShouldExecute()
    {
        if (entity.World.ElapsedMilliseconds < nextTryMs || Raid is not { } raid) return false;
        return entity.Pos.SquareDistanceTo(manager!.RaidTarget(raid)) > Near * Near;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        if (Raid is { } raid) WalkTo(manager!.RaidTarget(raid), Near - 2);
    }

    public override bool ContinueExecute(float dt)
    {
        if (Raid == null || Arrived) return false;
        if (PathFailed)
        {
            nextTryMs = entity.World.ElapsedMilliseconds + 5000;
            return false;
        }
        KeepWalking();
        return true;
    }

    public override void FinishExecute(bool cancelled)
    {
        StopWalking();
        base.FinishExecute(cancelled);
    }
}
