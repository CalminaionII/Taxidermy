using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// <c>ModConfig/TaxidermyConfig.json</c> - every setting SERVER-OWNED (rules of the world), editable
/// in the file or through Integrated Mod Manager, ConfigKit or ConfigLib (Calm, 2026-09-24):
/// <c>assets/taxidermy/config/imm.json</c> and <c>configlib-patches.json</c> (ConfigKit reads the
/// ConfigLib format). Chances are whole PERCENTS because IMM's fields are Integer / Slider /
/// Boolean / Dropdown / String - no decimals; the file used 0..1 fractions until 2026-09-24 (never
/// released) and Load turns those into percents once.
/// </summary>
public sealed class TaxidermyConfig
{
    public const string FileName = "TaxidermyConfig.json";

    /// <summary>
    /// The head's chance with AlwaysHeadAndHide off (Calm, 2026-09-26: "50% for the head" - the
    /// percent sliders went, drops follow the game's rules). A definition can set its own
    /// (<c>chance</c>, 0..1).
    /// </summary>
    public const float HeadChance = 0.5f;

    /// <summary>
    /// With both switches off, the chance a creature that drops NO hide in the game (the drifter,
    /// shiver, bowtorn, chickens) still gives one - scaled like the head. Calm, 2026-09-26 (1.0.2):
    /// "was hoping it would be a chance when turned off"; 50%, the same as the head.
    /// </summary>
    public const float HidelessHideChance = 0.5f;

    /// <summary>
    /// true: every knife harvest gives the head, and at least one hide - one is added when the
    /// game or another mod (Butchering) left none. false (default): the head on its chance
    /// (<see cref="HeadChance"/>, scaled the way the game scales a hide - harvest multiplier x the
    /// player's loot trait, never more than one), the hides as OnePeltPerAnimal says. Calm,
    /// 2026-09-26: "a toggle to override to always have a head and hide".
    /// </summary>
    public bool AlwaysHeadAndHide { get; set; } = false;

    /// <summary>Animals (not <c>"category": "mob"</c>) drop heads at all. Off: never, whatever else says (Calm, 2026-09-28, 1.0.3).</summary>
    public bool AnimalHeads { get; set; } = true;

    /// <summary>Mobs (<c>"category": "mob"</c>) drop heads at all. Off: never, AlwaysHeadAndHide included.</summary>
    public bool MobHeads { get; set; } = true;

    /// <summary>Whether this definition's creatures may drop heads (its category's switch).</summary>
    public bool HeadsFor(AnimalDefinition def) => def?.IsMob == true ? MobHeads : AnimalHeads;

    /// <summary>
    /// true (default): EXACTLY ONE hide per animal, guaranteed - one added when there is none (the
    /// drifters, shivers, bowtorns and chickens too, and any carcass Butchering left bare), the
    /// rest taken away. false: the game's own count, two or three for some animals, none at times
    /// with Butchering. Calm, 2026-09-26: "a realism thing ... a guaranteed drop of 1 pelt whether
    /// vanilla or the own one"; before that it only capped how many hides became own pelts.
    /// </summary>
    public bool OnePeltPerAnimal { get; set; } = true;

    /// <summary>
    /// true (default): when a carcass gives a head, the antlers, horns or tusks the game would put
    /// in the harvest stay on the head instead, and come off when it is skinned. No head: the
    /// carcass gives them as the game does. false: the game's way always, and skinning gives none.
    /// Never doubled - a head only gives back what its carcass handed over (Specimen.AntlersKey).
    /// Calm, 2026-09-30.
    /// </summary>
    public bool AntlersOnHead { get; set; } = true;

    /// <summary>The short-lived PeltCount number (2026-09-24): read once - above 1 means the vanilla way - never written back.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? PeltCount { get; set; }

    /// <summary>
    /// The animals' own pelts on (default) or off (Calm, 2026-09-24). On: every plain hide the
    /// harvest holds is the animal's own pelt, where it has a design. Off: plain hides; new heads
    /// take a plain pelt of their size and the pelt family leaves the creative tab. Pelts already
    /// made stay and still work. Fox, raccoon and bear were always vanilla's and are unaffected.
    /// </summary>
    /// <summary>
    /// Animals (definitions without <c>"category": "mob"</c>) drop their own pelts - every plain hide
    /// they give becomes their own, where they have a design. Off: the game's own hides. Calm,
    /// 2026-09-28 (1.0.3): the one CustomPelts switch split per category.
    /// </summary>
    public bool AnimalOwnPelts { get; set; } = true;

    /// <summary>Mobs (<c>"category": "mob"</c>) drop their own pelts. Off: as in the game - none, unless MobVanillaHides.</summary>
    public bool MobOwnPelts { get; set; } = true;

    /// <summary>
    /// Mobs may give the game's plain hide of their size - used when their own pelt is off (or
    /// they have no design). Off (default): a mob's hide can only be its own pelt.
    /// </summary>
    public bool MobVanillaHides { get; set; } = false;

    /// <summary>The pre-1.0.3 single switch: read once into both own-pelt switches, never written back.</summary>
    [Newtonsoft.Json.JsonProperty("CustomPelts", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public bool? OldCustomPelts { get; set; }

    /// <summary>Whether this definition's creatures drop their own pelts (its category's switch).</summary>
    public bool OwnPeltsFor(AnimalDefinition def) => def?.IsMob == true ? MobOwnPelts : AnimalOwnPelts;

    /// <summary>Read the file (the defaults if it is missing or unreadable), old fractions converted, values clamped.</summary>
    public static TaxidermyConfig Load(ICoreAPI api)
    {
        TaxidermyConfig c;
        try { c = api.LoadModConfig<TaxidermyConfig>(FileName) ?? new(); }
        catch (Exception e)
        {
            api.Logger.Error("[Taxidermy] {0} could not be read - the defaults stand: {1}", FileName, e.Message);
            c = new();
        }
        if (c.PeltCount is int n) { c.OnePeltPerAnimal = n <= 1; c.PeltCount = null; }
        if (c.OldCustomPelts is bool own) { c.AnimalOwnPelts = c.MobOwnPelts = own; c.OldCustomPelts = null; }
        return c;
    }

    public bool SameAs(TaxidermyConfig o) =>
        o != null && AlwaysHeadAndHide == o.AlwaysHeadAndHide && AnimalHeads == o.AnimalHeads && MobHeads == o.MobHeads && OnePeltPerAnimal == o.OnePeltPerAnimal
        && AntlersOnHead == o.AntlersOnHead && AnimalOwnPelts == o.AnimalOwnPelts && MobOwnPelts == o.MobOwnPelts && MobVanillaHides == o.MobVanillaHides;

    public override string ToString() =>
        $"always head and hide {AlwaysHeadAndHide}, animal heads {AnimalHeads}, mob heads {MobHeads}, one hide per animal {OnePeltPerAnimal}, antlers on the head {AntlersOnHead}, animals' own pelts {AnimalOwnPelts}, mobs' own pelts {MobOwnPelts}, mobs' vanilla hides {MobVanillaHides}";
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
        // Read here, before the assets load, so AssetsFinalize's creative stacks already know
        // the own-pelt switches; saved back (with any new setting filled in) on the server only.
        Config = TaxidermyConfig.Load(api);
        api.RegisterItemClass("TaxidermyHead", typeof(ItemHead));
        api.RegisterItemClass("TaxidermyMount", typeof(ItemMount));
        api.RegisterBlockClass("TaxidermyMount", typeof(BlockMount));
        api.RegisterBlockEntityClass("TaxidermyMount", typeof(BEMount));
        api.RegisterItemClass("TaxidermyHide", typeof(ItemTaxidermyHide));
        api.RegisterCollectibleBehaviorClass("TaxidermyHeadSkinning", typeof(BehaviorHeadSkinning));
        api.RegisterBlockClass("TaxidermySkinnedHead", typeof(BlockSkinnedHead));
        api.RegisterBlockEntityClass("TaxidermySkinnedHead", typeof(BESkinnedHead));
        // Building a mount in the world (1.0.4).
        api.RegisterCollectibleBehaviorClass("TaxidermyAssemblyStart", typeof(BehaviorAssemblyStart));
        api.RegisterBlockClass("TaxidermyAssembly", typeof(BlockAssembly));
        api.RegisterBlockEntityClass("TaxidermyAssembly", typeof(BEAssembly));
        api.RegisterItemClass("TaxidermyNeedleThread", typeof(ItemNeedleThread));
        api.RegisterItemClass("TaxidermyNeedleBare", typeof(ItemNeedleBare));
        api.RegisterCollectibleBehaviorClass("TaxidermyBoneCarve", typeof(BehaviorBoneCarve));
        // Registered on BOTH sides even though only the server attaches it: a behaviour name
        // has to exist in the registry wherever an entity carrying it is deserialised.
        api.RegisterEntityBehaviorClass(BehaviorCode, typeof(EntityBehaviorTaxidermy));

        BarrelPatch.Apply(api);
        api.Event.MatchesGridRecipe += CoatsMatchInGrid;
        api.Network.RegisterChannel(Channel).RegisterMessageType<HeadAdjustPacket>().RegisterMessageType<OpenAdjustPacket>();
    }

    /// <summary>The grid's mount recipe: refused when the head and the pelt are different coats (Specimen.CoatsMatch).</summary>
    private static bool CoatsMatchInGrid(IPlayer player, GridRecipe recipe, ItemSlot[] ingredients, int gridWidth)
    {
        if (recipe?.Output?.ResolvedItemStack?.Collectible?.Code?.ToString() != Specimen.MountItem) return true;
        var head = ingredients.FirstOrDefault(s => s?.Itemstack?.Collectible is ItemHead)?.Itemstack;
        var pelt = ingredients.FirstOrDefault(s => s?.Itemstack?.Collectible is ItemTaxidermyHide)?.Itemstack;
        return Specimen.CoatsMatch(head, pelt);
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        capi.Network.GetChannel(Channel).SetMessageHandler<OpenAdjustPacket>(p => OnOpenAdjust(capi, p));
        MountMesher.PrepareCaches(capi);
        ItemTaxidermyHide.PrepareCaches(capi);
    }

    /// <summary>
    /// Server side, from the interaction: check the claim and, only if the player may build
    /// there, tell their client to open the Adjust window. TryAccess sends vanilla's refusal
    /// message itself when they may not.
    /// </summary>
    public void RequestAdjust(IPlayer player, BlockPos pos, bool head)
    {
        if (api is not ICoreServerAPI sapi || player is not IServerPlayer splayer) return;
        if (!sapi.World.Claims.TryAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak)) return;
        sapi.Network.GetChannel(Channel).SendPacket(new OpenAdjustPacket { X = pos.X, Y = pos.Y, Z = pos.Z, Head = head }, splayer);
    }

    private void OnOpenAdjust(ICoreClientAPI capi, OpenAdjustPacket p)
    {
        var pos = new BlockPos(p.X, p.Y, p.Z);
        if (p.Head)
        {
            if (capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityAntlerMount mount
                && mount.Inventory[0]?.Itemstack is { Collectible: ItemHead } stack && Specimen.Of(stack) != null)
                new GuiDialogMount(capi, pos, stack).TryOpen();
            return;
        }
        if (capi.World.BlockAccessor.GetBlockEntity<BEMount>(pos) is { Data: not null } be && Find(be.Data) is { } def)
            new GuiDialogMount(capi, be, def).TryOpen();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.StoreModConfig(Config, TaxidermyConfig.FileName);
        ListenForConfigReloads(api);
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
        // Backstop only - the window opens after RequestAdjust has said yes. Silent (TestAccess),
        // because a refusal here would repeat for every number dragged.
        if (api.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
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
            if (!DropsAHide(list)) hideless.Add(props.Code.ToString());
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
    /// Each type under the definition that OWNS it (Find), not every one whose pattern matches -
    /// vanilla files the moose as a deer, so "game:deer-*" would list every moose twice.
    /// </summary>
    private void BuildCreativeStacks(ICoreAPI api)
    {
        var raw = api.World.GetItem(new AssetLocation(Specimen.HeadRaw));
        var preserved = api.World.GetItem(new AssetLocation(Specimen.HeadPreserved));
        var mount = api.World.GetItem(new AssetLocation(Specimen.MountItem));
        // per creative tab (AnimalDefinition.CreativeTab): the heads, preserved heads and mounts listed there
        var byTab = new Dictionary<string, (List<JsonItemStack> raws, List<JsonItemStack> preserveds, List<JsonItemStack> mounts)>();
        int count = 0;
        foreach (var def in Animals)
        {
            string tab = string.IsNullOrEmpty(def.CreativeTab) ? "taxidermy" : def.CreativeTab;
            if (!byTab.TryGetValue(tab, out var lists)) byTab[tab] = lists = (new(), new(), new());
            var (raws, preserveds, mounts) = lists;
            foreach (var props in api.World.EntityTypes.Where(p => Find(p.Code.ToString()) == def))
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
                    data.SetString("hideSize", def.SizeFor(props.Code.ToString()));
                    data.SetString("pelt", Specimen.PeltToken(def, props, Config.OwnPeltsFor(def)));
                    if (raw != null) raws.Add(Stack(raw, data));
                    if (preserved != null) preserveds.Add(Stack(preserved, data));
                    if (mount != null) mounts.Add(Stack(mount, data));
                    count++;
                }
            }
        }
        // The hide family (Calm, 2026-09-24): every state of every animal with a pelt design, in
        // our tab only - the finished pelt in EVERY coat (so each design can be checked on each
        // coat), the other states in the base coat.
        var hides = new Dictionary<Item, List<JsonItemStack>>();
        foreach (var def in Animals.Where(d => Config.OwnPeltsFor(d) && !string.IsNullOrEmpty(d.PeltShape)))
        {
            foreach (var props in api.World.EntityTypes.Where(p => Find(p.Code.ToString()) == def))
            {
                string size = def.SizeFor(props.Code.ToString());
                int coats = 1 + Math.Max(0, props.Client?.TexturesAlternatesCount ?? 0);
                foreach (var state in new[] { "raw", "oiled", "pelt", "salted", "soaked" })
                {
                    var item = api.World.GetItem(new AssetLocation(ItemTaxidermyHide.CodeFor(state, size)));
                    if (item == null) continue;
                    if (!hides.TryGetValue(item, out var list)) hides[item] = list = new();
                    for (int coat = 0; coat < (state == "pelt" ? coats : 1); coat++)
                    {
                        var data = new TreeAttribute();
                        data.SetInt("version", 1);
                        data.SetString("entityCode", props.Code.ToString());
                        data.SetString("definition", def.Code);
                        data.SetInt("textureIndex", coat);
                        data.SetString("pose", def.Poses[0].Code);
                        data.SetString("hideSize", size);
                        data.SetString("pelt", Specimen.PeltToken(def, props));
                        list.Add(Stack(item, data));
                    }
                }
            }
        }
        foreach (var (item, list) in hides)
            item.CreativeInventoryStacks = [new CreativeTabAndStackList { Tabs = ["taxidermy"], Stacks = list.ToArray() }];

        CreativeTabAndStackList[] Lists(System.Func<(List<JsonItemStack> raws, List<JsonItemStack> preserveds, List<JsonItemStack> mounts), List<JsonItemStack>> pick) =>
            byTab.Where(kv => pick(kv.Value).Count > 0)
                 .Select(kv => new CreativeTabAndStackList { Tabs = [kv.Key, "general", "items"], Stacks = pick(kv.Value).ToArray() })
                 .ToArray();
        if (raw != null) raw.CreativeInventoryStacks = Lists(l => l.raws);
        if (preserved != null) preserved.CreativeInventoryStacks = Lists(l => l.preserveds);
        if (mount != null) mount.CreativeInventoryStacks = Lists(l => l.mounts);
        api.Logger.Event("[Taxidermy] {0} creative head(s) per state, in {1} tab(s): {2}", count, byTab.Count, string.Join(", ", byTab.Keys));
    }

    private static JsonItemStack Stack(Item item, ITreeAttribute data)
    {
        var tree = new TreeAttribute();
        tree[Specimen.Key] = data.Clone();
        // Flat, beside the tree: what the size-specific grid recipes match on.
        var size = data.GetString("hideSize");
        if (!string.IsNullOrEmpty(size)) tree.SetString("hideSize", size);
        var pelt = data.GetString("pelt");
        if (!string.IsNullOrEmpty(pelt)) tree.SetString("pelt", pelt);
        var js = new JsonItemStack { Type = EnumItemClass.Item, Code = item.Code, StackSize = 1, Attributes = new JsonObject(JToken.Parse(tree.ToJsonToken())) };
        js.ResolvedItemstack = new ItemStack(item);
        js.ResolvedItemstack.Attributes[Specimen.Key] = data.Clone();
        // The RESOLVED stack is what the creative tab actually hands out, and it is built
        // separately from the json above - so it needs the flat size too, or a creative head
        // matches no size-specific recipe at all (2026-09-22: it did not, and that is exactly
        // how Calm tests).
        Specimen.SetSize(js.ResolvedItemstack, data);
        return js;
    }

    /// <summary>Entity types our definitions cover whose own harvest has no hide at all (the mobs, chickens).</summary>
    private readonly HashSet<string> hideless = new();

    /// <summary>
    /// Does this harvest list any hide? Reads the harvestable behaviour's drops as written, "drops"
    /// or the "dropsByType" the drifters use, so a species hide (fox, bear) counts too.
    /// </summary>
    private static bool DropsAHide(JsonObject[] behaviors)
    {
        foreach (var b in behaviors)
        {
            if (b["code"].AsString() != "harvestable") continue;
            if (b["drops"].Token?.ToString().Contains("\"hide-") == true) return true;
            if (b["dropsByType"].Token?.ToString().Contains("\"hide-") == true) return true;
        }
        return false;
    }

    /// <summary>
    /// True when this entity type drops no hide of its own, so its pelt is ADDED to the harvest
    /// rather than swapped in for vanilla's hide. Server side only (filled in AssetsFinalize).
    /// </summary>
    public bool IsHideless(string entityCode) => hideless.Contains(entityCode);

    private static readonly System.Reflection.MethodInfo DropMultiplierGetter =
        HarmonyLib.AccessTools.PropertyGetter(typeof(EntityBehaviorHarvestable), "dropQuantityMultiplier");

    /// <summary>
    /// What the game multiplies a hide's quantity by in this harvest (EntityBehaviorHarvestable.
    /// GenerateDrops): its own dropQuantityMultiplier - 0.5 crushed, 0.25 acidified, and what other
    /// mods patch in, Butchering's 0.5 (0.8 light mode) for a field harvest - times the player's
    /// animalLootDropRate trait unless the creature is mechanical.
    /// </summary>
    public static float HarvestMultiplier(Entity entity, IPlayer byPlayer)
    {
        float m = 1;
        var harvest = entity.GetBehavior<EntityBehaviorHarvestable>();
        if (harvest != null && DropMultiplierGetter != null)
        {
            try { m = (float)DropMultiplierGetter.Invoke(harvest, null); }
            catch { m = 1; }
        }
        if (entity.Properties.Attributes?["isMechanical"].AsBool() != true && byPlayer?.Entity != null)
            m *= byPlayer.Entity.Stats.GetBlended("animalLootDropRate");
        return m;
    }

    private long pendingReload;

    /// <summary>
    /// Live reload from the three config managers (the Rift Weapons / Clothes Have Pockets
    /// pattern): ConfigKit and ConfigLib forward an admin's save to the server BEFORE the server has
    /// written its file and raise it again after, so the reload is deferred 50 ms and RE-ARMED on
    /// every event - never debounced, the post-write event is the one that matters. Silent when
    /// nothing of ours changed (the managers forward every save). All settings are server-owned
    /// and read at harvest time, so nothing has to reach the clients (CustomPelts' creative tab
    /// follows on the next join).
    /// </summary>
    private void ListenForConfigReloads(ICoreServerAPI sapi)
    {
        EventBusListenerDelegate onSaved = delegate (string eventName, ref EnumHandling handling, IAttribute data)
        {
            string uid = (data as ITreeAttribute)?.GetString("player");
            string savedBy = uid == null ? null : (sapi.World.PlayerByUid(uid)?.PlayerName ?? uid);
            if (pendingReload != 0) sapi.Event.UnregisterCallback(pendingReload);
            pendingReload = sapi.Event.RegisterCallback(_ => { pendingReload = 0; ReloadConfig(sapi, savedBy); }, 50);
        };
        string id = Mod.Info.ModID;
        sapi.Event.RegisterEventBusListener(onSaved, 0.5, "imm." + id);
        sapi.Event.RegisterEventBusListener(onSaved, 0.5, "configkit:" + id + ":config-saved");
        sapi.Event.RegisterEventBusListener(onSaved, 0.5, "configlib:" + id + ":config-saved");
    }

    private void ReloadConfig(ICoreServerAPI sapi, string savedBy)
    {
        var now = TaxidermyConfig.Load(sapi);
        if (now.SameAs(Config)) return;
        Config = now;
        sapi.Logger.Notification("[Taxidermy] settings reloaded{0}: {1}", savedBy == null ? "" : " (saved by " + savedBy + ")", now);
    }

    /// <summary>
    /// Whether this carcass gives its head - rolled once and remembered on the carcass, so a second
    /// pass over the harvest never rolls again.
    /// </summary>
    public bool RollHead(Entity entity, AnimalDefinition def, float harvestMultiplier = 1)
    {
        const string Key = "taxidermyHeadRoll";
        if (!Config.HeadsFor(def)) return false;
        if (Config.AlwaysHeadAndHide) return true;
        int known = entity.Attributes.GetInt(Key);
        if (known != 0) return known == 1;
        // At most one head; its chance scaled the way the game scales a hide (Calm, 2026-09-26).
        float chance = Math.Clamp((def.ChanceFor(entity.Code.ToString()) ?? TaxidermyConfig.HeadChance) * harvestMultiplier, 0f, 1f);
        bool hit = chance >= 1 || (chance > 0 && entity.World.Rand.NextDouble() < chance);
        entity.Attributes.SetInt(Key, hit ? 1 : 2);
        return hit;
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
        if (api is Vintagestory.API.Client.ICoreClientAPI capi)
        {
            MountMesher.Clear(capi);
            ItemTaxidermyHide.ClearCache(capi);
        }
        base.Dispose();
    }
}
