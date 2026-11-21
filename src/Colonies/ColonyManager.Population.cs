using System;
using System.Linq;

namespace CreateAColony.Colonies;

/// <summary>
/// The population cap: PopulationBase settlers, plus PopulationPerTownHallLevel for every level the Town
/// Hall has been built to (a freshly placed Town Hall is level 0, so 20; level 1 makes it 25). When the
/// colony is full nobody can be hired or welcomed, wanderers stop coming and couples have no children.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The Town Hall's built level, 0 until builders finish its first level.</summary>
    public int TownHallLevel(Colony colony) => colony.Buildings.Find(b => b.Type == BuildingTypes.TownHall)?.Level ?? 0;

    /// <summary>How many settlers the colony can hold; 0 or less in the config means no limit.</summary>
    public int PopulationCap(Colony colony) =>
        Config.PopulationBase <= 0 ? 0 : Config.PopulationBase
            + (Math.Max(0, Config.PopulationPerTownHallLevel) + (int)ResearchAdd(colony, "popPerTownHall")) * TownHallLevel(colony); // Bigger Village

    /// <summary>Everyone who lives here: settlers and children, not visitors or the dead.</summary>
    public int Population(Colony colony) => colony.Citizens.Count(c => c.Alive && !c.Visitor && !c.WaitingForRoom);

    public bool IsFull(Colony colony) => PopulationCap(colony) > 0 && Population(colony) >= PopulationCap(colony);
}
