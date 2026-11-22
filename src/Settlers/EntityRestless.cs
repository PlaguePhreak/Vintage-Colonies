using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// One of the Restless (zombies), on an undead raid (0.187.0, design doc https://claude.ai/artifact/W5h1tzifqjBvYEoRfBR73s).
/// Built like a bandit on the settler body (the player's model with the character creator's skin parts, given a
/// rotting skin by SettlerLooks) with clothes slots for torn clothes, but no hands: it fights with its fists and
/// teeth. AiTaskRestlessRaid makes it shamble on the Town Hall and attack settlers and players; a bite can make
/// a settler ill. It never breaks a block. ColonyManager.Undead spawns it, dresses it and sends it off at sunrise.
/// </summary>
public class EntityRestless : EntityHumanoid
{
}
