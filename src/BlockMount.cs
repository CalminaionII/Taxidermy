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
/// The placed mount. It has no shape of its own (no base yet - Calm wants to see the poses
/// first); the block entity renders the animal. The wrench's own mode menu gains a "Pose"
/// entry through <c>IExtraWrenchModes</c>, and its default Rotate mode turns the animal
/// through <c>IWrenchOrientable</c>. Both are vanilla's mechanisms (vssurvivalmod
/// Item/ItemWrench.cs): right-click is +1, left-click is -1.
/// </summary>
public sealed class BlockMount : Block, IExtraWrenchModes, IWrenchOrientable
{
    private SkillItem[] modes;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        modes =
        [
            new SkillItem { Code = new AssetLocation("taxidermy:pose"), Name = Lang.Get("taxidermy:wrench-pose") },
            new SkillItem { Code = new AssetLocation("taxidermy:adjust"), Name = Lang.Get("taxidermy:wrench-adjust") },
        ];
        if (api is ICoreClientAPI capi)
        {
            modes[0].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("game:textures/icons/rotate.svg"), 48, 48, 5, -1));
            modes[1].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("game:textures/icons/moveud.svg"), 48, 48, 5, -1));
        }
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        foreach (var mode in modes ?? []) mode.Dispose();
        base.OnUnloaded(api);
    }

    public SkillItem[] GetExtraWrenchModes(IPlayer byPlayer, BlockSelection blockSelection) => modes;

    public void OnWrenchInteract(IPlayer player, BlockSelection blockSel, int mode, int v)
    {
        if (mode == 1)
        {
            // Adjust: a window, so client side only. The server hears about changes by packet.
            if (api is ICoreClientAPI capi && api.World.BlockAccessor.GetBlockEntity<BEMount>(blockSel.Position) is { Data: not null } be
                && capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(be.Data) is { } def)
                new GuiDialogMount(capi, be, def).TryOpen();
            return;
        }
        if (mode != 0 || !CanChange(player, blockSel)) return;
        api.World.BlockAccessor.GetBlockEntity<BEMount>(blockSel.Position)?.CyclePose(Direction(player, v == 1 ? 1 : -1));
    }

    public void Rotate(EntityAgent byEntity, BlockSelection blockSel, int dir)
    {
        if (byEntity is EntityPlayer p && CanChange(p.Player, blockSel))
            api.World.BlockAccessor.GetBlockEntity<BEMount>(blockSel.Position)?.RotateBy(Direction(p.Player, dir));
    }

    // Sprint-click goes the other way (Calm, 2026-09-20). Read the KEY, not the sprint
    // ACTION, and from WorldData - shared reference sect.7.
    private static int Direction(IPlayer player, int dir) => player.WorldData.EntityControls.CtrlKey ? -dir : dir;

    // Both wrench paths run on client and server; the block entity's MarkDirty carries the
    // result down, so only the server acts.
    private bool CanChange(IPlayer player, BlockSelection sel) =>
        api.Side == EnumAppSide.Server && api.World.Claims.TryAccess(player, sel.Position, EnumBlockAccessFlags.BuildOrBreak);

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
            if (api is ICoreClientAPI capi && world.BlockAccessor.GetBlockEntity<BEMount>(blockSel.Position) is { Data: not null } be
                && capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(be.Data) is { } def)
                new GuiDialogMount(capi, be, def).TryOpen();
            return true;
        }
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
        {
            slot.MarkDirty();
            return false;
        }
        if (world.Side == EnumAppSide.Server)
        {
            var stack = StackAt(world, blockSel.Position);
            world.BlockAccessor.SetBlock(0, blockSel.Position);
            if (!byPlayer.InventoryManager.TryGiveItemstack(stack)) world.SpawnItemEntity(stack, blockSel.Position.ToVec3d().Add(0.5, 0.5, 0.5));
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

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) => [StackAt(world, pos)];
    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) => StackAt(world, pos);

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
        return data == null ? base.GetPlacedBlockName(world, pos) : Lang.Get("taxidermy:block-mount-of", Specimen.AnimalName(data));
    }

    /// <summary>
    /// 0 = not holding a wrench, 1 = wrench in Rotate mode, 2 = wrench in Pose mode. The info box
    /// and the hints show only what the held tool will do (Calm, 2026-09-21).
    /// </summary>
    private static int WrenchMode(IPlayer player, BlockSelection sel)
    {
        var slot = player?.InventoryManager?.ActiveHotbarSlot;
        if (slot?.Itemstack?.Collectible is not ItemWrench wrench) return 0;
        return wrench.GetToolMode(slot, player, sel) switch { 0 => 1, 1 => 2, _ => 3 };
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var be = world.BlockAccessor.GetBlockEntity<BEMount>(pos);
        if (be?.Data == null) return base.GetPlacedBlockInfo(world, pos, forPlayer);
        var sb = new StringBuilder();
        sb.AppendLine(Lang.Get("taxidermy:pose-label", Lang.Get("taxidermy:pose-" + be.Data.GetString("pose"))));
        sb.AppendLine(Lang.Get(WrenchMode(forPlayer, forPlayer?.CurrentBlockSelection) switch
        {
            1 => "taxidermy:mount-help-rotate",
            2 => "taxidermy:mount-help-pose",
            3 => "taxidermy:mount-help-adjust",
            _ => "taxidermy:mount-help",
        }));
        return sb.ToString();
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer) =>
        WrenchMode(forPlayer, selection) switch
        {
            1 =>
            [
                new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-rotate", MouseButton = EnumMouseButton.Right },
                new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-rotate-back", MouseButton = EnumMouseButton.Right, HotKeyCode = "ctrl" },
            ],
            2 =>
            [
                new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-pose-next", MouseButton = EnumMouseButton.Right },
                new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-pose-prev", MouseButton = EnumMouseButton.Right, HotKeyCode = "ctrl" },
            ],
            3 => [new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-adjust", MouseButton = EnumMouseButton.Right }],
            _ =>
            [
                new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-pickup", MouseButton = EnumMouseButton.Right, RequireFreeHand = true },
                new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-adjust", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift", RequireFreeHand = true },
                new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-wrench", MouseButton = EnumMouseButton.Right, Itemstacks = WrenchStacks(world) },
            ],
        };

    private static ItemStack[] wrenchStacks;
    private static ItemStack[] WrenchStacks(IWorldAccessor world) =>
        wrenchStacks ??= world.Items.Where(i => i?.Code != null && i.Code.Path.StartsWith("wrench-")).Select(i => new ItemStack(i)).ToArray();
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
        if (!Api.World.Claims.TryAccess(fromPlayer, Pos, EnumBlockAccessFlags.BuildOrBreak)) return;
        var p = SerializerUtil.Deserialize<MountAdjustPacket>(data);
        var def = Api.ModLoader.GetModSystem<TaxidermyModSystem>().Find(Data);
        if (def != null && def.Poses.Any(q => q.Code == p.Pose)) Data.SetString("pose", p.Pose);
        OffX = GameMath.Clamp(p.OffX, -16, 16) / 16f;
        OffY = GameMath.Clamp(p.OffY, -16, 16) / 16f;
        OffZ = GameMath.Clamp(p.OffZ, -16, 16) / 16f;
        // The window's rotation is negated both ways so its up arrow turns the animal LEFT (Calm, 2026-09-22).
        Rotation = GameMath.Mod(-p.RotationDeg * GameMath.DEG2RAD, GameMath.TWOPI);
        Tilt = GameMath.Clamp(p.TiltDeg, -90, 90) * GameMath.DEG2RAD;
        Rebuild();
        MarkDirty(true);
    }

    public void CyclePose(int dir)
    {
        var def = Api.ModLoader.GetModSystem<TaxidermyModSystem>().Find(Data);
        if (Data == null || def == null) return;
        int index = Array.FindIndex(def.Poses, p => p.Code == Data.GetString("pose"));
        Data.SetString("pose", def.Poses[GameMath.Mod(index + dir, def.Poses.Length)].Code);
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
            return;
        }
        float stand = appliedStand = StandOffset();
        var props = Api.World.GetEntityType(new AssetLocation(Data.GetString("entityCode")));
        if (props != null)
        {
            float w = Math.Min(1f, props.CollisionBoxSize.X);
            float h = Math.Min(2f, props.CollisionBoxSize.Y);
            // The box stays on the block whatever the nudge (Calm, 2026-09-22); only the stand height moves it.
            Box = new Cuboidf(0.5f - w / 2, stand, 0.5f - w / 2, 0.5f + w / 2, h + stand, 0.5f + w / 2);
        }
        if (Api is not ICoreClientAPI capi) return;
        try
        {
            var shared = MountMesher.Get(capi, Data, def);
            if (shared == null) return;
            mesh = shared.Clone();
            // Tilt first, in the rest frame where the animal faces -X, so it is nose up/down
            // whatever way it is then turned. About the feet, at the block centre.
            if (Tilt != 0) mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, 0, Tilt);
            // The nudge is in the ANIMAL's frame, applied before the turn so "forward" is the way
            // it faces whichever way that is (Calm, 2026-09-22: it changed meaning by facing).
            // Rest pose faces -X, so forward is -X and its right-hand side is -Z.
            // Sign confirmed in game 2026-09-22: positive back/forward must move it forward (up arrow = forward).
            if (OffX != 0 || OffZ != 0) mesh.Translate(OffZ, 0, -OffX);
            mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, Rotation, 0);
            if (stand != 0 || OffY != 0) mesh.Translate(0, stand + OffY, 0);
        }
        catch (Exception e)
        {
            // Log the full exception: on a NullReference the message is empty and the frame is the diagnosis.
            Api.Logger.Error("[Taxidermy] Cannot build the mount at {0} ({1}): {2}", Pos, Data.GetString("entityCode"), e);
        }
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
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Data = tree.GetTreeAttribute(Specimen.Key)?.Clone();
        Rotation = tree.GetFloat("rotation");
        OffX = tree.GetFloat("offx"); OffY = tree.GetFloat("offy"); OffZ = tree.GetFloat("offz");
        Tilt = tree.GetFloat("tilt");
        if (Api != null)
        {
            Rebuild();
            if (Api.Side == EnumAppSide.Client) MarkDirty(true);
        }
    }
}
