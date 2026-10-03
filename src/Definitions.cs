using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// One animal the mod knows how to mount. Loaded from every
/// <c>assets/&lt;domain&gt;/config/taxidermy/*.json</c> in every mod, so an addon adds its own
/// animals with a single file in its own domain and never has to patch ours. Only the fixed
/// asset categories load at all, which is why this sits under <c>config/</c> - a folder of
/// the mod's own invention is silently skipped (shared reference sect.16g).
/// </summary>
public sealed class AnimalDefinition
{
    /// <summary>Stable id, saved into every specimen. Never rename after release.</summary>
    public string Code { get; set; }

    /// <summary>Wildcard patterns matched against the full entity code, domain included.</summary>
    public string[] EntityCodes { get; set; } = [];

    /// <summary>
    /// The poses, in menu order. The first is the default. A pose with no animation is the
    /// shape's rest pose - which is what "standing" is for every vanilla animal.
    /// </summary>
    public PoseDefinition[] Poses { get; set; } = [new()];

    /// <summary>Extra scale on top of the entity's own <c>client.size</c>.</summary>
    public float Scale { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The pelt size a mount of this animal must be stuffed with: small, medium, large or
    /// huge. Since 2026-09-22 this is a RULE, not advice: Capture writes it into the specimen
    /// tree, the stack carries it flat (Specimen.SetSize), and each of the four grid recipes
    /// matches one size - so a hare's head will not take a bear's pelt.
    /// </summary>
    public string HideSize { get; set; } = "medium";

    /// <summary>
    /// HideSize per entity type, where one definition covers animals that drop different sizes
    /// - wildcards over the entity code, first match wins, e.g. { "*-pudu-*": "small" }. Calm,
    /// 2026-09-24: "if any of the mounts have the wrong hide change it to match the drop" - a
    /// pudu drops a small hide, an elk a large one, and both are "deer".
    /// </summary>
    public Dictionary<string, string> HideSizeByType { get; set; }

    /// <summary>
    /// Which pelt a mount of this animal must be stuffed with, when it has one of its own - a
    /// template filled from the entity's variants, e.g. "bear-{type}" gives "bear-polar" for a
    /// polar bear. Each value has its own grid recipe taking only that pelt (Calm, 2026-09-23:
    /// an elephant head took a penguin's pelt). Null: any generic pelt of HideSize, which is all
    /// most vanilla animals ever drop.
    /// </summary>
    public string Pelt { get; set; }

    /// <summary>
    /// The animal's own pelt DESIGN - a flat, headless shape (e.g. "taxidermy:item/pelt/hare",
    /// hand-laid by tools/make_hare_pelt.py) whose fur side samples the animal's own texture.
    /// A template like Pelt ("taxidermy:item/pelt/deer-{type}"), because most species have a
    /// model per type and each model lays its texture out differently.
    /// An animal with one harvests into the game:hide-*-taxidermy-* family (ItemTaxidermyHide)
    /// instead of vanilla's generic hide, and its mount takes that pelt. Null: vanilla's hides.
    /// </summary>
    public string PeltShape { get; set; }

    /// <summary>Which of the entity's textures is its fur, for the pelt. Null: the first one.</summary>
    public string FurTexture { get; set; }

    /// <summary>Chance, 0..1, that this animal's carcass gives a head; null means the built-in 50% (TaxidermyConfig.HeadChance).</summary>
    public float? Chance { get; set; }

    /// <summary>
    /// A chance per entity type, wildcards over the entity code as in HideSizeByType, first match
    /// wins - e.g. the mobs by tier (Calm, 2026-09-26: "normal drifters at like 10%"). Used for the
    /// head and, for a creature with no hide in the game, its hide (both switches off).
    /// </summary>
    public Dictionary<string, float> ChanceByType { get; set; }

    /// <summary>
    /// "animal" (default, left out) or "mob". Decides which of the server's own-pelt / vanilla-hide
    /// switches apply (Calm, 2026-09-28: modders tag their monsters so one setting covers them all).
    /// </summary>
    public string Category { get; set; }

    [Newtonsoft.Json.JsonIgnore] public bool IsMob => string.Equals(Category, "mob", StringComparison.OrdinalIgnoreCase);

    /// <summary>This entity type's chance: ChanceByType's first match, else Chance; null for the built-in default.</summary>
    public float? ChanceFor(string entityCode)
    {
        if (ChanceByType != null && entityCode != null)
        {
            var code = new AssetLocation(entityCode);
            foreach (var (pattern, chance) in ChanceByType)
                if (WildcardUtil.Match(pattern, pattern.Contains(':') ? code.ToString() : code.Path)) return chance;
        }
        return Chance;
    }

    /// <summary>The hide size for one entity type: HideSizeByType's first match, else HideSize.</summary>
    public string SizeFor(string entityCode)
    {
        if (HideSizeByType != null && entityCode != null)
        {
            var code = new AssetLocation(entityCode);
            foreach (var (pattern, size) in HideSizeByType)
                if (WildcardUtil.Match(pattern, pattern.Contains(':') ? code.ToString() : code.Path)) return size;
        }
        return HideSize;
    }

    /// <summary>
    /// Which element of the model is the head: the topmost element whose name contains one of
    /// these, case-insensitive, first match wins. Every vanilla animal calls it "head"; the
    /// shiver's is "Neck" and the bowtorn's "Neck3".
    /// </summary>
    public string[] HeadElements { get; set; } = ["head"];

    /// <summary>
    /// Keep the model's eye elements on the head - ON by default since 2026-09-23 (Calm: "keep
    /// the eyes, pretend to be glass eyes, and they're on the full model anyway"). Set false to
    /// drop anything named "eye" from this animal's head: vanilla draws its eyes as a bar through
    /// the head, which read as holes on a trophy - the reason they were stripped at first. Names
    /// cannot tell a bar from real modelled eyes (vanilla alone uses Eye, R Eye, eyes, EyeL,
    /// EyeSockets...; FotSA elephants build Eye Base > sockets > eyeballs > lids), so it is a
    /// switch per animal, not a rule.
    /// </summary>
    public bool KeepEyes { get; set; } = true;

    /// <summary>When two definitions match one entity, the higher priority wins; ties by code.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// The creative tab its heads and mounts are listed in, besides General and Items. Null: the
    /// mod's own "taxidermy" tab. A new tab exists as soon as something names it; its title is the
    /// lang key <c>game:tabname-&lt;tab&gt;</c> (Calm, 2026-09-25: the FotSA animals in their own tab).
    /// </summary>
    public string CreativeTab { get; set; }

    /// <summary>
    /// HEAD skinning only - the animal's own carcass harvest is untouched. Everything skinning a
    /// raw head gives, the hide included since 2026-09-30 (Calm: "put it in the drop list like
    /// everything else"), each entry with its own chance. A list with no hide in it - or none at
    /// all - still gets the small raw hide it always had (<see cref="HeadSkinningDrop.WithHide"/>),
    /// so definitions written for 1.0.2 and earlier keep working. Every shipped animal lists its
    /// own at 50% each (Calm: "sometimes you mess up the carving").
    /// </summary>
    public HeadSkinningDrop[] HeadSkinningDrops { get; set; }

    public bool Matches(string entityCode) => Enabled && EntityCodes.Any(p => WildcardUtil.Match(p, entityCode));

    public PoseDefinition PoseByCode(string code) => Poses.FirstOrDefault(p => p.Code == code) ?? Poses[0];
}

/// <summary>One entry of <see cref="AnimalDefinition.HeadSkinningDrops"/>.</summary>
public sealed class HeadSkinningDrop
{
    public string Type { get; set; } = "item";
    public string Code { get; set; }
    /// <summary>Used when <see cref="QuantityBySize"/> has no entry for the head's size.</summary>
    public float Quantity { get; set; } = 1;
    /// <summary>By the head's hide size (small, medium, large, huge); fractions round at random.</summary>
    public Dictionary<string, float> QuantityBySize { get; set; }
    /// <summary>0..1, rolled first: a miss gives none of this entry - a slip of the knife.</summary>
    public float Chance { get; set; } = 1;

    private static readonly HeadSkinningDrop LegacyHide = new() { Code = "game:hide-raw-small" };

    /// <summary>The list as given, plus the small raw hide when it names no hide - what 1.0.2 always gave.</summary>
    public static IEnumerable<HeadSkinningDrop> WithHide(HeadSkinningDrop[] drops)
    {
        drops ??= [];
        bool hasHide = drops.Any(d => d.Code != null && new AssetLocation(d.Code).Path.StartsWith("hide-"));
        return hasHide ? drops : drops.Append(LegacyHide);
    }

    public ItemStack Make(IWorldAccessor world, string size)
    {
        if (string.IsNullOrEmpty(Code)) return null;
        if (Chance < 1 && world.Rand.NextDouble() >= Chance) return null;
        var loc = new AssetLocation(Code);
        CollectibleObject what = Type == "block" ? world.GetBlock(loc) : world.GetItem(loc);
        float q = QuantityBySize != null && QuantityBySize.TryGetValue(size, out var v) ? v : Quantity;
        int n = GameMath.RoundRandom(world.Rand, q);
        return what == null || n <= 0 ? null : new ItemStack(what, n);
    }
}

public sealed class PoseDefinition
{
    /// <summary>Stable id, saved into the mount. Shown through lang key <c>taxidermy:pose-&lt;code&gt;</c>.</summary>
    public string Code { get; set; } = "standing";

    /// <summary>Animation code in the entity's shape, or null for the rest pose.</summary>
    public string Animation { get; set; }

    /// <summary>Zero-based frame of that animation to freeze at. Clamped to the animation's length.</summary>
    public float Frame { get; set; }
}

/// <summary>
/// The specimen's identity, carried as one <c>taxidermy</c> tree attribute on the raw and
/// preserved specimen stacks and on the mount's block entity. Everything the client needs to
/// rebuild the animal is in here, so a picked-up mount comes back exactly as it was.
/// </summary>
public static class Specimen
{
    public const string Key = "taxidermy";

    public const string Domain = "taxidermy";
    public const string HeadRaw = "taxidermy:head-raw";
    public const string HeadPreserved = "taxidermy:head-preserved";
    public const string MountItem = "taxidermy:mount";
    public const string MountBlock = "taxidermy:mount";
    public static readonly string[] Sizes = ["small", "medium", "large", "huge"];

    public static TreeAttribute Capture(Entity entity, AnimalDefinition definition)
    {
        var data = new TreeAttribute();
        data.SetInt("version", 1);
        data.SetString("entityCode", entity.Code.ToString());
        data.SetString("definition", definition.Code);
        // Which coat: wolves have ten alternates, bears one per type. Index 0 is the base texture.
        data.SetInt("textureIndex", entity.WatchedAttributes.GetInt("textureIndex"));
        // Antlers, horns and tusks are items in the antler-growth inventory, attached to the
        // body at render time. Record the item codes so the mount can do the same.
        var antlers = entity.GetBehavior<EntityBehaviorAntlerGrowth>()?.Inventory;
        if (antlers != null)
        {
            var codes = new List<string>();
            foreach (var slot in antlers) if (!slot.Empty) codes.Add(slot.Itemstack.Collectible.Code.ToString());
            if (codes.Count > 0) data.SetString("attachments", string.Join(",", codes));
        }
        data.SetString("pose", definition.Poses[0].Code);
        // The pelt a mount of this animal must be stuffed with. In the tree so it survives
        // every step (the barrel patch copies the tree wholesale); stamped flat onto the stack
        // by SetSize, because a grid recipe can only match a FLAT attribute - a nested match
        // would have to equal the whole taxidermy tree, which differs per animal.
        data.SetString("hideSize", definition.SizeFor(entity.Code.ToString()));
        bool customPelts = entity.Api?.ModLoader.GetModSystem<TaxidermyModSystem>()?.Config.OwnPeltsFor(definition) ?? true;
        data.SetString("pelt", PeltToken(definition, entity.Properties, customPelts));
        return data;
    }

    /// <summary>
    /// The pelt a head of this animal needs: the definition's Pelt template with the entity's
    /// variants filled in ("bear-{type}" -> "bear-polar"), or "size-&lt;HideSize&gt;" for an animal
    /// with no pelt of its own. Matched by the grid recipes through the flat "pelt" attribute.
    /// With custom pelts switched off (TaxidermyConfig.CustomPelts), an animal whose own pelt is
    /// one of OUR designs takes the plain pelt of its size instead - its own would never drop.
    /// </summary>
    public static string PeltToken(AnimalDefinition def, EntityProperties props, bool customPelts = true)
    {
        if (string.IsNullOrEmpty(def.Pelt) || (!customPelts && !string.IsNullOrEmpty(def.PeltShape)))
            return "size-" + def.SizeFor(props?.Code?.ToString());
        return Fill(def.Pelt, props);
    }

    /// <summary>A template with the entity's variants filled in: "deer-{type}" -> "deer-pudu".</summary>
    public static string Fill(string template, EntityProperties props)
    {
        if (template == null || props?.Variant == null) return template;
        foreach (var kv in props.Variant) template = template.Replace("{" + kv.Key + "}", kv.Value);
        return template;
    }

    /// <summary>
    /// Copy the specimen's hide size onto the stack itself, where a recipe ingredient's
    /// <c>attributes</c> can match it. Called wherever a head stack is made or rebuilt.
    /// </summary>
    public static void SetSize(ItemStack stack, ITreeAttribute data)
    {
        if (stack == null || data == null) return;
        var size = data.GetString("hideSize");
        if (!string.IsNullOrEmpty(size)) stack.Attributes.SetString("hideSize", size);
        // What the recipes actually match since 2026-09-23 - one recipe per pelt.
        var pelt = data.GetString("pelt");
        if (!string.IsNullOrEmpty(pelt)) stack.Attributes.SetString("pelt", pelt);
    }

    public static ITreeAttribute Of(ItemStack stack) => stack?.Attributes?.GetTreeAttribute(Key);

    /// <summary>
    /// What a head or mount with NO animal draws as - the plain ones the handbook shows, and every
    /// recipe output it lists (a recipe's output carries the head's pelt label, not its animal):
    /// a wolf in its base coat (Calm, 2026-10-02), instead of the item's fallback hide square.
    /// Null if the wolf is not there to draw, which leaves the fallback.
    /// </summary>
    public const string ExampleEntity = "game:wolf-eurasian-adult-male";
    private static ITreeAttribute example;

    public static ITreeAttribute Example(ICoreClientAPI capi)
    {
        if (example != null) return example;
        var props = capi.World.GetEntityType(new AssetLocation(ExampleEntity));
        var def = props == null ? null : capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(ExampleEntity);
        if (def == null) return null;
        var data = new TreeAttribute();
        data.SetInt("version", 1);
        data.SetString("entityCode", ExampleEntity);
        data.SetString("definition", def.Code);
        data.SetInt("textureIndex", 0);
        data.SetString("pose", def.Poses[0].Code);
        data.SetString("hideSize", def.SizeFor(ExampleEntity));
        return example = data;
    }

    /// <summary>
    /// A head takes only a pelt in its own coat (Calm, 2026-10-01: a white wolf took a brown wolf's
    /// pelt) - the same rule the hides stack by. The recipes' pelt label carries animal and sex only,
    /// so the coat is checked here, for the grid (TaxidermyModSystem's MatchesGridRecipe) and for
    /// sewing on the ground alike. A pelt with no coat of its own - a plain pelt, the game's fox,
    /// raccoon and bear pelts, another mod's own pelt items - fits any coat, as before.
    /// </summary>
    public static bool CoatsMatch(ItemStack head, ItemStack pelt)
    {
        var h = Of(head);
        var p = pelt?.Collectible is ItemTaxidermyHide ? Of(pelt) : null;
        if (h == null || p == null) return true;
        if (h.GetString("definition") != p.GetString("definition")) return true;
        if (!h.HasAttribute("textureIndex") || !p.HasAttribute("textureIndex")) return true;
        return h.GetInt("textureIndex") == p.GetInt("textureIndex");
    }

    /// <summary>
    /// On a raw head: the antler/horn/tusk item codes its carcass handed over instead of dropping
    /// them (TaxidermyConfig.AntlersOnHead), comma-joined - what skinning gives back, and nothing
    /// else, so they are never doubled. Beside the tree, not in it: the barrel keeps only the tree,
    /// and a preserved head is never skinned.
    /// </summary>
    public const string AntlersKey = "taxidermyAntlers";

    public static string[] KeptAntlers(ItemStack stack)
    {
        var joined = stack?.Attributes?.GetString(AntlersKey);
        return string.IsNullOrEmpty(joined) ? [] : joined.Split(',');
    }

    public static string[] Attachments(ITreeAttribute data)
    {
        var joined = data?.GetString("attachments");
        return string.IsNullOrEmpty(joined) ? [] : joined.Split(',');
    }

    /// <summary>The creature's own display name, from vanilla's <c>item-creature-*</c> lang keys.</summary>
    public static string AnimalName(ITreeAttribute data)
    {
        var raw = data?.GetString("entityCode");
        if (raw == null) return "?";
        var code = new AssetLocation(raw);
        return Vintagestory.API.Config.Lang.GetMatching(code.Domain + ":item-creature-" + code.Path);
    }

    /// <summary>
    /// "Wolf (male)" -> "Wolf", for item and block names - the tooltip shows the sex (Calm,
    /// 2026-09-29). A name with the sex in the word itself ("Bighorn ram", "Rooster") stays.
    /// </summary>
    public static string AnimalNameNoSex(ITreeAttribute data) =>
        System.Text.RegularExpressions.Regex.Replace(AnimalName(data), @"\s*\([^)]*\)\s*$", "").Trim();
}
