using System.Collections.Concurrent;
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
    // Concurrent: read on the tesselation thread (displays, BESkinnedHead) while the main thread builds.
    private const string HeadCacheKey = "taxidermy-head-meshes";
    private const string HeadItemCacheKey = "taxidermy-head-item-meshes";
    private const string PlaceholderCacheKey = "taxidermy-placeholder-mesh";

    /// <summary>
    /// The stand-in cube for a mount whose animal cannot be built - the animal's mod is not
    /// installed, so there is no shape to lift. Vanilla's own white-with-a-red-question-mark
    /// texture (game:textures/unknown.png), which says "something is missing here" in the
    /// language the game already uses. Calm, 2026-09-22: an invisible block is very hard to
    /// find, and these must be easy to see and break.
    /// The specimen is untouched underneath, so reinstalling the animal mod brings the real
    /// mount back.
    /// </summary>
    public static MeshData GetPlaceholderMesh(ICoreClientAPI capi)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, PlaceholderCacheKey, () => new Dictionary<string, MeshData>());
        if (cache.TryGetValue("mesh", out var cached)) return cached;
        try
        {
            var block = capi.World.GetBlock(new AssetLocation(Specimen.MountBlock));
            var shape = capi.Assets.TryGet("taxidermy:shapes/block/placeholder.json")?.ToObject<Shape>();
            if (block == null || shape == null)
            {
                // Silence here is what an invisible mount looks like, so say it out loud.
                capi.Logger.Warning("[Taxidermy] No placeholder mesh: block={0}, shape={1}", block != null, shape != null);
                return null;
            }
            capi.Tesselator.TesselateShape(block, shape, out var mesh);
            if (mesh != null) cache["mesh"] = mesh;
            return mesh;
        }
        catch (Exception e)
        {
            capi.Logger.Error("[Taxidermy] Cannot build the placeholder mesh: {0}", e);
            return null;
        }
    }

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

    /// <summary>Main thread, at client start: see ItemTaxidermyHide.PrepareCaches.</summary>
    public static void PrepareCaches(ICoreClientAPI capi) =>
        ObjectCacheUtil.GetOrCreate(capi, HeadCacheKey, () => new ConcurrentDictionary<string, MeshData>());

    public static MeshData TryGetHeadMesh(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def, bool flat = false)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, HeadCacheKey, () => new ConcurrentDictionary<string, MeshData>());
        return cache.TryGetValue(HeadKey(data, def, flat), out var m) ? m : null;
    }

    private static string HeadKey(ITreeAttribute data, AnimalDefinition def, bool flat = false) =>
        IdentityKey(data) + "|" + def.Scale + "|" + def.Code + (flat ? "|flat" : "");

    /// <summary>
    /// The animal's head, lifted out of its own model: the topmost element whose name contains
    /// "head" and everything under it (antlers and horns step-parent onto "head", so they come
    /// too), eyes removed, in the rest pose, grounded and centred on the block. Falls back to
    /// the whole animal when a model has no element called head. Main thread only.
    /// <paramref name="flat"/>: the head as it lies on the ground - see BuildHead.
    /// </summary>
    public static MeshData GetHeadMesh(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def, bool flat = false)
    {
        var cache = ObjectCacheUtil.GetOrCreate(capi, HeadCacheKey, () => new ConcurrentDictionary<string, MeshData>());
        string key = HeadKey(data, def, flat);
        if (cache.TryGetValue(key, out var cached)) return cached;
        MeshData mesh = null;
        try { mesh = BuildHead(capi, data, def, flat); }
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

    /// <summary>
    /// <paramref name="flat"/>: the head as it lies on the ground - none of the body's pose (the
    /// parents' transform is skipped and the root's own tilt zeroed), turned about its pitch axis to
    /// where it settles on a floor: the angle, -90..+30 degrees, that puts its centre lowest above
    /// its lowest point (a wolf sits level on its jaw, a deer tips back ~23 degrees onto its jaw).
    /// The neck comes too (2026-09-26; 2026-09-25 it was dropped), but only the head is measured
    /// for the settle and the floor, so the neck is free to go into the ground.
    /// </summary>
    private static MeshData BuildHead(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def, bool flat = false)
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
        // is what sinks into the plaque, which makes the head sit and position naturally. On the
        // ground too since 2026-09-26 (Calm: some heads, like the corrupt drifter's, lose detail
        // without it) - but the head ALONE decides how it settles, so the neck may go into the floor.
        var headOnly = head;
        var necks = new List<ShapeElement>();
        while (chain.Count > 0 && chain[^1].Name != null && NeckWords.Any(w => chain[^1].Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            head = chain[^1];
            necks.Insert(0, head);
            chain.RemoveAt(chain.Count - 1);
        }
        if (!def.KeepEyes) RemoveEyes(head);
        if (flat) foreach (var neck in necks) FillMissingFaces(neck);

        // The head's own coordinates are relative to its parent's frame. Tesselate it as a root
        // - which places it in that frame - then push the vertices through the parents'
        // accumulated transform (vsapi ShapeElement.GetLocalTransformMatrix, in block units).
        // GetLocalTransformMatrix multiplies ONTO its output buffer rather than starting from
        // identity - vanilla's GetInverseModelMatrix resets it before every call, and without
        // that the parents compound and the head lands nowhere near the slot (2026-09-21).
        var chainMatrix = Mat4f.Create();
        var tmp = new float[16];
        if (flat)
        {
            // Keep only which way the body TURNS the head (the parents' rotation about Y), drop its
            // tilts. Most models face -X outright, but some are built facing another way and turned
            // by a parent - vanilla's bear faces +Z and its root "Cube1" turns it -90 about Y - and
            // without that turn a bear head lies sideways and Settle rolls it instead of tipping it.
            double turn = chain.Sum(p => p.RotationY);
            chain.Clear();
            head.RotationX = head.RotationZ = 0;
            if (turn != 0) Mat4f.RotateY(chainMatrix, chainMatrix, (float)(turn * GameMath.DEG2RAD));
        }
        foreach (var parent in chain)
        {
            Mat4f.Identity(tmp);
            Mat4f.Mul(chainMatrix, chainMatrix, parent.GetLocalTransformMatrix(0, tmp));
        }

        var texSource = new AtlasSource(capi, textures, baseKeys, data.GetInt("textureIndex"), entityCode, shape.Textures);
        var meta = new TesselationMetaData { TypeForLogging = logName, TexSource = texSource };

        // On the ground with a neck: the head alone, in the same frame as the whole piece (the
        // necks' own transforms on top), is what Settle and the floor are measured on.
        MeshData reference = null;
        if (flat && necks.Count > 0)
        {
            var headParent = headOnly.ParentElement;
            headOnly.ParentElement = null;
            shape.Elements = [headOnly];
            capi.Tesselator.TesselateShape(meta, shape, out reference);
            headOnly.ParentElement = headParent;
            var neckMatrix = (float[])chainMatrix.Clone();
            foreach (var neck in necks)
            {
                Mat4f.Identity(tmp);
                Mat4f.Mul(neckMatrix, neckMatrix, neck.GetLocalTransformMatrix(0, tmp));
            }
            Transform(reference, neckMatrix);
        }

        head.ParentElement = null;
        shape.Elements = [head];
        capi.Tesselator.TesselateShape(meta, shape, out var mesh);
        Transform(mesh, chainMatrix);
        reference ??= mesh;
        if (flat) Settle(reference, reference == mesh ? null : mesh);

        float scale = props.Client.Size * def.Scale;
        if (scale != 1)
        {
            mesh.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);
            if (reference != mesh) reference.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);
        }

        // Feet-on-the-floor equivalent for a head: lowest point at y = 0, centred on the block.
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < reference.VerticesCount; i++)
        {
            float x = reference.xyz[i * 3], y = reference.xyz[i * 3 + 1], z = reference.xyz[i * 3 + 2];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        if (reference.VerticesCount > 0) mesh.Translate(0.5f - (minX + maxX) / 2, -minY, 0.5f - (minZ + maxZ) / 2);
        return mesh;
    }

    /// <summary>
    /// Turn the mesh about Z (the pitch of a model facing -X) to where it would settle on a floor;
    /// <paramref name="also"/>, when given, is turned the same way (the neck follows the head).
    /// </summary>
    private static void Settle(MeshData mesh, MeshData also = null)
    {
        int n = mesh.VerticesCount;
        if (n == 0) return;
        float cx = 0, cy = 0;
        for (int i = 0; i < n; i++) { cx += mesh.xyz[i * 3]; cy += mesh.xyz[i * 3 + 1]; }
        cx /= n; cy /= n;
        int best = 0; float bestHeight = float.MaxValue;
        for (int deg = -90; deg <= 30; deg++)
        {
            float a = deg * GameMath.DEG2RAD, sin = MathF.Sin(a), cos = MathF.Cos(a), low = float.MaxValue;
            // the centroid is the pivot, so its height after the turn stays cy
            for (int i = 0; i < n; i++)
            {
                float x = mesh.xyz[i * 3] - cx, y = mesh.xyz[i * 3 + 1] - cy;
                low = Math.Min(low, x * sin + y * cos + cy);
            }
            if (cy - low < bestHeight - 1e-5f) { bestHeight = cy - low; best = deg; }
        }
        if (best == 0) return;
        mesh.Rotate(new Vec3f(cx, cy, 0), 0, 0, best * GameMath.DEG2RAD);
        also?.Rotate(new Vec3f(cx, cy, 0), 0, 0, best * GameMath.DEG2RAD);
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

    /// <summary>
    /// A model leaves out the faces nobody should see - the back of the neck where it meets the
    /// body, the back of a paper-thin part - and the game draws no face from behind. Cut off the
    /// body and lying on the ground, the head was see-through from behind (Calm, 2026-09-26: the
    /// manatee's big neck, the boar's neck). Each missing face gets a copy of the one opposite,
    /// which has the same size; new face objects in a new array, because Clone shares them with
    /// the living animal's shape. The NECK elements only, not their children: filling every part
    /// put odd faces on fur strands and the like (Calm, 2026-09-26, the musk ox).
    /// </summary>
    private static void FillMissingFaces(ShapeElement el)
    {
        var faces = el.FacesResolved;
        if (faces != null && el.HasFaces())
        {
            ShapeElementFace[] filled = null;
            for (int i = 0; i < 6; i++)
            {
                if (faces[i] != null) continue;
                var src = faces[BlockFacing.ALLFACES[i].Opposite.Index] ?? faces.FirstOrDefault(f => f != null);
                if (src?.Uv == null) continue;
                filled ??= (ShapeElementFace[])faces.Clone();
                filled[i] = new ShapeElementFace
                {
                    Texture = src.Texture, Uv = (float[])src.Uv.Clone(), ReflectiveMode = src.ReflectiveMode,
                    WindMode = (sbyte[])src.WindMode?.Clone(), WindData = (sbyte[])src.WindData?.Clone(),
                    Rotation = src.Rotation, Glow = src.Glow
                };
            }
            if (filled != null) el.FacesResolved = filled;
        }
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
        ObjectCacheUtil.TryGet<ConcurrentDictionary<string, MeshData>>(capi, HeadCacheKey)?.Clear();
        ObjectCacheUtil.TryGet<Dictionary<string, MeshData>>(capi, PlaceholderCacheKey)?.Clear();
    }

    /// <summary>Animals already reported missing this session - see Build.</summary>
    private static readonly HashSet<string> WarnedMissing = new();

    private static MeshData Build(ICoreClientAPI capi, ITreeAttribute data, AnimalDefinition def)
    {
        var entityCode = new AssetLocation(data.GetString("entityCode"));
        var props = capi.World.GetEntityType(entityCode);
        var loaded = props?.Client?.LoadedShape;
        if (loaded == null)
        {
            // Once per animal per session: a mount or head of a missing animal is asked for again
            // every frame, and this line filled Calm's log 16,696 times in two minutes (2026-09-24).
            lock (WarnedMissing)
                if (WarnedMissing.Add(entityCode.ToString()))
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
        float scale = props.Client.Size * def.Scale;
        MeshData Tesselate()
        {
            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TypeForLogging = logName,
                TexSource = texSource,
                WithJointIds = matrices != null,
            }, shape, out var m);
            if (matrices != null) ApplyJoints(m, matrices);
            m.CustomInts = null;
            if (scale != 1) m.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);
            return m;
        }

        var mesh = Tesselate();
        var reference = mesh;

        // Feet on the floor and footprint centred on the block, whatever the pose does: a lying
        // or rearing animal's bounding box wanders, and Calm saw poses drift off centre.
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < reference.VerticesCount; i++)
        {
            minX = Math.Min(minX, reference.xyz[i * 3]); maxX = Math.Max(maxX, reference.xyz[i * 3]);
            minY = Math.Min(minY, reference.xyz[i * 3 + 1]);
            minZ = Math.Min(minZ, reference.xyz[i * 3 + 2]); maxZ = Math.Max(maxZ, reference.xyz[i * 3 + 2]);
        }
        if (reference.VerticesCount > 0) mesh.Translate(0.5f - (minX + maxX) / 2, -minY, 0.5f - (minZ + maxZ) / 2);

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
