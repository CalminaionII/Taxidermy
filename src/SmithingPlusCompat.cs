using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Taxidermy;

/// <summary>
/// Smithing Plus picks its "tool heads" by name - any code matching
/// <c>(head|blade|boss|barrel|stirrup|part)</c> (its ToolHeadSelector) - so our animal heads get its
/// repairable-tool-head and cast-tool-head behaviours and are marked <c>forgable</c> (Smithing Plus
/// Core.AssetsFinalize, github.com/jayugg/SmithingPlus). This takes all of that back off our heads.
///
/// It does not touch Smithing Plus's own crash on leaving a world (its ConfigLoader.Dispose nulls the
/// config while the creative search cache still reads it) - that hits any tool it tags; Calm,
/// 2026-10-03: "just want mine fixed".
///
/// Runs after every other mod's AssetsFinalize (Smithing Plus keeps the default 0.1), on both sides,
/// as Smithing Plus tags on both. Matches by namespace, so Taxidermy needs no reference to it and
/// this does nothing when it is not installed.
/// </summary>
public sealed class SmithingPlusCompat : ModSystem
{
    public override double ExecuteOrder() => 1.0;

    public override void AssetsFinalize(ICoreAPI api)
    {
        int stripped = 0;
        foreach (var item in api.World.Items)
        {
            if (item is not ItemHead) continue;
            var kept = item.CollectibleBehaviors.Where(b => b.GetType().Namespace?.StartsWith("SmithingPlus") != true).ToArray();
            bool changed = kept.Length != item.CollectibleBehaviors.Length;
            item.CollectibleBehaviors = kept;
            // Our head.json sets neither; Smithing Plus's MakeForgeable adds both.
            if (item.Attributes?.Token is JObject attributes)
                changed |= attributes.Remove("forgable") | attributes.Remove("inForgeTransform");
            if (changed) stripped++;
        }
        if (stripped > 0) api.Logger.Notification("[Taxidermy] Took Smithing Plus's tool-head tags off {0} head item(s)", stripped);
    }
}
