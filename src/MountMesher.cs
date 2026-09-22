using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// Turns a specimen into a static block mesh: the entity's own shape, its attachments
/// (antlers, horns, tusks), frozen at one frame of one of its own animations.
///
/// How: clone the loaded entity shape, step-parent the attachment shapes onto it exactly as
/// the entity renderer does (<c>EntityBehaviorContainer.addGearToShape</c>, vsessentialsmod),
/// resolve joints, run a <c>ClientAnimator</c> to the wanted frame and read its joint
/// matrices, tesselate with joint ids, and multiply every vertex through its joint's matrix.
/// That is what <c>entityanimated.vsh</c> does on the GPU every frame
/// (<c>worldPos = modelMatrix * ElementTransforms[jointId] * vertex</c>), done once on the CPU.
/// The result goes into the chunk mesh, so it is lit like any block - per-vertex light and
/// AO - and costs nothing per frame. No animator is kept.
///
/// Entity textures live in the ENTITY atlas, which a chunk mesh cannot sample. Each one
/// used is inserted into the block atlas (cached by name, so it happens once per texture).
///
/// Meshes are cached per specimen identity + pose; the block entity clones and rotates.
/// All of this is main-thread: atlas insertion is GPU work.
/// </summary>
public static class MountMesher
{
    private const string CacheKey = "taxidermy-mount-meshes";
    private const string ItemCacheKey = "taxidermy-item-meshes";
    private const string HeadCacheKey = "taxidermy-head-meshes";
    private const string HeadItemCacheKey = "taxidermy-head-item-meshes";

    /// <summary>What makes two specimens render differently: animal, coat, attachments.</summary>
    public static string IdentityKey(ITreeAttribute data) =>
        string.Join("|", data.GetString("entityCode"), data.GetInt("textureIndex"), data.GetString("attachments", ""));

    private static string MountKey(ITreeAttribute data, AnimalDefinition def) =>
        string.Join("|", IdentityKey(data), data.GetString("pose"), def.Code, def.Scale);

    public static MeshData Get(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, CacheKey, () => new Dictionary<string, MeshData>());
        string key = MountKey(data, def);
        if (cache.TryGetValue(key, out var cached)) return cached;
        var mesh = Build(capi, data, def);
        if (mesh != null) cache[key] = mesh;
        return mesh;
    }

    // ------------------------------------------------------------------ heads

    public static MeshData TryGetHeadMesh(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, HeadCacheKey, () => new Dictionary<string, MeshData>());
        return cache.TryGetValue(HeadKey(data, def), out var m) ? m : null;
    }

    private static string HeadKey(ITreeAttribute data, AnimalDefinition def) => IdentityKey(data) + "|" + def.Scale + "|" + def.Code;

    /// <summary>
    /// The animal's head, lifted out of its own model: the topmost element whose name contains
    /// "head" and everything under it (antlers and horns step-parent onto "head", so they come
    /// too), eyes removed, in the rest pose, grounded and centred on the block. Falls back to
    /// the whole animal when a model has no element called head. Main thread only.
    /// </summary>
    public static MeshData GetHeadMesh(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, HeadCacheKey, () => new Dictionary<string, MeshData>());
        string key = HeadKey(data, def);
        if (cache.TryGetValue(key, out var cached)) return cached;
        MeshData mesh = null;
        try { mesh = BuildHead(capi, data, def); }
        catch (Exception e) { capi.Logger.Error("[Taxidermy] Cannot build the head for {0}: {1}", data.GetString("entityCode"), e); }
        mesh ??= Get(capi, data, def);
        if (mesh != null) cache[key] = mesh;
        return mesh;
    }

    public static MultiTextureMeshRef GetHeadItemMesh(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var refs = ObjectCacheUtil.GetOrCreate(capi, HeadItemCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());
        string key = HeadKey(data, def);
        if (refs.TryGetValue(key, out var cached)) return cached;
        var source = GetHeadMesh(capi, data, def);
        if (source == null || source.VerticesCount == 0) return null;
        var meshRef = capi.Render.UploadMultiTextureMesh(FitToBlock(source.Clone()));
        refs[key] = meshRef;
        return meshRef;
    }

    private static readonly string[] HeadWords = ["head"];
    private static readonly string[] EyeWords = ["eye"];
    private static readonly string[] NeckWords = ["neck", "throat"];

    private static MeshData BuildHead(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var entityCode = new AssetLocation(data.GetString("entityCode"));
        var props = capi.World.GetEntityType(entityCode);
        var loaded = props?.Client?.LoadedShape;
        if (loaded == null) return null;
        string logName = "taxidermy:head " + entityCode.ToShortString();

        var shape = loaded.Clone();
        var textures = new Dictionary<string, CompositeTexture>();
        foreach (var kv in props.Client.Textures) textures[kv.Key] = kv.Value.Clone();
        var baseKeys = new HashSet<string>(textures.Keys);

        // Attachments first, while the head is still in the tree they step-parent onto.
        var attachments = Specimen.Attachments(data);
        foreach (var code in attachments)
        {
            var item = capi.World.GetItem(new AssetLocation(code));
            var iatta = item == null ? null : IAttachableToEntity.FromCollectible(item);
            if (iatta == null) continue;
            string[] deleted = [];
            shape = EntityBehaviorContainer.addGearToShape(capi, null, capi.BlockTextureAtlas, shape, new ItemStack(item),
                iatta, "default", logName, ref deleted, textures);
        }

        // Find the topmost "head" element and the chain of parents above it.
        var chain = new List<ShapeElement>();
        var head = FindHead(shape.Elements, chain, def.HeadElements is { Length: > 0 } ? def.HeadElements : HeadWords);
        if (head == null)
        {
            capi.Logger.Warning("[Taxidermy] {0} has no element named head - showing the whole animal", entityCode);
            return null;
        }
        // Take the neck too (Calm, 2026-09-21): it is usually the head's parent, and on a wall it
        // is what sinks into the plaque, which makes the head sit and position naturally.
        while (chain.Count > 0 && chain[^1].Name != null && NeckWords.Any(w => chain[^1].Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            head = chain[^1];
            chain.RemoveAt(chain.Count - 1);
        }
        RemoveEyes(head);

        // The head's own coordinates are relative to its parent's frame. Tesselate it as a root
        // - which places it in that frame - then push the vertices through the parents'
        // accumulated transform (vsapi ShapeElement.GetLocalTransformMatrix, in block units).
        // GetLocalTransformMatrix multiplies ONTO its output buffer rather than starting from
        // identity - vanilla's GetInverseModelMatrix resets it before every call, and without
        // that the parents compound and the head lands nowhere near the slot (2026-09-21).
        var chainMatrix = Mat4f.Create();
        var tmp = new float[16];
        foreach (var parent in chain)
        {
            Mat4f.Identity(tmp);
            Mat4f.Mul(chainMatrix, chainMatrix, parent.GetLocalTransformMatrix(0, tmp));
        }

        head.ParentElement = null;
        shape.Elements = [head];
        var texSource = new AtlasSource(capi, textures, baseKeys, data.GetInt("textureIndex"), entityCode, shape.Textures);
        capi.Tesselator.TesselateShape(new TesselationMetaData { TypeForLogging = logName, TexSource = texSource }, shape, out var mesh);
        Transform(mesh, chainMatrix);

        float scale = props.Client.Size * def.Scale;
        if (scale != 1) mesh.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);

        // Feet-on-the-floor equivalent for a head: lowest point at y = 0, centred on the block.
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = mesh.xyz[i * 3], y = mesh.xyz[i * 3 + 1], z = mesh.xyz[i * 3 + 2];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        if (mesh.VerticesCount > 0) mesh.Translate(0.5f - (minX + maxX) / 2, -minY, 0.5f - (minZ + maxZ) / 2);
        return mesh;
    }

    private static ShapeElement FindHead(ShapeElement[] elements, List<ShapeElement> chain, string[] words)
    {
        foreach (var el in elements ?? [])
        {
            if (el.Name != null && words.Any(w => el.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) return el;
            chain.Add(el);
            var found = FindHead(el.Children, chain, words);
            if (found != null) return found;
            chain.RemoveAt(chain.Count - 1);
        }
        return null;
    }

    /// <summary>Eye elements are separate boxes with a see-through texture; on a trophy they read as holes.</summary>
    private static void RemoveEyes(ShapeElement el)
    {
        if (el.Children == null) return;
        el.Children = el.Children.Where(c => c.Name == null || !EyeWords.Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var c in el.Children) RemoveEyes(c);
    }

    /// <summary>Every vertex and normal through one column-major matrix.</summary>
    private static void Transform(MeshData mesh, float[] m)
    {
        var n = new float[3];
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = mesh.xyz[i * 3], y = mesh.xyz[i * 3 + 1], z = mesh.xyz[i * 3 + 2];
            mesh.xyz[i * 3]     = m[0] * x + m[4] * y + m[8] * z + m[12];
            mesh.xyz[i * 3 + 1] = m[1] * x + m[5] * y + m[9] * z + m[13];
            mesh.xyz[i * 3 + 2] = m[2] * x + m[6] * y + m[10] * z + m[14];
            VertexFlags.UnpackNormal(mesh.Flags[i], n);
            float nx = m[0] * n[0] + m[4] * n[1] + m[8] * n[2];
            float ny = m[1] * n[0] + m[5] * n[1] + m[9] * n[2];
            float nz = m[2] * n[0] + m[6] * n[1] + m[10] * n[2];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0.0001f)
                mesh.Flags[i] = (mesh.Flags[i] & VertexFlags.ClearNormalBitMask) | VertexFlags.PackNormal(nx / len, ny / len, nz / len);
        }
    }

    /// <summary>Centre on the block and shrink so the longest side is one block: the item-slot version.</summary>
    private static MeshData FitToBlock(MeshData mesh)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = mesh.xyz[i * 3], y = mesh.xyz[i * 3 + 1], z = mesh.xyz[i * 3 + 2];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        float extent = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        float fit = extent > 0 ? 1f / extent : 1f;
        mesh.Translate(0.5f - (minX + maxX) / 2, 0.5f - (minY + maxY) / 2, 0.5f - (minZ + maxZ) / 2);
        if (Math.Abs(fit - 1) > 0.001f) mesh.Scale(new Vec3f(0.5f, 0.5f, 0.5f), fit, fit, fit);
        return mesh;
    }

    /// <summary>
    /// The same animal as an uploaded mesh for the ITEM (inventory, hand, ground), fitted
    /// into a unit cube and centred so every animal sits in a slot the same way. One GPU
    /// upload per specimen identity + pose, freed when the world is left.
    /// </summary>
    public static MultiTextureMeshRef GetItemMesh(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var refs = ObjectCacheUtil.GetOrCreate(capi, ItemCacheKey, () => new Dictionary<string, MultiTextureMeshRef>());
        string key = MountKey(data, def);
        if (refs.TryGetValue(key, out var cached)) return cached;

        var source = Get(capi, data, def);
        if (source == null || source.VerticesCount == 0) return null;
        var meshRef = capi.Render.UploadMultiTextureMesh(FitToBlock(source.Clone()));
        refs[key] = meshRef;
        return meshRef;
    }

    /// <summary>World teardown: atlas positions and GPU meshes are both world-scoped.</summary>
    public static void Clear(ICoreClientAPI capi)
    {
        foreach (var refKey in new[] { ItemCacheKey, HeadItemCacheKey })
        {
            var refs = ObjectCacheUtil.TryGet<Dictionary<string, MultiTextureMeshRef>>(capi, refKey);
            if (refs == null) continue;
            foreach (var r in refs.Values) r?.Dispose();
            refs.Clear();
        }
        ObjectCacheUtil.TryGet<Dictionary<string, MeshData>>(capi, CacheKey)?.Clear();
        ObjectCacheUtil.TryGet<Dictionary<string, MeshData>>(capi, HeadCacheKey)?.Clear();
    }

    private static MeshData Build(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var entityCode = new AssetLocation(data.GetString("entityCode"));
        var props = capi.World.GetEntityType(entityCode);
        var loaded = props?.Client?.LoadedShape;
        if (loaded == null)
        {
            capi.Logger.Warning("[Taxidermy] No loaded shape for {0} - is the entity from a mod that is not installed?", entityCode);
            return null;
        }
        string logName = "taxidermy:" + entityCode.ToShortString();

        var shape = loaded.Clone();
        // Our own copy of the texture table: attachments add keys to it, and the entity's must not change.
        var textures = new Dictionary<string, CompositeTexture>();
        foreach (var kv in props.Client.Textures) textures[kv.Key] = kv.Value.Clone();
        var baseKeys = new HashSet<string>(textures.Keys);

        var attachments = Specimen.Attachments(data);
        foreach (var code in attachments)
        {
            var item = capi.World.GetItem(new AssetLocation(code));
            var iatta = item == null ? null : IAttachableToEntity.FromCollectible(item);
            if (iatta == null)
            {
                capi.Logger.Warning("[Taxidermy] Attachment {0} on {1} is not attachable - skipped", code, entityCode);
                continue;
            }
            string[] deleted = [];
            shape = EntityBehaviorContainer.addGearToShape(capi, null, capi.BlockTextureAtlas, shape, new ItemStack(item),
                iatta, "default", logName, ref deleted, textures);
        }

        var pose = def.PoseByCode(data.GetString("pose"));
        float[] matrices = null;
        if (!string.IsNullOrEmpty(pose.Animation))
        {
            shape.InitForAnimations(capi.Logger, logName);
            matrices = Evaluate(capi, shape, pose, attachments.Length > 0, entityCode);
        }

        var texSource = new AtlasSource(capi, textures, baseKeys, data.GetInt("textureIndex"), entityCode);
        capi.Tesselator.TesselateShape(new TesselationMetaData
        {
            TypeForLogging = logName,
            TexSource = texSource,
            WithJointIds = matrices != null,
        }, shape, out var mesh);

        if (matrices != null) ApplyJoints(mesh, matrices);
        mesh.CustomInts = null;

        float scale = props.Client.Size * def.Scale;
        if (scale != 1) mesh.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);

        // Feet on the floor and footprint centred on the block, whatever the pose does: a lying
        // or rearing animal's bounding box wanders, and Calm saw poses drift off centre.
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            minX = Math.Min(minX, mesh.xyz[i * 3]); maxX = Math.Max(maxX, mesh.xyz[i * 3]);
            minY = Math.Min(minY, mesh.xyz[i * 3 + 1]);
            minZ = Math.Min(minZ, mesh.xyz[i * 3 + 2]); maxZ = Math.Max(maxZ, mesh.xyz[i * 3 + 2]);
        }
        if (mesh.VerticesCount > 0) mesh.Translate(0.5f - (minX + maxX) / 2, -minY, 0.5f - (minZ + maxZ) / 2);

        if (mesh.VerticesCount == 0) capi.Logger.Warning("[Taxidermy] Empty mesh for {0} pose {1}", entityCode, pose.Code);
        return mesh;
    }

    /// <summary>
    /// The joint matrices for one frame. Vanilla's animator advances by dt and eases in over
    /// several ticks; we want a single exact frame, so: activate (which sets the start frame
    /// and generates the frames), force the easing to 1, and evaluate once with dt = 0.
    /// With dt = 0 <c>RunningAnimation.Progress</c> neither moves the frame nor changes the
    /// easing, and <c>CalcBlendedWeight</c> then gives the pose its full weight.
    /// </summary>
    private static float[] Evaluate(ICoreClientAPI capi, Shape shape, PoseDefinition pose, bool hasAttachments, AssetLocation entityCode)
    {
        var anims = shape.Animations ?? [];
        Animation Find(string code) => anims.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase) && a.KeyFrames?.Length > 0 && a.QuantityFrames > 0);

        // Deer and the like carry "-antlers" variants of their animations, played when the
        // antler shape is attached. Use it when it exists, silently otherwise.
        var anim = (hasAttachments ? Find(pose.Animation + "-antlers") : null) ?? Find(pose.Animation);
        if (anim == null)
        {
            capi.Logger.Warning("[Taxidermy] {0} has no animation '{1}' for pose '{2}' - using the rest pose", entityCode, pose.Animation, pose.Code);
            return null;
        }

        var animator = new ClientAnimator(() => 1.0, anims, shape.Elements, shape.JointsById);
        float frame = GameMath.Clamp(pose.Frame, 0, anim.QuantityFrames - 1);
        var meta = new AnimationMetaData
        {
            Code = anim.Code,
            Animation = anim.Code,
            Weight = 1,
            AnimationSpeed = 1,
            EaseInSpeed = 1,
            EaseOutSpeed = 1,
            BlendMode = EnumAnimationBlendMode.Average,
            StartFrameOnce = frame,
        }.Init();
        var active = new Dictionary<string, AnimationMetaData> { [anim.Code] = meta };

        animator.OnFrame(active, 0);
        var state = animator.GetAnimationState(anim.Code);
        state.CurrentFrame = frame;
        state.EasingFactor = 1;
        animator.OnFrame(active, 0);
        return animator.Matrices;
    }

    /// <summary>Column-major 4x4 per joint, 16 floats each, as Mat4f lays them out.</summary>
    private static void ApplyJoints(MeshData mesh, float[] matrices)
    {
        var ids = mesh.CustomInts?.Values;
        if (ids == null) return;
        var n = new float[3];
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            int m = ids[i] * 16;
            if (m + 15 >= matrices.Length) continue;   // a joint the animator has no matrix for: leave the vertex alone
            float x = mesh.xyz[i * 3], y = mesh.xyz[i * 3 + 1], z = mesh.xyz[i * 3 + 2];
            mesh.xyz[i * 3]     = matrices[m] * x + matrices[m + 4] * y + matrices[m + 8] * z + matrices[m + 12];
            mesh.xyz[i * 3 + 1] = matrices[m + 1] * x + matrices[m + 5] * y + matrices[m + 9] * z + matrices[m + 13];
            mesh.xyz[i * 3 + 2] = matrices[m + 2] * x + matrices[m + 6] * y + matrices[m + 10] * z + matrices[m + 14];

            // Normals turn with the joint. Animation matrices are rotations and translations,
            // so the upper 3x3 is enough; renormalise in case of scale.
            VertexFlags.UnpackNormal(mesh.Flags[i], n);
            float nx = matrices[m] * n[0] + matrices[m + 4] * n[1] + matrices[m + 8] * n[2];
            float ny = matrices[m + 1] * n[0] + matrices[m + 5] * n[1] + matrices[m + 9] * n[2];
            float nz = matrices[m + 2] * n[0] + matrices[m + 6] * n[1] + matrices[m + 10] * n[2];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0.0001f)
                mesh.Flags[i] = (mesh.Flags[i] & VertexFlags.ClearNormalBitMask) | VertexFlags.PackNormal(nx / len, ny / len, nz / len);
        }
    }

    /// <summary>
    /// Maps the shape's texture codes to block-atlas positions. Base entity textures are baked
    /// (alternates expanded, wildcards resolved) and inserted by name; the coat is chosen by
    /// the specimen's textureIndex, where 0 is the base and i is Alternates[i-1] - the same
    /// layout the entity renderer uses. Textures that attachments added already carry a
    /// block-atlas sub id, because addGearToShape was given the block atlas to insert into.
    /// </summary>
    private sealed class AtlasSource : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly Dictionary<string, TextureAtlasPosition> positions = new();
        private readonly HashSet<string> warned = new();
        private readonly AssetLocation entityCode;

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public AtlasSource(ICoreClientAPI capi, Dictionary<string, CompositeTexture> textures, HashSet<string> baseKeys, int textureIndex, AssetLocation entityCode, IDictionary<string, AssetLocation> extras = null)
        {
            this.capi = capi;
            this.entityCode = entityCode;
            var atlas = capi.BlockTextureAtlas;
            // Textures the shape itself declares and the entity does not (the rug's flesh underside).
            foreach (var (key, loc) in extras ?? new Dictionary<string, AssetLocation>())
            {
                if (textures.ContainsKey(key)) continue;
                if (atlas.GetOrInsertTexture(loc, out _, out var epos)) positions[key] = epos;
                else capi.Logger.Warning("[Taxidermy] Shape texture '{0}' ({1}) could not be put in the block atlas", key, loc);
            }
            foreach (var (key, ct) in textures)
            {
                TextureAtlasPosition pos = null;
                if (baseKeys.Contains(key))
                {
                    var baked = CompositeTexture.Bake(capi.Assets, ct);
                    var variant = baked.BakedVariants is { Length: > 0 } v ? v[GameMath.Mod(textureIndex, v.Length)] : baked;
                    if (variant.TextureFilenames == null || variant.TextureFilenames.Length <= 1)
                    {
                        atlas.GetOrInsertTexture(variant.BakedName, out _, out pos);
                    }
                    else
                    {
                        // Overlays (the elk's fringe): rebuild a composite from the chosen base and insert that.
                        var composite = new CompositeTexture(variant.TextureFilenames[0].Clone()) { BlendedOverlays = ct.BlendedOverlays?.Select(o => o.Clone()).ToArray() };
                        atlas.GetOrInsertTexture(composite, out _, out pos);
                    }
                }
                else if (ct.Baked != null && ct.Baked.TextureSubId >= 0 && ct.Baked.TextureSubId < atlas.Positions.Length)
                {
                    pos = atlas.Positions[ct.Baked.TextureSubId];
                }
                if (pos != null) positions[key] = pos;
                else capi.Logger.Warning("[Taxidermy] Texture '{0}' ({1}) of {2} could not be put in the block atlas", key, ct.Base, entityCode);
            }
        }

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                if (positions.TryGetValue(textureCode, out var pos)) return pos;
                if (warned.Add(textureCode))
                    capi.Logger.Warning("[Taxidermy] {0} uses texture code '{1}' which it does not declare", entityCode, textureCode);
                return positions.Values.FirstOrDefault() ?? capi.BlockTextureAtlas.UnknownTexturePosition;
            }
        }
    }
}
