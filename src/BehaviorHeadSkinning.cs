using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// Skinning a raw head on the ground (Calm, 2026-09-25): hold the knife on it (vanilla's ground
/// processing - time, sound, animation, particles), and what comes off it - the hide and the
/// animal's headSkinningDrops - drops on the ground while the head disappears. Since 2026-09-30
/// there is no container, window or bone pile any more (see OnContainedInteractStop).
///
/// Vanilla's finishing method is not virtual, so this class re-implements the interface ground
/// storage calls (GetCollectibleInterface&lt;IContainedInteractable&gt;); the rest is vanilla's.
/// </summary>
public sealed class BehaviorHeadSkinning : CollectibleBehaviorGroundStoredProcessable, IContainedInteractable
{
    private int toolCost = 1;

    public BehaviorHeadSkinning(CollectibleObject collObj) : base(collObj) { }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);
        toolCost = properties["toolDurabilityCost"].AsInt(1);
    }

    /// <summary>
    /// Vanilla starts the knife animation on the client in every step and stops it in Stop/Cancel -
    /// but those arrive through the ground storage block, and ours is replaced the moment the head
    /// is skinned, so the client's Stop never comes and the knife kept cutting (Calm, 2026-09-25).
    /// So the client stops it itself: on the step that finishes the job, and - should the server
    /// swap the block first - as soon as the spot is no longer ground storage.
    /// </summary>
    public new bool OnContainedInteractStart(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        bool started = base.OnContainedInteractStart(be, slot, byPlayer, blockSel);
        if (started && be.Api is ICoreClientAPI capi && ProcessingAnimationCode != null)
        {
            var pos = blockSel.Position.Copy();
            long since = capi.World.ElapsedMilliseconds, id = 0;
            id = capi.Event.RegisterGameTickListener(_ =>
            {
                bool gone = capi.World.BlockAccessor.GetBlock(pos) is not BlockGroundStorage;
                if (!gone && capi.World.ElapsedMilliseconds - since < (ProcessTime + 1) * 1000) return;
                if (gone) byPlayer.Entity.StopAnimation(ProcessingAnimationCode);
                capi.Event.UnregisterGameTickListener(id);
            }, 100);
        }
        return started;
    }

    public new bool OnContainedInteractStep(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        bool more = base.OnContainedInteractStep(secondsUsed, be, slot, byPlayer, blockSel);
        if (!more && byPlayer.Entity.World is IClientWorldAccessor) byPlayer.Entity.StopAnimation(ProcessingAnimationCode);
        return more;
    }

    public new void OnContainedInteractStop(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        byPlayer.Entity.StopAnimation(ProcessingAnimationCode);
        var world = be.Api.World;
        if (world.Side != EnumAppSide.Server || slot?.Itemstack == null || secondsUsed <= ProcessTime - 0.05f) return;
        if (Tool != null && byPlayer.InventoryManager.ActiveTool != Tool) return;

        string size = slot.Itemstack.Attributes.GetString("hideSize") ?? "medium";
        var yield = new List<ItemStack>();
        // Everything, the hide included, from the animal's headSkinningDrops (Calm, 2026-09-30).
        var data = Specimen.Of(slot.Itemstack);
        var def = data == null ? null : world.Api.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        foreach (var drop in HeadSkinningDrop.WithHide(def?.HeadSkinningDrops))
        {
            if (drop.Make(world, size) is { } stack) yield.Add(stack);
        }
        // The antlers its carcass handed over (TaxidermyConfig.AntlersOnHead) - always, no slip of
        // the knife (Calm: they are the hard parts, and the trophy).
        foreach (var code in Specimen.KeptAntlers(slot.Itemstack))
        {
            if (world.GetItem(new AssetLocation(code)) is { } antler) yield.Add(new ItemStack(antler));
        }

        var pos = blockSel.Position;
        slot.Itemstack = null;
        var toolSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (Tool != null) toolSlot.Itemstack?.Collectible.DamageItem(world, byPlayer.Entity, toolSlot, toolCost);
        world.PlaySoundAt(CompletionSound ?? ProcessingSound, pos, 0, byPlayer);

        // Everything drops on the ground and the head is gone - no container, no window, no bone
        // pile (Calm, 2026-09-30: the pile is a ribcage, wrong for a head; and the window is the
        // prime suspect for the player-next-to-the-skinner crash). What Immersive Corpse Drop
        // installs did before, now for everyone. Old skinned-head blocks still work (BESkinnedHead).
        world.BlockAccessor.SetBlock(0, pos);
        foreach (var stack in yield) world.SpawnItemEntity(stack, pos.ToVec3d().Add(0.5, 0.25, 0.5));
    }
}
