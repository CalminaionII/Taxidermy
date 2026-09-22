using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Taxidermy;

/// <summary>
/// The finished mount as an item: right-click a block to place <c>taxidermy:mount</c> carrying
/// the same specimen data; picking the block up gives this back. Renders as the animal itself,
/// like vanilla's taxidermied fish, fitted into a slot.
/// </summary>
public sealed class ItemMount : Item
{
    public override string GetHeldItemName(ItemStack itemStack)
    {
        var data = Specimen.Of(itemStack);
        return data == null ? base.GetHeldItemName(itemStack) : Lang.Get("taxidermy:item-mount-of", Specimen.AnimalName(data));
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        var data = Specimen.Of(inSlot.Itemstack);
        if (data == null)
        {
            dsc.AppendLine(Lang.Get("taxidermy:specimen-empty"));
            return;
        }
        dsc.AppendLine(Lang.Get("taxidermy:mount-item-help"));
        var attachments = Specimen.Attachments(data);
        if (attachments.Length > 0)
            dsc.AppendLine(Lang.Get("taxidermy:specimen-with", string.Join(", ", attachments.Select(c => AttachmentName(world, c)))));
    }

    internal static string AttachmentName(IWorldAccessor world, string code)
    {
        var item = world.GetItem(new AssetLocation(code));
        return item == null ? code : item.GetHeldItemName(new ItemStack(item));
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (blockSel == null || Specimen.Of(slot.Itemstack) == null)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            return;
        }
        handling = EnumHandHandling.PreventDefault;
        // Placed on the server only; the block change syncs down. The client just claims the
        // interaction so nothing else runs.
        if (api.Side != EnumAppSide.Server || !firstEvent) return;

        var player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return;
        var world = api.World;
        var mount = world.GetBlock(new AssetLocation(Specimen.MountBlock));
        if (mount == null) return;

        var sel = blockSel.Clone();
        if (!world.BlockAccessor.GetBlock(sel.Position).IsReplacableBy(mount))
        {
            sel.Position = sel.Position.AddCopy(sel.Face);
            sel.DidOffset = true;
        }
        if (!world.Claims.TryAccess(player, sel.Position, EnumBlockAccessFlags.BuildOrBreak)) return;

        string failureCode = "";
        if (!mount.TryPlaceBlock(world, player, slot.Itemstack, sel, ref failureCode))
        {
            if (failureCode != "__ignore__") (api as ICoreServerAPI)?.SendIngameError(player as IServerPlayer, failureCode, Lang.Get("placefailure-" + failureCode));
            return;
        }
        // Face the player, snapped to 45 degrees. An entity looks at something with
        // yaw = atan2(dx, dz) (vsessentialsmod AiTaskLookAtEntity), and EntityShapeRenderer
        // then turns every model by yaw + 90 degrees - so the mesh needs both. The first
        // build used the player's own yaw and every mount faced west.
        if (world.BlockAccessor.GetBlockEntity(sel.Position) is BEMount be)
        {
            double dx = byEntity.Pos.X - (sel.Position.X + 0.5), dz = byEntity.Pos.Z - (sel.Position.Z + 0.5);
            float yaw = (float)Math.Atan2(dx, dz) + GameMath.PIHALF;
            be.SetRotation(MathF.Round(yaw / (GameMath.PI / 4)) * (GameMath.PI / 4));
        }
        // Server-only path: no dualCallByPlayer, or the placer is the one person who hears nothing (shared reference sect.10c).
        world.PlaySoundAt(new AssetLocation("game:sounds/block/leather"), sel.Position.X + 0.5, sel.Position.Y + 0.5, sel.Position.Z + 0.5, null);
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }
    }

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        var data = Specimen.Of(itemstack);
        var def = data == null ? null : capi.ModLoader.GetModSystem<TaxidermyModSystem>().Find(data);
        if (def != null)
        {
            var meshRef = MountMesher.GetItemMesh(capi, data, def);
            if (meshRef != null) renderinfo.ModelRef = meshRef;
        }
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
    }

    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot) =>
        [new WorldInteraction { ActionLangCode = "taxidermy:heldhelp-place", MouseButton = EnumMouseButton.Right }];
}
