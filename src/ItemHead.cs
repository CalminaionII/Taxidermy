using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// The animal's head - raw off the carcass, preserved out of the borax barrel. One class, one
/// itemtype with a <c>state</c> variant. Renders as the head lifted out of the animal's own
/// model (eyes removed, antlers on), and can be set down through vanilla ground storage.
///
/// Calm's decision of 2026-09-21: a head instead of a pelt or a body. Same process, nothing
/// to model, and a preserved head is also what a wall trophy will be made of.
/// </summary>
public sealed class ItemHead : Item, IContainedMeshSource
{
    public bool IsPreserved => Variant["state"] == "preserved";

    public override string GetHeldItemName(ItemStack itemStack)
    {
        var data = Specimen.Of(itemStack);
        if (data == null) return base.GetHeldItemName(itemStack);
        return Lang.Get("taxidermy:item-head-" + Variant["state"] + "-of", Specimen.AnimalName(data));
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var data = Specimen.Of(inSlot.Itemstack);
        if (data == null)
        {
            dsc.AppendLine(Lang.Get("taxidermy:specimen-empty"));
            return;
        }
        var def = api.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        string size = Lang.Get("taxidermy:size-" + (def?.HideSize ?? "medium"));
        dsc.AppendLine(Lang.Get("taxidermy:head-" + Variant["state"] + "-help", size));
        var attachments = Specimen.Attachments(data);
        if (attachments.Length > 0)
            dsc.AppendLine(Lang.Get("taxidermy:specimen-with", string.Join(", ", attachments.Select(c => ItemMount.AttachmentName(world, c)))));
    }

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        var data = Specimen.Of(itemstack);
        var def = data == null ? null : capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        if (def != null)
        {
            var meshRef = MountMesher.GetHeadItemMesh(capi, data, def);
            if (meshRef != null) renderinfo.ModelRef = meshRef;
        }
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
    }

    /// <summary>
    /// Ground storage and vanilla's antler mount both ask here, from the TESSELATION thread. The head is built on the main thread
    /// (atlas inserts are GPU work); if it is not cached yet, queue the build and a redraw and
    /// let vanilla show the default item mesh for a frame - the same dance BEGroundStorage
    /// does for its own stacking models.
    /// </summary>
    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        if (api is not ICoreClientAPI capi) return null;
        var data = Specimen.Of(slot.Itemstack);
        var def = data == null ? null : capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        if (def == null) return null;

        bool onAntlerMount = atBlockPos != null && capi.World.BlockAccessor.GetBlock(atBlockPos) is BlockAntlerMount;
        var cached = MountMesher.TryGetHeadMesh(capi, data, def);
        if (cached == null && Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) cached = MountMesher.GetHeadMesh(capi, data, def);
        if (cached != null) return onAntlerMount ? ForAntlerMount(cached.Clone(), data) : ForGround(cached.Clone());

        // Returning null makes the display block cache ITS fallback under our key (BEContainerDisplay
        // MeshCache, an ObjectCache dictionary per block-entity class) and never ask again - so
        // once the head exists, evict our key from every display cache before the redraw.
        var pos = atBlockPos?.Copy();
        string cacheKey = GetMeshCacheKey(slot);
        capi.Event.EnqueueMainThreadTask(() =>
        {
            MountMesher.GetHeadMesh(capi, data, def);
            foreach (var entry in capi.ObjectCache.Where(kv => kv.Key.StartsWith("meshesDisplay-")).ToArray())
                (entry.Value as Dictionary<string, MeshData>)?.Remove(cacheKey);
            if (pos != null)
            {
                if (capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityDisplay display) display.updateMeshes();
                capi.World.BlockAccessor.MarkBlockDirty(pos);
            }
        }, "taxidermy-head-mesh");
        return null;
    }

    /// <summary>
    /// Vanilla's antler mount draws its item rotated by (meshAngle - 90 degrees) about the block
    /// centre, and at meshAngle 0 its plaque backs onto the NORTH wall, facing south. Our head
    /// faces -X at rest, so before that rotation it has to face +X with its back at the west
    /// edge - then the mount's own rotation puts it facing out from whichever wall it is on.
    /// Preserved heads carry <c>antlerMountable: true</c>, which is all the mount asks for.
    /// </summary>
    private static MeshData ForAntlerMount(MeshData m, ITreeAttribute data)
    {
        m.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, GameMath.PI + data.GetFloat("rot"), 0);
        float minX = float.MaxValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < m.VerticesCount; i++)
        {
            minX = Math.Min(minX, m.xyz[i * 3]);
            minY = Math.Min(minY, m.xyz[i * 3 + 1]);
            maxY = Math.Max(maxY, m.xyz[i * 3 + 1]);
        }
        // Adjust window: sideways is along the wall (z here, before the mount's own turn), up/down
        // is y, back/forward is x - the head's depth axis at this point.
        // Signs confirmed in game 2026-09-22: positive back/forward comes OUT from the wall.
        if (m.VerticesCount > 0) m.Translate(1f / 16 - minX - data.GetFloat("offz"), 0.5f - (minY + maxY) / 2 + data.GetFloat("offy"), data.GetFloat("offx"));
        // Tilt: nose up/down about the head's own centre. The head faces +X here, so the
        // horizontal axis across it is Z.
        float tilt = data.GetFloat("tilt");
        if (tilt != 0 && m.VerticesCount > 0)
        {
            float cx = float.MaxValue, cX = float.MinValue, cy = float.MaxValue, cY = float.MinValue, cz = float.MaxValue, cZ = float.MinValue;
            for (int i = 0; i < m.VerticesCount; i++)
            {
                cx = Math.Min(cx, m.xyz[i * 3]); cX = Math.Max(cX, m.xyz[i * 3]);
                cy = Math.Min(cy, m.xyz[i * 3 + 1]); cY = Math.Max(cY, m.xyz[i * 3 + 1]);
                cz = Math.Min(cz, m.xyz[i * 3 + 2]); cZ = Math.Max(cZ, m.xyz[i * 3 + 2]);
            }
            m.Rotate(new Vec3f((cx + cX) / 2, (cy + cY) / 2, (cz + cZ) / 2), 0, 0, tilt);
        }
        return m;
    }

    /// <summary>
    /// Ground storage sets its mesh angle to atan2(player - block), snapped to 90 degrees, and
    /// rotates the item by it - so an item facing +Z at rest ends up facing the player. The head
    /// faces -X at rest: a quarter turn first (Calm, 2026-09-21: heads on the ground should
    /// face the player like the mounts do).
    /// </summary>
    private static MeshData ForGround(MeshData m)
    {
        m.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, GameMath.PIHALF, 0);
        return m;
    }

    public string GetMeshCacheKey(ItemSlot slot)
    {
        var data = Specimen.Of(slot.Itemstack);
        return Code + "|" + (data == null ? "" : MountMesher.IdentityKey(data) + "|" + data.GetFloat("offx") + "|" + data.GetFloat("offy") + "|" + data.GetFloat("offz") + "|" + data.GetFloat("rot") + "|" + data.GetFloat("tilt"));
    }
}
