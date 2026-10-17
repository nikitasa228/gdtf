using System.Globalization;
using System.Xml.Linq;

namespace Candela.Gdtf;

public static partial class GdtfReader
{
    private static readonly Dictionary<string, SnapMode> SnapModes = ReadContext.MapEnum<SnapMode>();
    private static readonly Dictionary<string, ChannelMaster> ChannelMasters = ReadContext.MapEnum<ChannelMaster>();
    private static readonly Dictionary<string, RelationType> RelationTypes = ReadContext.MapEnum<RelationType>();

    private static void ParseDmxModes(ReadContext ctx, XElement e)
    {
        foreach (var me in e.Elements(X.DmxMode))
        {
            var mode = new DmxMode
            {
                Name = ReadContext.Text(me, X.Name),
                Description = ReadContext.Text(me, X.Description),
                GeometryName = ReadContext.NonEmpty(me, X.Geometry),
            };
            string path = $"DMXModes/{mode.Name}";
            foreach (var child in me.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "DMXChannels":
                        foreach (var ce in child.Elements(X.DmxChannel))
                        {
                            try
                            {
                                mode.Channels.Add(ParseChannel(ctx, ce, mode, path));
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                ctx.Warn(path, $"DMX channel skipped ({ex.GetType().Name}: {ex.Message}).");
                            }
                        }
                        break;
                    case "Relations":
                        foreach (var re in child.Elements(X.Relation))
                        {
                            mode.Relations.Add(new DmxRelation
                            {
                                Name = ReadContext.Text(re, X.Name),
                                MasterName = ReadContext.NonEmpty(re, X.Master),
                                FollowerName = ReadContext.NonEmpty(re, X.Follower),
                                Type = ctx.Enum(re, X.Type, RelationTypes, RelationType.Multiply, path),
                            });
                        }
                        break;
                    case "FTMacros":
                        foreach (var fe in child.Elements(X.FtMacro)) mode.Macros.Add(ParseMacro(ctx, fe, path));
                        break;
                    default:
                        ctx.Warn(path, $"Unexpected element <{child.Name.LocalName}> ignored.");
                        break;
                }
            }
            ctx.FixtureType.DmxModes.Add(mode);
        }
    }

    private static DmxChannel ParseChannel(ReadContext ctx, XElement ce, DmxMode mode, string modePath)
    {
        var ch = new DmxChannel
        {
            Mode = mode,
            GeometryName = ReadContext.NonEmpty(ce, X.Geometry),
            InitialFunctionName = ReadContext.NonEmpty(ce, X.InitialFunction),
        };
        string path = $"{modePath}/DMXChannel {ch.GeometryName}";

        var brk = ReadContext.Str(ce, X.DmxBreak)?.Trim();
        if (string.Equals(brk, "Overwrite", StringComparison.OrdinalIgnoreCase)) ch.BreakIsOverwrite = true;
        else if (!string.IsNullOrEmpty(brk))
        {
            if (int.TryParse(brk, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) && b >= 1) ch.DmxBreak = b;
            else ctx.Warn(path, $"DMXBreak=\"{brk}\" is not a break number; 1 used.");
        }

        ch.Offsets = ParseOffsets(ctx, ReadContext.Str(ce, X.Offset), path);
        ch.Highlight = ctx.DmxOpt(ce, X.Highlight, path);
        ch.LegacyDefault = ctx.DmxOpt(ce, X.Default, path);

        foreach (var le in ce.Elements(X.LogicalChannel))
        {
            var lc = new LogicalChannel
            {
                Channel = ch,
                AttributeName = ReadContext.NonEmpty(le, X.Attribute),
                Snap = ctx.Enum(le, X.Snap, SnapModes, SnapMode.No, path),
                Master = ctx.Enum(le, X.Master, ChannelMasters, ChannelMaster.None, path),
                MibFade = ctx.Float(le, X.MibFade, 0f, path),
                DmxChangeTimeLimit = ctx.Float(le, X.DmxChangeTimeLimit, 0f, path),
            };
            int index = 0;
            foreach (var fe in le.Elements(X.ChannelFunction))
                lc.Functions.Add(ParseFunction(ctx, fe, lc, ++index, path));
            ch.LogicalChannels.Add(lc);
        }

        // GDTF naming rule: "{Geometry}_{Attribute of the first logical channel}".
        string attr = ch.LogicalChannels.Count > 0 ? ch.LogicalChannels[0].Name : "";
        ch.Name = $"{ch.GeometryName}_{attr}";
        if (ch.LogicalChannels.Count == 0) ctx.Warn(path, "DMX channel has no logical channel.");
        return ch;
    }

    private static int[] ParseOffsets(ReadContext ctx, string? text, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
        var s = text.AsSpan().Trim();
        if (s.Equals("None", StringComparison.OrdinalIgnoreCase)) return Array.Empty<int>();
        var list = new List<int>(2);
        while (!s.IsEmpty)
        {
            int comma = s.IndexOf(',');
            var part = (comma < 0 ? s : s[..comma]).Trim();
            s = comma < 0 ? ReadOnlySpan<char>.Empty : s[(comma + 1)..];
            if (part.IsEmpty) continue;
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) && o >= 1) list.Add(o);
            else ctx.Warn(path, $"Offset part \"{part.ToString()}\" is not a DMX address; dropped.");
        }
        if (list.Count > DmxValue.MaxByteCount)
        {
            ctx.Warn(path, $"Channel has {list.Count} offsets; only the first {DmxValue.MaxByteCount} are used.");
            list.RemoveRange(DmxValue.MaxByteCount, list.Count - DmxValue.MaxByteCount);
        }
        return list.ToArray();
    }

    private static ChannelFunction ParseFunction(ReadContext ctx, XElement fe, LogicalChannel lc, int index, string path)
    {
        var f = new ChannelFunction { LogicalChannel = lc, Index = index };
        bool hasMin = false, hasMax = false;
        foreach (var a in fe.Attributes())
        {
            string v = a.Value;
            switch (a.Name.LocalName)
            {
                case "Name": f.Name = v; break;
                case "Attribute": f.AttributeName = string.IsNullOrWhiteSpace(v) ? null : v; break;
                case "OriginalAttribute": f.OriginalAttribute = string.IsNullOrWhiteSpace(v) ? null : v; break;
                case "DMXFrom": f.DmxFrom = ParseDmxText(ctx, v, DmxValue.Zero, path, "DMXFrom"); break;
                case "Default": f.Default = ParseDmxText(ctx, v, DmxValue.Zero, path, "Default"); break;
                case "PhysicalFrom": f.PhysicalFrom = ctx.ParseFloat(v, 0f, path, "PhysicalFrom"); break;
                case "PhysicalTo": f.PhysicalTo = ctx.ParseFloat(v, 1f, path, "PhysicalTo"); break;
                case "RealFade": f.RealFade = ctx.ParseFloat(v, 0f, path, "RealFade"); break;
                case "RealAcceleration": f.RealAcceleration = ctx.ParseFloat(v, 0f, path, "RealAcceleration"); break;
                case "Wheel": f.WheelName = NullIfBlank(v); break;
                case "Emitter": f.EmitterName = NullIfBlank(v); break;
                case "Filter": f.FilterName = NullIfBlank(v); break;
                case "ColorSpace": f.ColorSpaceName = NullIfBlank(v); break;
                case "Gamut": f.GamutName = NullIfBlank(v); break;
                case "ModeMaster": f.ModeMasterName = NullIfBlank(v); break;
                case "ModeFrom": f.ModeFrom = ParseDmxText(ctx, v, DmxValue.Zero, path, "ModeFrom"); break;
                case "ModeTo": f.ModeTo = ParseDmxText(ctx, v, DmxValue.Zero, path, "ModeTo"); break;
                case "DMXProfile": f.DmxProfileName = NullIfBlank(v); break;
                case "Min": f.Min = ctx.ParseFloat(v, 0f, path, "Min"); hasMin = !string.IsNullOrWhiteSpace(v); break;
                case "Max": f.Max = ctx.ParseFloat(v, 1f, path, "Max"); hasMax = !string.IsNullOrWhiteSpace(v); break;
                case "CustomName": f.CustomName = NullIfBlank(v); break;
            }
        }
        if (!hasMin) f.Min = f.PhysicalFrom;
        if (!hasMax) f.Max = f.PhysicalTo;
        if (string.IsNullOrEmpty(f.Name))
            f.Name = $"{f.AttributeName ?? "NoFeature"} {index.ToString(CultureInfo.InvariantCulture)}";

        foreach (var se in fe.Elements())
        {
            string n = se.Name.LocalName;
            if (n == "ChannelSet")
            {
                var set = new ChannelSet { Function = f };
                foreach (var a in se.Attributes())
                {
                    switch (a.Name.LocalName)
                    {
                        case "Name": set.Name = a.Value; break;
                        case "DMXFrom": set.DmxFrom = ParseDmxText(ctx, a.Value, DmxValue.Zero, path, "ChannelSet DMXFrom"); break;
                        case "PhysicalFrom":
                            if (!string.IsNullOrWhiteSpace(a.Value)) set.PhysicalFrom = ctx.ParseFloat(a.Value, 0f, path, "ChannelSet PhysicalFrom");
                            break;
                        case "PhysicalTo":
                            if (!string.IsNullOrWhiteSpace(a.Value)) set.PhysicalTo = ctx.ParseFloat(a.Value, 0f, path, "ChannelSet PhysicalTo");
                            break;
                        case "WheelSlotIndex":
                            if (!string.IsNullOrWhiteSpace(a.Value))
                            {
                                // GDTF Builder writes int.MinValue for "no slot"; any negative index means the same.
                                if (int.TryParse(a.Value.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int wi))
                                    set.WheelSlotIndex = System.Math.Max(wi, 0);
                                else
                                    ctx.Warn(path, $"WheelSlotIndex=\"{a.Value}\" is not a slot index; ignored.");
                            }
                            break;
                    }
                }
                f.ChannelSets.Add(set);
            }
            else if (n == "SubChannelSet")
            {
                f.SubChannelSets.Add(new SubChannelSet
                {
                    Name = ReadContext.Text(se, X.Name),
                    PhysicalFrom = ctx.Float(se, X.PhysicalFrom, 0f, path),
                    PhysicalTo = ctx.Float(se, X.PhysicalTo, 1f, path),
                    SubPhysicalUnitName = ReadContext.NonEmpty(se, X.SubPhysicalUnit),
                    DmxProfileName = ReadContext.NonEmpty(se, X.DmxProfile),
                });
            }
            else
            {
                ctx.Warn(path, $"Unexpected element <{n}> in ChannelFunction ignored.");
            }
        }
        return f;
    }

    private static FtMacro ParseMacro(ReadContext ctx, XElement fe, string path)
    {
        var macro = new FtMacro
        {
            Name = ReadContext.Text(fe, X.Name),
            ChannelFunctionName = ReadContext.NonEmpty(fe, X.ChannelFunction),
        };
        foreach (var step in fe.Elements(X.MacroDmx).Elements(X.MacroDmxStep))
        {
            var s = new MacroDmxStep { Duration = ctx.Float(step, X.Duration, 1f, path) };
            foreach (var ve in step.Elements(X.MacroDmxValue))
            {
                s.Values.Add(new MacroDmxValue
                {
                    Value = ctx.Dmx(ve, X.Value, DmxValue.Zero, path),
                    DmxChannelName = ReadContext.NonEmpty(ve, X.DmxChannel),
                });
            }
            macro.Steps.Add(s);
        }
        return macro;
    }

    private static DmxValue ParseDmxText(ReadContext ctx, string text, DmxValue fallback, string path, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (DmxValue.TryParse(text, out var v)) return v;
        ctx.Warn(path, $"{what}=\"{text}\" is not a DMX value; using {fallback}.");
        return fallback;
    }

    private static string? NullIfBlank(string v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
