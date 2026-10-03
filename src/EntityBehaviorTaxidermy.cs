using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// Attached in code to every entity type a definition matches (see the mod system). It makes the
/// HEAD; the harvest step that adds it, and sets the hides, is BarrelPatch.SwapHide - a postfix on
/// vanilla's GenerateDrops that runs once per carcass at the knife harvest. The head used to come
/// through IHarvestableDrops inside GenerateDrops, but vanilla builds a carcass's drops only once,
/// and a falx hit (the rip harvest) could build them first with the head held back - the knife
/// then gave the hide and no head (Calm, 2026-09-26, drifters).
/// </summary>
public sealed class EntityBehaviorTaxidermy : EntityBehavior
{
    public EntityBehaviorTaxidermy(Entity entity) : base(entity) { }

    public override string PropertyName() => TaxidermyModSystem.BehaviorCode;

    /// <summary>
    /// Set while vanilla's rip harvest runs (BarrelPatch): drifters, shivers and bowtorns hit
    /// with a ripHarvest blade have a chance to run the whole harvest and throw its first item on
    /// the ground - which could be our head or hide. Calm, 2026-09-26: no head or hide from a rip
    /// harvest; they come from the knife harvest only.
    /// </summary>
    [System.ThreadStatic] internal static bool RipHarvesting;

    /// <summary>This carcass's head, when its roll hits (at most one; RollHead remembers the roll).</summary>
    internal static ItemStack MakeHead(Entity entity, TaxidermyModSystem system, AnimalDefinition definition, float multiplier)
    {
        var world = entity.World;
        if (!system.RollHead(entity, definition, multiplier)) return null;

        var item = world.GetItem(new AssetLocation(Specimen.HeadRaw));
        if (item == null)
        {
            world.Logger.Error("[Taxidermy] {0} is not registered - no head from {1}", Specimen.HeadRaw, entity.Code);
            return null;
        }
        var stack = new ItemStack(item);
        var data = Specimen.Capture(entity, definition);
        stack.Attributes[Specimen.Key] = data;
        Specimen.SetSize(stack, data);
        return stack;
    }
}
