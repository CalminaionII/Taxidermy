using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Taxidermy;

/// <summary>
/// The old threaded Needle and Thread, 60 stitches. Sewing a mount uses thread by the animal's size. Needles already
/// threaded keep working, and when the thread runs out the bare needle comes back.
/// </summary>
public sealed class ItemNeedleThread : Item
{
    public const string BareCode = "taxidermy:needle-bare";

    public override void DamageItem(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, int amount = 1, bool destroyOnZeroDurability = true)
    {
        var stack = itemslot?.Itemstack;
        if (stack == null) return;
        if (amount < GetRemainingDurability(stack))
        {
            base.DamageItem(world, byEntity, itemslot, amount, destroyOnZeroDurability);
            return;
        }
        // Out of thread: the bare needle stays where the threaded one was.
        var bare = world.GetItem(new AssetLocation(BareCode));
        itemslot.Itemstack = bare == null ? null : new ItemStack(bare);
        itemslot.MarkDirty();
        world.PlaySoundAt(new AssetLocation("game:sounds/effect/clothrip"), byEntity, (byEntity as EntityPlayer)?.Player, true, 16, 0.6f);
    }
}
