using System;
using System.Collections.Concurrent;
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
/// Looked at again 2026-10-10 (review): a "say wait once more" marker would cover vanilla's displays but
/// can leave a display that asks the key only once (Universal Display) on the stand-in for good - worse
/// than this. The real fix is every head's textures in the atlas at load, so a head can be built on any
/// thread at once; not done for 1.0.4.
///
/// ONE QUEUED BUILD PER MODEL (2026-10-10, review): heads, pelts and skinned heads used to queue a build
/// and a redraw per ask. <see cref="Queue"/> keeps one build per model key and redraws every display that
/// asked meanwhile, once it is done.
/// </summary>
internal static class DisplayMeshes
{
    public const string Wait = "|wait";

    static readonly ConcurrentDictionary<string, byte> Queued = new();
    static readonly ConcurrentDictionary<string, ConcurrentDictionary<BlockPos, byte>> Waiting = new();

    /// <summary>
    /// Any thread: build the model <paramref name="key"/> names on the main thread - once, however many displays ask -
    /// and redraw every one that asked (<paramref name="pos"/>) when it is done.
    /// </summary>
    public static void Queue(ICoreClientAPI capi, string key, BlockPos pos, Action build)
    {
        var waiting = Waiting.GetOrAdd(key, _ => new ConcurrentDictionary<BlockPos, byte>());
        if (pos != null) waiting[pos.Copy()] = 0;
        if (!Queued.TryAdd(key, 0)) return;
        capi.Event.EnqueueMainThreadTask(() =>
        {
            try { build(); }
            finally
            {
                Queued.TryRemove(key, out _);
                if (Waiting.TryRemove(key, out var redraw))
                    foreach (var p in redraw.Keys) Redraw(capi, p);
            }
        }, "taxidermy-display-mesh");
    }

    /// <summary>World start: nothing queued in a world we left is still coming.</summary>
    public static void Clear()
    {
        Queued.Clear();
        Waiting.Clear();
    }

    /// <summary>Main thread: redraw the display at <paramref name="pos"/> now that our mesh exists.</summary>
    public static void Redraw(ICoreClientAPI capi, BlockPos pos)
    {
        if (pos == null) return;
        if (capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityDisplay display) display.updateMeshes();
        capi.World.BlockAccessor.MarkBlockDirty(pos);
    }
}
