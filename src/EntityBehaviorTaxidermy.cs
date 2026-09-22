using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// Attached in code to every entity type a definition matches (see the mod system). Vanilla's
/// <c>EntityBehaviorHarvestable.GenerateDrops</c> asks every behaviour implementing
/// <c>IHarvestableDrops</c> for extra stacks and adds them to the carcass's harvest inventory
/// alongside the meat and hide - the antler-growth behaviour delivers antlers the same way.
/// So the head appears in the normal knife-harvest window, no new tool involved, beside the
/// meat and hide - which stay, because the hide is what the mount is stuffed with.
///
/// The drop rolls once, at harvest, on the server. Whether a specimen replaces the hide is
/// deliberately NOT decided here - the interface can only add, and Calm wants to see (a) in
/// play before deciding.
/// </summary>
public sealed class EntityBehaviorTaxidermy : EntityBehavior, IHarvestableDrops
{
    public EntityBehaviorTaxidermy(Entity entity) : base(entity) { }

    public override string PropertyName() => TaxidermyModSystem.BehaviorCode;

    public ItemStack[] GetHarvestableDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        if (world.Side != EnumAppSide.Server) return null;
        var system = entity.Api.ModLoader.GetModSystem<TaxidermyModSystem>();
        var definition = system.Find(entity.Code.ToString());
        if (definition == null) return null;
        float chance = Math.Clamp(definition.Chance ?? system.Config.HeadChance, 0f, 1f);
        if (chance < 1 && world.Rand.NextDouble() > chance) return null;

        var item = world.GetItem(new AssetLocation(Specimen.HeadRaw));
        if (item == null)
        {
            world.Logger.Error("[Taxidermy] {0} is not registered - no head from {1}", Specimen.HeadRaw, entity.Code);
            return null;
        }
        var stack = new ItemStack(item);
        stack.Attributes[Specimen.Key] = Specimen.Capture(entity, definition);
        return [stack];
    }
}
