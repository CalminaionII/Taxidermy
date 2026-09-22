using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Taxidermy;

/// <summary>
/// What the client sends when a field in the Adjust window changes. Every packet type needs a
/// ProtoContract, broadcast or not - shared reference sect.8c; singleplayer would never show
/// it missing.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class MountAdjustPacket
{
    public string Pose;
    public float OffX, OffY, OffZ;   // in 1/16ths of a block
    public float RotationDeg;
    public float TiltDeg;
}

/// <summary>
/// The Adjust window for a placed mount: pose, X/Y/Z nudge, rotation. Opened from the wrench's
/// "Adjust mount" tool mode. Live: every change goes to the server as a block entity packet
/// and comes back to every client through the block entity's normal sync. Calm asked for
/// fine tuning because poses shift the animal off centre and some sit at the wrong height
/// (2026-09-21); this is also the pose menu he mentioned on day one.
///
/// Layout with a running y, per shared reference sect.10e; number inputs with IntMode off
/// and an explicit Interval per field, for the same reason.
/// </summary>
/// <summary>Adjustments for a head hanging on a vanilla antler mount, sent over the mod channel.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class HeadAdjustPacket
{
    public int X, Y, Z;
    public float OffX, OffY, OffZ;   // in 1/16ths of a block
    public float RotationDeg;
    public float TiltDeg;
}

public sealed class GuiDialogMount : GuiDialog
{
    public const int PacketId = 4201;

    private readonly string title;
    private readonly PoseDefinition[] poses;   // null: no pose row (a head on a wall)
    private readonly Action<MountAdjustPacket> send;
    private string pose;
    private float offX, offY, offZ, rotDeg, tiltDeg;
    private bool filling;

    public override string ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    /// <summary>A placed mount: pose + position, sent as a block entity packet.</summary>
    public GuiDialogMount(ICoreClientAPI capi, BEMount be, AnimalDefinition def)
        : this(capi, Lang.Get("taxidermy:adjust-title"), def.Poses, be.Data?.GetString("pose") ?? def.Poses[0].Code,
               be.OffX * 16, be.OffY * 16, be.OffZ * 16, -be.Rotation * GameMath.RAD2DEG, be.Tilt * GameMath.RAD2DEG,
               p => capi.Network.SendBlockEntityPacket(be.Pos, PacketId, p))
    { }

    /// <summary>A head on an antler mount: position only, stored on the head, sent over the mod channel.</summary>
    public GuiDialogMount(ICoreClientAPI capi, BlockPos pos, ItemStack head)
        : this(capi, Lang.Get("taxidermy:adjust-head-title"), null, null,
               Specimen.Of(head)?.GetFloat("offx") * 16 ?? 0, Specimen.Of(head)?.GetFloat("offy") * 16 ?? 0,
               Specimen.Of(head)?.GetFloat("offz") * 16 ?? 0, -(Specimen.Of(head)?.GetFloat("rot") ?? 0) * GameMath.RAD2DEG,
               (Specimen.Of(head)?.GetFloat("tilt") ?? 0) * GameMath.RAD2DEG,
               p => capi.Network.GetChannel(TaxidermyModSystem.Channel).SendPacket(new HeadAdjustPacket
                   { X = pos.X, Y = pos.Y, Z = pos.Z, OffX = p.OffX, OffY = p.OffY, OffZ = p.OffZ, RotationDeg = p.RotationDeg, TiltDeg = p.TiltDeg }))
    { }

    private GuiDialogMount(ICoreClientAPI capi, string title, PoseDefinition[] poses, string pose,
        float offX, float offY, float offZ, float rotDeg, float tiltDeg, Action<MountAdjustPacket> send) : base(capi)
    {
        this.title = title;
        this.poses = poses;
        this.pose = pose;
        this.offX = offX; this.offY = offY; this.offZ = offZ; this.rotDeg = rotDeg; this.tiltDeg = tiltDeg;
        this.send = send;
        Compose();
    }

    private const int W = 300, LabelW = 90, FieldW = 200, RowH = 30, LabelH = 22;
    private static ElementBounds Row(int x, int y, int w, int h) => ElementBounds.Fixed(x, y, w, h);

    private void Compose()
    {
        ClearComposers();
        ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        ElementBounds dialog = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.RightMiddle)
            .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

        var c = capi.Gui.CreateCompo("taxidermy-adjust", dialog)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(title, () => TryClose())
            .BeginChildElements(bg);

        int y = 32;
        if (poses != null)
        {
            c.AddStaticText(Lang.Get("taxidermy:adjust-pose"), CairoFont.WhiteDetailText(), Row(0, y + 4, LabelW, LabelH));
            var codes = poses.Select(p => p.Code).ToArray();
            var names = codes.Select(code => Lang.Get("taxidermy:pose-" + code)).ToArray();
            int selected = Math.Max(0, Array.IndexOf(codes, pose));
            c.AddDropDown(codes, names, selected, OnPose, Row(LabelW, y, FieldW, RowH), "pose");
            y += RowH + 8;
        }

        foreach (var (key, label) in new[] { ("x", "taxidermy:adjust-x"), ("y", "taxidermy:adjust-y"), ("z", "taxidermy:adjust-z"), ("rot", "taxidermy:adjust-rot"), ("tilt", "taxidermy:adjust-tilt") })
        {
            c.AddStaticText(Lang.Get(label), CairoFont.WhiteDetailText(), Row(0, y + 4, LabelW, LabelH));
            string k = key;
            c.AddNumberInput(Row(LabelW, y, FieldW, RowH), v => OnNumber(k, v), CairoFont.WhiteDetailText(), key);
            y += RowH + 6;
        }
        y += 4;
        c.AddSmallButton(Lang.Get("taxidermy:adjust-reset"), OnReset, Row(0, y, 120, 26));
        c.AddSmallButton(Lang.Get("taxidermy:adjust-close"), () => TryClose(), Row(W - 120, y, 120, 26));

        SingleComposer = c.EndChildElements().Compose();
        Fill();
    }

    private void Fill()
    {
        filling = true;
        Set("x", offX, 0.5f); Set("y", offY, 0.5f); Set("z", offZ, 0.5f); Set("rot", rotDeg, 5f); Set("tilt", tiltDeg, 5f);
        filling = false;
    }

    private void Set(string key, float value, float interval)
    {
        var el = SingleComposer?.GetNumberInput(key);
        if (el == null) return;
        el.IntMode = false;
        el.Interval = interval;
        el.SetValue(value);
    }

    private void OnPose(string code, bool selected)
    {
        pose = code;
        Send();
    }

    private void OnNumber(string key, string text)
    {
        if (filling) return;
        if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) return;
        switch (key)
        {
            case "x": offX = v; break;
            case "y": offY = v; break;
            case "z": offZ = v; break;
            case "rot": rotDeg = v; break;
            case "tilt": tiltDeg = v; break;
        }
        Send();
    }

    private bool OnReset()
    {
        offX = offY = offZ = tiltDeg = 0;
        Fill();
        Send();
        return true;
    }

    private void Send() => send(new MountAdjustPacket { Pose = pose, OffX = offX, OffY = offY, OffZ = offZ, RotationDeg = rotDeg, TiltDeg = tiltDeg });
}
