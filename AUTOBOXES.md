# Auto Boxes - the guide

# What Auto Boxes does

**1. It measures your block's model and gives it a selection box and a collision box of the right size -
automatically.** You never write a `selectionbox` or `collisionbox` again, and never have to check that
they match the model. Change the model, and the boxes change with it. It works out every part of the model
where the game draws it - turned, scaled, inside its parent - and puts one box round the whole thing. If
your block faces different ways, the box turns with it (by its north / east / south / west versions
automatically; if your own code turns it, see 2C).

**2. If your block is bigger than one block, it makes the whole of it clickable and solid.** The game
normally only lets you click or bump into the part of a block inside its own space. Auto Boxes fixes that
with an invisible **filler** - explained below.

So: **any** block gets correct boxes without writing them, and **big** blocks also get the filler.

---

# When and why the filler is used

## The problem

Every block in Vintage Story lives in one space - one 1 x 1 x 1 cube of the world. A block's model can be
drawn bigger than that (a tall statue, a long bench, a big animal), but the game only **checks** a block
where you look through **its own space**:

- **Clicking:** when you aim at the part that sticks out into the next space, the game looks for a block
  in *that* space, finds air, and ignores it. You can only click the part inside the block's own space.
- **Bumping into it:** the same - the game only checks the block's own space, so you walk straight
  through the part that sticks out.

```
      +-------+
      | top   |   <- this space is air: the top of the statue is drawn here,
      |       |      but you cannot click it or bump into it
      +-------+
      | base  |   <- the statue's own space: only this part works
      |       |
      +-------+
```

## What the filler does

Auto Boxes puts an invisible **filler** block in every **empty** space your block reaches - here, the
space above the statue. Now the game finds a block there, and the filler:

- shows your block's **whole outline**, wherever you aim at it,
- is **solid** for its part of your block,
- passes every **click, break, name and help text** on to your block - aiming at the top of the statue is
  just like aiming at its base,
- is **never seen** - not drawn, not in the creative tabs or the handbook, drops nothing,
- **goes away** when your block is broken or picked up.

A space that already holds something (a wall, water, another block) is left alone - that part of your
block just is not clickable there. Fillers never replace anything a player built.

## What your block gets

| Your block... | Boxes measured from the model | Filler |
|---|---|---|
| fits inside one block | **Yes** - correct selection and collision box, nothing written | not used - none is ever placed (having it in the mod does no harm) |
| sticks out into the next space - taller, longer or wider than one block | **Yes** - one box round the whole model | **Yes** - in every empty space it reaches |
| only pokes out a tiny bit (less than 1/32 of a block - half a pixel) | **Yes** | not used - that small an overhang stays part of its own block's box |

Good to know:

- **One filler file per mod**, however many big blocks it has - they all share it.
- The filler cannot be **built into** - a player cannot place a block in a space your block reaches until
  your block is picked up. That is on purpose: the space belongs to your block.
- Fillers are made when your block is **placed**. A big block placed before you added Auto Boxes gets
  none - break it and place it again.

---

In this guide, replace:

| This | With | Example |
|---|---|---|
| `MyMod` | your mod's namespace - the word after `namespace` at the top of your own `.cs` files | `Bookends` |
| `mymod` | your mod id from `modinfo.json` (lowercase) | `bookends` |
| `BEYourBlock` | the name of your block entity class | `BEBigShelf` |
| `BlockYourBlock` | the name of your block class | `BlockBigShelf` |

The kit (folder **AutoBoxes Kit**):

| File | What it is |
|---|---|
| `AutoBoxes.cs` | the tools: cutting the box into spaces, placing and removing the fillers |
| `AutoBoxesFiller.cs` | the invisible filler block |
| `AutoBoxesMeasure.cs` | measures your block's size from its model |
| `autoboxesfiller.json` | the filler's blocktype |

---

# How the box is measured - and what that means

Auto Boxes finds the **furthest point** of your model in every direction - left, right, front, back, top
and bottom - and makes **one box** between them. Every part counts, where the game draws it (turned,
scaled, inside its parent part).

| Your model | The box |
|---|---|
| a square / cube, or any plain block shape | **exact** - it matches perfectly |
| parts that make a rectangle overall (a crate, a cabinet, a bench seat) | exact to the outside edges |
| an odd shape - an L, a table on thin legs, an animal | covers the furthest points, so it **includes the empty space** between the parts (under a table top, between legs) |
| a part turned at an angle | the box goes round the turned part's corners, so it is a little bigger than the part |

## The types of box

A block has two kinds of box. Auto Boxes makes both from the same measured box:

| Box | What it does | What Auto Boxes gives it |
|---|---|---|
| **Selection box** | the outline you see when you aim at the block, and the area you can click (pick up, use, break) | the whole measured box - shown in full from **every** space it reaches, so the outline never changes as you aim along it |
| **Collision box** | the solid part - what you, animals and dropped items bump into and stand on | the measured box, **space by space** (each space is solid for its own part) - or none, for walk-through (see below) |

And what it can do with them:

| Can it... | |
|---|---|
| give any block correct boxes with none written | **Yes** - always |
| reach past the block (taller, longer, wider) | **Yes** - up to wherever the model goes, through fillers |
| turn with the block | **Yes** - by its north / east / south / west versions automatically; turned by your own code with **2C** |
| be solid or walk-through | **Yes** - solid by default; walk-through with one line (below) |
| follow the shape part by part (gaps you can walk through) | **No** - it is always **one** box round the whole model |
| move with an animation (a door swinging open) | **No** - the box is measured from the model standing still |

**What that means for the solid part.** It is one box, so an odd shape is solid in its empty spaces too -
you cannot walk under a table or between an animal's legs. For the outline that is fine (it shows the whole
thing); for walking into it, it is a little rougher than the model. Make the block walk-through instead if
that suits it better: in your block entity, change `AutoCollisionAt` to
`public Cuboidf[] AutoCollisionAt(BlockPos cell) => [];` - then nothing outside the block's own space is
solid, and inside it the block's own collision from its JSON is used. (With no collision box in the JSON the
game makes the block's own space a full solid cube - add `"collisionbox": null` to the JSON for none at all.)

**Left out of the box:**

- parts with no size (the pivot points Model Creator makes),
- parts listed in the shape's `ignoreElements` in your JSON - handy for something that should not count,
  like a thin rope or a light glow.

---

# PART 1 - once per mod

## 1. Copy the kit into your mod

- `AutoBoxes.cs`, `AutoBoxesFiller.cs` and `AutoBoxesMeasure.cs` -> your mod's code folder (where your
  other `.cs` files are).
- `autoboxesfiller.json` -> your mod's `assets/mymod/blocktypes/` folder.

## 2. Set the namespace

Each of the three `.cs` files has this line near the top:

```csharp
namespace MyMod;   // CHANGE: the namespace your own code files use (the word after "namespace" at their top)
```

In **all three**, change `MyMod` to your mod's namespace. For example, if your own files say
`namespace Bookends;`, make it:

```csharp
namespace Bookends;
```

## 3. Set your mod id

In `AutoBoxes.cs`, find this line:

```csharp
    public const string FillerCode = "mymod:autoboxesfiller";   // CHANGE: your mod id (lowercase) before the colon
```

Change `mymod` to **your mod id** - exactly the `"modid"` in your `modinfo.json`, not a short name. For
example Desk Picture Frames' `modinfo.json` says `"modid": "deskpictureframe"`, so its line is:

```csharp
    public const string FillerCode = "deskpictureframe:autoboxesfiller";   // CHANGE: your mod id (lowercase) before the colon
```

(Not `"dpf:..."` - the game files every block under its mod's real id. Get this wrong and nothing breaks,
but big blocks silently get no fillers.)

## 4. Register the filler

Open your ModSystem file (the class that says `: ModSystem`). Find:

```csharp
    public override void Start(ICoreAPI api)
    {
```

Paste under it:

```csharp
        api.RegisterBlockClass("MyModAutoBoxesFiller", typeof(BlockAutoBoxesFiller));
        api.RegisterBlockEntityClass("MyModAutoBoxesFiller", typeof(BEAutoBoxesFiller));
```

and change `MyMod` **inside the quotes** to any name of your own - a short version of your mod's name is
good (e.g. `"DPFAutoBoxesFiller"`). No other mod may use the same name. Keep `BlockAutoBoxesFiller` and
`BEAutoBoxesFiller` in `typeof(...)` exactly as they are - those are the filler's own classes, **not** your
block's.

Your mod has no `Start` method? Paste this whole method inside your ModSystem class instead:

```csharp
    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockClass("MyModAutoBoxesFiller", typeof(BlockAutoBoxesFiller));
        api.RegisterBlockEntityClass("MyModAutoBoxesFiller", typeof(BEAutoBoxesFiller));
    }
```

## 5. Name the filler in its JSON

Open `autoboxesfiller.json` (the one you copied into `assets/mymod/blocktypes/` in step 1 - it **must** be
there, or big blocks get no fillers). Change these two lines to the **same name** you used in step 4 -
the filler's name, **not** your block's class names:

```json
  "class": "MyModAutoBoxesFiller",
  "entityClass": "MyModAutoBoxesFiller",
```

**The names, all in one place:**

| Name | Where it goes | Your choice? |
|---|---|---|
| `MyModAutoBoxesFiller` | in quotes in step 4's two lines, and `"class"` / `"entityClass"` in the filler JSON (step 5) | **Yes** - any name, the same in all four spots, not used by another mod |
| `autoboxesfiller` | `"code"` in the filler JSON, and after the colon in `FillerCode` (step 3) | **Yes** - any name, the same in both spots |
| `mymod` | before the colon in `FillerCode` (step 3) | **No** - your mod id, exactly as in `modinfo.json` |

**Part 1 is done.** You never do it again for this mod.

---

# PART 2 - your blocks

Does your block already have its own `BE...` class and `Block...` class?

- **No** - it is only a JSON file -> do **2A** (only once per mod - after that, other plain blocks just
  need two lines in their JSON, see **Your block's JSON**).
- **Yes** -> do **2B** (and **2C** if your block entity turns the model itself - 2C says how to tell).

---

## 2A - A block with no code yet

### 2A.1 Make the file

Make a new file in your code folder called `BEYourBlock.cs` and paste all of this in. Then replace
`BEYourBlock`, `BlockYourBlock` and `MyMod` as in the table at the top.

```csharp
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace MyMod;

public class BEYourBlock : BlockEntity, IAutoBoxesOwner
{
    Cuboidf autoBox;
    Dictionary<(int x, int y, int z), Cuboidf> autoPieces = new();
    List<(int x, int y, int z)> autoHeld = new();

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        autoBox = AutoBoxesMeasure.FromModel(api, Block);
        autoPieces = AutoBoxes.Cut(autoBox);
    }

    public override void OnBlockPlaced(ItemStack byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        if (Api.Side == EnumAppSide.Server) autoHeld = AutoBoxes.Sync(Api.World, Pos, autoPieces.Keys, autoHeld);
    }

    public override void OnBlockRemoved()
    {
        if (Api.Side == EnumAppSide.Server) AutoBoxes.ReleaseAll(Api.World, Pos, autoHeld);
        base.OnBlockRemoved();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetString("autoboxesHeld", AutoBoxes.Write(autoHeld));
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        autoHeld = AutoBoxes.Read(tree.GetString("autoboxesHeld"));
    }

    public Cuboidf[] AutoBoxesAt(BlockPos cell)
    {
        var o = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
        return autoPieces.ContainsKey(o) ? [AutoBoxes.Whole(autoBox, o)] : [];
    }

    public Cuboidf[] AutoCollisionAt(BlockPos cell) =>
        AutoBoxes.PieceAt(autoBox, (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z));
}

public class BlockYourBlock : Block
{
    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var auto = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoBoxesAt(pos);
        if (auto != null && auto.Length > 0) return auto;
        return base.GetSelectionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var auto = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoCollisionAt(pos);
        if (auto != null && auto.Length > 0) return auto;
        return base.GetCollisionBoxes(blockAccessor, pos);
    }
}
```

### 2A.2 Register it

In your ModSystem's `Start` method, under the two lines from step 4, paste:

```csharp
        api.RegisterBlockClass("MyModYourBlock", typeof(BlockYourBlock));
        api.RegisterBlockEntityClass("MyModYourBlock", typeof(BEYourBlock));
```

(Again, change `MyMod` inside the quotes to your mod's name, and `YourBlock` to your block.)

### 2A.3 Point your block's JSON at it

Open **your block's** JSON (in `assets/mymod/blocktypes/`). Add these two lines near the top, under
`"code"`:

```json
  "class": "MyModYourBlock",
  "entityClass": "MyModYourBlock",
```

If it already has a `"class"` line, replace it with these.

**Done** - go to **Test it** at the bottom.

---

## 2B - A block that already has its own code

You add pieces to your existing `BEYourBlock` and `BlockYourBlock`. Your own code stays as it is.

Each step says what to **find**, then what to **paste**. If a step says "don't have one?", paste the
whole method instead - put it just above the **last `}`** of the class.

### In your block entity file (`BEYourBlock`)

**B1 - The top of the file.** Find the `using` lines at the top. Paste these under them (skip any that
are already there):

```csharp
using System.Collections.Generic;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
```

**B2 - The class line.** Find your class line. It looks like:

```csharp
public class BEYourBlock : BlockEntity
```

Add `, IAutoBoxesOwner` to the end of it:

```csharp
public class BEYourBlock : BlockEntity, IAutoBoxesOwner
```

(If it already has more after `BlockEntity`, put `, IAutoBoxesOwner` at the very end, before any `{`.)

**B3 - Three lines at the start of the class.** Find the `{` straight after your class line. Paste
under it:

```csharp
    Cuboidf autoBox;
    Dictionary<(int x, int y, int z), Cuboidf> autoPieces = new();
    List<(int x, int y, int z)> autoHeld = new();
```

**B4 - Initialize.** Find `base.Initialize(api);` (inside `public override void Initialize(ICoreAPI api)`).
Paste under it:

```csharp
        autoBox = AutoBoxesMeasure.FromModel(api, Block);
        autoPieces = AutoBoxes.Cut(autoBox);
```

Don't have an `Initialize`? Paste this whole method:

```csharp
    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        autoBox = AutoBoxesMeasure.FromModel(api, Block);
        autoPieces = AutoBoxes.Cut(autoBox);
    }
```

**B5 - OnBlockPlaced.** In your **block entity** file, find `base.OnBlockPlaced(byItemStack);` - it is inside
`public override void OnBlockPlaced(ItemStack byItemStack = null)`. (Your **block** file may have an
`OnBlockPlaced` too, with `world` and `blockPos` in its brackets - that is a different one, leave it alone.)
Paste under it:

```csharp
        if (Api.Side == EnumAppSide.Server) autoHeld = AutoBoxes.Sync(Api.World, Pos, autoPieces.Keys, autoHeld);
```

Don't have an `OnBlockPlaced`? Paste this whole method:

```csharp
    public override void OnBlockPlaced(ItemStack byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        if (Api.Side == EnumAppSide.Server) autoHeld = AutoBoxes.Sync(Api.World, Pos, autoPieces.Keys, autoHeld);
    }
```

**B6 - OnBlockRemoved.** In your **block entity** file, find `base.OnBlockRemoved();` - it is inside
`public override void OnBlockRemoved()` (empty brackets). Paste **ABOVE** it (not under):

```csharp
        if (Api.Side == EnumAppSide.Server) AutoBoxes.ReleaseAll(Api.World, Pos, autoHeld);
```

Don't have an `OnBlockRemoved`? Paste this whole method:

```csharp
    public override void OnBlockRemoved()
    {
        if (Api.Side == EnumAppSide.Server) AutoBoxes.ReleaseAll(Api.World, Pos, autoHeld);
        base.OnBlockRemoved();
    }
```

**B7 - ToTreeAttributes.** Find `base.ToTreeAttributes(tree);`. Paste under it:

```csharp
        tree.SetString("autoboxesHeld", AutoBoxes.Write(autoHeld));
```

Don't have a `ToTreeAttributes`? Paste this whole method:

```csharp
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetString("autoboxesHeld", AutoBoxes.Write(autoHeld));
    }
```

**B8 - FromTreeAttributes.** Find the `base.FromTreeAttributes(...)` line. Paste under it:

```csharp
        autoHeld = AutoBoxes.Read(tree.GetString("autoboxesHeld"));
```

Don't have a `FromTreeAttributes`? Paste this whole method:

```csharp
    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        autoHeld = AutoBoxes.Read(tree.GetString("autoboxesHeld"));
    }
```

**B9 - Two new methods.** Find the **last `}`** of your block entity class. Paste ABOVE it:

```csharp
    public Cuboidf[] AutoBoxesAt(BlockPos cell)
    {
        var o = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
        return autoPieces.ContainsKey(o) ? [AutoBoxes.Whole(autoBox, o)] : [];
    }

    public Cuboidf[] AutoCollisionAt(BlockPos cell) =>
        AutoBoxes.PieceAt(autoBox, (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z));
```

### In your block file (`BlockYourBlock`)

**B10 - The top of the file.** Paste under the `using` lines (skip it if it is already there):

```csharp
using Vintagestory.API.MathTools;
```

**B11 - The outline.** Does your block class already have `GetSelectionBoxes`?

- **Yes:** find its first `{`. Paste under it:

```csharp
        var autoSel = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoBoxesAt(pos);
        if (autoSel != null && autoSel.Length > 0) return autoSel;
```

- **No:** find the **last `}`** of your block class and paste ABOVE it:

```csharp
    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var autoSel = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoBoxesAt(pos);
        if (autoSel != null && autoSel.Length > 0) return autoSel;
        return base.GetSelectionBoxes(blockAccessor, pos);
    }
```

**B12 - The solid part.** Does your block class already have `GetCollisionBoxes`?

- **Yes:** find its first `{`. Paste under it:

```csharp
        var autoCol = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoCollisionAt(pos);
        if (autoCol != null && autoCol.Length > 0) return autoCol;
```

- **No:** find the **last `}`** of your block class and paste ABOVE it:

```csharp
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var autoCol = (blockAccessor.GetBlockEntity(pos) as IAutoBoxesOwner)?.AutoCollisionAt(pos);
        if (autoCol != null && autoCol.Length > 0) return autoCol;
        return base.GetCollisionBoxes(blockAccessor, pos);
    }
```

(If your existing method is written on one line with `=>` instead of `{ }`, use the "No" version and
delete your old one.)

**Different words in the brackets?** The pasted lines use the names `blockAccessor` and `pos`. If your
existing method's first line uses other names - say `GetSelectionBoxes(IBlockAccessor ba, BlockPos position)` -
change `blockAccessor` and `pos` in the lines you pasted to match. The same goes for `api` in B4 and `tree` in
B7 and B8.

**Your block's JSON needs nothing** - it already points at your classes.

**Done** - unless your block entity turns its model itself: then do **2C**. Otherwise go to **Test it**.

---

## 2C - Only if your block entity turns the model itself

**Do you need this?** Look in your block entity for an angle that turns the model - usually a line like:

```csharp
blockMesh = blockMesh.Rotate(new Vec3f(0.5f, 0.0f, 0.5f), 0f, MeshAngleRad, 0f);
```

If your block is turned that way (it faces you when placed, but has no north / east / south / west
versions in its JSON), the box has to be turned by the same angle - otherwise it stays facing one way.
Do 2B first, then these four steps. In them, `MeshAngleRad` is the name of your angle - use yours.

**C1 - Measure with the turn.** In your block entity, find the two lines you pasted in B4:

```csharp
        autoBox = AutoBoxesMeasure.FromModel(api, Block);
        autoPieces = AutoBoxes.Cut(autoBox);
```

Replace both with this one line:

```csharp
        AutoBoxesMeasureTurned();
```

**C2 - Two new methods.** Find the **last `}`** of your block entity class. Paste ABOVE it:

```csharp
    void AutoBoxesMeasureTurned()
    {
        autoBox = AutoBoxesMeasure.FromModel(Api, Block, false)?.RotatedCopyRad(0f, MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5));
        autoPieces = AutoBoxes.Cut(autoBox);
    }

    public void AutoBoxesAngleChanged()
    {
        AutoBoxesMeasureTurned();
        if (Api.Side == EnumAppSide.Server) autoHeld = AutoBoxes.Sync(Api.World, Pos, autoPieces.Keys, autoHeld);
    }
```

**What the first line does - two things, one after the other:**

```csharp
autoBox = AutoBoxesMeasure.FromModel(Api, Block, false)       // 1. measure the model - false = WITHOUT the cardinal turn
              ?.RotatedCopyRad(0f, MeshAngleRad, 0f, ...);    // 2. THEN turn it by your angle
```

| Part | What it does | Does `false` change it? |
|---|---|---|
| `FromModel(Api, Block, false)` | measures the model; `false` = leave out the north / east / south / west turn from the JSON | **yes - this is the only thing `false` changes** |
| `.RotatedCopyRad(0f, MeshAngleRad, ...)` | turns the box by your angle | **no** - this always happens |

So `false` does **not** stop your angle working - the box is still turned by it. It only stops the box being
turned **twice** when your block also has north / east / south / west versions (like Desk Picture Frames'
wall frames): once by the versions, and again by your angle. Blocks without those versions are not affected
either way, so one block entity can serve both kinds. (Only if your block's drawing code also applies the
versions' turn itself - the box then comes out wrong on every side but north - change `false` to `true`.)

**C3 - When the angle is loaded.** In your block entity's `FromTreeAttributes`, find the line that reads
your angle, for example:

```csharp
        MeshAngleRad = tree.GetFloat("meshAngleRad", 0f);
```

Paste under it:

```csharp
        if (Api != null) AutoBoxesMeasureTurned();
```

**C4 - When the angle is set.** Find where your code sets the angle when the block is placed - usually in
your **block** file, inside `TryPlaceBlock` or `OnBlockPlaced`, a line like:

```csharp
                be.MeshAngleRad = angle;
```

Paste under it (use the name your code uses for the block entity there - here `be`):

```csharp
                be.AutoBoxesAngleChanged();
```

If something else can turn the block later (a wrench, a menu), paste the same line after it sets the
angle there too.

**Done** - go to **Test it**.

---

# Your block's JSON

## The two lines that connect it to the code

A block's JSON says which code runs it with two lines:

```json
  "class": "MyModYourBlock",
  "entityClass": "MyModYourBlock",
```

- `"class"` = the name in quotes in your `api.RegisterBlockClass(...)` line.
- `"entityClass"` = the name in quotes in your `api.RegisterBlockEntityClass(...)` line.

They must be spelled **exactly** the same (capitals too). Both are needed: the block class shows the boxes,
the block entity measures them and looks after the fillers.

- **2A** (new code): you add these two lines (step 2A.3).
  **Several blocks can use the same two names.** The 2A code is not tied to one block - it measures
  whichever block it is on. So do 2A **once**, and every other plain block in your mod only needs these
  two lines. The statue and the bench below use the same ones.
- **2B** (existing code): your JSON already has them - nothing to do.

## Old box lines - optional

If your block's JSON already has any of these, you **can leave them** - it works either way:

```json
  "selectionbox": { ... },
  "selectionboxes": [ ... ],
  "collisionbox": { ... },
  "collisionboxes": [ ... ],
```

Auto Boxes is asked first; the written boxes are only used if a block has no auto boxes. Deleting them is
just tidying, so nobody later thinks they still do something.

## A whole example

A plain block with no code of its own, after 2A - the statue, 2.5 blocks tall:

```json
{
  "code": "statue",
  "class": "MyModYourBlock",
  "entityClass": "MyModYourBlock",
  "shape": { "base": "mymod:block/statue" },
  "textures": { "all": { "base": "game:block/stone/rock/granite1" } },
  "blockmaterial": "Stone",
  "resistance": 1.5,
  "sidesolid": { "all": false },
  "sideopaque": { "all": false },
  "creativeinventory": { "general": ["*"] }
}
```

No box anywhere - it is measured from `mymod:block/statue`. `sidesolid` / `sideopaque` false, because a
statue does not fill its whole block (otherwise the blocks next to it would hide their faces).

## What the measuring follows from your JSON - by itself

| In your JSON | What happens to the box |
|---|---|
| `"shape": { "base": "..." }` | the box is measured from this model |
| `"shapeByType": { ... }` - a different model per variant | each variant is measured from **its own** model |
| `"rotateY"` / `"rotateYByType"` in the shape (also `rotateX`, `rotateZ`) | the box is **turned** the same way - e.g. a block with north / east / south / west variants |
| `"ignoreElements": [ "..." ]` in the shape | those parts are left out of the box as well |

Example - a bench that faces the way you place it, using the game's `HorizontalOrientable`:

```json
{
  "code": "bench",
  "class": "MyModYourBlock",
  "entityClass": "MyModYourBlock",
  "behaviors": [ { "name": "HorizontalOrientable" } ],
  "variantgroups": [ { "code": "side", "loadFromProperties": "abstract/horizontalorientation" } ],
  "shapeByType": {
    "*-north": { "base": "mymod:block/bench", "rotateY": 0 },
    "*-east":  { "base": "mymod:block/bench", "rotateY": 270 },
    "*-south": { "base": "mymod:block/bench", "rotateY": 180 },
    "*-west":  { "base": "mymod:block/bench", "rotateY": 90 }
  },
  "textures": { "all": { "base": "game:block/wood/planks/oak1" } },
  "sidesolid": { "all": false },
  "sideopaque": { "all": false },
  "creativeinventory": { "general": ["*-north"] }
}
```

Every facing gets its box turned to match - nothing to write per facing.

## What it does NOT follow

| In your JSON | So... |
|---|---|
| `"offsetX"` / `"offsetY"` / `"offsetZ"` in the shape | the box does not move with it - move the model in the model file instead |
| `"scale"` in the shape | the box is not scaled - scale the model in the model file instead |
| `"selectiveElements"` | ignored - the whole model is measured (use `ignoreElements` to leave parts out) |
| `"alternates"` (random looks) | only the main `base` model is measured |
| `"overlays"` | not counted |
| a model turned by your **code** (an angle in the block entity) | see **2C** |

---

# Test it

1. Build your mod and start the game.
2. Place your big block with **empty space** around it.
3. Aim at the part outside its own block: the **whole** outline shows, and right-click / break work there.
4. Walk into it: it is solid.
5. Break it: nothing invisible is left behind.

A block placed **before** you added Auto Boxes gets no fillers - break it and place it again.

# If something is wrong

| You see | Check |
|---|---|
| Build error: `AutoBoxes` / `IAutoBoxesOwner` / `AutoBoxesMeasure` not found | step 1 (the three files are in your code folder) and step 2 (their `namespace` line is the SAME as the one at the top of your own file) |
| Build error: `Dictionary` / `List` / `Cuboidf` not found | B1 / B10 - the `using` lines at the top of your file |
| Only its own block is clickable, nothing outside | step 3 (`FillerCode` has your mod id EXACTLY as in `modinfo.json`), the filler JSON is in `assets/<your mod id>/blocktypes/` (step 1), and its names match step 4 (step 5) |
| Game log: block class / entity class not found | the names in quotes in step 4 / 2A.2 match the JSON names |
| Game log: `Could not measure` | the block's model path in its JSON (`shape` -> `base`) |
| Build error: `No overload for method 'FromModel' takes 3 arguments` | your `AutoBoxesMeasure.cs` is an **older copy** from before the kit had the `false` option - see **Updating a mod that already has the kit** |
| The box does not turn with the block | **2C** - your block entity turns its model itself |
| One part not clickable | something is already in that space (wall, water, another block) - fillers only go into empty air |

---

# Updating a mod that already has the kit

The kit files in your mod are **your own copies**. When the kit in the **AutoBoxes Kit** folder is improved,
the copies already in your mods do **not** change with it - you update them yourself:

1. Copy the new file from the kit over the old one in your mod.
2. Set its `namespace` line again (Part 1, step 2) - and for `AutoBoxes.cs`, the `FillerCode` line too
   (Part 1, step 3). The copy comes with `MyMod` / `mymod` in them.

Nothing else in your mod has to change unless the guide's steps changed too.

## What has changed

| Date | File | Change | If your copy is older |
|---|---|---|---|
| 2026-10-06 | `AutoBoxesMeasure.cs` | `FromModel` can leave out the north / east / south / west turn: `FromModel(api, block, false)` - needed by **2C** for blocks that also have those versions | copy the new file over (steps above), **or** change two lines yourself - see below |

**The two-line change** in your `AutoBoxesMeasure.cs`, if you would rather not copy the file:

Find:

```csharp
    public static Cuboidf FromModel(ICoreAPI api, Block block)
```

Change to:

```csharp
    public static Cuboidf FromModel(ICoreAPI api, Block block, bool withShapeTurn = true)
```

Then find:

```csharp
        if (cs.rotateX == 0 && cs.rotateY == 0 && cs.rotateZ == 0) return raw.Clone();
```

Change to:

```csharp
        if (!withShapeTurn || (cs.rotateX == 0 && cs.rotateY == 0 && cs.rotateZ == 0)) return raw.Clone();
```

`withShapeTurn` is an on/off switch that is **on** unless you say `false`, so every other use works exactly
as before.

