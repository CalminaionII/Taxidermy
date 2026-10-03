using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace Taxidermy;

/// <summary>
/// The one Harmony patch in the mod, and why it exists.
///
/// <c>BarrelRecipe.TryCraftNow</c> (vssurvivalmod, Systems/Recipe/Barrel/BarrelRecipe.cs) builds
/// its result as <c>Output.ResolvedItemStack.Clone()</c> and then calls <c>CarryOverFreshness</c>;
/// nothing else from the input survives. A raw specimen sealed with borax would therefore come
/// out as a preserved specimen that has forgotten which animal it was. There is no hook for it
/// - no <c>copyAttributesFrom</c> on barrel recipes - so this captures the pelt's tree before
/// the craft and puts it back on the output after.
///
/// It is a no-op unless the recipe's output is in our domain, so it cannot touch
/// anyone else's barrel. Applied once per process: singleplayer runs Start() for both sides
/// in one process, and patching twice would run the prefix and postfix twice.
/// </summary>
public static class BarrelPatch
{
    private const string HarmonyId = "taxidermy.barrel";
    private static Harmony harmony;

    /// <summary>Vanilla's generic raw hides, the only ones an animal's own pelt replaces.</summary>
    private static readonly System.Text.RegularExpressions.Regex GenericRawHide = new("^hide-raw-(small|medium|large|huge)$");

    public static void Apply(ICoreAPI api)
    {
        if (harmony != null) return;
        harmony = new Harmony(HarmonyId);
        var target = AccessTools.Method(typeof(BarrelRecipe), nameof(BarrelRecipe.TryCraftNow));
        if (target == null)
        {
            api.Logger.Error("[Taxidermy] BarrelRecipe.TryCraftNow not found - preserved specimens will lose their animal");
            return;
        }
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(BarrelPatch), nameof(Before)),
            postfix: new HarmonyMethod(typeof(BarrelPatch), nameof(After)));

        // The second and last patch: sneak + right-click with an empty hand on an antler mount
        // that holds one of our heads opens the Adjust window instead of taking the head down.
        // Vanilla's BlockAntlerMount.OnBlockInteractStart has no hook for that; everything
        // else about the mount stays vanilla's. No-op unless the mount holds our head.
        // Harvest: an animal with its own pelt design gives ITS raw hide, not vanilla's generic one
        // (Calm, 2026-09-24). A postfix on the harvest itself, so vanilla's files stay untouched.
        var drops = AccessTools.Method(typeof(EntityBehaviorHarvestable), nameof(EntityBehaviorHarvestable.GenerateDrops));
        if (drops != null) harmony.Patch(drops, postfix: new HarmonyMethod(typeof(BarrelPatch), nameof(SwapHide)));
        else api.Logger.Error("[Taxidermy] EntityBehaviorHarvestable.GenerateDrops not found - animals will drop vanilla hides");

        // No head or hide from vanilla's rip harvest (a blade hit on a drifter, shiver or bowtorn):
        // a flag around it that EntityBehaviorTaxidermy reads. Finalizer, so it is always cleared.
        var rip = AccessTools.Method(typeof(EntityBehaviorRipHarvestable), nameof(EntityBehaviorRipHarvestable.OnEntityReceiveDamage));
        if (rip != null) harmony.Patch(rip, prefix: new HarmonyMethod(typeof(BarrelPatch), nameof(RipStart)), finalizer: new HarmonyMethod(typeof(BarrelPatch), nameof(RipEnd)));
        else api.Logger.Error("[Taxidermy] EntityBehaviorRipHarvestable.OnEntityReceiveDamage not found - a blade hit may drop a head");

        var interact = AccessTools.Method(typeof(BlockAntlerMount), nameof(BlockAntlerMount.OnBlockInteractStart));
        if (interact != null) harmony.Patch(interact, prefix: new HarmonyMethod(typeof(BarrelPatch), nameof(AntlerMountInteract)));
        else api.Logger.Error("[Taxidermy] BlockAntlerMount.OnBlockInteractStart not found - heads on antler mounts cannot be adjusted");
    }

    public static void RipStart() => EntityBehaviorTaxidermy.RipHarvesting = true;

    public static void RipEnd() => EntityBehaviorTaxidermy.RipHarvesting = false;

    public static bool AntlerMountInteract(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        var slot = byPlayer?.InventoryManager?.ActiveHotbarSlot;
        if (slot == null || !slot.Empty || !byPlayer.WorldData.EntityControls.ShiftKey) return true;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityAntlerMount mount) return true;
        var stack = mount.Inventory[0]?.Itemstack;
        if (stack?.Collectible is not ItemHead || Specimen.Of(stack) == null) return true;
        // The server decides and tells the client to open the window (claims, 2026-09-23). Both
        // sides still swallow the click, so vanilla never takes the head down on a sneak-click.
        if (world.Side == EnumAppSide.Server)
            world.Api.ModLoader.GetModSystem<TaxidermyModSystem>().RequestAdjust(byPlayer, blockSel.Position, true);
        __result = true;
        return false;
    }

    public static void Remove()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
    }

    /// <summary>
    /// After vanilla fills the carcass's harvest inventory: the HEAD (EntityBehaviorTaxidermy.
    /// MakeHead - here, not inside GenerateDrops, so a falx rip harvest cannot use it up), then the
    /// hide rules (Calm, 2026-09-26):
    /// <list type="number">
    /// <item>How many: OnePeltPerAnimal on - EXACTLY ONE hide, the extras taken away and one added
    /// when there is none (the hideless drifters and chickens, a carcass Butchering left bare). Off -
    /// the game's own count, with one added under AlwaysHeadAndHide when there is none - or, with
    /// both off, for a creature with no hide in the game at all, on a half chance (1.0.2).</item>
    /// <item>Which: with CustomPelts on and a pelt design, every plain raw hide (game:hide-raw-
    /// small..huge) becomes the animal's own raw pelt - same size, same count - carrying the animal
    /// and its coat. So pelts drop at exactly the game's rates.</item>
    /// </list>
    /// Then the inventory is saved back the way GenerateDrops saves it (WatchedAttributes
    /// "harvestableInv") so the client sees it. Once per carcass: this postfix runs on every
    /// GenerateDrops call, and without the mark a hide already taken would be added again.
    /// </summary>
    public static void SwapHide(EntityBehaviorHarvestable __instance, IPlayer byPlayer)
    {
        const string Done = "taxidermyHidesDone";
        var entity = __instance.entity;
        // Not during a rip harvest (a blade hit throws the first item out): the rules run at the knife harvest.
        if (entity?.World?.Side != EnumAppSide.Server || entity.Attributes.GetBool(Done) || EntityBehaviorTaxidermy.RipHarvesting) return;
        var system = entity.Api.ModLoader.GetModSystem<TaxidermyModSystem>();
        var def = system?.Find(entity.Code.ToString());
        if (def == null || __instance.Inventory is not { } inv) return;
        entity.Attributes.SetBool(Done, true);
        var config = system.Config;
        bool changed = false;

        // Not when the harvest already holds one - a carcass whose drops an older version built.
        if (!inv.Any(s => s.Itemstack?.Collectible is ItemHead)
            && EntityBehaviorTaxidermy.MakeHead(entity, system, def, TaxidermyModSystem.HarvestMultiplier(entity, byPlayer)) is { } head)
            changed |= AddToHarvest(inv, head);

        // The antlers stay on the head (1.0.3): vanilla's antler growth put them in this harvest
        // (IHarvestableDrops); with a head here they are taken out and noted on it, for skinning.
        if (config.AntlersOnHead && inv.FirstOrDefault(s => s.Itemstack?.Collectible is ItemHead)?.Itemstack is { } headStack)
            changed |= KeepAntlersOnHead(inv, headStack);

        // Which hides this creature may be given (1.0.3): its own pelt under its category's switch,
        // the game's plain hide always for animals and only with MobVanillaHides for mobs. Hides
        // the creature drops of its own accord are never taken - only what this mod adds or turns.
        bool ownPelt = config.OwnPeltsFor(def) && !string.IsNullOrEmpty(def.PeltShape);
        bool mayAdd = ownPelt || !def.IsMob || config.MobVanillaHides;

        int hides = inv.Where(IsHide).Sum(s => s.Itemstack.StackSize);
        if (config.OnePeltPerAnimal && hides > 1)
        {
            bool kept = false;
            foreach (var slot in inv.Where(IsHide).ToArray())
            {
                if (!kept) { slot.Itemstack.StackSize = 1; kept = true; }
                else slot.Itemstack = null;
            }
            changed = true;
        }
        else if (hides == 0 && mayAdd && (config.OnePeltPerAnimal || config.AlwaysHeadAndHide))
            changed |= AddPlainHide(entity, def, inv);
        else if (hides == 0 && mayAdd && system.IsHideless(entity.Code.ToString())
            && entity.World.Rand.NextDouble() < (def.ChanceFor(entity.Code.ToString()) ?? TaxidermyConfig.HidelessHideChance) * TaxidermyModSystem.HarvestMultiplier(entity, byPlayer))
            changed |= AddPlainHide(entity, def, inv);   // both switches off: the mobs and chickens on a chance (1.0.2)

        if (ownPelt)
        {
            foreach (var slot in inv.ToArray())
            {
                var code = slot.Itemstack?.Collectible?.Code;
                if (code == null || code.Domain != "game") continue;
                var m = GenericRawHide.Match(code.Path);
                if (!m.Success) continue;
                var item = entity.World.GetItem(new AssetLocation(ItemTaxidermyHide.CodeFor("raw", m.Groups[1].Value)));
                if (item == null) continue;
                var own = new ItemStack(item, slot.Itemstack.StackSize);
                ItemTaxidermyHide.CarryAnimal(Specimen.Capture(entity, def), own);
                slot.Itemstack = own;
                changed = true;
            }
        }
        if (changed) SyncHarvest(entity, inv);
    }

    /// <summary>
    /// Each item the head wears (its <c>attachments</c>) that is also in the harvest is taken out -
    /// one of each - and its code noted on the head (<see cref="Specimen.AntlersKey"/>). Only what
    /// was actually handed over is noted, so skinning can never make antlers the carcass did not give.
    /// </summary>
    private static bool KeepAntlersOnHead(InventoryBase inv, ItemStack headStack)
    {
        if (headStack.Attributes.HasAttribute(Specimen.AntlersKey)) return false;
        var kept = new List<string>();
        foreach (var code in Specimen.Attachments(Specimen.Of(headStack)))
        {
            var slot = inv.FirstOrDefault(s => s.Itemstack?.Collectible?.Code?.ToString() == code);
            if (slot == null) continue;
            if (--slot.Itemstack.StackSize <= 0) slot.Itemstack = null;
            kept.Add(code);
        }
        if (kept.Count == 0) return false;
        headStack.Attributes.SetString(Specimen.AntlersKey, string.Join(",", kept));
        return true;
    }

    /// <summary>Any hide or pelt: the game's (plain or a species' own), ours, or another mod's pelt item.</summary>
    private static bool IsHide(ItemSlot slot) =>
        slot.Itemstack?.Collectible?.Code?.Path is { } path && (path.StartsWith("hide-") || path.StartsWith("pelt-"));

    /// <summary>
    /// A plain raw hide of the animal's size into a free slot (the inventory grows if it must, as
    /// vanilla's does) - turned into the animal's own pelt by the step after, where it has one.
    /// </summary>
    private static bool AddPlainHide(Entity entity, AnimalDefinition def, InventoryBase inv) =>
        entity.World.GetItem(new AssetLocation("game:hide-raw-" + def.SizeFor(entity.Code.ToString()))) is { } plain
        && AddToHarvest(inv, new ItemStack(plain));

    /// <summary>Into a free slot of the harvest - the inventory grows if it must, as vanilla's does.</summary>
    private static bool AddToHarvest(InventoryBase inv, ItemStack stack)
    {
        var free = inv.FirstOrDefault(s => s.Empty);
        if (free == null && inv is InventoryGeneric grown)
        {
            grown.AddSlots(1);
            free = inv[inv.Count - 1];
        }
        if (free == null) return false;
        free.Itemstack = stack;
        return true;
    }

    private static void SyncHarvest(Entity entity, InventoryBase inv)
    {
        var tree = new TreeAttribute();
        inv.ToTreeAttributes(tree);
        entity.WatchedAttributes["harvestableInv"] = tree;
        entity.WatchedAttributes.MarkPathDirty("harvestableInv");
    }

    public static void Before(BarrelRecipe __instance, ItemSlot[] inputSlots, out ITreeAttribute __state)
    {
        __state = null;
        // Only our own outputs (a pelt + borax -> skin). A pelt soaked in lime becomes a plain
        // vanilla hide and is supposed to forget.
        // Also the game:hide-*-taxidermy-* family (salted -> soaked in lime or borax), 2026-09-24.
        var outCollectible = __instance.Output?.ResolvedItemStack?.Collectible;
        if (outCollectible?.Code?.Domain != Specimen.Domain && outCollectible is not ItemTaxidermyHide) return;
        foreach (var slot in inputSlots)
        {
            __state = Specimen.Of(slot?.Itemstack)?.Clone();
            if (__state != null) return;
        }
    }

    public static void After(bool __result, ItemSlot[] inputSlots, ITreeAttribute __state)
    {
        if (!__result || __state == null) return;
        foreach (var slot in inputSlots)
        {
            var stack = slot?.Itemstack;
            if (stack?.Collectible?.Code?.Domain != Specimen.Domain && stack?.Collectible is not ItemTaxidermyHide) continue;
            stack.Attributes[Specimen.Key] = __state.Clone();
            Specimen.SetSize(stack, __state);
            slot.MarkDirty();
        }
    }
}
