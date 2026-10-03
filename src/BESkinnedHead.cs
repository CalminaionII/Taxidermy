using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// What is left of a head after skinning: vanilla's generic container (take-only through its
/// <c>retrieveOnly</c> attribute, vanilla's window), still drawn as the HEAD it was, lying as it lay
/// (Calm, 2026-09-26) - vanilla's bone pile only if the head cannot be drawn. Once it is empty and
/// the window is closed it becomes that pile for real - <c>carcass-&lt;size&gt;</c>, broken for bones
/// and cleared by vanilla after two weeks, like the bones a dead animal rots into.
/// </summary>
public sealed class BESkinnedHead : BlockEntityGenericContainer
{
    private const string HeadKey = "taxidermyHead", AngleKey = "taxidermyHeadAngle";

    /// <summary>The skinned head's specimen tree, and ground storage's turn it lay at.</summary>
    private ITreeAttribute headData;
    private float meshAngle;

    /// <summary>
    /// False until this block entity's data has arrived. The client gets the new block a moment
    /// before its data, and drawing then showed the bone pile for a blink before the head (Calm,
    /// 2026-09-26: "turns to carcass but then quickly changes to the head") - so nothing is drawn
    /// until it knows what it is.
    /// </summary>
    private bool dataLoaded;

    public void SetHead(ItemStack head, float angle)
    {
        headData = Specimen.Of(head)?.Clone();
        meshAngle = angle;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (headData != null) tree[HeadKey] = headData;
        tree.SetFloat(AngleKey, meshAngle);
    }

    /// <summary>
    /// The head exactly as ground storage drew it - the same flat mesh (ItemHead.GenMesh), turned by
    /// the same angle about the block centre (BEGroundStorage.genTransformationMatrices). The mesh is
    /// built on the main thread; until it exists nothing is drawn rather than a flash of bones.
    /// </summary>
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (Api is not ICoreClientAPI capi) return base.OnTesselation(mesher, tessThreadTesselator);
        if (!dataLoaded) return true;
        if (headData == null) return base.OnTesselation(mesher, tessThreadTesselator);
        var def = capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(headData);
        if (def == null) return base.OnTesselation(mesher, tessThreadTesselator);
        var cached = MountMesher.TryGetHeadMesh(capi, headData, def, true);
        if (cached == null)
        {
            var data = headData;
            var pos = Pos.Copy();
            capi.Event.EnqueueMainThreadTask(() =>
            {
                if (MountMesher.GetHeadMesh(capi, data, def, true) != null) capi.World.BlockAccessor.MarkBlockDirty(pos);
            }, "taxidermy-skinned-head-mesh");
            return true;
        }
        var mesh = ItemHead.ForGround(cached.Clone());
        mesh.MatrixTransform(new Matrixf().Translate(0.5f, 0, 0.5f).RotateY(meshAngle).Translate(-0.5f, 0, -0.5f).Values);
        mesher.AddMeshData(mesh);
        return true;
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        // Vanilla's container plays the chest lid (Calm: "remove the sound as it has a chest sound");
        // an empty SoundAttributes is what the window itself defaults to - it plays nothing.
        OpenSound = new SoundAttributes();
        CloseSound = new SoundAttributes();
        // Bones only once the window is CLOSED and empty, as vanilla's carcass does (Calm, 2026-09-26:
        // turning into bones on the last slot change shut the window and dropped the item in hand).
        if (api.Side == EnumAppSide.Server) Inventory.OnInventoryClosed += _ => BecomeBonesIfEmpty();
    }

    /// <summary>
    /// The window grows to fit, as vanilla's harvest window does (it adds a slot per extra drop):
    /// the block's 4 slots are a minimum, so a long headSkinningDrops list is never cut short.
    /// </summary>
    public void Fill(IEnumerable<ItemStack> stacks)
    {
        var list = stacks.ToList();
        if (list.Count > Inventory.Count && Inventory is InventoryGeneric grown) grown.AddSlots(list.Count - Inventory.Count);
        for (int i = 0; i < list.Count && i < Inventory.Count; i++) Inventory[i].Itemstack = list[i];
        MarkDirty(true);
        BecomeBonesIfEmpty();
    }

    /// <summary>
    /// Loading (a reload, or the server's sync to the client) builds the block's 4 slots and reads
    /// only those; a grown window would lose the rest. Grow to the saved count and read again.
    /// </summary>
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        headData = tree.GetTreeAttribute(HeadKey);
        meshAngle = tree.GetFloat(AngleKey);
        dataLoaded = true;
        var invTree = tree.GetTreeAttribute("inventory");
        int saved = invTree?.GetInt("qslots") ?? 0;
        if (saved > Inventory.Count && Inventory is InventoryGeneric grown)
        {
            grown.AddSlots(saved - Inventory.Count);
            Inventory.FromTreeAttributes(invTree);
        }
    }

    private void BecomeBonesIfEmpty()
    {
        if (!Inventory.Empty) return;
        // After the slot change has finished - never swap the block out from under it.
        Api.World.RegisterCallback(_ =>
        {
            if (Api.World.BlockAccessor.GetBlockEntity(Pos) != this || !Inventory.Empty) return;
            // Gone, not a bone pile (Calm, 2026-09-30). Only skinned heads from before that day still reach here.
            Api.World.BlockAccessor.SetBlock(0, Pos);
        }, 0);
    }
}

/// <summary>The block of <see cref="BESkinnedHead"/>: right-click opens it, as a carcass is opened.</summary>
public sealed class BlockSkinnedHead : Block
{
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BESkinnedHead head)
            return head.OnPlayerRightClick(byPlayer, blockSel);
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }
}
