using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// Old worlds only: skinning no longer makes this block (BehaviorHeadSkinning drops everything on the ground). What was
/// left of a skinned head: vanilla's generic container, take-only through its <c>retrieveOnly</c> attribute, drawn as
/// the head it was. It goes once it is empty and its window is closed.
/// </summary>
public sealed class BESkinnedHead : BlockEntityGenericContainer
{
    private const string HeadKey = "taxidermyHead", AngleKey = "taxidermyHeadAngle";

    /// <summary>The skinned head's specimen tree, and ground storage's turn it lay at.</summary>
    private ITreeAttribute headData;
    private float meshAngle;

    /// <summary>
    /// False until this block entity's data has arrived. The client gets the block a moment before its data, so nothing
    /// is drawn until it knows what it is, or the bone pile would flash first.
    /// </summary>
    private bool dataLoaded;

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (headData != null) tree[HeadKey] = headData;
        tree.SetFloat(AngleKey, meshAngle);
    }

    /// <summary>
    /// The head exactly as ground storage drew it - the same flat mesh (ItemHead.GenMesh), turned by the same angle
    /// about the block centre (BEGroundStorage.genTransformationMatrices). The mesh is built on the main thread; until
    /// it exists nothing is drawn rather than a flash of bones.
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
            // the flat (ground) version only - kept apart from the both-versions build heads queue
            DisplayMeshes.Queue(capi, "headflat|" + MountMesher.IdentityKey(data) + "|" + def.Code + "|" + def.Scale, Pos, () => MountMesher.GetHeadMesh(capi, data, def, true));
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
        // An empty SoundAttributes plays nothing, instead of vanilla's chest lid.
        OpenSound = new SoundAttributes();
        CloseSound = new SoundAttributes();
        // Only once the window is closed and empty, as vanilla's carcass does: acting on the last slot change would
        // shut the window and drop the item in hand.
        if (api.Side == EnumAppSide.Server) Inventory.OnInventoryClosed += _ => BecomeBonesIfEmpty();
    }

    /// <summary>
    /// Loading (a reload, or the server's sync to the client) builds the block's 4 slots and reads only those; a grown
    /// window would lose the rest. Grow to the saved count and read again.
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
            // Gone, rather than a bone pile.
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
