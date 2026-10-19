using System.Numerics;
using Hexa.NET.ImGui;

namespace Candela.App.Ui;

/// <summary>
/// Vector illustrations of the connectors of Candela.Electrical's catalogue (<c>ConnectorInfo.IllustrationKey</c>):
/// the mating face of each connector as seen when plugging in — XLR 3/4/5-pin, DIN, RJ45 and etherCON, powerCON
/// (blue power in, grey power out), powerCON TRUE1 / TRUE1 TOP, Schuko, IEC C14/C20, CEE 16/32 A (blue, 3-pin) and
/// 16/32/63 A three-phase (red, 5-pin), Socapex 19-pin, Harting Han 16E, BNC, USB, DC jack, E27, track adapter,
/// hybrid power/data connectors, fixed leads, terminal blocks, loose ends and batteries. Drawn into a draw list with
/// a few primitives (no textures, no allocation), so they stay sharp at every UI scale.
/// </summary>
internal static class ConnectorIcons
{
    private static readonly uint Body = Theme.U32(Theme.Rgb(0x2B2E35));
    private static readonly uint BodyLight = Theme.U32(Theme.Rgb(0x3A3E47));
    private static readonly uint Edge = Theme.U32(Theme.Rgb(0x9AA0AA));
    private static readonly uint Metal = Theme.U32(Theme.Rgb(0xC9CDD3));
    private static readonly uint Gold = Theme.U32(Theme.Rgb(0xD9B44A));
    private static readonly uint Hole = Theme.U32(Theme.Rgb(0x111214));
    private static readonly uint PowerconBlue = Theme.U32(Theme.Rgb(0x2D6CDF));
    private static readonly uint PowerconGrey = Theme.U32(Theme.Rgb(0xB8BCC4));
    private static readonly uint True1Yellow = Theme.U32(Theme.Rgb(0xF5C400));
    private static readonly uint CeeBlue = Theme.U32(Theme.Rgb(0x2F6FD1));
    private static readonly uint CeeRed = Theme.U32(Theme.Rgb(0xD23B3B));
    private static readonly uint Copper = Theme.U32(Theme.Rgb(0xC77B3A));
    private static readonly uint Green = Theme.U32(Theme.Rgb(0x3FA34D));
    private static readonly uint[] WireColors =
    {
        Theme.U32(Theme.Rgb(0x8B5A2B)), Theme.U32(Theme.Rgb(0x2D6CDF)), Theme.U32(Theme.Rgb(0x6DBE45)),
    };

    /// <summary>Every illustration key that has its own drawing (the rest get the generic "unknown connector").</summary>
    public static readonly string[] Keys =
    {
        "connector-xlr3", "connector-xlr4", "connector-xlr5", "connector-xlr", "connector-din3", "connector-din5",
        "connector-rj45", "connector-ethercon", "connector-powercon", "connector-powercon-blue", "connector-powercon-grey",
        "connector-true1", "connector-true1-top", "connector-schuko", "connector-iec-c14", "connector-iec-c20",
        "connector-cee16", "connector-cee32", "connector-cee16-3p", "connector-cee32-3p", "connector-cee63-3p",
        "connector-socapex", "connector-harting16", "connector-bnc", "connector-usb", "connector-dc-jack", "connector-e27",
        "connector-track-adapter", "connector-martin-pde", "connector-martin-dce", "connector-martin-8pin",
        "connector-hybrid-4pin", "connector-pwb-cab", "connector-fixed-cable", "connector-terminal-block",
        "connector-loose-end", "connector-ip-moulded", "connector-battery",
    };

    /// <summary>True when <paramref name="key"/> has its own drawing.</summary>
    public static bool HasDrawing(string? key) => key is not null && Array.IndexOf(Keys, key) >= 0;

    /// <summary>Draws the connector <paramref name="key"/> into the square at <paramref name="min"/> of side <paramref name="size"/> pixels.</summary>
    public static void Draw(ImDrawListPtr dl, Vector2 min, float size, string? key)
    {
        var c = min + new Vector2(size * 0.5f);
        float r = size * 0.46f;
        float line = MathF.Max(1f, size / 28f);
        switch (key)
        {
            case "connector-xlr3":
                Xlr(dl, c, r, line, 3);
                break;
            case "connector-xlr4":
                Xlr(dl, c, r, line, 4);
                break;
            case "connector-xlr5":
                Xlr(dl, c, r, line, 5);
                break;
            case "connector-xlr":
                Xlr(dl, c, r, line, 0);
                break;
            case "connector-din3":
                Din(dl, c, r, line, 3);
                break;
            case "connector-din5":
                Din(dl, c, r, line, 5);
                break;
            case "connector-rj45":
                Rj45(dl, c, r * 0.95f, line);
                break;
            case "connector-ethercon":
                DSizeFlange(dl, c, r, line);
                Rj45(dl, c, r * 0.55f, line);
                break;
            case "connector-powercon":
                Powercon(dl, c, r, line, PowerconBlue, PowerconGrey);
                break;
            case "connector-powercon-blue":
                Powercon(dl, c, r, line, PowerconBlue, PowerconBlue);
                break;
            case "connector-powercon-grey":
                Powercon(dl, c, r, line, PowerconGrey, PowerconGrey);
                break;
            case "connector-true1":
                True1(dl, c, r, line, top: false);
                break;
            case "connector-true1-top":
                True1(dl, c, r, line, top: true);
                break;
            case "connector-schuko":
                Schuko(dl, c, r, line);
                break;
            case "connector-iec-c14":
                IecC14(dl, c, r, line);
                break;
            case "connector-iec-c20":
                IecC20(dl, c, r, line);
                break;
            case "connector-cee16":
            case "connector-cee32":
                Cee(dl, c, r, line, CeeBlue, 3);
                break;
            case "connector-cee16-3p":
            case "connector-cee32-3p":
            case "connector-cee63-3p":
                Cee(dl, c, r, line, CeeRed, 5);
                break;
            case "connector-socapex":
                Socapex(dl, c, r, line);
                break;
            case "connector-harting16":
                Harting(dl, c, r, line);
                break;
            case "connector-bnc":
                Bnc(dl, c, r, line);
                break;
            case "connector-usb":
                Usb(dl, c, r, line);
                break;
            case "connector-dc-jack":
                DcJack(dl, c, r, line);
                break;
            case "connector-e27":
                E27(dl, c, r, line);
                break;
            case "connector-track-adapter":
                Track(dl, c, r, line);
                break;
            case "connector-martin-pde":
            case "connector-martin-dce":
            case "connector-martin-8pin":
            case "connector-hybrid-4pin":
            case "connector-pwb-cab":
                Hybrid(dl, c, r, line, key switch
                {
                    "connector-martin-8pin" => 8,
                    "connector-hybrid-4pin" => 4,
                    "connector-pwb-cab" => 4,
                    "connector-martin-dce" => 6,
                    _ => 7,
                });
                break;
            case "connector-fixed-cable":
                FixedCable(dl, c, r, line);
                break;
            case "connector-terminal-block":
                TerminalBlock(dl, c, r, line);
                break;
            case "connector-loose-end":
                LooseEnd(dl, c, r, line);
                break;
            case "connector-ip-moulded":
                IpMoulded(dl, c, r, line);
                break;
            case "connector-battery":
                Battery(dl, c, r, line);
                break;
            default:
                Unknown(dl, c, r, line);
                break;
        }
    }

    private static Vector2 Polar(Vector2 c, float radius, float degrees)
    {
        float a = degrees * MathF.PI / 180f;
        return c + new Vector2(MathF.Sin(a), -MathF.Cos(a)) * radius;
    }

    private static void Shell(ImDrawListPtr dl, Vector2 c, float r, float line, uint fill)
    {
        dl.AddCircleFilled(c, r, fill, 48);
        dl.AddCircle(c, r, Edge, 48, line);
    }

    private static void Xlr(ImDrawListPtr dl, Vector2 c, float r, float line, int pins)
    {
        // Metal shell, black insert, latch notch at the top; pin layout of the female (socket) face.
        Shell(dl, c, r, line, Metal);
        dl.AddCircleFilled(c, r * 0.80f, Body, 48);
        dl.AddRectFilled(c + new Vector2(-r * 0.14f, -r * 0.98f), c + new Vector2(r * 0.14f, -r * 0.74f), Hole);
        float pin = r * 0.13f;
        switch (pins)
        {
            case 3:
                dl.AddCircleFilled(c + new Vector2(-r * 0.36f, -r * 0.18f), pin, Gold, 16);
                dl.AddCircleFilled(c + new Vector2(r * 0.36f, -r * 0.18f), pin, Gold, 16);
                dl.AddCircleFilled(c + new Vector2(0f, r * 0.40f), pin, Gold, 16);
                break;
            case 4:
                for (int i = 0; i < 4; i++)
                    dl.AddCircleFilled(Polar(c, r * 0.42f, 45f + 90f * i), pin, Gold, 16);
                break;
            case 5:
                for (int i = 0; i < 4; i++)
                    dl.AddCircleFilled(Polar(c, r * 0.48f, -100f + 66.6f * i), pin * 0.85f, Gold, 16);
                dl.AddCircleFilled(Polar(c, r * 0.48f, 180f), pin * 0.85f, Gold, 16);
                break;
            default:
                for (int i = 0; i < 3; i++)
                    dl.AddCircle(Polar(c, r * 0.40f, -60f + 120f * i), pin, Gold, 16, line);
                break;
        }
    }

    private static void Din(ImDrawListPtr dl, Vector2 c, float r, float line, int pins)
    {
        Shell(dl, c, r * 0.9f, line, Metal);
        dl.AddCircleFilled(c, r * 0.72f, Body, 48);
        dl.AddRectFilled(c + new Vector2(-r * 0.1f, r * 0.62f), c + new Vector2(r * 0.1f, r * 0.9f), Metal);
        float step = pins == 3 ? 90f : 45f;
        float start = pins == 3 ? -90f : -90f;
        for (int i = 0; i < pins; i++)
            dl.AddCircleFilled(Polar(c, r * 0.42f, start + step * i), r * 0.09f, Gold, 12);
    }

    private static void Rj45(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.85f, -r * 0.62f);
        var max = c + new Vector2(r * 0.85f, r * 0.62f);
        dl.AddRectFilled(min, max, Hole, r * 0.08f);
        dl.AddRect(min, max, Edge, r * 0.08f, ImDrawFlags.None, line);
        // Latch opening at the bottom, 8 gold contacts at the top.
        dl.AddRectFilled(c + new Vector2(-r * 0.30f, r * 0.30f), c + new Vector2(r * 0.30f, r * 0.62f), Body);
        float w = r * 1.4f / 8f;
        for (int i = 0; i < 8; i++)
        {
            float x = min.X + r * 0.15f + w * i;
            dl.AddRectFilled(new Vector2(x + w * 0.2f, min.Y + r * 0.08f), new Vector2(x + w * 0.75f, min.Y + r * 0.42f), Gold);
        }
    }

    private static void DSizeFlange(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        // Neutrik D-size flange: rounded square, two mounting holes, round metal housing.
        var min = c - new Vector2(r);
        var max = c + new Vector2(r);
        dl.AddRectFilled(min, max, BodyLight, r * 0.25f);
        dl.AddRect(min, max, Edge, r * 0.25f, ImDrawFlags.None, line);
        dl.AddCircleFilled(c + new Vector2(-r * 0.72f, -r * 0.72f), r * 0.1f, Hole, 12);
        dl.AddCircleFilled(c + new Vector2(r * 0.72f, r * 0.72f), r * 0.1f, Hole, 12);
        dl.AddCircleFilled(c, r * 0.80f, Metal, 40);
        dl.AddCircleFilled(c, r * 0.70f, Body, 40);
    }

    private static void Powercon(ImDrawListPtr dl, Vector2 c, float r, float line, uint ringTop, uint ringBottom)
    {
        // A coloured ring (blue = power in, grey = power out), the key nose and three contacts.
        DSizeFlange(dl, c, r, line);
        dl.PathArcTo(c, r * 0.74f, MathF.PI, 2f * MathF.PI, 24);
        dl.PathStroke(ringTop, ImDrawFlags.None, r * 0.12f);
        dl.PathArcTo(c, r * 0.74f, 0f, MathF.PI, 24);
        dl.PathStroke(ringBottom, ImDrawFlags.None, r * 0.12f);
        dl.AddCircleFilled(c, r * 0.48f, BodyLight, 32);
        dl.AddRectFilled(c + new Vector2(-r * 0.08f, -r * 0.62f), c + new Vector2(r * 0.08f, -r * 0.44f), Hole);
        dl.AddRectFilled(c + new Vector2(-r * 0.36f, -r * 0.06f), c + new Vector2(-r * 0.18f, r * 0.20f), Metal);
        dl.AddRectFilled(c + new Vector2(r * 0.18f, -r * 0.06f), c + new Vector2(r * 0.36f, r * 0.20f), Metal);
        dl.AddRectFilled(c + new Vector2(-r * 0.08f, r * 0.22f), c + new Vector2(r * 0.08f, r * 0.40f), Gold);
    }

    private static void True1(ImDrawListPtr dl, Vector2 c, float r, float line, bool top)
    {
        // TRUE1: black housing with the yellow ring; TRUE1 TOP adds the IP65 seal (a second, darker ring).
        DSizeFlange(dl, c, r, line);
        dl.AddCircle(c, r * 0.72f, True1Yellow, 40, r * 0.14f);
        if (top)
            dl.AddCircle(c, r * 0.86f, Theme.U32(Theme.Rgb(0x8C7A1C)), 40, r * 0.06f);
        dl.AddCircleFilled(c, r * 0.52f, Hole, 32);
        dl.AddCircleFilled(c + new Vector2(-r * 0.24f, -r * 0.12f), r * 0.09f, Metal, 12);
        dl.AddCircleFilled(c + new Vector2(r * 0.24f, -r * 0.12f), r * 0.09f, Metal, 12);
        dl.AddCircleFilled(c + new Vector2(0f, r * 0.24f), r * 0.10f, Gold, 12);
        dl.AddRectFilled(c + new Vector2(-r * 0.07f, -r * 0.56f), c + new Vector2(r * 0.07f, -r * 0.40f), True1Yellow);
    }

    private static void Schuko(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        Shell(dl, c, r, line, Body);
        dl.AddCircleFilled(c, r * 0.78f, BodyLight, 40);
        dl.AddCircleFilled(c + new Vector2(-r * 0.38f, 0f), r * 0.12f, Hole, 16);
        dl.AddCircleFilled(c + new Vector2(r * 0.38f, 0f), r * 0.12f, Hole, 16);
        // Earth clips at the top and bottom of the recess.
        dl.AddRectFilled(c + new Vector2(-r * 0.18f, -r * 0.80f), c + new Vector2(r * 0.18f, -r * 0.66f), Metal);
        dl.AddRectFilled(c + new Vector2(-r * 0.18f, r * 0.66f), c + new Vector2(r * 0.18f, r * 0.80f), Metal);
    }

    private static void IecC14(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        // C14 inlet: trapezoid with chamfered bottom corners, three flat pins.
        Span<Vector2> pts = stackalloc Vector2[6];
        pts[0] = c + new Vector2(-r * 0.85f, -r * 0.6f);
        pts[1] = c + new Vector2(r * 0.85f, -r * 0.6f);
        pts[2] = c + new Vector2(r * 0.85f, r * 0.15f);
        pts[3] = c + new Vector2(r * 0.45f, r * 0.6f);
        pts[4] = c + new Vector2(-r * 0.45f, r * 0.6f);
        pts[5] = c + new Vector2(-r * 0.85f, r * 0.15f);
        Polygon(dl, pts, Body, Edge, line);
        dl.AddRectFilled(c + new Vector2(-r * 0.52f, -r * 0.22f), c + new Vector2(-r * 0.40f, r * 0.12f), Metal);
        dl.AddRectFilled(c + new Vector2(r * 0.40f, -r * 0.22f), c + new Vector2(r * 0.52f, r * 0.12f), Metal);
        dl.AddRectFilled(c + new Vector2(-r * 0.06f, -r * 0.48f), c + new Vector2(r * 0.06f, -r * 0.12f), Gold);
    }

    private static void IecC20(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.9f, -r * 0.62f);
        var max = c + new Vector2(r * 0.9f, r * 0.62f);
        dl.AddRectFilled(min, max, Body, r * 0.1f);
        dl.AddRect(min, max, Edge, r * 0.1f, ImDrawFlags.None, line);
        dl.AddRectFilled(c + new Vector2(-r * 0.6f, -r * 0.06f), c + new Vector2(-r * 0.25f, r * 0.08f), Metal);
        dl.AddRectFilled(c + new Vector2(r * 0.25f, -r * 0.06f), c + new Vector2(r * 0.6f, r * 0.08f), Metal);
        dl.AddRectFilled(c + new Vector2(-r * 0.07f, -r * 0.45f), c + new Vector2(r * 0.07f, -r * 0.12f), Gold);
    }

    private static void Cee(ImDrawListPtr dl, Vector2 c, float r, float line, uint color, int pins)
    {
        // IEC 60309: coloured housing (blue 230 V, red 400 V), key slot at 6 h, earth pin larger.
        Shell(dl, c, r, line, color);
        dl.AddCircleFilled(c, r * 0.72f, Body, 40);
        dl.AddRectFilled(c + new Vector2(-r * 0.08f, r * 0.62f), c + new Vector2(r * 0.08f, r * 0.86f), Hole);
        if (pins == 3)
        {
            dl.AddCircleFilled(c + new Vector2(0f, -r * 0.42f), r * 0.14f, Copper, 16);
            dl.AddCircleFilled(c + new Vector2(-r * 0.38f, r * 0.22f), r * 0.11f, Copper, 16);
            dl.AddCircleFilled(c + new Vector2(r * 0.38f, r * 0.22f), r * 0.11f, Copper, 16);
        }
        else
        {
            dl.AddCircleFilled(c + new Vector2(0f, -r * 0.44f), r * 0.13f, Copper, 16);
            for (int i = 0; i < 4; i++)
                dl.AddCircleFilled(Polar(c, r * 0.44f, 72f * (i + 1)), r * 0.09f, Copper, 12);
        }
    }

    private static void Socapex(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        // 19 pins: centre, inner ring of 6, outer ring of 12.
        Shell(dl, c, r, line, Metal);
        dl.AddCircleFilled(c, r * 0.82f, Body, 48);
        float pin = r * 0.07f;
        dl.AddCircleFilled(c, pin, Gold, 10);
        for (int i = 0; i < 6; i++)
            dl.AddCircleFilled(Polar(c, r * 0.32f, 60f * i), pin, Gold, 10);
        for (int i = 0; i < 12; i++)
            dl.AddCircleFilled(Polar(c, r * 0.62f, 30f * i + 15f), pin, Gold, 10);
        dl.AddRectFilled(c + new Vector2(-r * 0.06f, -r * 0.98f), c + new Vector2(r * 0.06f, -r * 0.82f), Hole);
    }

    private static void Harting(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.95f, -r * 0.55f);
        var max = c + new Vector2(r * 0.95f, r * 0.55f);
        dl.AddRectFilled(min, max, Metal, r * 0.12f);
        dl.AddRectFilled(min + new Vector2(r * 0.12f), max - new Vector2(r * 0.12f), Body, r * 0.06f);
        float step = (max.X - min.X - r * 0.4f) / 7f;
        for (int row = 0; row < 2; row++)
        {
            for (int i = 0; i < 8; i++)
                dl.AddCircleFilled(new Vector2(min.X + r * 0.2f + step * i, c.Y + (row == 0 ? -r * 0.18f : r * 0.18f)), r * 0.06f, Gold, 10);
        }
    }

    private static void Bnc(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        Shell(dl, c, r * 0.75f, line, Metal);
        dl.AddRectFilled(c + new Vector2(-r * 0.95f, -r * 0.1f), c + new Vector2(-r * 0.7f, r * 0.1f), Metal);
        dl.AddRectFilled(c + new Vector2(r * 0.7f, -r * 0.1f), c + new Vector2(r * 0.95f, r * 0.1f), Metal);
        dl.AddCircleFilled(c, r * 0.5f, Body, 32);
        dl.AddCircleFilled(c, r * 0.12f, Gold, 12);
    }

    private static void Usb(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.85f, -r * 0.42f);
        var max = c + new Vector2(r * 0.85f, r * 0.42f);
        dl.AddRectFilled(min, max, Metal, r * 0.06f);
        dl.AddRectFilled(min + new Vector2(r * 0.1f), max - new Vector2(r * 0.1f), Hole);
        dl.AddRectFilled(c + new Vector2(-r * 0.6f, -r * 0.3f), c + new Vector2(r * 0.6f, -r * 0.02f), BodyLight);
        for (int i = 0; i < 4; i++)
            dl.AddRectFilled(c + new Vector2(-r * 0.5f + r * 0.3f * i, -r * 0.02f), c + new Vector2(-r * 0.36f + r * 0.3f * i, r * 0.08f), Gold);
    }

    private static void DcJack(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        Shell(dl, c, r * 0.8f, line, Body);
        dl.AddCircleFilled(c, r * 0.48f, Metal, 32);
        dl.AddCircleFilled(c, r * 0.32f, Hole, 24);
        dl.AddCircleFilled(c, r * 0.12f, Gold, 12);
    }

    private static void E27(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.55f, -r * 0.85f);
        var max = c + new Vector2(r * 0.55f, r * 0.55f);
        dl.AddRectFilled(min, max, Metal, r * 0.12f);
        for (int i = 0; i < 4; i++)
        {
            float y = min.Y + r * 0.25f + r * 0.3f * i;
            dl.AddLine(new Vector2(min.X, y), new Vector2(max.X, y + r * 0.1f), BodyLight, line * 1.5f);
        }

        dl.AddRectFilled(new Vector2(c.X - r * 0.3f, max.Y), new Vector2(c.X + r * 0.3f, max.Y + r * 0.22f), Body);
        dl.AddCircleFilled(new Vector2(c.X, max.Y + r * 0.3f), r * 0.1f, Gold, 12);
    }

    private static void Track(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.95f, -r * 0.35f);
        var max = c + new Vector2(r * 0.95f, r * 0.35f);
        dl.AddRectFilled(min, max, Metal, r * 0.08f);
        dl.AddRect(min, max, Edge, r * 0.08f, ImDrawFlags.None, line);
        for (int i = 0; i < 3; i++)
        {
            float y = min.Y + r * 0.18f + r * 0.17f * i;
            dl.AddLine(new Vector2(min.X + r * 0.1f, y), new Vector2(max.X - r * 0.1f, y), Copper, line * 1.5f);
        }

        dl.AddRectFilled(c + new Vector2(-r * 0.2f, r * 0.35f), c + new Vector2(r * 0.2f, r * 0.75f), Body);
    }

    private static void Hybrid(ImDrawListPtr dl, Vector2 c, float r, float line, int pins)
    {
        // Proprietary power + data connector: shell with power pins (copper, outer) and data pins (gold, inner).
        Shell(dl, c, r, line, Metal);
        dl.AddCircleFilled(c, r * 0.80f, Body, 40);
        int power = Math.Max(2, pins / 2);
        for (int i = 0; i < power; i++)
            dl.AddCircleFilled(Polar(c, r * 0.55f, 180f / (power + 1) * (i + 1) + 90f), r * 0.11f, Copper, 12);
        for (int i = 0; i < pins - power; i++)
            dl.AddCircleFilled(Polar(c, r * 0.48f, -90f + 180f / (pins - power + 1) * (i + 1)), r * 0.08f, Gold, 12);
        dl.AddRectFilled(c + new Vector2(-r * 0.1f, -r * 0.98f), c + new Vector2(r * 0.1f, -r * 0.78f), Hole);
    }

    private static void FixedCable(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        // A captive lead leaving the housing through a cable gland.
        dl.AddRectFilled(c + new Vector2(-r * 0.9f, -r * 0.9f), c + new Vector2(-r * 0.2f, r * 0.9f), BodyLight, r * 0.1f);
        dl.AddRectFilled(c + new Vector2(-r * 0.25f, -r * 0.3f), c + new Vector2(r * 0.15f, r * 0.3f), Metal, r * 0.05f);
        dl.AddBezierCubic(c + new Vector2(r * 0.15f, 0f), c + new Vector2(r * 0.6f, 0f), c + new Vector2(r * 0.4f, r * 0.8f),
            c + new Vector2(r * 0.95f, r * 0.8f), Hole, r * 0.24f, 16);
    }

    private static void TerminalBlock(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.95f, -r * 0.5f);
        var max = c + new Vector2(r * 0.95f, r * 0.5f);
        dl.AddRectFilled(min, max, Green, r * 0.06f);
        dl.AddRect(min, max, Edge, r * 0.06f, ImDrawFlags.None, line);
        for (int i = 0; i < 3; i++)
        {
            var s = new Vector2(min.X + r * 0.32f + r * 0.63f * i, c.Y);
            dl.AddCircleFilled(s, r * 0.2f, Metal, 16);
            dl.AddLine(s + new Vector2(-r * 0.14f, -r * 0.14f), s + new Vector2(r * 0.14f, r * 0.14f), BodyLight, line * 1.5f);
        }
    }

    private static void LooseEnd(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        dl.AddRectFilled(c + new Vector2(-r * 0.95f, -r * 0.18f), c + new Vector2(-r * 0.2f, r * 0.18f), Hole, r * 0.08f);
        for (int i = 0; i < 3; i++)
        {
            var start = c + new Vector2(-r * 0.2f, -r * 0.1f + r * 0.1f * i);
            var end = c + new Vector2(r * 0.55f, -r * 0.55f + r * 0.55f * i);
            dl.AddLine(start, end, WireColors[i], r * 0.12f);
            dl.AddLine(end, end + new Vector2(r * 0.3f, 0f), Copper, r * 0.07f);
        }
    }

    private static void IpMoulded(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.55f, -r * 0.95f);
        var max = c + new Vector2(r * 0.55f, r * 0.6f);
        dl.AddRectFilled(min, max, Hole, r * 0.3f);
        dl.AddRect(min, max, Edge, r * 0.3f, ImDrawFlags.None, line);
        dl.AddCircleFilled(c + new Vector2(0f, -r * 0.3f), r * 0.28f, BodyLight, 24);
        dl.AddRectFilled(new Vector2(c.X - r * 0.15f, max.Y), new Vector2(c.X + r * 0.15f, c.Y + r * 0.98f), Hole);
    }

    private static void Battery(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        var min = c + new Vector2(-r * 0.85f, -r * 0.45f);
        var max = c + new Vector2(r * 0.7f, r * 0.45f);
        dl.AddRect(min, max, Edge, r * 0.1f, ImDrawFlags.None, line * 1.5f);
        dl.AddRectFilled(new Vector2(max.X, c.Y - r * 0.18f), new Vector2(max.X + r * 0.18f, c.Y + r * 0.18f), Edge);
        dl.AddRectFilled(min + new Vector2(r * 0.12f), new Vector2(min.X + r * 0.95f, max.Y - r * 0.12f), Green, r * 0.05f);
    }

    private static void Unknown(ImDrawListPtr dl, Vector2 c, float r, float line)
    {
        Shell(dl, c, r * 0.9f, line, Body);
        var size = ImGui.CalcTextSize("?");
        dl.AddText(c - size * 0.5f, Edge, "?");
    }

    private static void Polygon(ImDrawListPtr dl, ReadOnlySpan<Vector2> points, uint fill, uint edge, float line)
    {
        for (int i = 0; i < points.Length; i++)
            dl.PathLineTo(points[i]);
        dl.PathFillConvex(fill);
        for (int i = 0; i < points.Length; i++)
            dl.PathLineTo(points[i]);
        dl.PathStroke(edge, ImDrawFlags.Closed, line);
    }
}
