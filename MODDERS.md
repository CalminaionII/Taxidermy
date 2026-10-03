# Taxidermy - guide for modders

The full modder reference that used to be on the ModDB page (it no longer fits there). Questions and reports: the Taxidermy page on mods.vintagestory.at.

## For Modders

Add your own animals with one JSON file - no patches, no code, and no dependency entry.

### Quick Start

**1.** **Create the file**

Make a file at `assets/<yourmod>/config/taxidermy/<anything>.json` - any file name will do.

**2.** **Copy the example below into it**

Change **code**, **entityCodes** and **hideSize** to your animal's, and the poses to animations your model has.

**3.** **Load a world**

Your animal's heads and mounts are in the Taxidermy creative tab, and the server log says `[Taxidermy] N animal definition(s) loaded`. If something in your file is wrong, the log names the file and the problem.

**Minimal example** - that is all most animals need:

```json
[
  {
    "code": "yourmod:badger",
    "entityCodes": [ "yourmod:badger-*-adult-*" ],
    "hideSize": "medium",
    "poses": [
      { "code": "standing" },
      { "code": "sitting",  "animation": "sit",   "frame": 15 },
      { "code": "sleeping", "animation": "sleep", "frame": 15 },
      { "code": "digging",  "animation": "dig",   "frame": 20 }
    ],
    "headSkinningDrops": [
      { "code": "game:bushmeat-raw", "quantityBySize": { "small": 1, "medium": 1, "large": 2, "huge": 2 } },
      { "code": "game:fat", "quantityBySize": { "small": 0.5, "medium": 1, "large": 1.5, "huge": 2 } }
    ]
  }
]
```

#### Show the full example with every field

Only **code**, **entityCodes** and **poses** are required. Each field is explained in the reference below.

```json
[
  {
    "code": "yourmod:badger",
    "enabled": true,
    "priority": 0,
    "entityCodes": [ "yourmod:badger-*-adult-*" ],
    "poses": [
      { "code": "standing" },
      { "code": "sitting",  "animation": "sit",   "frame": 15 },
      { "code": "sleeping", "animation": "sleep", "frame": 15 },
      { "code": "digging",  "animation": "dig",   "frame": 20 }
    ],
    "scale": 1,
    "hideSize": "medium",
    "hideSizeByType": { "*-honey-*": "small" },
    "pelt": "yourmod-badger-{type}-{gender}",
    "peltShape": "yourmod:item/pelt/badger",
    "furTexture": "badger",
    "chance": 0.5,
    "chanceByType": { "*-honey-*": 0.2 },
    "headElements": [ "head" ],
    "keepEyes": true,
    "creativeTab": "yourmod-taxidermy",
    "category": "animal",
    "headSkinningDrops": [
      { "code": "game:bushmeat-raw", "quantityBySize": { "small": 1, "medium": 1, "large": 2, "huge": 2 } },
      { "code": "game:fat", "quantityBySize": { "small": 0.5, "medium": 1, "large": 1.5, "huge": 2 } }
    ]
  }
]
```

### Field Reference

Everything else follows from your file: the head is lifted out of your model at load, textures and coat variants come from the entity, antlers come along if they use the game's attachable mechanism, and poses bake from whatever animations your shape has. It needs the standard shape renderer and the vanilla harvestable behaviour, which nearly every animal mod uses. To change one of this mod's animals, patch `taxidermy:config/taxidermy/vanilla.json` as you would any file.

-

-

-

-

-

| **Required** |  |
|---|---|
| `code` | A stable id, saved into every head and mount. **Never rename it after release.** |
| `entityCodes` | Wildcard patterns, domain included. Every entity type they match gets the harvest drop. |
| `poses` | A pose with no animation is the model's rest pose; otherwise give an animation code from your shape and a zero-based frame to freeze at. The first pose is the default. Name them in your lang file as `taxidermy:pose-<code>`. |
| **Hide size & pelts - all optional** |  |
| `hideSize` | `small`, `medium`, `large` or `huge`: the size of pelt the mount takes - make it the size your animal drops. Without a pelt of its own (below), any plain pelt of that size. |
| `hideSizeByType` | Sizes per entity type, wildcards over the entity code, first match wins - for one definition covering animals that drop different sizes (vanilla's deer run from a small pudu to a large elk). |
| `pelt` | For an animal with a pelt of its own. A template filled from the entity's variants, e.g. `yourmod-badger-{type}`. Add `{gender}` if your animal's males and females have pelts of their own: `yourmod-badger-{type}-{gender}` - any of your entity's variant groups works this way, so this one needs a group called gender, as the game's animals have. Each value it can produce needs a mount recipe taking that pelt, matched on the head's **pelt** attribute. See *Three ways in* below. |
| `peltShape` | Gives your animal a pelt cut from its own coat, instead of a plain one. The value is a flat item shape (a rug) in your mod. Faces using `#furside` show your animal's own texture in whatever coat it had; faces using `#fleshside` show leather. Set `textureSizes.furside` to your animal's texture size, for example `[64, 64]`, and lay the fur faces' UVs over the part of your texture they should show. Like **pelt**, it can contain `{type}` for a different design per type. A head only takes one of these pelts in its own coat, the coat the animal had - nothing for you to set. The oiled, salted and soaked stages draw an overlay on the fur. The game and this mod cover many texture sizes; for any other you can ship `textures/item/hide/<oiled\|salted\|soaked>/<width>x<height>.png` in your own mod - without one the stage shows on the leather side only. How the pelt then drops is under *One of this mod's pelt designs* below. |
| `furTexture` | Which of your entity's textures is the fur, when it has several. The first one otherwise. |
| **Head chances & drops - all optional** |  |
| `chance` | 0 to 1. This animal's own chance of giving a head, in place of the built-in half (0.5) - for example 0.1 for a rare trophy. The game's harvest rules still lower it, and the server's AlwaysHeadAndHide switch still overrides it. |
| `chanceByType` | A chance per entity type, wildcards over the entity code, first match wins - like hideSizeByType. It wins over **chance** for the types it matches. This mod uses it for the mobs by tier: `{ "drifter-normal": 0.1, "drifter-deep": 0.2, ..., "drifter-*": 0.5 }`. For a creature that drops no hide in the game, it is also the chance of a hide when the server's hide switches are off. |
| `headSkinningDrops` | What a raw head gives when it is skinned with a knife. You will usually want it. Left out, the head gives only a small raw hide. The list in the example gives bushmeat and fat; this mod's own animals also give the hide and bones, each at a 50% chance - see *What skinning a head gives* below. |
| **Model & behaviour - all optional** |  |
| `scale` | Extra size on top of the entity's own, if your animal comes out too big or too small. |
| `headElements` | Name fragments to look for instead of "head", first match wins. Most models need nothing here. Set it if your model names its head something else, or if another part's name also contains "head" and gets picked first. |
| `keepEyes` | On by default. Keeps the model's eyes on the head, as glass eyes. Set it to false if your animal's eyes show as holes on a hung head. |
| `category` | `"animal"` (the default) or `"mob"`. Which of the server's switches cover your creature: the animal ones, or the mob ones - so a server that turns mob hides off turns them off for your monsters too. Tag anything hostile as a mob, like the drifters. |
| `creativeTab` | The creative tab your heads and mounts are listed in, besides General and Items - this mod's Taxidermy tab otherwise. Any name makes a new tab; title it with the lang key `game:tabname-<your tab>`. |
| **Housekeeping - all optional** |  |
| `enabled` | True by default. Set it to false to switch a definition off without deleting it. |
| `priority` | Which definition wins when two of them match the same entity, highest first. |

### What Skinning A Head Gives

A raw head is skinned with a knife, on the ground. Everything it gives comes from **headSkinningDrops**, a list of items - the hide too. A list that names no hide, or no list at all, still gives the game's small raw hide, as it did before 1.0.3. Each item has:

| `code` | The item, for example `game:bushmeat-raw`. Any item from the game or any mod. Add `"type": "block"` if it is a block. |
|---|---|
| `quantity` | How many, the same for every head. 1 if you leave it out. |
| `quantityBySize` | Or how many by the head's size: `small`, `medium`, `large` and `huge` - the animal's **hideSize**. |
| `chance` | Optional, 0 to 1, rolled first: a miss gives none of that item, a slip of the knife. Always, if you leave it out. This mod's own animals use 0.5 for everything, the hide included. |

- Whole numbers are exact. Anything after the point is a chance: `1.5` is one, plus a 50% chance of a second; `0.25` is a one-in-four chance of one.

- A head that should give the hide and nothing else gets an empty list, `[ ]`.

- This only changes skinning a head - how your animal is harvested stays exactly as your mod has it.

- **An item code that does not exist is skipped without a warning**, so if something is missing, check its spelling.

A head that always gives one bushmeat and sometimes a bone:

```json
"headSkinningDrops": [
  { "code": "game:bushmeat-raw", "quantity": 1 },
  { "code": "game:bone", "quantity": 0.25 }
]
```

### Three Ways In

How each one follows the player's settings:

**A. Heads only** - just the file above

Heads drop on the server's head setting, and the mount takes the game's plain pelt of your **hideSize** through this mod's own recipes, so you write no recipe at all. The own-pelt switches change nothing for your animal: it has no pelt of its own, so it always takes the plain one. A pelt from one of this mod's animals turns into a plain pelt of the same size in the crafting grid, and then fits yours too.

**B. Your own pelt items** - add `pelt`

Add **pelt**, and one mount recipe per value it can produce, taking your pelt. Your pelts drop the way your mod drops them, and the own-pelt switches leave them alone - they only turn off this mod's own pelt designs. **A head whose pelt has no recipe cannot be mounted, and nothing tells the player why, so cover every value.**

**C. One of this mod's pelt designs** - add `pelt` and `peltShape`

Add **pelt** and **peltShape**, and one recipe per value taking `game:hide-pelt-taxidermy-<size>`. Every plain raw hide of the game's that your animal gives becomes its pelt, so it follows the server's hide settings - one per animal out of the box, added even if your animal drops no hide. It only swaps the game's own hides: an animal that drops a hide item from your mod never gets one. With its category's own-pelt switch off, none of this happens and your heads take the plain pelt of their size from this mod's recipes, so your recipes only need to cover the switch on.

### How The Pelt Label Works

**The fox, as this mod does it.** The game already has its own fox pelts (`game:hide-pelt-fox-red`, `game:hide-pelt-fox-arctic`). This mod's fox definition says:

```json
"pelt": "fox-{type}"
```

So a red fox's head is labelled **fox-red** and an arctic fox's **fox-arctic** - the `{type}` is filled in from the animal. Then there is one recipe per label: a head labelled **fox-red** plus `game:hide-pelt-fox-red` makes a red fox mount. This mod never makes or drops the fox pelt - the game does. The label just makes sure the right pelt goes with the right head. Your animal with your own pelt items works exactly the same way, and so do Fauna of the Stone Age's elephants and penguins.

**The wolf, with its sex.** Wolves have this mod's own pelt designs, a male's and a female's, so the wolf definition says:

```json
"pelt": "wolf-{type}-{gender}"
```

A male wolf's head is labelled **wolf-eurasian-male** and a female's **wolf-eurasian-female**, with one recipe for each, so a male's head only takes a male's pelt. The fox has no `{gender}` because the game's fox pelts are the same for both sexes. Any of the animal's variant groups can go in the label the same way. The coat never does: a head only takes one of this mod's pelt designs in its own coat without being told, while your own pelt items are matched by the label alone.

| **No pelt label** | The head takes any plain pelt of its size, the game's own. |
|---|---|
| **A pelt label and a recipe for it** | The head takes only that pelt. A plain pelt will not do. |
| **A pelt label with no recipe** | **The head cannot be mounted at all.** There is no fallback to a plain pelt. |

### Shipping It With Your Mod

**Do not add this mod to your dependencies.** Ship the file unconditionally. The game never reads a config asset nothing asks for, so for anyone who does not have this mod installed your file is inert and silent - no warnings, no errors. A dependency entry would do the opposite and stop your mod loading without it.

**Mount recipes are the one exception:** they name this mod's items, so without it installed they would log errors. Ship them as a JSON patch, at `assets/<yourmod>/patches/<anything>.json`. The `dependsOn` line means the patch only applies when this mod is installed, and is skipped silently when it is not - this is how Taxidermy Patches adds its recipes for Fauna of the Stone Age. One entry per recipe:

```json
[
  {
    "op": "add",
    "path": "/-",
    "file": "taxidermy:recipes/grid/mount.json",
    "dependsOn": [ { "modid": "taxidermy" } ],
    "value": {
      "ingredientPattern": "HP,GS",
      "shapeless": true,
      "width": 2,
      "height": 2,
      "ingredients": {
        "H": { "type": "item", "code": "taxidermy:head-preserved", "attributes": { "pelt": "yourmod-badger-honey-male" } },
        "P": { "type": "item", "code": "yourmod:pelt-badger-honey-male" },
        "G": { "type": "item", "code": "game:drygrass", "quantity": 4 },
        "S": { "type": "item", "code": "taxidermy:needle-threaded", "isTool": true, "toolDurabilityCost": 4 }
      },
      "copyAttributesFrom": "H",
      "output": { "type": "item", "code": "taxidermy:mount", "quantity": 1 }
    }
  }
]
```

- What matters is the head's **pelt** attribute and **copyAttributesFrom**.

- This badger's label has **{gender}** in it, so it needs one recipe per sex: this is the male's, and the female's is the same with **-female**. Without **{gender}** one recipe covers both.

- Dry grass is 2, 4, 6 or 8 for small, medium, large or huge, and the needle's **toolDurabilityCost** is the same number - one stitch per grass.

- Making a mount in the world reads these same recipes - which pelt fits which head, and how much grass - so one patch covers both ways. A recipe that still takes the game's sewing kit keeps working.

- For one of this mod's pelt designs, **P** is `{ "type": "item", "code": "game:hide-pelt-taxidermy-medium", "attributes": { "pelt": "yourmod-badger-honey-male" } }` instead.
