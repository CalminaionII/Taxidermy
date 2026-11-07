using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace Taxidermy.Handbook;

/// <summary>
/// One cycling handbook page per head state, hide stage and mount animal. A real head or skin carries its animal, so a
/// plain page's code would match nothing and H on it would open no page.
/// <list type="bullet">
/// <item>Cycles: every head, hide and mount stack the creative tab has gets a handbook page, and HandbookGroups folds
/// them into one page per head state, per hide stage (all four sizes together) and per mount animal.</item>
/// <item>The right page: ItemHead, ItemTaxidermyHide and ItemMount answer the handbook's
/// <c>IHandBookPageCodeProvider</c> with <see cref="PageCodeFor"/>, the page of the creative stack of the same animal
/// and coat, so H on any real one or a link to it opens its group showing that animal.</item>
/// <item>The right recipe: while the handbook builds one of our pages, it sees per-animal copies of the head barrel
/// recipe and the mount grid recipes instead of the plain ones, so Created by and Used in show that animal. Only for
/// our pages: the borax, needle or dry grass page would otherwise list a mount or a head for every animal. The barrels
/// and the grid never see them.</item>
/// <item>The exact item: hide recipes get the same per-animal copies, and "Ingredient for" and "cures into" show the
/// page animal's own item. See <see cref="ListEntryPrefix"/> and <see cref="ExactTransitions"/>.</item>
/// </list>
/// </summary>
internal static class HandbookVariants
{
    /// <summary>
    /// The stack whose page of ours the handbook is building, else null (per thread - the handbook also builds on a
    /// worker).
    /// </summary>
    [ThreadStatic] private static ItemStack page;

    private static Harmony harmony;
    private static readonly object gate = new();
    private static readonly Dictionary<CollectibleObject, List<ItemStack>> stacksOf = new();
    private static Dictionary<string, ItemStack> byKey;
    private static (List<BarrelRecipe> from, int count, List<BarrelRecipe> list) barrel;
    private static (List<GridRecipe> from, int count, List<GridRecipe> list) grid;

    public static void Apply(ICoreClientAPI capi)
    {
        if (harmony != null) return;
        harmony = new Harmony("taxidermy.handbookvariants");
        Patch(capi, () => harmony.Patch(AccessTools.Method(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), nameof(CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo)),
            prefix: new HarmonyMethod(typeof(HandbookVariants), nameof(BuildingPrefix)),
            finalizer: new HarmonyMethod(typeof(HandbookVariants), nameof(BuildingFinalizer))), "page building");
        Patch(capi, () => harmony.Patch(AccessTools.Method(typeof(ApiAdditions), nameof(ApiAdditions.GetBarrelRecipes)),
            postfix: new HarmonyMethod(typeof(HandbookVariants), nameof(BarrelPostfix))), "barrel recipes");
        // The world's grid recipe list is on the game's own class (ClientMain), implemented as IWorldAccessor's rather
        // than a plain property of its own, so it is found through the interface.
        Patch(capi, () => harmony.Patch(GridRecipesGetter(capi.World.GetType()) ?? throw new MissingMethodException("GridRecipes getter"),
            postfix: new HarmonyMethod(typeof(HandbookVariants), nameof(GridPostfix))), "grid recipes");
        // every output under "Ingredient for" passes through this: its grid part reads the game's ingredient search,
        // not GridRecipes, so a plain head there gives a plain mount however the recipes above are shown
        Patch(capi, () => harmony.Patch(AccessTools.Method(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addToListUniquely")
                ?? throw new MissingMethodException("addToListUniquely"),
            prefix: new HarmonyMethod(typeof(HandbookVariants), nameof(ListEntryPrefix))), "Ingredient for list");
    }

    /// <summary>
    /// The running world's own getter of <see cref="IWorldAccessor.GridRecipes"/>, however its class implements it.
    /// </summary>
    private static System.Reflection.MethodInfo GridRecipesGetter(Type world)
    {
        var map = world.GetInterfaceMap(typeof(IWorldAccessor));
        int at = Array.FindIndex(map.InterfaceMethods, m => m.Name == "get_" + nameof(IWorldAccessor.GridRecipes));
        return at < 0 ? null : map.TargetMethods[at];
    }

    /// <summary>A patch that fails only costs the handbook its per-animal recipes - logged, never thrown.</summary>
    private static void Patch(ICoreClientAPI capi, Action patch, string what)
    {
        try { patch(); }
        catch (Exception e) { capi.Logger.Warning("[Taxidermy] Handbook {0} not patched - pages show the plain recipes: {1}", what, e.Message); }
    }

    public static void Remove()
    {
        harmony?.UnpatchAll("taxidermy.handbookvariants");
        harmony = null;
        lock (gate)
        {
            stacksOf.Clear();
            byKey = null;
            barrel = default;
            grid = default;
        }
    }

    // ---------------------------------------------------------------- the stacks

    /// <summary>The creative tab's stacks of this head, hide or mount, once each - its handbook pages.</summary>
    public static List<ItemStack> StacksOf(CollectibleObject item)
    {
        lock (gate)
        {
            if (stacksOf.TryGetValue(item, out var known)) return known;
        }
        var seen = new HashSet<string>();
        var list = new List<ItemStack>();
        foreach (var tab in item.CreativeInventoryStacks ?? [])
            foreach (var js in tab.Stacks ?? [])
                if (js?.ResolvedItemstack is { } stack && Specimen.Of(stack) != null && seen.Add(GuiHandbookItemStackPage.PageCodeForStack(stack)))
                    list.Add(stack);
        lock (gate) stacksOf[item] = list;
        return list;
    }

    private static string KeyOf(CollectibleObject item, ITreeAttribute data, int coat) =>
        item.Code + "|" + data.GetString("definition") + "|" + data.GetString("entityCode") + "|" + coat;

    /// <summary>The creative stack of this item for this animal and coat (else its base coat), or null.</summary>
    public static ItemStack VariantOf(ICoreAPI api, CollectibleObject item, ITreeAttribute data)
    {
        if (item == null || data == null) return null;
        Dictionary<string, ItemStack> index;
        lock (gate) index = byKey;
        if (index == null)
        {
            index = new Dictionary<string, ItemStack>();
            foreach (var collectible in api.World.Items.Where(i => i is ItemHead or ItemTaxidermyHide or ItemMount))
                foreach (var stack in StacksOf(collectible))
                    index.TryAdd(KeyOf(collectible, Specimen.Of(stack), Specimen.Of(stack).GetInt("textureIndex")), stack);
            lock (gate) byKey = index;
        }
        // the hide stages before the pelt are in the base coat only (BuildCreativeStacks)
        return index.TryGetValue(KeyOf(item, data, data.GetInt("textureIndex")), out var exact) ? exact
            : index.TryGetValue(KeyOf(item, data, 0), out var baseCoat) ? baseCoat : null;
    }

    /// <summary>
    /// The handbook page for a head, hide or mount: the page of its creative stack (same animal and coat), which
    /// HandbookGroups opens as its group showing that animal. A plain one (a recipe's) opens its item's first page.
    /// Null = the game's own page code.
    /// </summary>
    public static string PageCodeFor(ICoreAPI api, ItemStack stack)
    {
        if (stack?.Collectible == null || api == null) return null;
        try
        {
            var data = Specimen.Of(stack);
            var variant = data != null ? VariantOf(api, stack.Collectible, data) : StacksOf(stack.Collectible).FirstOrDefault();
            return variant == null ? null : GuiHandbookItemStackPage.PageCodeForStack(variant);
        }
        catch (Exception) { return null; }   // the handbook must never break over a page code
    }

    // ---------------------------------------------------------------- the recipes

    public static void BuildingPrefix(ItemSlot inSlot, out ItemStack __state)
    {
        __state = page;
        page = inSlot?.Itemstack is { Collectible: ItemHead or ItemTaxidermyHide or ItemMount } stack ? stack : null;
    }

    public static Exception BuildingFinalizer(Exception __exception, ItemStack __state)
    {
        page = __state;
        return __exception;
    }

    public static void BarrelPostfix(ICoreAPI api, ref List<BarrelRecipe> __result)
    {
        if (page == null || __result == null) return;
        try { __result = BarrelFor(api, __result); }
        catch (Exception) { /* the plain recipes */ }
    }

    public static void GridPostfix(object __instance, ref List<GridRecipe> __result)
    {
        if (page == null || __result == null || __instance is not IWorldAccessor world) return;
        try { __result = GridFor(world.Api, __result); }
        catch (Exception) { /* the plain recipes */ }
    }

    /// <summary>
    /// A plain head, hide or mount about to be listed under "Ingredient for" on one of our pages becomes the page
    /// animal's own.
    /// </summary>
    public static void ListEntryPrefix(ICoreClientAPI capi, ref ItemStack entry)
    {
        if (page == null || entry?.Collectible is not (ItemHead or ItemTaxidermyHide or ItemMount) || Specimen.Of(entry) != null) return;
        try
        {
            if (Specimen.Of(page) is { } data && VariantOf(capi, entry.Collectible, data) is { } own) entry = Sized(own, entry.StackSize);
        }
        catch (Exception) { /* the plain stack */ }
    }

    /// <summary>
    /// What a hide of ours turns into, as the handbook should show it: that animal's own next stage. The item JSON can
    /// only name the plain item, and the real change keeps the animal in OnTransitionNow. Only while one of our pages
    /// is being built; the game's own transitions always get the props as they are.
    /// </summary>
    public static TransitionableProperties[] ExactTransitions(ICoreAPI api, ItemStack stack, TransitionableProperties[] props)
    {
        if (page == null || props == null) return props;
        try
        {
            var data = Specimen.Of(stack);
            if (data == null) return props;
            // the earlier stages exist in the base coat only, so on a page of the same animal the page's coat decides:
            // the oiled hide then also counts as what the pelt of each other coat cures from
            if (Specimen.Of(page) is { } onPage && onPage.GetString("definition") == data.GetString("definition")
                                                && onPage.GetString("entityCode") == data.GetString("entityCode")) data = onPage;
            TransitionableProperties[] shown = null;
            for (int i = 0; i < props.Length; i++)
            {
                var to = props[i]?.TransitionedStack?.ResolvedItemstack;
                if (to?.Collectible is not ItemTaxidermyHide || Specimen.Of(to) != null) continue;
                if (VariantOf(api, to.Collectible, data) is not { } own) continue;
                shown ??= (TransitionableProperties[])props.Clone();
                shown[i] = props[i].Clone();
                shown[i].TransitionedStack.ResolvedItemstack = Sized(own, to.StackSize);
            }
            return shown ?? props;
        }
        catch (Exception) { return props; }
    }

    /// <summary>The barrel recipes with each plain head or hide recipe replaced by one per animal and coat.</summary>
    private static List<BarrelRecipe> BarrelFor(ICoreAPI api, List<BarrelRecipe> original)
    {
        lock (gate)
        {
            if (barrel.from == original && barrel.count == original.Count && barrel.list != null) return barrel.list;
        }
        var ours = original.Where(r => r?.Output?.ResolvedItemStack?.Collectible is ItemHead or ItemTaxidermyHide
                                       && r.Ingredients?.Any(i => i?.ResolvedItemStack?.Collectible is ItemHead or ItemTaxidermyHide) == true).ToList();
        var list = original.Except(ours).ToList();
        foreach (var recipe in ours)
        {
            int at = Array.FindIndex(recipe.Ingredients, i => i?.ResolvedItemStack?.Collectible is ItemHead or ItemTaxidermyHide);
            var inItem = recipe.Ingredients[at].ResolvedItemStack.Collectible;
            foreach (var made in StacksOf(recipe.Output.ResolvedItemStack.Collectible))
            {
                var from = VariantOf(api, inItem, Specimen.Of(made));
                if (from == null) continue;
                var copy = recipe.Clone();
                copy.Ingredients = recipe.Ingredients.Select(i => i?.Clone()).ToArray();
                copy.Output = recipe.Output.Clone();
                Show(copy.Ingredients[at], from, recipe.Ingredients[at].ResolvedItemStack.StackSize);
                copy.Output.ResolvedItemStack = Sized(made, recipe.Output.ResolvedItemStack.StackSize);
                list.Add(copy);
            }
        }
        lock (gate) barrel = (original, original.Count, list);
        return list;
    }

    /// <summary>
    /// The grid recipes with the mount recipes replaced by one per animal and coat (its head, its own pelt, its mount)
    /// and the hide recipes likewise (its hide in, its hide out - raw + fat = oiled, raw + salt = salted).
    /// </summary>
    private static List<GridRecipe> GridFor(ICoreAPI api, List<GridRecipe> original)
    {
        lock (gate)
        {
            if (grid.from == original && grid.count == original.Count && grid.list != null) return grid.list;
        }
        var mounts = original.Where(r => r?.Output?.ResolvedItemStack?.Collectible is ItemMount && r.ResolvedIngredients != null).ToList();
        var hides = original.Where(r => r?.Output?.ResolvedItemStack?.Collectible is ItemTaxidermyHide
                                        && r.ResolvedIngredients?.Any(i => i?.ResolvedItemStack?.Collectible is ItemTaxidermyHide) == true).ToList();
        var list = original.Except(mounts).Except(hides).ToList();
        // a mount recipe per pelt label, which its head ingredient asks for - read from the resolved attributes: the
        // client is sent only those, never the recipe file's "attributes"
        var byPelt = new Dictionary<string, GridRecipe>();
        foreach (var recipe in mounts)
        {
            var head = recipe.ResolvedIngredients.FirstOrDefault(i => i?.ResolvedItemStack?.Collectible is ItemHead);
            string pelt = head?.ResolvedAttributes?.GetString("pelt") ?? head?.ResolvedItemStack?.Attributes?.GetString("pelt");
            if (pelt != null) byPelt.TryAdd(pelt, recipe);
        }
        var mountItem = mounts.FirstOrDefault()?.Output.ResolvedItemStack.Collectible;
        foreach (var made in mountItem == null ? [] : StacksOf(mountItem))
        {
            var data = Specimen.Of(made);
            if (data.GetString("pelt") is not { } pelt || !byPelt.TryGetValue(pelt, out var recipe)) continue;
            list.Add(GridCopy(api, recipe, made));
        }
        foreach (var recipe in hides)
            foreach (var made in StacksOf(recipe.Output.ResolvedItemStack.Collectible))
                list.Add(GridCopy(api, recipe, made));
        lock (gate) grid = (original, original.Count, list);
        return list;
    }

    /// <summary>A grid recipe making exactly this stack, from that animal's own head and hide.</summary>
    private static GridRecipe GridCopy(ICoreAPI api, GridRecipe recipe, ItemStack made)
    {
        var data = Specimen.Of(made);
        var copy = recipe.Clone();
        copy.ResolvedIngredients = recipe.ResolvedIngredients.Select(i => i?.Clone()).ToArray();
        copy.Output = recipe.Output.Clone();
        foreach (var ingredient in copy.ResolvedIngredients)
        {
            var item = ingredient?.ResolvedItemStack?.Collectible;
            if (item is not (ItemHead or ItemTaxidermyHide)) continue;
            // its own head; its own pelt in its coat (a head takes only a pelt of its own coat) - a vanilla or another
            // mod's pelt stays as the recipe has it
            if (VariantOf(api, item, data) is { } own) Show(ingredient, own, ingredient.ResolvedItemStack.StackSize);
        }
        copy.Output.ResolvedItemStack = Sized(made, recipe.Output.ResolvedItemStack.StackSize);
        return copy;
    }

    /// <summary>The ingredient's own stack - its ResolvedItemStack setter would write the game's shared one.</summary>
    private static readonly AccessTools.FieldRef<CraftingRecipeIngredient, ItemStack> ownStack =
        AccessTools.FieldRefAccess<CraftingRecipeIngredient, ItemStack>("resolvedItemStack");

    /// <summary>
    /// An ingredient showing (and matching, for Used in) exactly this stack. Game 1.22 keeps one stack per distinct
    /// ingredient, shared by every recipe using it (vsapi CraftingRecipeIngredient, deduplicationIndex): on a copy the
    /// ResolvedItemStack setter writes that shared one, and the copy goes on showing the plain head. A copy reads its
    /// own stack first, so that is the one set.
    /// </summary>
    private static void Show(CraftingRecipeIngredient ingredient, ItemStack stack, int size)
    {
        var shown = Sized(stack, size);
        ownStack(ingredient) = shown;
        ingredient.ResolvedAttributes = shown.Attributes.Clone();
        ingredient.Attributes = new JsonObject(JToken.Parse(stack.Attributes.ToJsonToken()));
    }

    private static ItemStack Sized(ItemStack stack, int size)
    {
        var copy = stack.Clone();
        copy.StackSize = Math.Max(1, size);
        return copy;
    }
}
