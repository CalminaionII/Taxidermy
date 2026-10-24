using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// Making the needle in the world as well as in the grid, as GROUND crafting (Calm, 2026-10-01:
/// the left hand will not take a bone or twine - "do ground crafting for it then").
/// </summary>
internal static class GroundCraft
{
    public static void Give(IPlayer byPlayer, ItemStack stack, BlockPos pos)
    {
        if (byPlayer?.InventoryManager.TryGiveItemstack(stack, true) == true) return;
        byPlayer?.Entity.World.SpawnItemEntity(stack, pos.ToVec3d().Add(0.5, 0.5, 0.5));
    }

    /// <summary>After a slot on the ground changed: redraw, and clear the spot if nothing is left on it.</summary>
    public static void Changed(BlockEntityContainer be, ItemSlot slot)
    {
        slot.MarkDirty();
        if (be.Inventory.Empty) be.Api.World.BlockAccessor.SetBlock(0, be.Pos);
        else be.MarkDirty(true);
    }
}

/// <summary>
/// On the game's bone (patched in FIRST, patches/bone-needle.json): a bone on the ground, hold
/// right-click with a knife -> a bone needle (Calm, 2026-10-01: a plain right-click with a knife does
/// nothing on a bone pile, so it is free). SNEAK + knife goes to the game's own ground crafting, which
/// carves the flute exactly as before -
/// ground storage asks only one handler, so this one passes the click on. Which of the two a hold is
/// gets decided when it starts and kept to the end, so letting go of sprint half way cannot hand a
/// half-carved needle to the flute.
/// </summary>
public sealed class BehaviorBoneCarve : CollectibleBehavior, IContainedInteractable
{
    public const float Seconds = 2.5f;
    private const string Anim = "knifecut";
    /// <summary>The flute's own carving sound (game:bone's processingSound), played the way the game plays it.</summary>
    private static readonly AssetLocation Sound = new("game:sounds/tool/groundcrafting/boneflute*");

    /// <summary>Players whose current hold is carving a needle (not the flute).</summary>
    private readonly HashSet<string> carving = new();

    public BehaviorBoneCarve(CollectibleObject collObj) : base(collObj) { }

    /// <summary>The game's own bone crafting (the flute), which every other click goes to.</summary>
    private IContainedInteractable Vanilla =>
        collObj.CollectibleBehaviors.FirstOrDefault(b => b != this && b is IContainedInteractable) as IContainedInteractable;

    /// <summary>A knife selected and NOT sneaking (sneak + knife is the game's flute) - the game's own sneak control, whatever key.</summary>
    private static bool Wants(IPlayer byPlayer) =>
        byPlayer?.Entity?.Controls.Sneak == false && byPlayer.InventoryManager.ActiveTool == EnumTool.Knife;

    private bool Ours(IPlayer byPlayer) { lock (carving) return byPlayer != null && carving.Contains(byPlayer.PlayerUID); }

    public bool OnContainedInteractStart(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (!Wants(byPlayer))
        {
            // A needle carve whose Stop never came (the pile taken away mid-cut, the player gone)
            // would otherwise still be marked, and this flute carve would finish as a needle.
            lock (carving) carving.Remove(byPlayer?.PlayerUID ?? "");
            return Vanilla?.OnContainedInteractStart(be, slot, byPlayer, blockSel) ?? false;
        }
        lock (carving) carving.Add(byPlayer.PlayerUID);
        byPlayer.Entity.StartAnimation(Anim);
        be.Api.World.PlaySoundAt(Sound, blockSel.Position, 0, byPlayer);
        if (be.Api is ICoreClientAPI capi)
        {
            // The game's Stop arrives through the ground storage block - and when the last bone is
            // used the block is gone, so it never comes and the knife kept cutting (Calm, 2026-10-01;
            // the head skinning had the same). Stop it here too: once the bone pile is gone or the
            // time is well past.
            var pos = blockSel.Position.Copy();
            long since = capi.World.ElapsedMilliseconds, id = 0;
            id = capi.Event.RegisterGameTickListener(_ =>
            {
                bool gone = capi.World.BlockAccessor.GetBlock(pos) is not BlockGroundStorage;
                if (!gone && capi.World.ElapsedMilliseconds - since < (Seconds + 0.5f) * 1000) return;
                byPlayer.Entity.StopAnimation(Anim);
                lock (carving) carving.Remove(byPlayer.PlayerUID);
                capi.Event.UnregisterGameTickListener(id);
            }, 100);
        }
        return true;
    }

    public bool OnContainedInteractStep(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (!Ours(byPlayer)) return Vanilla?.OnContainedInteractStep(secondsUsed, be, slot, byPlayer, blockSel) ?? false;
        if (be.Api.World.Rand.NextDouble() < 0.05) be.Api.World.PlaySoundAt(Sound, blockSel.Position, 0, byPlayer);
        if (secondsUsed < Seconds) return true;
        byPlayer.Entity.StopAnimation(Anim);
        return false;
    }

    public void OnContainedInteractStop(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        byPlayer?.Entity.StopAnimation(Anim);
        if (!Ours(byPlayer))
        {
            Vanilla?.OnContainedInteractStop(secondsUsed, be, slot, byPlayer, blockSel);
            return;
        }
        lock (carving) carving.Remove(byPlayer.PlayerUID);
        var world = be.Api.World;
        if (world.Side != EnumAppSide.Server || secondsUsed < Seconds - 0.05f || slot.Itemstack == null) return;
        var needle = world.GetItem(new AssetLocation(ItemNeedleThread.BareCode));
        if (needle == null) return;
        var pos = be.Pos.Copy();
        slot.TakeOut(1);
        GroundCraft.Changed(be, slot);
        var knife = byPlayer.InventoryManager.ActiveHotbarSlot;
        knife.Itemstack?.Collectible.DamageItem(world, byPlayer.Entity, knife, 1);
        knife.MarkDirty();
        GroundCraft.Give(byPlayer, new ItemStack(needle), pos);
        world.PlaySoundAt(Sound, pos, 0, byPlayer);
    }

    public WorldInteraction[] GetContainedInteractionHelp(BlockEntityContainer be, ItemSlot slot, IPlayer forPlayer, BlockSelection blockSel)
    {
        // Asked every frame while the bone is looked at: the knives are listed once, not searched for each time.
        var knives = ObjectCacheUtil.GetOrCreate(be.Api, "taxidermy-knives", () => be.Api.World.Items.Where(i => i?.Tool == EnumTool.Knife).Select(i => new ItemStack(i)).ToArray());
        var vanilla = Vanilla?.GetContainedInteractionHelp(be, slot, forPlayer, blockSel) ?? [];
        return [.. vanilla, new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-carveneedle", MouseButton = EnumMouseButton.Right, Itemstacks = knives }];
    }
}

/// <summary>
/// The bare bone needle - THE sewing needle since 2026-10-24 (Calm: "the needle is a normal tool and it uses up the
/// thread"): kept, and sewing a mount takes one flax twine from the player (BlockAssembly.KeptNeedle, in the grid the
/// mount recipes with it and a flax twine). It used to be threaded on the ground with flax twine into the Needle and
/// Thread; that is gone - a threaded needle's 60 stitches from one twine would undercut it. Needles already threaded
/// still sew on their thread and come back bare when it runs out.
/// </summary>
public sealed class ItemNeedleBare : Item
{
}
