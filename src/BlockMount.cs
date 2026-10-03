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
        var box = blockAccessor.GetBlockEntity<BEMount>(pos)?.Box;
        return box != null ? [box] : base.GetSelectionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => [];

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

public sealed class BEMount : BlockEntity
{
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
    private static Cuboidf ModelBox(MeshData m, float dx, float dy, float dz)
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
        const float Max = 1f, Min = 1f / 8f;
        static (float, float) Fit(float lo, float hi)
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
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Data = tree.GetTreeAttribute(Specimen.Key)?.Clone();
        Rotation = tree.GetFloat("rotation");
        OffX = tree.GetFloat("offx"); OffY = tree.GetFloat("offy"); OffZ = tree.GetFloat("offz");
        Tilt = tree.GetFloat("tilt");
        BoxFollows = tree.GetBool("boxfollows");
        if (Api != null)
        {
            Rebuild();
            if (Api.Side == EnumAppSide.Client) MarkDirty(true);
        }
    }
}
