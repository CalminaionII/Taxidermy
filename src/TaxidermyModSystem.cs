using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

public sealed class TaxidermyConfig
{
    /// <summary>
    /// Chance, 0..1, that harvesting a supported carcass yields a head. A definition can set
    /// its own. Calm wants it random (2026-09-21); the number is a first guess.
    /// </summary>
    public float HeadChance { get; set; } = 0.35f;
}

public sealed class TaxidermyModSystem : ModSystem
{
    public const string BehaviorCode = "taxidermy";
    public const string Channel = "taxidermy";

    public TaxidermyConfig Config { get; private set; } = new();
    public IReadOnlyList<AnimalDefinition> Animals { get; private set; } = [];

    private ICoreAPI api;

    public override void Start(ICoreAPI api)
    {
        this.api = api;
        api.RegisterItemClass("TaxidermyHead", typeof(ItemHead));
        api.RegisterItemClass("TaxidermyMount", typeof(ItemMount));
        api.RegisterBlockClass("TaxidermyMount", typeof(BlockMount));
        api.RegisterBlockEntityClass("TaxidermyMount", typeof(BEMount));
        // Registered on BOTH sides even though only the server attaches it: a behaviour name
        // has to exist in the registry wherever an entity carrying it is deserialised.
        api.RegisterEntityBehaviorClass(BehaviorCode, typeof(EntityBehaviorTaxidermy));

        BarrelPatch.Apply(api);
        api.Network.RegisterChannel(Channel).RegisterMessageType<HeadAdjustPacket>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        Config = api.LoadModConfig<TaxidermyConfig>("TaxidermyConfig.json") ?? new();
        Config.HeadChance = Math.Clamp(Config.HeadChance, 0f, 1f);
        api.StoreModConfig(Config, "TaxidermyConfig.json");
        api.Network.GetChannel(Channel).SetMessageHandler<HeadAdjustPacket>(OnHeadAdjust);
    }

    /// <summary>
    /// The Adjust window for a head on a vanilla antler mount. The head is an item in the
    /// mount's inventory, so the numbers go onto the head's own tree - a tuned head stays
    /// tuned wherever it is hung next - and the mount's dirty flag carries it to every client,
    /// whose cache key includes them so the mesh is rebuilt.
    /// </summary>
    private void OnHeadAdjust(IServerPlayer player, HeadAdjustPacket p)
    {
        var pos = new BlockPos(p.X, p.Y, p.Z);
        if (!api.World.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak)) return;
        if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityAntlerMount mount) return;
        var stack = mount.Inventory[0]?.Itemstack;
        var data = Specimen.Of(stack);
        if (stack?.Collectible is not ItemHead || data == null) return;
        data.SetFloat("offx", GameMath.Clamp(p.OffX, -16, 16) / 16f);
        data.SetFloat("offy", GameMath.Clamp(p.OffY, -16, 16) / 16f);
        data.SetFloat("offz", GameMath.Clamp(p.OffZ, -16, 16) / 16f);
        data.SetFloat("rot", GameMath.Mod(-p.RotationDeg * GameMath.DEG2RAD, GameMath.TWOPI));
        data.SetFloat("tilt", GameMath.Clamp(p.TiltDeg, -90, 90) * GameMath.DEG2RAD);
        mount.Inventory[0].MarkDirty();
        mount.MarkDirty(true);
    }

    /// <summary>
    /// Definitions come from assets, so this cannot happen in Start() - assets do not exist
    /// yet and GetMany returns nothing, silently (shared reference sect.16f/16g). The count is
    /// logged on purpose: a mod that loads and half-works is the worst outcome.
    /// </summary>
    public override void AssetsLoaded(ICoreAPI api)
    {
        var byCode = new Dictionary<string, AnimalDefinition>();
        foreach (var asset in api.Assets.GetMany("config/taxidermy/").OrderBy(a => a.Location.ToString()))
        {
            AnimalDefinition[] defs;
            try { defs = asset.ToObject<AnimalDefinition[]>(); }
            catch (Exception e)
            {
                api.Logger.Error("[Taxidermy] Cannot read {0}: {1}", asset.Location, e.Message);
                continue;
            }
            foreach (var def in defs ?? [])
            {
                string problem = Validate(def);
                if (problem != null)
                {
                    api.Logger.Error("[Taxidermy] Definition in {0} skipped: {1}", asset.Location, problem);
                    continue;
                }
                if (!byCode.TryAdd(def.Code, def))
                    api.Logger.Error("[Taxidermy] Duplicate definition {0} in {1} skipped - patch the existing one instead", def.Code, asset.Location);
            }
        }
        Animals = byCode.Values.OrderByDescending(d => d.Priority).ThenBy(d => d.Code).ToArray();
        api.Logger.Event("[Taxidermy] {0} animal definition(s) loaded", Animals.Count);
        if (Animals.Count == 0) api.Logger.Error("[Taxidermy] No animal definitions loaded - nothing can be mounted");
    }

    private static string Validate(AnimalDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Code)) return "missing code";
        if (def.EntityCodes == null || def.EntityCodes.Length == 0) return def.Code + " has no entityCodes";
        if (def.Poses == null || def.Poses.Length == 0) return def.Code + " has no poses";
        if (def.Poses.Any(p => string.IsNullOrWhiteSpace(p.Code))) return def.Code + " has a pose with no code";
        if (def.Poses.Select(p => p.Code).Distinct().Count() != def.Poses.Length) return def.Code + " has duplicate pose codes";
        if (!(def.Scale > 0) || !float.IsFinite(def.Scale)) return def.Code + " has an invalid scale";
        return null;
    }

    /// <summary>
    /// Attach the harvest behaviour to every entity type a definition matches. Done in code so
    /// an addon's definition file is the whole job - no patch onto each entity. Server side
    /// only: the drop is generated there, and vanilla lists its own server-only behaviours the
    /// same way. Creative stacks for the specimens are built here too, one per definition,
    /// because the specimen needs attributes and a plain creativeinventory entry cannot carry
    /// which animal it is.
    /// </summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        int attached = 0;
        var behaviorJson = new JsonObject(JToken.Parse("{\"code\":\"" + BehaviorCode + "\"}"));
        foreach (var props in api.World.EntityTypes)
        {
            var def = props?.Server == null ? null : Find(props.Code.ToString());
            if (def == null) continue;
            var list = props.Server.BehaviorsAsJsonObj ?? [];
            if (list.Any(b => b["code"].AsString() == BehaviorCode)) continue;
            props.Server.BehaviorsAsJsonObj = list.Append(behaviorJson).ToArray();
            attached++;
        }
        api.Logger.Event("[Taxidermy] Harvest behaviour attached to {0} entity type(s)", attached);

        BuildCreativeStacks(api);
    }

    /// <summary>
    /// Every head, preserved head and mount the mod can produce, in the mod's own creative tab
    /// (a tab exists as soon as something names it; lang key tabname-taxidermy) and the general
    /// one: one per entity TYPE a definition matches - every species, type and gender - and one
    /// per coat variant where the type has alternates (a wolf has ten). Calm, 2026-09-21.
    /// </summary>
    private void BuildCreativeStacks(ICoreAPI api)
    {
        var raw = api.World.GetItem(new AssetLocation(Specimen.HeadRaw));
        var preserved = api.World.GetItem(new AssetLocation(Specimen.HeadPreserved));
        var mount = api.World.GetItem(new AssetLocation(Specimen.MountItem));
        var raws = new List<JsonItemStack>();
        var preserveds = new List<JsonItemStack>();
        var mounts = new List<JsonItemStack>();
        foreach (var def in Animals)
        {
            foreach (var props in api.World.EntityTypes.Where(p => def.Matches(p.Code.ToString())))
            {
                int coats = 1 + Math.Max(0, props.Client?.TexturesAlternatesCount ?? 0);
                for (int coat = 0; coat < coats; coat++)
                {
                    var data = new TreeAttribute();
                    data.SetInt("version", 1);
                    data.SetString("entityCode", props.Code.ToString());
                    data.SetString("definition", def.Code);
                    data.SetInt("textureIndex", coat);
                    data.SetString("pose", def.Poses[0].Code);
                    if (raw != null) raws.Add(Stack(raw, data));
                    if (preserved != null) preserveds.Add(Stack(preserved, data));
                    if (mount != null) mounts.Add(Stack(mount, data));
                }
            }
        }
        string[] tabs = ["taxidermy", "general", "items"];
        if (raw != null && raws.Count > 0) raw.CreativeInventoryStacks = [new CreativeTabAndStackList { Tabs = tabs, Stacks = raws.ToArray() }];
        if (preserved != null && preserveds.Count > 0) preserved.CreativeInventoryStacks = [new CreativeTabAndStackList { Tabs = tabs, Stacks = preserveds.ToArray() }];
        if (mount != null && mounts.Count > 0) mount.CreativeInventoryStacks = [new CreativeTabAndStackList { Tabs = tabs, Stacks = mounts.ToArray() }];
        api.Logger.Event("[Taxidermy] {0} creative head(s) per state", raws.Count);
    }

    private static JsonItemStack Stack(Item item, ITreeAttribute data)
    {
        var tree = new TreeAttribute();
        tree[Specimen.Key] = data.Clone();
        var js = new JsonItemStack { Type = EnumItemClass.Item, Code = item.Code, StackSize = 1, Attributes = new JsonObject(JToken.Parse(tree.ToJsonToken())) };
        js.ResolvedItemstack = new ItemStack(item);
        js.ResolvedItemstack.Attributes[Specimen.Key] = data.Clone();
        return js;
    }

    public AnimalDefinition Find(string entityCode) => Animals.FirstOrDefault(d => d.Matches(entityCode));

    /// <summary>By the saved definition code first, then by entity code as a fallback for a renamed definition.</summary>
    public AnimalDefinition Find(ITreeAttribute data)
    {
        if (data == null) return null;
        return Animals.FirstOrDefault(d => d.Code == data.GetString("definition"))
            ?? Find(data.GetString("entityCode", ""));
    }

    public override void Dispose()
    {
        BarrelPatch.Remove();
        if (api is Vintagestory.API.Client.ICoreClientAPI capi) MountMesher.Clear(capi);
        base.Dispose();
    }
}
