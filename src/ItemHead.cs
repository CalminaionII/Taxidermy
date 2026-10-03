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

    /// <summary>
    /// A head's "pelt" follows the CURRENT rules, not the ones it was harvested under (Calm,
    /// 2026-09-24): whenever it enters a slot or the grid, its token is worked out again from the
    /// animal it records - so switching CustomPelts off turns every head in play to a plain pelt
    /// of its size (own pelts stop dropping), and on again back to its own. The same pass gives
    /// heads made in 1.0.0 (live since ~2026-09-22), which carry only the flat "hideSize" its
    /// recipes matched, the "pelt" 1.0.1's recipes match. Both recipe sets always exist.
    /// </summary>
    public override bool MatchesForCrafting(ItemStack inputStack, IRecipeBase gridRecipe, IRecipeIngredient ingredient)
    {
        SyncPeltToken(inputStack);
        return base.MatchesForCrafting(inputStack, gridRecipe, ingredient);
    }

    /// <summary>
    /// One plain head per state, and since 2026-10-02 the ONLY head page (Calm: one generic head
    /// instead of every animal's - about 320 pages); the creative tabs still list every animal and
    /// coat. The handbook lists a barrel recipe on a page only when the recipe's output EQUALS the
    /// page's stack, and the barrel's output carries no animal - so no per-animal page ever showed
    /// it. Not in the creative tabs: a head with no animal cannot be mounted.
    /// </summary>
    public override List<ItemStack> GetHandBookStacks(ICoreClientAPI capi) => [new ItemStack(this)];

    /// <summary>Also on entering any slot - the grid may test attributes before it asks MatchesForCrafting.</summary>
    public override void OnModifiedInInventorySlot(IWorldAccessor world, ItemSlot slot, ItemStack extractedStack = null)
    {
        base.OnModifiedInInventorySlot(world, slot, extractedStack);
        if (SyncPeltToken(slot?.Itemstack)) slot.MarkDirty();
    }

    internal bool SyncPeltToken(ItemStack stack)
    {
        // The SERVER's setting decides: a client re-stamping from its own local config would flip the
        // head back (Calm saw "its own pelt" with own pelts switched off). The server's stamp syncs.
        if (api.Side != EnumAppSide.Server) return false;
        var data = Specimen.Of(stack);
        if (data == null) return false;
        var system = api.ModLoader.GetModSystem<TaxidermyModSystem>();
        var def = system.Find(data);
        if (def == null) return false;
        var props = api.World.GetEntityType(new AssetLocation(data.GetString("entityCode", "")));
        if (props == null) return false;          // its animal's mod is gone: leave the head as it is
        string token = Specimen.PeltToken(def, props, system.Config.OwnPeltsFor(def));
        string size = def.SizeFor(props.Code.ToString());
        if (data.GetString("pelt") == token && stack.Attributes.GetString("pelt") == token
            && data.GetString("hideSize") == size) return false;
        data.SetString("pelt", token);
        data.SetString("hideSize", size);
        Specimen.SetSize(stack, data);
        return true;
    }

    public override string GetHeldItemName(ItemStack itemStack)
    {
        var data = Specimen.Of(itemStack);
        if (data == null) return base.GetHeldItemName(itemStack);
        return Lang.Get("taxidermy:item-head-" + Variant["state"] + "-of", Specimen.AnimalNameNoSex(data));
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var data = Specimen.Of(inSlot.Itemstack);
        if (data == null)
        {
            // The plain handbook head (GetHandBookStacks) - the only way to see one in normal play.
            dsc.AppendLine(Lang.Get("taxidermy:head-generic"));
            dsc.AppendLine(Lang.Get("taxidermy:head-see-handbook"));
            return;
        }
        var def = api.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        string sizeCode = data.GetString("hideSize") ?? def?.HideSize ?? "medium";
        // The steps moved to the handbook page (Calm, 2026-09-29); the tooltip keeps what is
        // particular to THIS head - sex and, preserved, the pelt its mount takes (Calm: no size on raw heads).
        if (ItemTaxidermyHide.SexLine(world, data) is { } sex) dsc.AppendLine(sex);
        if (IsPreserved)
        {
            // "pelt" is stamped by the server under its category's current own-pelt switch (SyncPeltToken),
            // so this follows the setting just as the recipe does: own pelts on names the animal's
            // pelt; off, any plain pelt of the size (Calm, 2026-09-29).
            string pelt = data.GetString("pelt");
            bool own = pelt != null && !pelt.StartsWith("size-");
            string label = ItemTaxidermyHide.AnimalLabel(data);
            string animal = label.Length > 0 ? char.ToUpperInvariant(label[0]) + label.Substring(1) : label;
            // Pelts made per sex ({gender} in the definition's label) name it too: "Wolf pelt (male)" (Calm, 2026-10-02).
            string peltSex = own && def?.Pelt?.Contains("{gender}") == true ? PeltSex(world, data) : null;
            if (peltSex != null) dsc.AppendLine(Lang.Get("taxidermy:head-needs-pelt-own-sex", animal, peltSex));
            else if (own) dsc.AppendLine(Lang.Get("taxidermy:head-needs-pelt-own", animal));
            else
            {
                // "Mount pelt: Medium plain pelt" - one line, size first (Calm, 2026-09-29).
                string size = Lang.Get("taxidermy:size-" + sizeCode);
                dsc.AppendLine(Lang.Get("taxidermy:head-needs-pelt-plain", size.Length > 0 ? char.ToUpperInvariant(size[0]) + size.Substring(1) : size));
            }
        }
        var attachments = Specimen.Attachments(data);
        if (attachments.Length > 0)
            dsc.AppendLine(Lang.Get("taxidermy:specimen-with", string.Join(", ", attachments.Select(c => ItemMount.AttachmentName(world, c)))));
        dsc.AppendLine(Lang.Get("taxidermy:head-see-handbook"));
    }

    /// <summary>"male" / "female" from the animal's gender variant, lower case for the pelt line; null without one.</summary>
    private static string PeltSex(IWorldAccessor world, ITreeAttribute data)
    {
        var props = world.GetEntityType(new AssetLocation(data.GetString("entityCode", "")));
        if (props?.Variant == null || !props.Variant.TryGetValue("gender", out var sex)) return null;
        return (Lang.HasTranslation("taxidermy:sex-" + sex) ? Lang.Get("taxidermy:sex-" + sex) : sex).ToLowerInvariant();
    }

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        // A plain head (the handbook's, and every recipe output it lists) shows the example wolf.
        var data = Specimen.Of(itemstack) ?? Specimen.Example(capi);
        var def = data == null ? null : capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        if (def != null)
        {
            var meshRef = MountMesher.GetHeadItemMesh(capi, data, def);
            if (meshRef != null) renderinfo.ModelRef = meshRef;
            // A head in hand is about to be placed: build its ground and mount meshes now, so the
            // display has them on its first look instead of flashing the item's default mesh (Calm,
            // 2026-10-03: placing a head flashed a hide). Cached, so a no-op after the first frame.
            if (target == EnumItemRenderTarget.HandTp && Specimen.Of(itemstack) != null) BuildBoth(capi, data, def);
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
        // On the ground the head is settled flush by the head alone, its neck going into the floor (MountMesher.BuildHead, flat).
        bool onGround = atBlockPos != null && capi.World.BlockAccessor.GetBlock(atBlockPos) is BlockGroundStorage;
        if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) BuildBoth(capi, data, def);
        var cached = MountMesher.TryGetHeadMesh(capi, data, def, onGround);
        if (cached != null) return onAntlerMount ? ForAntlerMount(cached.Clone(), data) : ForGround(cached.Clone());

        // Returning null makes the display cache vanilla's default mesh - under our WAIT key while the
        // head is unbuilt (GetMeshCacheKey), so once both versions exist the key changes and the
        // redraw asks here again (DisplayMeshes).
        var pos = atBlockPos?.Copy();
        capi.Event.EnqueueMainThreadTask(() =>
        {
            BuildBoth(capi, data, def);
            DisplayMeshes.Redraw(capi, pos);
        }, "taxidermy-head-mesh");
        return null;
    }

    /// <summary>Main thread: both versions of the head - on the ground (flat) and upright - so one key covers every display.</summary>
    private static void BuildBoth(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        MountMesher.GetHeadMesh(capi, data, def, false);
        MountMesher.GetHeadMesh(capi, data, def, true);
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
    internal static MeshData ForGround(MeshData m)
    {
        m.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, GameMath.PIHALF, 0);
        return m;
    }

    public string GetMeshCacheKey(ItemSlot slot)
    {
        var data = Specimen.Of(slot.Itemstack);
        string key = Code + "|" + (data == null ? "" : MountMesher.IdentityKey(data) + "|" + data.GetFloat("offx") + "|" + data.GetFloat("offy") + "|" + data.GetFloat("offz") + "|" + data.GetFloat("rot") + "|" + data.GetFloat("tilt"));
        return HeadBuilt(data) ? key : key + DisplayMeshes.Wait;
    }

    /// <summary>
    /// True once both versions of this head are built, or when there is nothing of ours to build
    /// (no specimen, no definition - the display's default mesh is then the final answer).
    /// </summary>
    private bool HeadBuilt(ITreeAttribute data)
    {
        if (data == null || api is not ICoreClientAPI capi) return true;
        var def = capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        if (def == null) return true;
        return MountMesher.TryGetHeadMesh(capi, data, def, false) != null && MountMesher.TryGetHeadMesh(capi, data, def, true) != null;
    }
}
