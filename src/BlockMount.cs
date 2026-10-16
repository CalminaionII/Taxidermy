using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// The placed mount. It has no shape of its own - it stands on whatever is under it - and the
/// block entity renders the animal. Everything about it is set in the Adjust window (sneak +
/// right-click, empty hand); plain right-click picks it up. The wrench modes it had at first
/// were removed on 2026-09-22 as redundant once the window existed.
/// </summary>
public sealed class BlockMount : Block
{
    /// <summary>
    /// Empty hand: sneak + right-click opens the Adjust window (Calm, 2026-09-21: a menu rather
    /// than wrench interactions - pose and position in one place, per mount), plain right-click
    /// picks the mount up. Anything held falls through to the item, which is how the wrench
    /// keeps working, since block interaction is tried before the held item's use.
    /// </summary>
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (slot == null || !slot.Empty) return base.OnBlockInteractStart(world, byPlayer, blockSel);
        if (byPlayer.WorldData.EntityControls.ShiftKey)
        {
            // The server decides and tells the client to open the window (claims, 2026-09-23).
            if (world.Side == EnumAppSide.Server)
                world.Api.ModLoader.GetModSystem<TaxidermyModSystem>().RequestAdjust(byPlayer, blockSel.Position, false);
            return true;
        }
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
        {
            slot.MarkDirty();
            return false;
        }
        if (world.Side == EnumAppSide.Server)
        {
            // An orphaned mount is destroyed, not picked up (Calm, 2026-09-22): its animal's mod
            // is gone, so the item would be a permanent dud in the inventory. See Orphaned().
            var stack = Orphaned(world, blockSel.Position) ? null : StackAt(world, blockSel.Position);
            world.BlockAccessor.SetBlock(0, blockSel.Position);
            if (stack != null && !byPlayer.InventoryManager.TryGiveItemstack(stack)) world.SpawnItemEntity(stack, blockSel.Position.ToVec3d().Add(0.5, 0.5, 0.5));
            world.PlaySoundAt(new AssetLocation("game:sounds/block/leather"), blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5, null);
        }
        return true;
    }

    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        base.OnNeighbourBlockChange(world, pos, neibpos);
        if (neibpos.Y == pos.Y - 1) world.BlockAccessor.GetBlockEntity<BEMount>(pos)?.StandChanged();
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var be = blockAccessor.GetBlockEntity<BEMount>(pos);
        if (be?.OwnBoxes() is { Length: > 0 } own) return own;
        var box = be?.Box;
        return box != null ? [box] : base.GetSelectionBoxes(blockAccessor, pos);
    }

    // Solid only once a Reach has arrived (auto boxes test, Calm 2026-10-06: "try collision for the whole thing").
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) =>
        blockAccessor.GetBlockEntity<BEMount>(pos)?.AutoCollisionAt(pos) ?? [];

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) =>
        Orphaned(world, pos) ? [] : [StackAt(world, pos)];

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) =>
        Orphaned(world, pos) ? null : StackAt(world, pos);

    /// <summary>
    /// True when the animal this mount was made from no longer exists - its mod is not
    /// installed - so there is nothing to build a model from and the block is showing the
    /// placeholder cube. Such a mount breaks into nothing: the item could never render or be
    /// placed as itself again, and leaving it in an inventory would only puzzle its owner.
    /// The block is otherwise untouched, so putting the animal's mod back restores it whole.
    /// </summary>
    private static bool Orphaned(IWorldAccessor world, BlockPos pos)
    {
        var data = world.BlockAccessor.GetBlockEntity<BEMount>(pos)?.Data;
        if (data == null) return true;
        if (world.GetEntityType(new AssetLocation(data.GetString("entityCode"))) == null) return true;
        // The animal is there but nothing says how to mount it any more - its definition file
        // was removed. Same outcome: no model, so nothing worth keeping.
        return world.Api.ModLoader.GetModSystem<TaxidermyModSystem>()?.Find(data) == null;
    }

    private ItemStack StackAt(IWorldAccessor world, BlockPos pos)
    {
        var stack = new ItemStack(world.GetItem(new AssetLocation(Specimen.MountItem)));
        if (world.BlockAccessor.GetBlockEntity<BEMount>(pos)?.Data is { } data) stack.Attributes[Specimen.Key] = data.Clone();
        return stack;
    }

    // OnPickBlock returns the specimen item, so without this the placed block would announce
    // itself under the item's name (shared reference sect.12b).
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        var data = world.BlockAccessor.GetBlockEntity<BEMount>(pos)?.Data;
        return data == null ? base.GetPlacedBlockName(world, pos) : Lang.Get("taxidermy:block-mount-of", Specimen.AnimalNameNoSex(data));
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var be = world.BlockAccessor.GetBlockEntity<BEMount>(pos);
        if (be?.Data == null) return base.GetPlacedBlockInfo(world, pos, forPlayer);
        var sb = new StringBuilder();
        if (ItemTaxidermyHide.SexLine(world, be.Data) is { } sex) sb.AppendLine(sex);
        sb.AppendLine(Lang.Get("taxidermy:pose-label", Lang.Get("taxidermy:pose-" + be.Data.GetString("pose"))));
        // Controls: the interaction help overlay and the handbook page (Calm, 2026-09-29).
        sb.AppendLine(Lang.Get("taxidermy:mount-see-handbook"));
        return sb.ToString();
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer) =>
    [
        new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-pickup", MouseButton = EnumMouseButton.Right, RequireFreeHand = true },
        new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-adjust", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift", RequireFreeHand = true },
    ];
}

public sealed class BEMount : BlockEntity, IAutoBoxesOwner
{
    // ── AUTO BOXES TEST (2026-10-05, Calm): the whole animal clickable ──────────────────────
    // The client measures the built model (as it always has for Box) WITHOUT the one-block cap,
    // up to ReachMax (6) each way, and tells the server; the server checks build permission, keeps it
    // as Reach and puts fillers (src/autoboxes/) in the EMPTY spaces it reaches. Box stays the
    // fallback until a Reach has arrived.
    public const int ReachPacketId = 4202;
    /// <summary>The whole box is solid (Calm, 2026-10-06: "try collision for now for the whole thing"); false = walk-through as before.</summary>
    public const bool ReachSolid = true;
    public const float ReachMax = 6f;   // Calm, 2026-10-06: 6 long, 6 high, 6 wide (the big elephants)

    /// <summary>The whole model's box (block frame, may reach past the block), as the server accepted it. Null until measured.</summary>
    public Cuboidf Reach { get; private set; }
    private Dictionary<(int x, int y, int z), Cuboidf> reachPieces = new();

    /// <summary>
    /// What is SOLID: one box per moving part of the posed model (body, each leg, neck, head, tail), as the server accepted
    /// them - so you can walk under an elephant and between its legs (2026-10-06: a player wanted collision, but closer
    /// than one box round the whole animal). The outline stays the whole Reach. Empty = the whole Reach is solid (a mount
    /// measured before this, or a model with no parts).
    /// </summary>
    public Cuboidf[] Parts { get; private set; } = [];
    private Dictionary<(int x, int y, int z), Cuboidf[]> solidPieces = new();
    private const int MaxParts = 64;
    private List<(int x, int y, int z)> heldFillers = new();

    // THE WHOLE BOX FROM EVERY PART (Calm, 2026-10-05: "the box be full, not the parts one"): the
    // mount and each filler give the WHOLE Reach, moved into their own frame, so the outline is the
    // whole animal wherever it is aimed at. The game draws a box past its block's edge; it only
    // TESTS a block's boxes when the line of sight crosses that block - which the fillers cover.
    // The parts version is kept in backup/BlockMount.parts-outline.cs.

    /// <summary>The whole Reach in this block's own frame; null before one has arrived.</summary>
    public Cuboidf[] OwnBoxes() => Reach != null ? Outline() : null;

    /// <summary>
    /// The outline (what you aim at and click), by this player's MountOutline setting: 0 the whole Reach, 1 every moving
    /// part (drawn together), 2 the torso (Torso), 3 the head only (HeadBox), 4 the placed box only (Box, as before 1.0.4). Without parts (an older mount until re-measured, a model with no
    /// joints) always the whole Reach. Each box is shown whole from every space the animal reaches. Collision is by parts
    /// whatever this says. The SERVER always uses the whole Reach (it has no setting; fillers keep their owner either way).
    /// </summary>
    private Cuboidf[] Outline()
    {
        int mode = Api?.Side == EnumAppSide.Client ? TaxidermyModSystem.MountOutline : 0;
        var cache = outlineCache;
        if (cache.mode == mode && cache.boxes != null) return cache.boxes;
        Cuboidf[] boxes = [Reach];
        var clipped = Parts.Select(p => Intersect(p, Reach)).Where(p => p != null).ToArray();
        if (mode == 1 && clipped.Length > 0) boxes = clipped;
        else if (mode == 2 && clipped.Length > 0) boxes = [Torso(clipped)];
        else if (mode == 3 && HeadBox != null && Intersect(HeadBox, Reach) is { } head) boxes = [head];
        // the placed box only - the one-block box at its feet, as mounts had before 1.0.4 (Calm, 2026-10-06)
        else if (mode == 4 && Box != null) boxes = [Box];
        outlineCache = (mode, boxes);
        return boxes;
    }
    private (int mode, Cuboidf[] boxes) outlineCache = (-1, null);

    /// <summary>Client: the head's box (its joints' vertices), for the head-only outline; null when no head was found.</summary>
    private Cuboidf HeadBox { get => headBox; set { headBox = value; outlineCache = (-1, null); } }
    private Cuboidf headBox;

    /// <summary>Client: one box round the vertices of the head's joints, less (dx, dy, dz) like ModelBox; null if none.</summary>
    private static Cuboidf HeadBoxOf(MeshData shared, MeshData built, float dx, float dy, float dz)
    {
        var joints = MountMesher.JointsOf(shared);
        var head = MountMesher.HeadJointsOf(shared);
        if (joints == null || head == null || built == null || joints.Length != built.VerticesCount) return null;
        float[] b = [float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < built.VerticesCount; i++)
        {
            if (!head.Contains(joints[i])) continue;
            for (int a = 0; a < 3; a++) { float v = built.xyz[i * 3 + a]; if (v < b[a]) b[a] = v; if (v > b[a + 3]) b[a + 3] = v; }
        }
        return b[0] > b[3] ? null : new Cuboidf(b[0] - dx, b[1] - dy, b[2] - dz, b[3] - dx, b[4] - dy, b[5] - dz);
    }

    /// <summary>
    /// The torso, for the "body only" outline (Calm, 2026-10-06: "cover a bit more of the body so like the torso area"):
    /// the biggest part, grown by every part that touches it, sits at body height (its middle within the biggest part's
    /// height) and is thick like the body (its thinnest side at least half the biggest part's thinnest side) - the front
    /// and rear body, a thick neck base; not the legs (below) nor the tail, ears or other thin parts. One box round them.
    /// </summary>
    private static Cuboidf Torso(Cuboidf[] parts)
    {
        static float Volume(Cuboidf b) => (b.X2 - b.X1) * (b.Y2 - b.Y1) * (b.Z2 - b.Z1);
        static float Thinnest(Cuboidf b) => Math.Min(b.X2 - b.X1, Math.Min(b.Y2 - b.Y1, b.Z2 - b.Z1));
        var body = parts.OrderByDescending(Volume).First();
        float yLo = body.Y1, yHi = body.Y2, thick = Thinnest(body) / 2;
        const float Touch = 1f / 16f;
        var torso = body.Clone();
        var left = parts.Where(p => p != body).ToList();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var p in left.ToArray())
            {
                float mid = (p.Y1 + p.Y2) / 2;
                bool touches = p.X1 <= torso.X2 + Touch && p.X2 >= torso.X1 - Touch && p.Y1 <= torso.Y2 + Touch && p.Y2 >= torso.Y1 - Touch
                    && p.Z1 <= torso.Z2 + Touch && p.Z2 >= torso.Z1 - Touch;
                if (!touches || mid < yLo || mid > yHi || Thinnest(p) < thick) continue;
                torso = new Cuboidf(Math.Min(torso.X1, p.X1), Math.Min(torso.Y1, p.Y1), Math.Min(torso.Z1, p.Z1), Math.Max(torso.X2, p.X2), Math.Max(torso.Y2, p.Y2), Math.Max(torso.Z2, p.Z2));
                left.Remove(p);
                grew = true;
            }
        }
        return torso;
    }

    public Cuboidf[] AutoBoxesAt(BlockPos cell)
    {
        var o = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
        return Reach != null && reachPieces.ContainsKey(o) ? Outline().Select(b => AutoBoxes.Whole(b, o)).ToArray() : [];
    }

    public Cuboidf[] AutoCollisionAt(BlockPos cell) =>
        ReachSolid && Reach != null && solidPieces.TryGetValue((cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z), out var pieces) ? pieces : [];

    private void SetReach(Cuboidf reach, Cuboidf[] parts = null)
    {
        Reach = reach;
        Parts = parts ?? [];
        outlineCache = (-1, null);
        reachPieces = AutoBoxes.Cut(reach);
        // solid: each part's own pieces, kept inside the Reach (its cap) and only where the Reach has a space (a filler)
        var solid = new Dictionary<(int x, int y, int z), List<Cuboidf>>();
        foreach (var part in Parts.Length > 0 ? Parts : (reach == null ? [] : [reach]))
        {
            var inside = reach == null ? part : Intersect(part, reach);
            if (inside == null) continue;
            foreach (var (cell, piece) in AutoBoxes.Cut(inside))
            {
                if (!reachPieces.ContainsKey(cell)) continue;
                if (!solid.TryGetValue(cell, out var list)) solid[cell] = list = new List<Cuboidf>();
                list.Add(piece);
            }
        }
        solidPieces = solid.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    private static Cuboidf Intersect(Cuboidf a, Cuboidf b)
    {
        var c = new Cuboidf(Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1), Math.Max(a.Z1, b.Z1), Math.Min(a.X2, b.X2), Math.Min(a.Y2, b.Y2), Math.Min(a.Z2, b.Z2));
        return c.X2 > c.X1 && c.Y2 > c.Y1 && c.Z2 > c.Z1 ? c : null;
    }

    private string lastSentKey;

    private static string KeyOf(Cuboidf reach, Cuboidf[] parts) =>
        reach == null ? null : string.Join(",", new[] { reach }.Concat(parts ?? []).SelectMany(b => new[] { b.X1, b.Y1, b.Z1, b.X2, b.Y2, b.Z2 }).Select(f => MathF.Round(f * 64)));

    /// <summary>Client: tell the server the measured box and parts when they differ from what it holds (once per value).</summary>
    private void SendReach(ICoreClientAPI capi, Cuboidf measured, Cuboidf[] parts)
    {
        if (measured == null) return;
        parts ??= [];
        string key = KeyOf(measured, parts);
        if (key == KeyOf(Reach, Parts) || key == lastSentKey) return;
        lastSentKey = key;
        var all = new[] { measured }.Concat(parts.Take(MaxParts)).ToArray();
        var bytes = new byte[24 * all.Length];
        for (int b = 0; b < all.Length; b++)
        {
            float[] v = [all[b].X1, all[b].Y1, all[b].Z1, all[b].X2, all[b].Y2, all[b].Z2];
            for (int i = 0; i < 6; i++) BitConverter.GetBytes(v[i]).CopyTo(bytes, b * 24 + i * 4);
        }
        capi.Network.SendBlockEntityPacket(Pos, ReachPacketId, bytes);
    }

    /// <summary>
    /// Client: one box per moving part of the built model (the joint each vertex is bound to), less (dx, dy, dz) like
    /// ModelBox. Parts wholly inside another part are left out. Null when the model has no joints.
    /// </summary>
    private static Cuboidf[] PartBoxes(MeshData shared, MeshData built, float dx, float dy, float dz)
    {
        var joints = MountMesher.JointsOf(shared);
        if (joints == null || built == null || joints.Length != built.VerticesCount) return null;
        var boxes = new Dictionary<int, float[]>();
        var xyz = built.xyz;
        for (int i = 0; i < built.VerticesCount; i++)
        {
            if (!boxes.TryGetValue(joints[i], out var b)) boxes[joints[i]] = b = [float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue];
            for (int a = 0; a < 3; a++) { float v = xyz[i * 3 + a]; if (v < b[a]) b[a] = v; if (v > b[a + 3]) b[a + 3] = v; }
        }
        const float Thin = 1f / 32f;   // never a zero-thickness part
        var list = boxes.Values.Select(b =>
        {
            for (int a = 0; a < 3; a++) if (b[a + 3] - b[a] < Thin) { float c = (b[a] + b[a + 3]) / 2; b[a] = c - Thin / 2; b[a + 3] = c + Thin / 2; }
            return new Cuboidf(b[0] - dx, b[1] - dy, b[2] - dz, b[3] - dx, b[4] - dy, b[5] - dz);
        }).ToList();
        bool Inside(Cuboidf s, Cuboidf o) => s.X1 >= o.X1 && s.Y1 >= o.Y1 && s.Z1 >= o.Z1 && s.X2 <= o.X2 && s.Y2 <= o.Y2 && s.Z2 <= o.Z2;
        var kept = new List<Cuboidf>();
        for (int i = 0; i < list.Count; i++)
        {
            bool covered = false;
            for (int j = 0; j < list.Count && !covered; j++)
                covered = j != i && Inside(list[i], list[j]) && !(Inside(list[j], list[i]) && j > i);   // two identical: keep the first
            if (!covered) kept.Add(list[i]);
        }
        // the biggest parts if a model has more than the packet takes
        return kept.OrderByDescending(b => (b.X2 - b.X1) * (b.Y2 - b.Y1) * (b.Z2 - b.Z1)).Take(MaxParts).ToArray();
    }

    /// <summary>Server: a client's measurement. Builders only; capped at ReachMax each way and near the block.</summary>
    private void ReceiveReach(IPlayer fromPlayer, byte[] data)
    {
        if (data == null || data.Length < 24 || data.Length % 24 != 0 || data.Length / 24 > MaxParts + 1 || Data == null) return;
        if (Api.World.Claims.TestAccess(fromPlayer, Pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        var all = new float[data.Length / 4];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = BitConverter.ToSingle(data, i * 4);
            if (!float.IsFinite(all[i]) || Math.Abs(all[i]) > ReachMax + 1) return;
        }
        var v = all.Take(6).ToArray();
        var parts = new List<Cuboidf>();
        for (int b = 6; b + 6 <= all.Length; b += 6)
        {
            parts.Add(new Cuboidf(Math.Min(all[b], all[b + 3]), Math.Min(all[b + 1], all[b + 4]), Math.Min(all[b + 2], all[b + 5]),
                Math.Max(all[b], all[b + 3]), Math.Max(all[b + 1], all[b + 4]), Math.Max(all[b + 2], all[b + 5])));
        }
        static (float, float) Cap(float lo, float hi)
        {
            if (hi < lo) (lo, hi) = (hi, lo);
            float c = (lo + hi) / 2, half = Math.Min((hi - lo) / 2, ReachMax / 2);
            return (c - half, c + half);
        }
        var (x1, x2) = Cap(v[0], v[3]);
        var (y1, y2) = Cap(v[1], v[4]);
        var (z1, z2) = Cap(v[2], v[5]);
        SetReach(new Cuboidf(x1, y1, z1, x2, y2, z2), parts.ToArray());
        heldFillers = AutoBoxes.Sync(Api.World, Pos, reachPieces.Keys, heldFillers);
        MarkDirty(true);
    }

    public override void OnBlockRemoved()
    {
        if (Api?.Side == EnumAppSide.Server) AutoBoxes.ReleaseAll(Api.World, Pos, heldFillers);
        heldFillers.Clear();
        base.OnBlockRemoved();
    }

    /// <summary>The specimen tree (see <see cref="Specimen"/>). Null only for a broken placement.</summary>
    public ITreeAttribute Data { get; private set; }

    /// <summary>Yaw in radians about the block centre.</summary>
    public float Rotation { get; private set; }

    /// <summary>Fine-tuning nudge from the Adjust window, in blocks.</summary>
    public float OffX { get; private set; }
    public float OffY { get; private set; }
    public float OffZ { get; private set; }

    /// <summary>Nose up/down, radians, about the feet - from the Adjust window.</summary>
    public float Tilt { get; private set; }

    /// <summary>
    /// Whether the selection box moves with the nudge. Off (the default, and what every mount
    /// placed before 2026-09-23 has) keeps it on the block, for dioramas; on lets you click a
    /// nudged animal where it actually stands. Per mount, from the Adjust window.
    /// </summary>
    public bool BoxFollows { get; private set; }

    /// <summary>Selection box, from the entity's own collision size. Built on both sides.</summary>
    public Cuboidf Box { get; private set; }

    private MeshData mesh;
    private float appliedStand;

    /// <summary>
    /// How far the mount sinks to stand on whatever is below: 0 on a full block or nothing,
    /// negative on a slab or a chiselled block, from the highest collision box under the block
    /// centre (or the highest anywhere if the centre is hollow). Calm, 2026-09-21: players
    /// build their own stands out of chiselled blocks, so the animal must sit on the cut
    /// surface whatever height it is.
    /// </summary>
    private float StandOffset()
    {
        var below = Pos.DownCopy();
        var block = Api.World.BlockAccessor.GetBlock(below);
        if (block == null || block.Id == 0) return 0;
        var boxes = block.GetCollisionBoxes(Api.World.BlockAccessor, below);
        if (boxes == null || boxes.Length == 0) return 0;
        float top = -1;
        foreach (var b in boxes)
            if (b.X1 <= 0.5f && b.X2 >= 0.5f && b.Z1 <= 0.5f && b.Z2 >= 0.5f) top = Math.Max(top, b.Y2);
        if (top < 0) top = boxes.Max(b => b.Y2);
        return Math.Min(0, top - 1);
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        Rebuild();
    }

    public override void OnBlockPlaced(ItemStack byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        Data = Specimen.Of(byItemStack)?.Clone();
        Rebuild();
        MarkDirty(true);
    }

    public void SetRotation(float radians)
    {
        Rotation = GameMath.Mod(radians, GameMath.TWOPI);
        Rebuild();
        MarkDirty(true);
    }

    public void RotateBy(int dir) => SetRotation(Rotation + dir * GameMath.PI / 8);

    public void StandChanged()
    {
        Rebuild();
        MarkDirty(true);
    }

    /// <summary>The Adjust window's packet: pose, nudge and rotation, applied server side.</summary>
    public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(fromPlayer, packetid, data);
        if (packetid == ReachPacketId) { ReceiveReach(fromPlayer, data); return; }
        if (packetid != GuiDialogMount.PacketId || Data == null) return;
        // Backstop only - the window opens after RequestAdjust has said yes. Silent, or a refusal
        // would repeat for every number dragged.
        if (Api.World.Claims.TestAccess(fromPlayer, Pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        var p = SerializerUtil.Deserialize<MountAdjustPacket>(data);
        var def = Api.ModLoader.GetModSystem<TaxidermyModSystem>().Find(Data);
        if (def != null && def.Poses.Any(q => q.Code == p.Pose)) Data.SetString("pose", p.Pose);
        OffX = GameMath.Clamp(p.OffX, -16, 16) / 16f;
        OffY = GameMath.Clamp(p.OffY, -16, 16) / 16f;
        OffZ = GameMath.Clamp(p.OffZ, -16, 16) / 16f;
        // The window's rotation is negated both ways so its up arrow turns the animal LEFT (Calm, 2026-09-22).
        Rotation = GameMath.Mod(-p.RotationDeg * GameMath.DEG2RAD, GameMath.TWOPI);
        Tilt = GameMath.Clamp(p.TiltDeg, -90, 90) * GameMath.DEG2RAD;
        BoxFollows = p.BoxFollows;
        Rebuild();
        MarkDirty(true);
    }

    /// <summary>
    /// Main thread only: on the client this inserts textures into the block atlas, which is
    /// GPU work. OnTesselation runs on the tesselation thread and must only hand over what
    /// was built here (shared reference sect.8d).
    /// </summary>
    private void Rebuild()
    {
        mesh = null;
        Box = null;
        if (Api == null || Data == null) return;
        var system = Api.ModLoader.GetModSystem<TaxidermyModSystem>();
        var def = system.Find(Data);
        if (def == null)
        {
            Api.Logger.Warning("[Taxidermy] No definition for specimen {0} at {1}", Data.GetString("entityCode"), Pos);
            if (Api is ICoreClientAPI noDef) mesh = MountMesher.GetPlaceholderMesh(noDef);
            return;
        }
        float stand = appliedStand = StandOffset();
        var props = Api.World.GetEntityType(new AssetLocation(Data.GetString("entityCode")));
        if (props != null)
        {
            float w = Math.Min(1f, props.CollisionBoxSize.X);
            // At most one block in every direction (Calm, 2026-09-23, on an elephant): the engine
            // only tests a block's selection boxes when the line of sight crosses that block's own
            // cell, so the upper half of the old 2-block box was drawn but could never be clicked.
            // It is the SIZE that is capped, not the position - with BoxFollows on, the whole box
            // still travels with the animal, even past the block edge (Calm, same day).
            float h = Math.Min(1f, props.CollisionBoxSize.Y);
            // By default the box stays on the block whatever the nudge (Calm, 2026-09-22); only the
            // stand height moves it. With BoxFollows on it takes the same nudge as the mesh below.
            float dx = 0, dy = 0, dz = 0;
            if (BoxFollows) (dx, dy, dz) = NudgeOffset();
            Box = new Cuboidf(0.5f - w / 2 + dx, stand + dy, 0.5f - w / 2 + dz, 0.5f + w / 2 + dx, h + stand + dy, 0.5f + w / 2 + dz);
        }
        if (Api is not ICoreClientAPI capi) return;
        try
        {
            var shared = MountMesher.Get(capi, Data, def);
            // No mesh means no model to lift - the animal's mod is gone. Fall through to the
            // placeholder below rather than returning, which is what left these blocks
            // invisible in the first place (Calm saw it in game, 2026-09-22).
            if (shared != null)
            {
                mesh = shared.Clone();
                // Tilt first, in the rest frame where the animal faces -X, so it is nose up/down
                // whatever way it is then turned. About the feet, at the block centre.
                if (Tilt != 0) mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, 0, Tilt);
                // The nudge is in the ANIMAL's frame, applied before the turn so "forward" is the
                // way it faces whichever way that is (Calm, 2026-09-22: it changed meaning by facing).
                // Rest pose faces -X, so forward is -X and its right-hand side is -Z.
                // Sign confirmed in game 2026-09-22: positive back/forward must move it forward.
                if (OffX != 0 || OffZ != 0) mesh.Translate(OffZ, 0, -OffX);
                mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, Rotation, 0);
                if (stand != 0 || OffY != 0) mesh.Translate(0, stand + OffY, 0);
                // On the client the box comes from the model as drawn, not the collision box - the
                // collision box is square and usually smaller, so long animals and lying poses had
                // to be clicked in one spot (Calm, 2026-09-23). Box follows off: take the nudge back
                // out, so the box stays put as before. The server cannot build the mesh and keeps
                // the collision-sized box from above.
                float dx = 0, dy = 0, dz = 0;
                if (!BoxFollows) (dx, dy, dz) = NudgeOffset();
                Box = ModelBox(mesh, dx, dy, dz) ?? Box;
                // Auto boxes: the same box without the one-block cap goes to the server, with the moving parts for collision.
                SendReach(capi, ModelBox(mesh, dx, dy, dz, ReachMax), PartBoxes(shared, mesh, dx, dy, dz));
                HeadBox = HeadBoxOf(shared, mesh, dx, dy, dz);
            }
        }
        catch (Exception e)
        {
            // Log the full exception: on a NullReference the message is empty and the frame is the diagnosis.
            Api.Logger.Error("[Taxidermy] Cannot build the mount at {0} ({1}): {2}", Pos, Data.GetString("entityCode"), e);
        }
        // Nothing to draw and a specimen that says there should be: the animal's mod is not
        // installed. Show the placeholder cube rather than empty air, which is impossible to
        // find (Calm, 2026-09-22). The block breaks into nothing - see BlockMount.Orphaned.
        mesh ??= MountMesher.GetPlaceholderMesh(capi);
    }

    /// <summary>
    /// One box around the built model, less (dx, dy, dz), at most one block in each direction:
    /// a model wider or deeper than a block keeps a block-wide box centred on it, a taller one
    /// keeps the bottom block (the feet - the part above could never be clicked anyway). Never
    /// thinner than an eighth, so a flat pose still has something to click.
    /// </summary>
    private static Cuboidf ModelBox(MeshData m, float dx, float dy, float dz, float cap = 1f)
    {
        if (m == null || m.VerticesCount == 0) return null;
        var xyz = m.xyz;
        float x1 = float.MaxValue, y1 = float.MaxValue, z1 = float.MaxValue;
        float x2 = float.MinValue, y2 = float.MinValue, z2 = float.MinValue;
        for (int i = 0; i < m.VerticesCount; i++)
        {
            float x = xyz[i * 3], y = xyz[i * 3 + 1], z = xyz[i * 3 + 2];
            if (x < x1) x1 = x; if (x > x2) x2 = x;
            if (y < y1) y1 = y; if (y > y2) y2 = y;
            if (z < z1) z1 = z; if (z > z2) z2 = z;
        }
        float Max = cap;
        const float Min = 1f / 8f;
        (float, float) Fit(float lo, float hi)
        {
            float c = (lo + hi) / 2, half = (hi - lo) / 2;
            half = Math.Clamp(half, Min / 2, Max / 2);
            return (c - half, c + half);
        }
        (x1, x2) = Fit(x1, x2);
        (z1, z2) = Fit(z1, z2);
        if (y2 - y1 > Max) y2 = y1 + Max;
        if (y2 - y1 < Min) y2 = y1 + Min;
        return new Cuboidf(x1 - dx, y1 - dy, z1 - dz, x2 - dx, y2 - dy, z2 - dz);
    }

    /// <summary>
    /// Where the nudge puts the animal, in world blocks - the same move Rebuild gives the mesh:
    /// (OffZ, 0, -OffX) in the animal's frame, turned by Rotation, plus OffY. Turned through
    /// Mat4f.RotateXYZ + MulWithVec3, which is exactly what MeshData.Rotate applies to every vertex
    /// (vsapi MeshData.MatrixTransform), so the box cannot disagree with the model on direction
    /// or sign. Tilt is left out: it pivots about the feet and barely moves the footprint.
    /// </summary>
    private (float dx, float dy, float dz) NudgeOffset()
    {
        if (OffX == 0 && OffY == 0 && OffZ == 0) return (0, 0, 0);
        var m = new float[16];
        Mat4f.RotateXYZ(m, 0, Rotation, 0);
        var v = new float[] { OffZ, 0, -OffX };
        var o = new float[3];
        Mat4f.MulWithVec3(m, v, o);
        return (o[0], OffY, o[2]);
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        var m = mesh;
        if (m != null) mesher.AddMeshData(m);
        // Chiselling the stand changes no block id, so no neighbour-change event reaches us -
        // but it does redraw the chunk, which lands here. Rebuild on the main thread if the
        // stand's height moved.
        if (Data != null && Math.Abs(StandOffset() - appliedStand) > 0.001f)
            Api.Event.EnqueueMainThreadTask(() => { Rebuild(); MarkDirty(true); }, "taxidermy-stand");
        return true;   // nothing else to draw - the block has no shape worth showing
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (Data != null) tree[Specimen.Key] = Data.Clone();
        tree.SetFloat("rotation", Rotation);
        tree.SetFloat("offx", OffX); tree.SetFloat("offy", OffY); tree.SetFloat("offz", OffZ);
        tree.SetFloat("tilt", Tilt);
        tree.SetBool("boxfollows", BoxFollows);
        if (Reach != null)
        {
            tree.SetFloat("reachX1", Reach.X1); tree.SetFloat("reachY1", Reach.Y1); tree.SetFloat("reachZ1", Reach.Z1);
            tree.SetFloat("reachX2", Reach.X2); tree.SetFloat("reachY2", Reach.Y2); tree.SetFloat("reachZ2", Reach.Z2);
            if (Parts.Length > 0)
                tree.SetString("reachParts", string.Join(";", Parts.Select(b => string.Join(",", new[] { b.X1, b.Y1, b.Z1, b.X2, b.Y2, b.Z2 }
                    .Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture))))));
        }
        tree.SetString("reachHeld", AutoBoxes.Write(heldFillers));
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Data = tree.GetTreeAttribute(Specimen.Key)?.Clone();
        Rotation = tree.GetFloat("rotation");
        OffX = tree.GetFloat("offx"); OffY = tree.GetFloat("offy"); OffZ = tree.GetFloat("offz");
        Tilt = tree.GetFloat("tilt");
        BoxFollows = tree.GetBool("boxfollows");
        var parts = new List<Cuboidf>();
        foreach (var entry in tree.GetString("reachParts", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = entry.Split(',').Select(s => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : float.NaN).ToArray();
            if (f.Length == 6 && f.All(float.IsFinite)) parts.Add(new Cuboidf(f[0], f[1], f[2], f[3], f[4], f[5]));
        }
        SetReach(tree.HasAttribute("reachX1")
            ? new Cuboidf(tree.GetFloat("reachX1"), tree.GetFloat("reachY1"), tree.GetFloat("reachZ1"), tree.GetFloat("reachX2"), tree.GetFloat("reachY2"), tree.GetFloat("reachZ2"))
            : null, parts.ToArray());
        heldFillers = AutoBoxes.Read(tree.GetString("reachHeld"));
        if (Api?.Side == EnumAppSide.Client)
            foreach (var o in heldFillers) Api.World.BlockAccessor.MarkBlockDirty(Pos.AddCopy(o.x, o.y, o.z));
        if (Api != null)
        {
            Rebuild();
            if (Api.Side == EnumAppSide.Client) MarkDirty(true);
        }
    }
}
