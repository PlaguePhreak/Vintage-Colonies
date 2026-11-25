using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Which tool a builder needs to clear a block, following the same rules as a player: soil, sand and
/// gravel want a shovel, wood an axe, stone and ore a pickaxe, and the tool's tier must reach the
/// block's mining tier. Plants, leaves, glass and the like come away by hand.
/// </summary>
public partial class ColonyManager
{
    public readonly record struct ToolNeed(EnumTool Type, int Tier);

    static readonly EnumTool[] BuilderTools = { EnumTool.Shovel, EnumTool.Axe, EnumTool.Pickaxe, EnumTool.Hoe };
    readonly Dictionary<(EnumTool, int), ItemStack?> toolSamples = new();

    public static EnumTool? ToolFor(Block block) => block.BlockMaterial switch
    {
        EnumBlockMaterial.Soil or EnumBlockMaterial.Sand or EnumBlockMaterial.Gravel => EnumTool.Shovel,
        EnumBlockMaterial.Wood => EnumTool.Axe,
        EnumBlockMaterial.Stone or EnumBlockMaterial.Ore or EnumBlockMaterial.Brick or EnumBlockMaterial.Ceramic
            or EnumBlockMaterial.Metal or EnumBlockMaterial.Ice => EnumTool.Pickaxe,
        _ => null
    };

    /// <summary>
    /// True for a tool of this type and at least this tier. A prospecting pick never counts as a pickaxe,
    /// so builders and miners leave it for the prospector.
    /// </summary>
    public static bool ToolFits(ItemStack? stack, EnumTool type, int tier) =>
        stack != null && stack.Collectible.Tool == type && stack.Collectible.ToolTier >= tier && !IsProspectingPick(stack)
        // Tailor's Delight's needles and awls count as hammers to the game, but nobody can build or smith with one.
        && stack.Collectible.Code?.Domain != "tailorsdelight";

    public static bool IsBuilderTool(ItemStack stack) => stack.Collectible.Tool is { } t && BuilderTools.Contains(t);

    public static string ToolRequestCode(ToolNeed need) => $"tool:{need.Type.ToString().ToLowerInvariant()}:{need.Tier}";

    public static ToolNeed? ParseToolCode(string code)
    {
        var parts = code.Split(':');
        if (parts.Length != 3 || parts[0] != "tool" || !Enum.TryParse<EnumTool>(parts[1], true, out var type)) return null;
        return new ToolNeed(type, int.TryParse(parts[2], out int tier) ? tier : 0);
    }

    /// <summary>The tool needed to clear whatever stands at pos for this step, or null if hands will do.</summary>
    public ToolNeed? ToolNeededAt(Colony colony, BuildStep step, BlockPos pos)
    {
        if (!Config.BuildersNeedTools) return null;
        // Soil where the blueprint has farmland: a hoe to till it.
        if (Tilling(step, pos)) return new ToolNeed(EnumTool.Hoe, 0);
        var block = sapi.World.BlockAccessor.GetBlock(pos);
        if (block.Id == 0 || block.IsLiquid() || KeepsBlock(colony, block, step.BlockId, pos)) return null;
        return ToolFor(block) is { } type ? new ToolNeed(type, block.RequiredMiningTier) : null;
    }

    /// <summary>Tools still needed for the rest of a work order, for the "Items needed" list.</summary>
    public HashSet<ToolNeed> ToolsNeeded(Colony colony, Building order, Blueprint blueprint)
    {
        var needs = new HashSet<ToolNeed>();
        foreach (var step in blueprint.Steps)
        {
            var pos = StepPos(order, step);
            if (IsStepDone(colony, step, pos, order)) continue;
            if (ToolNeededAt(colony, step, pos) is { } need) needs.Add(need);
        }
        // Keep only the highest tier per tool type.
        return needs.GroupBy(n => n.Type).Select(g => g.OrderByDescending(n => n.Tier).First()).ToHashSet();
    }

    /// <summary>An example tool of this type, for showing a picture.</summary>
    public ItemStack? ToolSample(EnumTool type, int minTier = 0)
    {
        if (!toolSamples.TryGetValue((type, minTier), out var sample))
        {
            // The tool the Primitive Craftsman makes (flint) when it is good enough, else the lowest tier that is.
            // Never a ruined blade from the ruins: it counts as a knife and its code sorts first, so the creative
            // Give button handed those out (play test, 0.199.0).
            var item = sapi.World.Items
                .Where(i => i?.Code != null && i.Tool == type && i.ToolTier >= minTier && !UnmakeableTool(i))
                .OrderBy(i => i.Code.Domain != "game")
                .ThenBy(i => !i.Code.Path.Contains("flint"))
                .ThenBy(i => i.ToolTier).ThenBy(i => i.Code.ToString()).FirstOrDefault()
                ?? sapi.World.Items.Where(i => i?.Code != null && i.Tool == type && !UnmakeableTool(i))
                    .OrderByDescending(i => i.ToolTier).ThenBy(i => i.Code.ToString()).FirstOrDefault();
            sample = item == null ? null : new ItemStack(item);
            toolSamples[(type, minTier)] = sample;
        }
        return sample;
    }

    /// <summary>Tools nobody can make: ruined weapons, and things that only count as a tool to the game.</summary>
    static bool UnmakeableTool(Item item) =>
        item.Code.Path.Contains("ruined") || item.Code.Domain == "tailorsdelight" || item.Code.Path.StartsWith("prospectingpick");

    /// <summary>Asks couriers to bring a tool to the builder's hut, once.</summary>
    public bool EnsureToolRequest(Colony colony, Building order, Building builderHut, ToolNeed need)
    {
        var chest = HutStorage(colony, builderHut).FirstOrDefault();
        if (chest == null) return false;
        string code = ToolRequestCode(need);
        string tag = RequestTag(order);
        if (!colony.Requests.Any(r => r.RequestedBy == tag && r.ItemCode == code)) AddRequest(colony, code, 1, chest.Pos, tag);
        return true;
    }
}
