using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
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
    /// The hide size a mount of this animal is meant to be stuffed with: small, medium, large
    /// or huge. The grid recipes cannot read it (one exists per size and any size crafts), so
    /// it is advice in the tooltip, not a rule.
    /// </summary>
    public string HideSize { get; set; } = "medium";

    /// <summary>Chance, 0..1, that this animal's carcass gives a head; null means the config default.</summary>
    public float? Chance { get; set; }

    /// <summary>
    /// Which element of the model is the head: the topmost element whose name contains one of
    /// these, case-insensitive, first match wins. Every vanilla animal calls it "head"; the
    /// shiver's is "Neck" and the bowtorn's "Neck3".
    /// </summary>
    public string[] HeadElements { get; set; } = ["head"];

    /// <summary>When two definitions match one entity, the higher priority wins; ties by code.</summary>
    public int Priority { get; set; }

    public bool Matches(string entityCode) => Enabled && EntityCodes.Any(p => WildcardUtil.Match(p, entityCode));

    public PoseDefinition PoseByCode(string code) => Poses.FirstOrDefault(p => p.Code == code) ?? Poses[0];
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
        return data;
    }

    public static ITreeAttribute Of(ItemStack stack) => stack?.Attributes?.GetTreeAttribute(Key);

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
}
