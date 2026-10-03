using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// Building a mount in the world (Calm's design, 2026-10-01; 1.0.4), beside the grid recipe:
/// a preserved head on the ground, right-clicked with its pelt, becomes an unfinished mount
/// (<see cref="BEAssembly"/>); dry grass is added by hand; sewing it with the Needle and Thread
/// makes the finished mount in one go (Calm, 2026-10-01: with everything together it needs no
/// stages). Until it is sewn, breaking it gives every part back.
///
/// Which pelt fits which head, and how much grass, come from the mount GRID RECIPES themselves
/// (<see cref="AssemblyRecipes"/>), so this mod's recipes, Taxidermy Patches and any modder's
/// recipe patch all work here unchanged.
/// </summary>
public static class AssemblyRecipes
{
    public sealed class Match
    {
        public int Grass;
    }

    private static bool IsMountRecipe(GridRecipe r) =>
        r?.Output?.ResolvedItemStack?.Collectible?.Code?.ToString() == Specimen.MountItem && r.ResolvedIngredients != null;

    private static bool IsHeadIngredient(CraftingRecipeIngredient ing) => ing?.Code?.Path?.StartsWith("head-") == true && ing.Code.Domain == Specimen.Domain;
    private static bool IsGrassIngredient(CraftingRecipeIngredient ing) => ing?.Code?.ToString() == "game:drygrass";
    private static bool IsToolIngredient(CraftingRecipeIngredient ing) => ing != null && (ing.IsTool || ing.Code?.Path == "sewingkit");
    private static bool IsPeltIngredient(CraftingRecipeIngredient ing) => ing != null && !IsHeadIngredient(ing) && !IsGrassIngredient(ing) && !IsToolIngredient(ing);

    /// <summary>The mount recipe this head and this pelt make together, or null.</summary>
    public static Match Find(IWorldAccessor world, ItemStack head, ItemStack pelt)
    {
        if (head == null || pelt == null) return null;
        foreach (var r in world.GridRecipes)
        {
            if (!IsMountRecipe(r)) continue;
            var ings = r.ResolvedIngredients.Where(i => i != null).ToArray();
            if (!ings.Any(i => IsHeadIngredient(i) && i.SatisfiesAsIngredient(head, false))) continue;
            if (!ings.Any(i => IsPeltIngredient(i) && i.SatisfiesAsIngredient(pelt, false))) continue;
            int grass = ings.FirstOrDefault(IsGrassIngredient)?.Quantity ?? 4;
            return new Match { Grass = Math.Max(1, grass) };
        }
        return null;
    }

    /// <summary>Whether this stack is the pelt of ANY mount recipe - a pelt that just is not this head's.</summary>
    public static bool IsAnyPelt(IWorldAccessor world, ItemStack stack)
    {
        if (stack == null) return false;
        foreach (var r in world.GridRecipes)
        {
            if (!IsMountRecipe(r)) continue;
            if (r.ResolvedIngredients.Any(i => IsPeltIngredient(i) && i.SatisfiesAsIngredient(stack, false))) return true;
        }
        return false;
    }
}

/// <summary>
/// On the preserved head: ground storage asks a stored item first whether it wants the click
/// (BEGroundStorage.OnPlayerInteractStart, before it tries to add the held item to the pile), so
/// right-clicking a head on the ground with its pelt starts the assembly instead of stacking the
/// pelt beside it.
/// </summary>
public sealed class BehaviorAssemblyStart : CollectibleBehavior, IContainedInteractable
{
    public BehaviorAssemblyStart(CollectibleObject collObj) : base(collObj) { }

    public bool OnContainedInteractStart(BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        var held = byPlayer?.InventoryManager?.ActiveHotbarSlot;
        if (held?.Itemstack == null || slot?.Itemstack?.Collectible is not ItemHead head || !head.IsPreserved) return false;
        var world = be.Api.World;
        if (!AssemblyRecipes.IsAnyPelt(world, held.Itemstack)) return false;   // not a pelt: ground storage as usual
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return true;
        if (world.Side != EnumAppSide.Server) return true;

        head.SyncPeltToken(slot.Itemstack);
        if (held.Itemstack.Collectible is ItemTaxidermyHide hide && hide.SyncPeltToken(held.Itemstack)) held.MarkDirty();
        var match = AssemblyRecipes.Find(world, slot.Itemstack, held.Itemstack);
        if (match == null)
        {
            (be.Api as ICoreServerAPI)?.SendIngameError(byPlayer as IServerPlayer, "taxidermy-wrongpelt", Lang.Get("taxidermy:assembly-wrongpelt"));
            return true;
        }
        if (!Specimen.CoatsMatch(slot.Itemstack, held.Itemstack))
        {
            (be.Api as ICoreServerAPI)?.SendIngameError(byPlayer as IServerPlayer, "taxidermy-wrongcoat", Lang.Get("taxidermy:assembly-wrongcoat"));
            return true;
        }
        var block = world.GetBlock(new AssetLocation(BEAssembly.BlockCode));
        if (block == null) return true;

        var headStack = slot.Itemstack.Clone();
        headStack.StackSize = 1;
        var peltStack = held.TakeOut(1);
        held.MarkDirty();
        var pos = blockSel.Position.Copy();
        // Face the player like a placed mount (ItemMount.OnHeldInteractStart).
        double dx = byPlayer.Entity.Pos.X - (pos.X + 0.5), dz = byPlayer.Entity.Pos.Z - (pos.Z + 0.5);
        float yaw = (float)Math.Atan2(dx, dz) + GameMath.PIHALF;
        yaw = MathF.Round(yaw / (GameMath.PI / 4)) * (GameMath.PI / 4);

        world.BlockAccessor.SetBlock(block.Id, pos);
        if (world.BlockAccessor.GetBlockEntity(pos) is BEAssembly asm) asm.Start(headStack, peltStack, match.Grass, yaw);
        world.PlaySoundAt(new AssetLocation("game:sounds/block/cloth"), pos.X + 0.5, pos.Y + 0.2, pos.Z + 0.5, null);
        return true;
    }

    public bool OnContainedInteractStep(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) => false;

    /// <summary>"Add the pelt" on a preserved head lying on the ground.</summary>
    public WorldInteraction[] GetContainedInteractionHelp(BlockEntityContainer be, ItemSlot slot, IPlayer forPlayer, BlockSelection blockSel)
    {
        if (slot?.Itemstack?.Collectible is not ItemHead { IsPreserved: true }) return [];
        return [new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-addpelt", MouseButton = EnumMouseButton.Right }];
    }

    public void OnContainedInteractStop(float secondsUsed, BlockEntityContainer be, ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) { }
}

/// <summary>The unfinished mount's block: grass by hand, one sewing with the needle, everything back when broken.</summary>
public sealed class BlockAssembly : Block
{
    public const float SewSeconds = 2.5f;
    /// <summary>The game's knife-cutting animation, as on the needle carving (Calm, 2026-10-01).</summary>
    private const string SewAnim = "knifecut";
    /// <summary>A stitch sound every half second while sewing (Calm, 2026-10-01).</summary>
    private const float StitchEvery = 0.5f;
    private static readonly AssetLocation StitchSound = new("game:sounds/player/clothrepair");

    /// <summary>Stitches played so far in each player's current sewing.</summary>
    private readonly Dictionary<string, int> stitches = new();

    /// <summary>Both sides call this: the server plays it for everyone else, the client for the sewer.</summary>
    private static void Stitch(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel) =>
        world.PlaySoundAt(StitchSound, blockSel.Position.X + 0.5, blockSel.Position.Y + 0.3, blockSel.Position.Z + 0.5, byPlayer, true, 16, 0.7f);

    private static bool IsNeedle(ItemSlot slot) => slot?.Itemstack?.Collectible is ItemNeedleThread;
    private static bool IsGrass(ItemSlot slot) => slot?.Itemstack?.Collectible?.Code?.ToString() == "game:drygrass";

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var be = world.BlockAccessor.GetBlockEntity<BEAssembly>(blockSel.Position);
        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (be == null || slot == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);
        if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return false;

        if (IsGrass(slot) && be.Grass < be.GrassNeeded)
        {
            if (world.Side == EnumAppSide.Server)
            {
                slot.TakeOut(1);
                slot.MarkDirty();
                be.AddGrass();
                world.PlaySoundAt(new AssetLocation("game:sounds/walk/grass1"), blockSel.Position.X + 0.5, blockSel.Position.Y + 0.2, blockSel.Position.Z + 0.5, null);
            }
            return true;
        }
        if (IsNeedle(slot))
        {
            if (be.ReadyToSew)
            {
                byPlayer.Entity.StartAnimation(SewAnim);
                lock (stitches) stitches[byPlayer.PlayerUID] = 0;
                Stitch(world, byPlayer, blockSel);
                if (world.Api is ICoreClientAPI client)
                {
                    // Sewing turns this block into a mount, so the game's Stop may never reach it
                    // (the needle carving lost its Stop the same way): stop the animation here too,
                    // once the pile is gone or the time is well past.
                    var pos = blockSel.Position.Copy();
                    long since = client.World.ElapsedMilliseconds, id = 0;
                    id = client.Event.RegisterGameTickListener(_ =>
                    {
                        bool gone = client.World.BlockAccessor.GetBlock(pos) is not BlockAssembly;
                        if (!gone && client.World.ElapsedMilliseconds - since < (SewSeconds + 0.5f) * 1000) return;
                        byPlayer.Entity.StopAnimation(SewAnim);
                        client.Event.UnregisterGameTickListener(id);
                    }, 100);
                }
                return true;
            }
            if (world.Api is ICoreClientAPI capi) capi.TriggerIngameError(this, "taxidermy-needgrass", Lang.Get("taxidermy:assembly-needgrass", be.GrassNeeded - be.Grass));
            return false;
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var be = world.BlockAccessor.GetBlockEntity<BEAssembly>(blockSel.Position);
        if (be == null || !be.ReadyToSew || !IsNeedle(byPlayer.InventoryManager.ActiveHotbarSlot) || secondsUsed >= SewSeconds)
        {
            byPlayer.Entity.StopAnimation(SewAnim);
            return false;
        }
        int due = (int)(secondsUsed / StitchEvery);
        bool play;
        lock (stitches)
        {
            play = stitches.TryGetValue(byPlayer.PlayerUID, out int done) && due > done;
            if (play) stitches[byPlayer.PlayerUID] = due;
        }
        if (play) Stitch(world, byPlayer, blockSel);
        return true;
    }

    public override bool OnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason)
    {
        byPlayer.Entity.StopAnimation(SewAnim);
        lock (stitches) stitches.Remove(byPlayer.PlayerUID);
        return true;
    }

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        byPlayer.Entity.StopAnimation(SewAnim);
        lock (stitches) stitches.Remove(byPlayer.PlayerUID);
        if (world.Side != EnumAppSide.Server || secondsUsed < SewSeconds - 0.05f) return;
        var be = world.BlockAccessor.GetBlockEntity<BEAssembly>(blockSel.Position);
        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (be == null || !be.ReadyToSew || !IsNeedle(slot)) return;
        slot.Itemstack.Collectible.DamageItem(world, byPlayer.Entity, slot, be.Stitches);
        slot.MarkDirty();
        be.Finish();
    }

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) =>
        world.BlockAccessor.GetBlockEntity<BEAssembly>(pos)?.Parts() ?? [];

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) =>
        world.BlockAccessor.GetBlockEntity<BEAssembly>(pos)?.HeadStack?.Clone();

    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => [];

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        var data = Specimen.Of(world.BlockAccessor.GetBlockEntity<BEAssembly>(pos)?.HeadStack);
        return data == null ? base.GetPlacedBlockName(world, pos) : Lang.Get("taxidermy:block-assembly-of", Specimen.AnimalNameNoSex(data));
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var be = world.BlockAccessor.GetBlockEntity<BEAssembly>(pos);
        if (be?.HeadStack == null) return base.GetPlacedBlockInfo(world, pos, forPlayer);
        var sb = new StringBuilder();
        sb.AppendLine(Lang.Get("taxidermy:assembly-grass", be.Grass, be.GrassNeeded));
        sb.AppendLine(be.ReadyToSew ? Lang.Get("taxidermy:assembly-next-sew", be.Stitches) : Lang.Get("taxidermy:assembly-next-grass"));
        sb.AppendLine(Lang.Get("taxidermy:assembly-break"));
        return sb.ToString();
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        var be = world.BlockAccessor.GetBlockEntity<BEAssembly>(selection.Position);
        if (be == null) return [];
        if (be.Grass < be.GrassNeeded)
        {
            var grass = world.GetItem(new AssetLocation("game:drygrass"));
            return grass == null ? [] : [new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-addgrass", MouseButton = EnumMouseButton.Right, Itemstacks = [new ItemStack(grass)] }];
        }
        var needles = world.Items.Where(i => i is ItemNeedleThread).Select(i => new ItemStack(i)).ToArray();
        return [new WorldInteraction { ActionLangCode = "taxidermy:blockhelp-sew", MouseButton = EnumMouseButton.Right, Itemstacks = needles }];
    }
}

/// <summary>
/// The unfinished mount: the head (which animal, which coat), the pelt and the grass so far, lying
/// together as a crafting pile. Sewing it turns the block into an ordinary mount (BlockMount /
/// BEMount) carrying the head's data - from there it is exactly a grid-crafted mount.
/// </summary>
public sealed class BEAssembly : BlockEntity
{
    public const string BlockCode = "taxidermy:assembly";

    public ItemStack HeadStack { get; private set; }
    public ItemStack PeltStack { get; private set; }
    public int Grass { get; private set; }
    public int GrassNeeded { get; private set; } = 4;
    public float Rotation { get; private set; }

    /// <summary>
    /// Thread the sewing takes: the same as the grass - 2 small, 4 medium, 6 large, 8 huge (Calm's
    /// 1 + 1 per size step, the body and the head now sewn in one go) and the same as the grid recipe.
    /// </summary>
    public int Stitches => Math.Max(1, GrassNeeded);
    public bool ReadyToSew => HeadStack != null && Grass >= GrassNeeded;

    private List<MeshData> meshes;

    public void Start(ItemStack head, ItemStack pelt, int grassNeeded, float rotation)
    {
        HeadStack = head;
        PeltStack = pelt;
        GrassNeeded = grassNeeded;
        Rotation = rotation;
        Grass = 0;
        MarkDirty(true);
    }

    public void AddGrass()
    {
        Grass = Math.Min(GrassNeeded, Grass + 1);
        MarkDirty(true);
    }

    /// <summary>Sewn: the pile becomes the finished mount, facing the way the pile did.</summary>
    public void Finish()
    {
        var world = Api.World;
        var mountBlock = world.GetBlock(new AssetLocation(Specimen.MountBlock));
        var mountItem = world.GetItem(new AssetLocation(Specimen.MountItem));
        if (mountBlock == null || mountItem == null || HeadStack == null) return;
        var mountStack = new ItemStack(mountItem) { Attributes = HeadStack.Attributes.Clone() };
        mountStack.Attributes.RemoveAttribute(Specimen.AntlersKey);
        float rotation = Rotation;
        var pos = Pos.Copy();
        world.BlockAccessor.SetBlock(mountBlock.Id, pos, mountStack);
        world.BlockAccessor.GetBlockEntity<BEMount>(pos)?.SetRotation(rotation);
        world.PlaySoundAt(new AssetLocation("game:sounds/block/leather"), pos.X + 0.5, pos.Y + 0.3, pos.Z + 0.5, null);
    }

    /// <summary>Everything that went in - head, pelt, grass.</summary>
    public ItemStack[] Parts()
    {
        var parts = new List<ItemStack>();
        if (HeadStack != null) parts.Add(HeadStack.Clone());
        if (PeltStack != null) parts.Add(PeltStack.Clone());
        var grass = Api?.World.GetItem(new AssetLocation("game:drygrass"));
        if (grass != null && Grass > 0) parts.Add(new ItemStack(grass, Grass));
        return parts.ToArray();
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        Rebuild();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (HeadStack != null) tree.SetItemstack("head", HeadStack);
        if (PeltStack != null) tree.SetItemstack("pelt", PeltStack);
        tree.SetInt("grass", Grass);
        tree.SetInt("grassNeeded", GrassNeeded);
        tree.SetFloat("rotation", Rotation);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        HeadStack = tree.GetItemstack("head");
        HeadStack?.ResolveBlockOrItem(worldAccessForResolve);
        PeltStack = tree.GetItemstack("pelt");
        PeltStack?.ResolveBlockOrItem(worldAccessForResolve);
        Grass = tree.GetInt("grass");
        GrassNeeded = tree.GetInt("grassNeeded", 4);
        Rotation = tree.GetFloat("rotation");
        if (Api != null)
        {
            Rebuild();
            if (Api.Side == EnumAppSide.Client) MarkDirty(true);
        }
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        var list = meshes;
        if (list != null) foreach (var m in list) mesher.AddMeshData(m);
        return true;
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// Main thread only (atlas work). Laid out in the animal's rest frame - its front towards -X,
    /// like the mounts - then turned by <see cref="Rotation"/>, which points that front at the
    /// player. Seen from where the player stood: the pelt flat in the middle at its real size, the
    /// head on the LEFT (-Z, the animal's right) nose towards the player, the grass on the RIGHT
    /// (+Z) - Calm, 2026-10-01.
    /// </summary>
    private void Rebuild()
    {
        meshes = null;
        if (Api is not ICoreClientAPI capi || HeadStack == null) return;
        var data = Specimen.Of(HeadStack);
        var def = data == null ? null : capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        if (def == null) return;
        var list = new List<MeshData>();
        try
        {
            var pelt = PeltMesh(capi);
            if (pelt != null) list.Add(pelt);
            if (MountMesher.GetHeadMesh(capi, data, def, true)?.Clone() is { } head)
            {
                // As a head lies on the ground (its neck sinking in, MountMesher.BuildHead flat),
                // raised only by the pelt's thickness (Calm, 2026-10-01).
                head.Translate(0, PeltThickness(pelt), -0.26f);
                list.Add(head);
            }
            list.AddRange(GrassMeshes(capi));
        }
        catch (Exception e)
        {
            capi.Logger.Error("[Taxidermy] Cannot draw the unfinished mount at {0}: {1}", Pos, e);
        }
        foreach (var m in list) m.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, Rotation, 0);
        meshes = list;
    }

    /// <summary>How thick the pelt lies - its top, capped so a fluffy or odd pelt model cannot lift the head far.</summary>
    private static float PeltThickness(MeshData pelt)
    {
        if (pelt == null || pelt.VerticesCount == 0) return 0;
        float max = float.MinValue;
        for (int i = 0; i < pelt.VerticesCount; i++) max = Math.Max(max, pelt.xyz[i * 3 + 1]);
        return Math.Clamp(max, 0, 0.08f);
    }

    /// <summary>Lowest point onto y = <paramref name="y"/>: item models are not all built from the floor up.</summary>
    private static void SitOn(MeshData m, float y)
    {
        if (m.VerticesCount == 0) return;
        float min = float.MaxValue;
        for (int i = 0; i < m.VerticesCount; i++) min = Math.Min(min, m.xyz[i * 3 + 1]);
        m.Translate(0, y - min, 0);
    }

    /// <summary>The pelt laid flat at its real size: our own pelts draw themselves; any other pelt uses its item model.</summary>
    private MeshData PeltMesh(ICoreClientAPI capi)
    {
        if (PeltStack == null) return null;
        var m = PeltStack.Collectible is IContainedMeshSource src
            ? src.GenMesh(new DummySlot(PeltStack), capi.BlockTextureAtlas, Pos)?.Clone()
            : ItemMesh(capi, PeltStack.Item);
        if (m != null) SitOn(m, 0);
        return m;
    }

    /// <summary>
    /// Dry grass bundles heaped on the right of the pile - one per bundle added, in fixed spots so
    /// they never jump about; a second layer on top once the first is full.
    /// </summary>
    private IEnumerable<MeshData> GrassMeshes(ICoreClientAPI capi)
    {
        if (Grass <= 0) yield break;
        var grassItem = capi.World.GetItem(new AssetLocation("game:drygrass"));
        var one = grassItem == null ? null : ItemMesh(capi, grassItem);
        if (one == null) yield break;
        SitOn(one, 0);
        (float x, float z, float yaw)[] spots =
        [
            (0.00f, 0.22f, 0.3f), (0.18f, 0.30f, -0.4f), (-0.16f, 0.30f, 1.2f), (0.08f, 0.40f, 2.0f),
            (-0.06f, 0.14f, -1.0f), (0.22f, 0.16f, 0.7f), (-0.20f, 0.18f, 2.6f), (0.12f, 0.24f, -2.2f),
        ];
        for (int i = 0; i < Grass && i < spots.Length * 2; i++)
        {
            var s = spots[i % spots.Length];
            var m = one.Clone();
            m.Scale(new Vec3f(0.5f, 0, 0.5f), 0.7f, 0.7f, 0.7f);
            m.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, s.yaw, 0);
            m.Translate(s.x, 0.01f + (i / spots.Length) * 0.07f, s.z);
            yield return m;
        }
    }

    /// <summary>
    /// An item's own model with its textures put in the BLOCK atlas, so it can go into the chunk
    /// mesh like a placed block. The shape's own texture list first, then the item's overrides.
    /// </summary>
    private static MeshData ItemMesh(ICoreClientAPI capi, Item item)
    {
        if (item?.Shape?.Base == null) return null;
        var textures = new Dictionary<string, AssetLocation>();
        var shapeLoc = item.Shape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        var shape = capi.Assets.TryGet(shapeLoc)?.ToObject<Shape>();
        if (shape?.Textures != null) foreach (var kv in shape.Textures) textures[kv.Key] = kv.Value;
        if (item.Textures != null) foreach (var kv in item.Textures) textures[kv.Key] = kv.Value.Base;
        var source = new ContainedTextureSource(capi, capi.BlockTextureAtlas, textures, "taxidermy unfinished mount " + item.Code);
        capi.Tesselator.TesselateItem(item, out var mesh, source);
        return mesh;
    }
}
