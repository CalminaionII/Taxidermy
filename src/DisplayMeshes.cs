using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// A display block (ground storage, vanilla's antler mount) asks our heads and hides for their mesh
/// on the TESSELATION thread. Ours are built on the main thread (atlas inserts), so the first ask
/// after a load gets null, and the display caches vanilla's default mesh for the item instead.
///
/// Vanilla keeps one cache per block-entity class, <c>"meshesDisplay-" + ClassCode</c>, and asks
/// the item's GetMeshCacheKey on EVERY lookup (vssurvivalmod BEContainerDisplay.getOrCreateMesh).
/// So while our mesh is unbuilt the key ends in <see cref="Wait"/>: the default mesh is cached
/// under that key, and once ours exists the key changes, the lookup misses and the display asks
/// GenMesh again. Nothing of vanilla's is ever removed.
///
/// Until 2026-10-03 this class evicted our key from vanilla's dictionaries instead - from the main
/// thread while the tesselation thread was adding to them, which crashed a client joining a world
/// full of heads ("Collection was modified", Calm, 2026-10-03).
///
/// Left open: a head that finishes between vanilla's two key lookups in one getOrCreateMesh gets
/// the default mesh under its real key - the default mesh until the next relog, never a crash.
/// </summary>
internal static class DisplayMeshes
{
    public const string Wait = "|wait";

    /// <summary>Main thread: redraw the display at <paramref name="pos"/> now that our mesh exists.</summary>
    public static void Redraw(ICoreClientAPI capi, BlockPos pos)
    {
        if (pos == null) return;
        if (capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityDisplay display) display.updateMeshes();
        capi.World.BlockAccessor.MarkBlockDirty(pos);
    }
}
