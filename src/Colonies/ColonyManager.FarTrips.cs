using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

/// <summary>
/// Far trips (0.205.0): a hunter that finds nothing in its work area goes further out for a while instead of standing
/// at the hut. The animals near the hut were all hunted in the second overnight test and the colony starved.
/// </summary>
public partial class ColonyManager
{
    readonly Random farRand = new();

    /// <summary>How far from its hut a worker on a far trip may go: its work area times FarTripRangeMult, at most FarTripMaxRadius.</summary>
    public int FarTripRadius(Building hut)
    {
        int r = GatherRadius(hut);
        return Math.Max(r, Math.Min(Config.FarTripMaxRadius, (int)(r * Math.Max(1f, Config.FarTripRangeMult))));
    }

    /// <summary>A dry spot on the ground outside the hut's work area but inside its far trip range, or null when none was found.</summary>
    public BlockPos? FarTripSpot(Building hut)
    {
        var ba = sapi.World.BlockAccessor;
        int inner = Math.Max(8, GatherRadius(hut) * 3 / 4), outer = FarTripRadius(hut);
        if (outer <= inner) outer = inner + 16;
        for (int tries = 0; tries < 12; tries++)
        {
            double angle = farRand.NextDouble() * Math.PI * 2, dist = inner + farRand.NextDouble() * (outer - inner);
            int x = hut.HutPos.X + (int)(Math.Cos(angle) * dist), z = hut.HutPos.Z + (int)(Math.Sin(angle) * dist);
            var column = new BlockPos(x, hut.HutPos.Y, z, hut.HutPos.dimension);
            if (!ba.IsValidPos(column) || ba.GetChunkAtBlockPos(column) == null) continue;
            int top = ba.GetRainMapHeightAt(x, z);
            if (top <= 0) continue;
            var ground = new BlockPos(x, top, z, hut.HutPos.dimension);
            if (ba.GetBlock(ground, BlockLayersAccess.Fluid).Id != 0) continue;
            if (Math.Abs(top - hut.HutPos.Y) > 30) continue;
            return ground.UpCopy();
        }
        return null;
    }
}
