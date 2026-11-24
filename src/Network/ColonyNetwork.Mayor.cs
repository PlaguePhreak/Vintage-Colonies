using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using CreateAColony.Colonies;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Network;

/// <summary>The Town Hall's Mayor tab (0.199.0): what goes in the snapshot, and its buttons (ColonyManager.Mayor.cs).</summary>
public partial class ColonyNetwork
{
    void FillMayor(ColonySnapshot snapshot, Colony colony)
    {
        var mayor = Manager.MayorOf(colony);
        snapshot.MayorName = mayor?.Name ?? "";
        snapshot.MayorState = mayor == null ? BailiffState.None : mayor.IsDowned ? BailiffState.Downed
            : Manager.IsBrokenDown(mayor) ? BailiffState.Sulking : BailiffState.OnDuty;
        snapshot.MayorMode = colony.MayorMode;
        snapshot.MayorFreeHuts = colony.MayorFreeHuts;
        if (colony.MayorPlan is { } plan)
        {
            snapshot.MayorPlan = Manager.PlanName(plan);
            snapshot.MayorPlanReason = Manager.ReasonText(plan);
            var th = colony.TownHallPos;
            snapshot.MayorPlanWhere = Vintagestory.API.Config.Lang.Get("createacolony:mayor-where", Distance(th, plan.Pos), Direction(th, plan.Pos));
            snapshot.MayorPlanApproved = plan.Approved;
            snapshot.MayorPlanNew = plan.BuildingId == 0;
        }
        snapshot.MayorIdle = Manager.MayorIdleText(colony);
        var order = Manager.MayorOrder(colony);
        snapshot.MayorOrder = order.Select(o => o.Type).ToList();
        snapshot.MayorOrderFrom = order.Select(o => o.From).ToList();
        snapshot.MayorSkip = colony.MayorSkip.ToList();
    }

    static int Distance(BlockPos a, BlockPos b) => (int)System.Math.Round(System.Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z)));

    /// <summary>"north-east" and so on, from a to b, in words.</summary>
    static string Direction(BlockPos a, BlockPos b)
    {
        int dx = b.X - a.X, dz = b.Z - a.Z;
        string ns = dz < -System.Math.Abs(dx) / 2 ? "north" : dz > System.Math.Abs(dx) / 2 ? "south" : "";
        string ew = dx > System.Math.Abs(dz) / 2 ? "east" : dx < -System.Math.Abs(dz) / 2 ? "west" : "";
        string key = ns + ew;
        return Vintagestory.API.Config.Lang.Get("createacolony:mayor-dir-" + (key.Length == 0 ? "here" : key));
    }

    string? MayorAction(IServerPlayer player, Colony colony, ColonyRole role, string text)
    {
        if (role < ColonyRole.Officer) return "createacolony:denied-officer";
        switch (text)
        {
            case "show":
                return ShowMayorPlan(player, colony);
            case "hide":
                ClearOutline(player);
                return null;
        }
        if (text is "approve" or "other" or "skip") ClearOutline(player);
        string? error = Manager.MayorAction(colony, text);
        // A new plan after Other spot: show it straight away.
        if (error == null && text == "other" && colony.MayorPlan is { BuildingId: 0 }) ShowMayorPlan(player, colony);
        return error;
    }

    /// <summary>Shows the player where the Mayor wants the new building: its ghost or outline, and the hut block's spot in yellow.</summary>
    string? ShowMayorPlan(IServerPlayer player, Colony colony)
    {
        if (colony.MayorPlan is not { } plan) return "createacolony:mayor-no-plan";
        if (plan.Station || plan.BuildingId != 0)
        {
            var only = new System.Collections.Generic.List<BlockPos> { plan.Pos.Copy() };
            sapi!.World.HighlightBlocks(player, OutlineSlot, only, new System.Collections.Generic.List<int> { ColorUtil.ColorFromRgba(255, 220, 60, 160) },
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
            return null;
        }
        var building = new Building { Type = plan.Type, HutPos = plan.Pos.Copy(), Rotation = plan.Rotation, Style = plan.Style, Variant = plan.Variant };
        var blueprint = Manager.Blueprints.Get(building, System.Math.Max(1, plan.Level));
        var outline = PreviewBuilding(player, building, blueprint);
        outline.Positions.Add(plan.Pos.Copy());
        outline.Colors.Add(ColorUtil.ColorFromRgba(255, 220, 60, 160));
        sapi!.World.HighlightBlocks(player, OutlineSlot, outline.Positions, outline.Colors,
            EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        return null;
    }
}
