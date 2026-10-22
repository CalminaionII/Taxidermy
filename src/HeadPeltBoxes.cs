using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>Client to server: the box a head or pelt in a ground storage / antler mount is drawn in (that block's frame).</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class DisplayBoxPacket
{
    public int X, Y, Z;
    public float X1, Y1, Z1, X2, Y2, Z2;
}

/// <summary>
/// AUTO BOXES TEST (2026-10-05, Calm: "do the heads next and then the pelts"): a head or pelt in the
/// game's ground storage or on its antler mount gets a box round what is actually drawn, clickable
/// everywhere it reaches - like the mounts.
///
/// Those are VANILLA blocks, so this is the glue the mount did not need:
/// * MEASURED from exactly what the block draws - the item's cached mesh times the block's own
///   transform for that slot (BlockEntityDisplay.OnTesselation adds getMesh(slot) with
///   tfMatrices[index]; public vssurvivalmod BEContainerDisplay.cs), so it follows ground storage's
///   turn, the antler mount's wall and the head's Adjust numbers with no maths of our own. Client
///   only - the server has no meshes. Up to 6 blocks each way (Calm, 2026-10-06); never thinner than 1/16.
/// * The client sends it (DisplayBoxPacket); the server checks build permission and that the block
///   still holds a Taxidermy head or pelt, then places fillers that CARRY the box
///   (AutoBoxes.SyncCarried) in the empty spaces it reaches.
/// * The block's own outline: a Harmony postfix on GetSelectionBoxes gives the whole box (the
///   antler mount keeps its plaque too). Fillers check every 2 s that the head/pelt is still there.
/// </summary>
public static class HeadPeltBoxes
{
    private const float Max = 6f, Min = 1f / 16f;
    /// <summary>The whole box is solid (Calm, 2026-10-06: "try collision for now for the whole thing").</summary>
    private const bool Solid = true;

    /// <summary>Both sides: the owner block's own solid piece, by position (from the measurement, the packet, or a loaded filler).</summary>
    private static readonly ConcurrentDictionary<BlockPos, Cuboidf[]> OwnPiece = new();

    private static void Remember(BlockPos pos, Cuboidf box)
    {
        if (Solid) OwnPiece[pos.Copy()] = AutoBoxes.PieceAt(box, (0, 0, 0));
    }   // same cap as the mounts (Calm, 2026-10-06)

    private static readonly MethodInfo GetMeshMethod = AccessTools.Method(typeof(BlockEntityDisplay), "getMesh");
    private static readonly FieldInfo TfField = AccessTools.Field(typeof(BlockEntityDisplay), "tfMatrices");

    private static ICoreClientAPI capi;
    private static readonly ConcurrentDictionary<BlockPos, int> Pending = new();

    private sealed record Measured(Cuboidf Box, MeshData Mesh, float[] Tf, ItemStack Stack);
    private static readonly Dictionary<BlockPos, Measured> MeasuredAt = new();   // client main thread only

    public static bool IsHolder(Block block) => block is BlockGroundStorage || block is BlockAntlerMount;

    /// <summary>The slot holding a Taxidermy head or pelt in a ground storage / antler mount, or -1.</summary>
    public static int HeldSlot(BlockEntity be)
    {
        if (be is not (BlockEntityGroundStorage or BlockEntityAntlerMount) || be is not BlockEntityDisplay display) return -1;
        var inv = display.Inventory;
        for (int i = 0; i < inv.Count; i++)
            if (inv[i]?.Itemstack?.Collectible is ItemHead or ItemTaxidermyHide) return i;
        return -1;
    }

    /// <summary>Both sides, from mod Start: what keeps a carried filler alive, and the message type.</summary>
    public static void Start(ICoreAPI api)
    {
        AutoBoxes.OwnerStillValid = (ba, pos) => HeldSlot(ba.GetBlockEntity(pos)) >= 0;
        AutoBoxes.CarriedSeen = (owner, box, solid) => { if (solid) Remember(owner, box); };
    }

    public static void Patch(Harmony harmony, ICoreAPI api)
    {
        var postfix = new HarmonyMethod(typeof(HeadPeltBoxes), nameof(SelectionPostfix));
        var done = new HashSet<MethodBase>();
        foreach (var type in new[] { typeof(BlockGroundStorage), typeof(BlockAntlerMount) })
        {
            var method = AccessTools.Method(type, nameof(Block.GetSelectionBoxes), [typeof(IBlockAccessor), typeof(BlockPos)]);
            if (method == null) { api.Logger.Error("[Taxidermy] {0}.GetSelectionBoxes not found - head/pelt boxes stay vanilla", type.Name); continue; }
            if (done.Add(method)) harmony.Patch(method, postfix: postfix);
        }
        // Only the aimed box is outlined on an antler mount holding one of our heads (head box + plaque box).
        // BlockAntlerMount does not override DoPartialSelection - Harmony can only patch the method where it is DECLARED
        // (Block's; the postfix checks for the antler mount). A failed patch must never stop the mod starting: on
        // 2026-10-06 it did, the server never registered the network channel and the client crashed sending a box.
        try
        {
            var partial = AccessTools.Method(typeof(BlockAntlerMount), nameof(Block.DoPartialSelection), [typeof(IWorldAccessor), typeof(BlockPos)]);
            if (partial != null && partial.DeclaringType != null && partial.DeclaringType != typeof(BlockAntlerMount))
                partial = AccessTools.DeclaredMethod(partial.DeclaringType, nameof(Block.DoPartialSelection), [typeof(IWorldAccessor), typeof(BlockPos)]);
            if (partial == null) api.Logger.Error("[Taxidermy] DoPartialSelection not found - head and plaque outline together");
            else harmony.Patch(partial, postfix: new HarmonyMethod(typeof(HeadPeltBoxes), nameof(PartialPostfix)));
        }
        catch (Exception e)
        {
            api.Logger.Error("[Taxidermy] Could not patch DoPartialSelection - head and plaque outline together: {0}", e.Message);
        }

        var solidPostfix = new HarmonyMethod(typeof(HeadPeltBoxes), nameof(CollisionPostfix));
        done.Clear();
        foreach (var type in new[] { typeof(BlockGroundStorage), typeof(BlockAntlerMount) })
        {
            var method = AccessTools.Method(type, nameof(Block.GetCollisionBoxes), [typeof(IBlockAccessor), typeof(BlockPos)]);
            if (method == null) { api.Logger.Error("[Taxidermy] {0}.GetCollisionBoxes not found - head/pelt collision stays vanilla", type.Name); continue; }
            if (done.Add(method)) harmony.Patch(method, postfix: solidPostfix);
        }
        if (GetMeshMethod == null || TfField == null) api.Logger.Error("[Taxidermy] BlockEntityDisplay.getMesh/tfMatrices not found - head/pelt boxes cannot be measured");
    }

    // ── Client ───────────────────────────────────────────────────────────────

    public static void StartClient(ICoreClientAPI api)
    {
        capi = api;
        MeasuredAt.Clear();
        Pending.Clear();   // static: nothing queued in a previous world carries over
        api.Event.RegisterGameTickListener(_ => ProcessPending(), 500);
    }

    /// <summary>From GenMesh (tesselation thread): our real mesh was just handed to this block - measure it soon.</summary>
    public static void Notice(BlockPos pos)
    {
        if (pos != null && capi != null) Pending[pos.Copy()] = 0;
    }

    private static void ProcessPending()
    {
        foreach (var pos in Pending.Keys.ToArray())
        {
            var m = Measure(pos, out bool gone);
            if (m != null)
            {
                // not connected yet (a world still loading): keep it queued and send it on a later tick
                if (!Send(pos, m.Box)) continue;
                MeasuredAt[pos] = m;
                Remember(pos, m.Box);
                Pending.TryRemove(pos, out _);
            }
            else if (gone || Pending[pos] > 20) Pending.TryRemove(pos, out _);
            else Pending[pos]++;
        }
    }

    private static Measured Measure(BlockPos pos, out bool gone)
    {
        gone = false;
        if (capi == null || GetMeshMethod == null || TfField == null) { gone = true; return null; }
        var be = capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityDisplay;
        int i = HeldSlot(be);
        if (i < 0) { gone = true; return null; }
        var slot = be.Inventory[i];
        // our mesh not built yet: the display is still showing its stand-in
        if ((slot.Itemstack.Collectible as IContainedMeshSource)?.GetMeshCacheKey(slot)?.EndsWith(DisplayMeshes.Wait) == true) return null;
        var tfs = TfField.GetValue(be) as float[][];
        var mesh = GetMeshMethod.Invoke(be, [slot]) as MeshData;
        if (tfs == null || i >= tfs.Length || tfs[i] == null || mesh == null || mesh.VerticesCount == 0) return null;

        float[] t = tfs[i];   // column-major, as Mat4f / MeshData.MatrixTransform
        float x1 = float.MaxValue, y1 = float.MaxValue, z1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue, z2 = float.MinValue;
        var xyz = mesh.xyz;
        for (int v = 0; v < mesh.VerticesCount; v++)
        {
            float x = xyz[v * 3], y = xyz[v * 3 + 1], z = xyz[v * 3 + 2];
            float wx = t[0] * x + t[4] * y + t[8] * z + t[12];
            float wy = t[1] * x + t[5] * y + t[9] * z + t[13];
            float wz = t[2] * x + t[6] * y + t[10] * z + t[14];
            x1 = Math.Min(x1, wx); x2 = Math.Max(x2, wx); y1 = Math.Min(y1, wy); y2 = Math.Max(y2, wy); z1 = Math.Min(z1, wz); z2 = Math.Max(z2, wz);
        }
        (x1, x2) = Fit(x1, x2); (y1, y2) = Fit(y1, y2); (z1, z2) = Fit(z1, z2);
        return new Measured(new Cuboidf(x1, y1, z1, x2, y2, z2), mesh, t, slot.Itemstack);
    }

    /// <summary>Centred, between 1/16 and 3 blocks.</summary>
    private static (float, float) Fit(float lo, float hi)
    {
        float c = (lo + hi) / 2, half = Math.Clamp((hi - lo) / 2, Min / 2, Max / 2);
        return (c - half, c + half);
    }

    /// <summary>
    /// Every NEW measurement goes to the server - no "same box as last time" skip (2026-10-06). The skip was what made a
    /// head forget its box: take a head off (the server drops its fillers) and put one back in the same spot, and the
    /// new head measured the same as the old one, so nothing was sent and it never got its fillers again. A measurement
    /// is only made when the head, its mesh or its transform changed, so this is not sent every frame.
    /// </summary>
    /// <returns>false when the channel is not there or not connected yet (GetChannel returns null while a world loads -
    /// a crash on 2026-10-06 once the skip above was gone); the caller tries again later.</returns>
    private static bool Send(BlockPos pos, Cuboidf box)
    {
        var channel = capi?.Network?.GetChannel(TaxidermyModSystem.Channel);
        if (channel == null || !channel.Connected) return false;
        channel.SendPacket(new DisplayBoxPacket
        {
            X = pos.X, Y = pos.Y, Z = pos.Z,
            X1 = box.X1, Y1 = box.Y1, Z1 = box.Z1, X2 = box.X2, Y2 = box.Y2, Z2 = box.Z2,
        });
        return true;
    }

    /// <summary>
    /// The block's own outline: the whole measured box (the antler mount keeps its plaque in it).
    /// Client main thread only; re-measures when the mesh or the block's transform has changed
    /// (a turn, a different pelt, the Adjust window).
    /// </summary>
    public static void SelectionPostfix(Block __instance, IBlockAccessor __0, BlockPos __1, ref Cuboidf[] __result)
    {
        IBlockAccessor blockAccessor = __0;   // positional: the overrides may name them differently
        BlockPos pos = __1;
        if (capi == null || !IsHolder(__instance) || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return;
        var be = blockAccessor.GetBlockEntity(pos) as BlockEntityDisplay;
        int i = HeldSlot(be);
        if (i < 0) { MeasuredAt.Remove(pos); return; }   // picked up: forget it, so whatever goes here next is measured afresh
        if (!MeasuredAt.TryGetValue(pos, out var m))
        {
            // measure on first look - a head whose mesh was already built (another of the same animal, or one put
            // back) never comes through GenMesh, so nothing else would start it
            Notice(pos);
            return;
        }
        var tfs = TfField.GetValue(be) as float[][];
        var mesh = GetMeshMethod.Invoke(be, [be.Inventory[i]]) as MeshData;
        if (tfs == null || i >= tfs.Length || !ReferenceEquals(tfs[i], m.Tf) || !ReferenceEquals(mesh, m.Mesh)
            || !ReferenceEquals(be.Inventory[i].Itemstack, m.Stack))   // another head (or the same one put back)
        {
            var again = Measure(pos, out _);
            if (again == null) return;
            if (!Send(pos, again.Box)) { Notice(pos); return; }   // not connected yet: the queue sends it later
            MeasuredAt[pos] = m = again;
            Remember(pos, again.Box);
        }
        // The antler mount: the head's own box FIRST, then the plaque's own (vanilla's) - two outlines, only the one aimed
        // at drawn (PartialPostfix). One box round both made a small head's outline as wide as the plaque (Calm, 2026-10-06).
        if (__instance is BlockAntlerMount && __result is { Length: > 0 }) __result = [m.Box, .. __result];
        else __result = [m.Box];
    }

    /// <summary>An antler mount holding one of our heads outlines only the box aimed at (the head, or the plaque).</summary>
    public static void PartialPostfix(Block __instance, IWorldAccessor __0, BlockPos __1, ref bool __result)
    {
        if (__result || __instance is not BlockAntlerMount || __0 == null || __1 == null) return;
        if (HeldSlot(__0.BlockAccessor.GetBlockEntity(__1)) >= 0) __result = true;
    }

    /// <summary>
    /// What is solid in the owner's own space: its piece of the box (any thread - physics asks). The
    /// antler mount keeps its plaque as well; ground storage's own plate is replaced.
    /// </summary>
    public static void CollisionPostfix(Block __instance, IBlockAccessor __0, BlockPos __1, ref Cuboidf[] __result)
    {
        if (!Solid || !IsHolder(__instance) || !OwnPiece.TryGetValue(__1, out var own) || own.Length == 0) return;
        if (HeldSlot(__0.GetBlockEntity(__1)) < 0) return;
        __result = __instance is BlockAntlerMount && __result is { Length: > 0 } ? [.. __result, .. own] : own;
    }

    /// <summary>
    /// Leaving a world (either side): these are static, so without this a head in the NEXT world at the
    /// same coordinates would be solid in this world's box until it was measured again.
    /// </summary>
    public static void Dispose(EnumAppSide side)
    {
        OwnPiece.Clear();
        if (side != EnumAppSide.Client) return;
        MeasuredAt.Clear();   // client main thread only - never from the singleplayer server's thread
        Pending.Clear();
        capi = null;
    }

    // ── Server ───────────────────────────────────────────────────────────────

    public static void StartServer(ICoreServerAPI sapi)
    {
        sapi.Network.GetChannel(TaxidermyModSystem.Channel).SetMessageHandler<DisplayBoxPacket>((player, p) => OnBox(sapi, player, p));
    }

    private static void OnBox(ICoreServerAPI sapi, IServerPlayer player, DisplayBoxPacket p)
    {
        var pos = new BlockPos(p.X, p.Y, p.Z, player.Entity?.Pos.Dimension ?? 0);
        if (HeldSlot(sapi.World.BlockAccessor.GetBlockEntity(pos)) < 0) return;
        if (sapi.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        float[] v = [p.X1, p.Y1, p.Z1, p.X2, p.Y2, p.Z2];
        if (v.Any(f => !float.IsFinite(f) || Math.Abs(f) > Max + 1)) return;
        var (x1, x2) = Fit(Math.Min(p.X1, p.X2), Math.Max(p.X1, p.X2));
        var (y1, y2) = Fit(Math.Min(p.Y1, p.Y2), Math.Max(p.Y1, p.Y2));
        var (z1, z2) = Fit(Math.Min(p.Z1, p.Z2), Math.Max(p.Z1, p.Z2));
        var box = new Cuboidf(x1, y1, z1, x2, y2, z2);
        Remember(pos, box);
        AutoBoxes.SyncCarried(sapi.World, pos, box, Solid, byPlayer: player);
    }
}
