using HarmonyLib;
using Vintagestory.API.Common;
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
        var interact = AccessTools.Method(typeof(BlockAntlerMount), nameof(BlockAntlerMount.OnBlockInteractStart));
        if (interact != null) harmony.Patch(interact, prefix: new HarmonyMethod(typeof(BarrelPatch), nameof(AntlerMountInteract)));
        else api.Logger.Error("[Taxidermy] BlockAntlerMount.OnBlockInteractStart not found - heads on antler mounts cannot be adjusted");
    }

    public static bool AntlerMountInteract(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        var slot = byPlayer?.InventoryManager?.ActiveHotbarSlot;
        if (slot == null || !slot.Empty || !byPlayer.WorldData.EntityControls.ShiftKey) return true;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityAntlerMount mount) return true;
        var stack = mount.Inventory[0]?.Itemstack;
        if (stack?.Collectible is not ItemHead || Specimen.Of(stack) == null) return true;
        if (world.Api is ICoreClientAPI capi) new GuiDialogMount(capi, blockSel.Position, stack).TryOpen();
        __result = true;
        return false;
    }

    public static void Remove()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
    }

    public static void Before(BarrelRecipe __instance, ItemSlot[] inputSlots, out ITreeAttribute __state)
    {
        __state = null;
        // Only our own outputs (a pelt + borax -> skin). A pelt soaked in lime becomes a plain
        // vanilla hide and is supposed to forget.
        if (__instance.Output?.ResolvedItemStack?.Collectible?.Code?.Domain != Specimen.Domain) return;
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
            if (stack?.Collectible?.Code?.Domain != Specimen.Domain) continue;
            stack.Attributes[Specimen.Key] = __state.Clone();
            slot.MarkDirty();
        }
    }
}
