using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// One hide family for every animal with a pelt design: <c>game:hide-&lt;state&gt;-taxidermy-&lt;size&gt;</c>
/// - raw, oiled, pelt, soaked, salted in small..huge, 20 codes in all (Calm, 2026-09-24).
///
/// The STATE and SIZE are in the code because vanilla reads them there: its recipes take hides
/// by <c>hide-pelt-*</c> / <c>hide-raw-*</c> plus a size tag, and build the next state's code from
/// the old one (<c>hide-raw-X</c> -> <c>hide-oiled-X</c>). So these work in every vanilla recipe
/// that takes a hide by size - clothes, lamellar, bellows, oiling, salting, the soaking barrel.
/// WHICH ANIMAL, and its exact coat, rides on the stack as the same specimen tree a head carries,
/// the way Rift Weapons keeps metal in the code only where another mod must see it.
///
/// The animal is kept through every step: curing (<see cref="OnTransitionNow"/>), vanilla's
/// oiling and salting grid recipes (<see cref="OnCreatedByCrafting"/>, called on the OUTPUT, so
/// vanilla's own hides are untouched) and the barrel (BarrelPatch).
/// </summary>
public sealed class ItemTaxidermyHide : Item, IContainedMeshSource
{
    public const string Group = "taxidermy";
    private const string MeshCacheKey = "taxidermy-hide-meshes";
    private const string RefCacheKey = "taxidermy-hide-refs";

    public string State => Variant["type"];
    public string Size => Variant["size"];

    public static string CodeFor(string state, string size) => $"game:hide-{state}-{Group}-{size}";

    // ---------------------------------------------------------------- names

    /// <summary>
    /// Vanilla's own pattern for species hides, with the animal's own name from its lang file:
    /// "Raw brown hare hide (small)", "Brown hare pelt (small)", "Oiled jackrabbit hide".
    /// </summary>
    public override string GetHeldItemName(ItemStack itemStack)
    {
        var data = Specimen.Of(itemStack);
        if (data == null) return Lang.Get("taxidermy:hide-generic-name-" + State);
        string animal = AnimalLabel(data);
        return Lang.Get("taxidermy:hide-name-" + State, animal, Lang.Get("taxidermy:size-" + Size), Capitalise(animal));
    }

    /// <summary>
    /// "Brown hare (male)" -> "brown hare". Vanilla writes species hides in lower case mid-sentence
    /// ("Raw arctic fox hide"); a name written wholly in title case by its mod (a proper name like
    /// "African Forest Elephant") is left as it is.
    /// </summary>
    public static string AnimalLabel(ITreeAttribute data)
    {
        string name = Regex.Replace(Specimen.AnimalName(data), @"\s*\([^)]*\)\s*$", "").Trim();
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool titleCase = words.Length > 1 && words.All(w => char.IsUpper(w[0]));
        return titleCase || name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>
    /// Sex and size under the name (Calm, 2026-09-29) - male and female hides stack apart but
    /// share a name. No sex line for an animal without a gender variant, or whose mod is gone.
    /// </summary>
    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var data = Specimen.Of(inSlot.Itemstack);
        if (data == null)
        {
            dsc.AppendLine(Lang.Get("taxidermy:hide-generic"));
            return;
        }
        if (SexLine(world, data) is { } sex) dsc.AppendLine(sex);
        dsc.AppendLine(State == "pelt" ? PeltSizeLine(Size) : SizeLine(Size));
    }

    public static string PeltSizeLine(string size) => Lang.Get("taxidermy:hide-info-pelt-size", Capitalise(Lang.Get("taxidermy:size-" + size)));

    /// <summary>"Sex: Male" from the entity's gender variant - null without one. Heads use it too.</summary>
    public static string SexLine(IWorldAccessor world, ITreeAttribute data)
    {
        var code = data?.GetString("entityCode");
        var props = code == null ? null : world.GetEntityType(new AssetLocation(code));
        if (props?.Variant == null || !props.Variant.TryGetValue("gender", out var sex)) return null;
        return Lang.Get("taxidermy:hide-info-sex", Lang.HasTranslation("taxidermy:sex-" + sex) ? Lang.Get("taxidermy:sex-" + sex) : Capitalise(sex));
    }

    public static string SizeLine(string size) => Lang.Get("taxidermy:hide-info-size", Capitalise(Lang.Get("taxidermy:size-" + size)));

    // ---------------------------------------------------------------- keeping the animal

    public override ItemStack OnTransitionNow(ItemSlot slot, TransitionableProperties props)
    {
        var result = base.OnTransitionNow(slot, props);
        CarryAnimal(Specimen.Of(slot.Itemstack), result);
        return result;
    }

    /// <summary>
    /// Vanilla calls this on the OUTPUT's collectible after a grid craft, so it runs for vanilla's
    /// raw -> oiled and raw -> salted recipes when they make one of ours, and never for vanilla's own
    /// hides. Only the animal is copied - not the spoil timer, which a blanket copyAttributesFrom on
    /// vanilla's recipes would have dragged along for every hide in the game.
    /// </summary>
    public override void OnCreatedByCrafting(ItemSlot[] allInputslots, ItemSlot outputSlot, IRecipeBase byRecipe)
    {
        base.OnCreatedByCrafting(allInputslots, outputSlot, byRecipe);
        if (outputSlot?.Itemstack == null || Specimen.Of(outputSlot.Itemstack) != null) return;
        var source = allInputslots?.FirstOrDefault(s => s?.Itemstack?.Collectible is ItemTaxidermyHide && Specimen.Of(s.Itemstack) != null);
        if (source != null) CarryAnimal(Specimen.Of(source.Itemstack), outputSlot.Itemstack);
    }

    // ---------------------------------------------------------------- stacking

    /// <summary>
    /// Two hides stack when they are the same animal, same gender, in the same coat (Calm,
    /// 2026-09-29: male and female stay separate). The specimen tree is the head's, and carries
    /// what only a head needs - antlers, pose - so two stags' hides would never stack on the tree
    /// alone. Compared here by <see cref="StackKey"/> instead; everything outside the tree as
    /// vanilla does. Hides made before this stack too, since nothing on them is rewritten.
    /// </summary>
    public override bool Equals(ItemStack thisStack, ItemStack otherStack, params string[] ignoreAttributeSubTrees)
    {
        var a = Specimen.Of(thisStack);
        var b = Specimen.Of(otherStack);
        if (a == null || b == null) return base.Equals(thisStack, otherStack, ignoreAttributeSubTrees);
        var ignore = (ignoreAttributeSubTrees ?? []).Append(Specimen.Key).ToArray();
        return base.Equals(thisStack, otherStack, ignore) && StackKey(a) == StackKey(b);
    }

    /// <summary>Definition, animal (gender included), coat: "wolf|game:wolf-eurasian-adult-male|3".</summary>
    private static string StackKey(ITreeAttribute data) =>
        data.GetString("definition") + "|" + data.GetString("entityCode") + "|" + data.GetInt("textureIndex");

    // ---------------------------------------------------------------- the pelt label

    /// <summary>
    /// A pelt's "pelt" label follows the CURRENT definition, like a head's (ItemHead.SyncPeltToken):
    /// since 1.0.4 the labels carry the sex (Calm, 2026-10-01: a ram head takes a ram's pelt), and a
    /// pelt made before that is relabelled from the animal it remembers the first time it is moved
    /// or put in the grid - so no pelt in a world goes to waste. Server only; the change syncs down.
    /// </summary>
    internal bool SyncPeltToken(ItemStack stack)
    {
        if (api?.Side != EnumAppSide.Server) return false;
        var data = Specimen.Of(stack);
        if (data == null) return false;
        var def = api.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        var props = def == null ? null : api.World.GetEntityType(new AssetLocation(data.GetString("entityCode", "")));
        if (props == null) return false;
        string token = Specimen.PeltToken(def, props);
        if (data.GetString("pelt") == token && stack.Attributes.GetString("pelt") == token) return false;
        data.SetString("pelt", token);
        stack.Attributes.SetString("pelt", token);
        return true;
    }

    public override bool MatchesForCrafting(ItemStack inputStack, IRecipeBase gridRecipe, IRecipeIngredient ingredient)
    {
        SyncPeltToken(inputStack);
        return base.MatchesForCrafting(inputStack, gridRecipe, ingredient);
    }

    public override void OnModifiedInInventorySlot(IWorldAccessor world, ItemSlot slot, ItemStack extractedStack = null)
    {
        base.OnModifiedInInventorySlot(world, slot, extractedStack);
        if (SyncPeltToken(slot?.Itemstack)) slot.MarkDirty();
    }

    public static void CarryAnimal(ITreeAttribute data, ItemStack target)
    {
        if (data == null || target?.Collectible is not ItemTaxidermyHide) return;
        var copy = data.Clone();
        target.Attributes[Specimen.Key] = copy;
        Specimen.SetSize(target, copy);
    }

    // ---------------------------------------------------------------- the handbook

    /// <summary>
    /// ONE generic page per stage - raw, oiled, salted, soaked, pelt - on the medium code, instead
    /// of a page for every animal and coat (Calm, 2026-10-02); the other sizes have none. The
    /// creative tab still lists every pelt. Drawn as the example wolf's (Specimen.Example), named
    /// "Animal pelt" and so on, so it is never mistaken for the game's own hides.
    /// </summary>
    public override List<ItemStack> GetHandBookStacks(ICoreClientAPI capi) =>
        Size == "medium" ? [new ItemStack(this)] : null;

    private ItemStack exampleStack;

    /// <summary>A hide with no animal draws as the example wolf's, in this stage.</summary>
    private ItemStack Drawn(ICoreClientAPI capi, ItemStack stack)
    {
        if (Specimen.Of(stack) != null) return stack;
        if (exampleStack == null && Specimen.Example(capi) is { } example)
        {
            exampleStack = new ItemStack(this);
            exampleStack.Attributes[Specimen.Key] = example.Clone();
        }
        return exampleStack ?? stack;
    }

    // ---------------------------------------------------------------- the pelt as drawn

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        itemstack = Drawn(capi, itemstack);
        // In hand every pelt is the same: its own copy centred on the block and exactly one block
        // across, so one tpHandTransform fits every animal (Calm, 2026-10-03: only the hyena sat
        // right - the models differ in size and in how far they reach from the block centre).
        // The icon and dropped pelts keep the shrink-to-fit below, which Calm is happy with.
        bool inHand = target is EnumItemRenderTarget.HandTp or EnumItemRenderTarget.HandTpOff;
        var meshRef = inHand ? GetHandMeshRef(capi, itemstack) : GetMeshRef(capi, itemstack);
        if (meshRef != null)
        {
            renderinfo.ModelRef = meshRef;
            if (!inHand) renderinfo.Transform = FitTransform(capi, itemstack, target, renderinfo.Transform);
        }
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
    }

    /// <summary>
    /// The icon and dropped-item transforms were tuned on the hare's pelt, about one block
    /// across. One code serves every animal of a size - a gazelle's and a moose's pelt are both
    /// "large" - so a bigger pelt is shrunk to that footprint here, per pelt, instead of per code.
    /// The hand does not come here: it draws its own centred one-block copy (GetHandMeshRef).
    /// Never enlarged. Placed on the ground (ground storage) it keeps its real size, as vanilla's
    /// bear rugs do.
    /// </summary>
    private const float TunedExtent = 1f;
    private const string TransformCacheKey = "taxidermy-hide-transforms";

    private ModelTransform FitTransform(ICoreClientAPI capi, ItemStack stack, EnumItemRenderTarget target, ModelTransform baseTransform)
    {
        if (baseTransform == null || MeshKey(stack) is not { } key) return baseTransform;
        var extents = ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey + "-extent", () => new ConcurrentDictionary<string, float>());
        if (!extents.TryGetValue(key, out float extent) || extent <= TunedExtent) return baseTransform;
        var cache = ObjectCacheUtil.GetOrCreate(capi, TransformCacheKey, () => new Dictionary<string, FittedTransform>());
        string ck = key + "|" + target + "|" + Code;
        // Rebuilt whenever the base transform changes, so .tfedit moves big pelts live too (Calm,
        // 2026-10-03: a pelt in hand would not move - the shrunk copy was made once and kept).
        if (cache.TryGetValue(ck, out var cached) && Same(cached.Snapshot, baseTransform)) return cached.Value;
        var snapshot = Snapshot(baseTransform);
        var fitted = baseTransform.Clone();
        float f = TunedExtent / extent;
        fitted.ScaleXYZ = new Vec3f(fitted.ScaleXYZ.X * f, fitted.ScaleXYZ.Y * f, fitted.ScaleXYZ.Z * f);
        cache[ck] = new FittedTransform(snapshot, fitted);
        return fitted;
    }

    private sealed record FittedTransform(float[] Snapshot, ModelTransform Value);

    private static bool Same(float[] s, ModelTransform t) =>
        s[0] == t.Translation.X && s[1] == t.Translation.Y && s[2] == t.Translation.Z &&
        s[3] == t.Rotation.X && s[4] == t.Rotation.Y && s[5] == t.Rotation.Z &&
        s[6] == t.Origin.X && s[7] == t.Origin.Y && s[8] == t.Origin.Z &&
        s[9] == t.ScaleXYZ.X && s[10] == t.ScaleXYZ.Y && s[11] == t.ScaleXYZ.Z;

    private static float[] Snapshot(ModelTransform t) =>
    [
        t.Translation.X, t.Translation.Y, t.Translation.Z,
        t.Rotation.X, t.Rotation.Y, t.Rotation.Z,
        t.Origin.X, t.Origin.Y, t.Origin.Z,
        t.ScaleXYZ.X, t.ScaleXYZ.Y, t.ScaleXYZ.Z,
    ];

    private const string HandRefCacheKey = "taxidermy-hide-hand-refs";

    /// <summary>The pelt for the hand: centred on (0.5, 0, 0.5), its widest side exactly one block.</summary>
    private MultiTextureMeshRef GetHandMeshRef(ICoreClientAPI capi, ItemStack stack)
    {
        string key = MeshKey(stack);
        if (key == null) return null;
        var refs = ObjectCacheUtil.GetOrCreate(capi, HandRefCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());
        if (refs.TryGetValue(key, out var cached)) return cached;
        var mesh = GetMesh(capi, stack)?.Clone();
        if (mesh == null || mesh.VerticesCount == 0) return null;
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, z0 = float.MaxValue, z1 = float.MinValue;
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = mesh.xyz[i * 3], y = mesh.xyz[i * 3 + 1], z = mesh.xyz[i * 3 + 2];
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y);
            z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
        }
        mesh.Translate(0.5f - (x0 + x1) / 2, -y0, 0.5f - (z0 + z1) / 2);
        float f = 1f / Math.Max(x1 - x0, z1 - z0);
        mesh.Scale(new Vec3f(0.5f, 0, 0.5f), f, f, f);
        var meshRef = capi.Render.UploadMultiTextureMesh(mesh);
        refs[key] = meshRef;
        return meshRef;
    }

    private MultiTextureMeshRef GetMeshRef(ICoreClientAPI capi, ItemStack stack)
    {
        string key = MeshKey(stack);
        if (key == null) return null;
        var refs = ObjectCacheUtil.GetOrCreate(capi, RefCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());
        if (refs.TryGetValue(key, out var cached)) return cached;
        var mesh = GetMesh(capi, stack);
        if (mesh == null) return null;
        var meshRef = capi.Render.UploadMultiTextureMesh(mesh);
        refs[key] = meshRef;
        return meshRef;
    }

    /// <summary>
    /// Ground storage asks here from the TESSELATION thread; the mesh is built on the main thread
    /// (atlas inserts are GPU work), so an unbuilt one is queued and redrawn - the head does the same.
    /// </summary>
    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        if (api is not ICoreClientAPI capi || MeshKey(slot.Itemstack) is not { } key) return null;
        var cache = ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey, () => new ConcurrentDictionary<string, MeshData>());
        if (cache.TryGetValue(key, out var built)) return built?.Clone();
        if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) return GetMesh(capi, slot.Itemstack)?.Clone();

        var stack = slot.Itemstack.Clone();
        var pos = atBlockPos?.Copy();
        capi.Event.EnqueueMainThreadTask(() =>
        {
            GetMesh(capi, stack);
            DisplayMeshes.Redraw(capi, pos);
        }, "taxidermy-hide-mesh");
        return null;
    }

    /// <summary>Ends in DisplayMeshes.Wait while our mesh is unbuilt, so the display's default mesh never sits under the real key.</summary>
    public string GetMeshCacheKey(ItemSlot slot)
    {
        string key = MeshKey(slot.Itemstack);
        string displayKey = Code + "|" + (key ?? "");
        if (key == null || api is not ICoreClientAPI capi) return displayKey;
        var cache = ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey, () => new ConcurrentDictionary<string, MeshData>());
        // A failed build is cached as null and counts as done - vanilla's mesh is then the answer.
        return cache.ContainsKey(key) ? displayKey : displayKey + DisplayMeshes.Wait;
    }

    /// <summary>What makes two stacks look different: state, animal, coat. Null: no design, draw vanilla's hide.</summary>
    private string MeshKey(ItemStack stack)
    {
        var data = Specimen.Of(stack);
        if (data == null) return null;
        return State + "|" + data.GetString("entityCode") + "|" + data.GetInt("textureIndex");
    }

    private MeshData GetMesh(ICoreClientAPI capi, ItemStack stack)
    {
        string key = MeshKey(stack);
        if (key == null) return null;
        var cache = ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey, () => new ConcurrentDictionary<string, MeshData>());
        // A failed build is remembered too (as null), or it would be retried - and logged - every frame.
        if (cache.TryGetValue(key, out var cached)) return cached;
        MeshData mesh = null;
        try { mesh = Build(capi, Specimen.Of(stack)); }
        catch (Exception e) { capi.Logger.Error("[Taxidermy] Cannot build the hide for {0}: {1}", Specimen.Of(stack)?.GetString("entityCode"), e); }
        cache[key] = mesh;
        if (mesh != null)
            ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey + "-extent", () => new ConcurrentDictionary<string, float>())[key] = Extent(mesh);
        return mesh;
    }

    /// <summary>The pelt's widest horizontal span, in blocks.</summary>
    private static float Extent(MeshData mesh)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = mesh.xyz[i * 3], z = mesh.xyz[i * 3 + 2];
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (z < z0) z0 = z;
            if (z > z1) z1 = z;
        }
        return mesh.VerticesCount == 0 ? 0 : Math.Max(x1 - x0, z1 - z0);
    }

    private MeshData Build(ICoreClientAPI capi, ITreeAttribute data)
    {
        var def = capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        if (string.IsNullOrEmpty(def?.PeltShape)) return null;
        var props = capi.World.GetEntityType(new AssetLocation(data.GetString("entityCode")));
        if (props?.Client == null) return null;

        // A design per model: "taxidermy:item/pelt/deer-{type}" -> ".../deer-pudu".
        var loc = new AssetLocation(Specimen.Fill(def.PeltShape, props)).WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        var shape = capi.Assets.TryGet(loc)?.ToObject<Shape>();
        if (shape == null)
        {
            capi.Logger.Warning("[Taxidermy] Pelt shape {0} for {1} not found", loc, def.Code);
            return null;
        }
        var source = new HideTextures(capi, props, def.FurTexture, data.GetInt("textureIndex"), State);
        capi.Tesselator.TesselateShape("taxidermy hide", shape, out var mesh, source);
        return mesh;
    }

    /// <summary>
    /// Main thread, at client start: create the caches the tesselation thread reads, so the game's
    /// own ObjectCache is never written from that thread (first use would add the entry there).
    /// </summary>
    public static void PrepareCaches(ICoreClientAPI capi)
    {
        ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey, () => new ConcurrentDictionary<string, MeshData>());
        ObjectCacheUtil.GetOrCreate(capi, MeshCacheKey + "-extent", () => new ConcurrentDictionary<string, float>());
    }

    public static void ClearCache(ICoreClientAPI capi)
    {
        foreach (string refKey in new[] { RefCacheKey, HandRefCacheKey })
        {
            var refs = ObjectCacheUtil.TryGet<Dictionary<string, MultiTextureMeshRef>>(capi, refKey);
            if (refs == null) continue;
            foreach (var r in refs.Values) r?.Dispose();
            refs.Clear();
        }
        ObjectCacheUtil.TryGet<ConcurrentDictionary<string, MeshData>>(capi, MeshCacheKey)?.Clear();
        ObjectCacheUtil.TryGet<ConcurrentDictionary<string, float>>(capi, MeshCacheKey + "-extent")?.Clear();
        ObjectCacheUtil.TryGet<Dictionary<string, FittedTransform>>(capi, TransformCacheKey)?.Clear();
    }

    /// <summary>
    /// Fur side: the animal's own texture in its own coat (textureIndex: 0 the base, i the i-th
    /// alternate - the entity renderer's layout). Leather side: vanilla's flesh, or its cured
    /// pelt leather for a pelt. Oiled, salted and soaked get vanilla's state overlay on both
    /// sides, exactly as its fox and raccoon hides do. An overlay must be the fur texture's own
    /// size: vanilla ships 32x32 and 64x64, and for every other size a pelt uses we ship vanilla's
    /// 64x64 patterns tiled to it (tools/make_hide_overlays.py - vanilla made its bear overlays
    /// the same way). Raw and the cured pelt look alike on top, as vanilla's do; the leather
    /// underneath tells them apart (Calm, 2026-09-24: "copy vanilla").
    /// </summary>
    private sealed class HideTextures : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly Dictionary<string, TextureAtlasPosition> positions = new();

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public HideTextures(ICoreClientAPI capi, EntityProperties props, string furKey, int textureIndex, string state)
        {
            this.capi = capi;
            var atlas = capi.BlockTextureAtlas;
            bool overlay = state is "oiled" or "salted" or "soaked";

            var textures = props.Client.Textures;
            CompositeTexture ct = null;
            if (textures is { Count: > 0 })
                ct = furKey != null && textures.TryGetValue(furKey, out var chosen) ? chosen : textures.Values.First();
            if (ct == null)
            {
                // The model's own texture, for a type whose entity file lists none (the tainted,
                // corrupt and nightmare drifters) - the head mesher falls back the same way.
                var own = props.Client.LoadedShape?.Textures;
                var loc = furKey != null && own != null && own.TryGetValue(furKey, out var named) ? named : own?.Values.FirstOrDefault();
                ct = new CompositeTexture(loc ?? new AssetLocation("game:unknown"));
            }
            var baked = CompositeTexture.Bake(capi.Assets, ct.Clone());
            var variant = baked.BakedVariants is { Length: > 0 } v ? v[GameMath.Mod(textureIndex, v.Length)] : baked;
            var furBase = variant.TextureFilenames?.FirstOrDefault() ?? ct.Base;
            var (w, h) = PngSize(capi, furBase);
            positions["furside"] = Insert(atlas, furBase, overlay ? StageOverlay(capi, state, w, h, props.Code.Domain) : null);

            var leather = new AssetLocation(state == "pelt" ? "game:item/resource/hide/pelt" : "game:item/resource/hide/flesh");
            positions["fleshside"] = Insert(atlas, leather, overlay ? $"game:item/resource/hide/{state}/32x32" : null);
        }

        private TextureAtlasPosition Insert(ITextureAtlasAPI atlas, AssetLocation baseTex, string overlay)
        {
            TextureAtlasPosition pos;
            if (overlay == null) atlas.GetOrInsertTexture(baseTex, out _, out pos);
            else
            {
                var composite = new CompositeTexture(baseTex.Clone())
                {
                    BlendedOverlays = [new BlendedOverlayTexture { Base = new AssetLocation(overlay), BlendMode = EnumColorBlendMode.Normal }]
                };
                atlas.GetOrInsertTexture(composite, out _, out pos);
            }
            return pos ?? atlas.UnknownTexturePosition;
        }

        /// <summary>A PNG's size from its header (bytes 16-23), to pick the overlay that fits.</summary>
        private static (int w, int h) PngSize(ICoreClientAPI capi, AssetLocation tex)
        {
            var asset = capi.Assets.TryGet(tex.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
            var d = asset?.Data;
            if (d == null || d.Length < 24) return (0, 0);
            return ((d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19], (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23]);
        }

        /// <summary>
        /// The stage overlay for a fur texture of exactly this size: vanilla's own for 32x32 and
        /// 64x64; otherwise item/hide/&lt;state&gt;/&lt;w&gt;x&lt;h&gt; in the animal's own domain (an animal mod
        /// can ship its own) or ours. Null: none fits, and the stage shows on the leather only.
        /// </summary>
        private static string StageOverlay(ICoreClientAPI capi, string state, int w, int h, string domain)
        {
            if (w == h && (w == 32 || w == 64)) return $"game:item/resource/hide/{state}/{w}x{h}";
            foreach (var dom in new[] { domain, Specimen.Domain }.Distinct())
                if (capi.Assets.TryGet(new AssetLocation(dom, $"textures/item/hide/{state}/{w}x{h}.png")) != null)
                    return $"{dom}:item/hide/{state}/{w}x{h}";
            return null;
        }

        public TextureAtlasPosition this[string textureCode] =>
            positions.TryGetValue(textureCode, out var pos) ? pos : capi.BlockTextureAtlas.UnknownTexturePosition;
    }
}
