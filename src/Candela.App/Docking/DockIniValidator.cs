using System.Globalization;
using System.Text.RegularExpressions;

namespace Candela.App.Docking;

/// <summary>
/// Checks the dock tree in Dear ImGui's settings text (the <c>[Docking][Data]</c> section embedded in layout files)
/// before it is handed to Dear ImGui. Dear ImGui trusts that text: a duplicated or zero node id, a node that is its own
/// parent or comes before its parent, a split node with one child, a leaf with children, a second central node or a
/// size outside its 16-bit fields ends in a native assertion (the process aborts) or an endless loop — and the
/// workspace layout is restored at every start, so one damaged file would stop the editor from ever starting again.
/// The check accepts exactly what Dear ImGui writes; anything else makes the layout "damaged" so the default
/// arrangement is used instead.
/// </summary>
internal static partial class DockIniValidator
{
    /// <summary>Most dock nodes accepted (a workspace has a few dozen).</summary>
    public const int MaxNodes = 1024;

    /// <summary>Deepest node nesting accepted.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// True when the dock tree of <paramref name="ini"/> can be loaded safely; otherwise <paramref name="reason"/> says
    /// what is wrong. Text without a <c>[Docking][Data]</c> section is valid.
    /// </summary>
    public static bool Validate(string? ini, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(ini))
            return true;
        if (ini.Contains('\0', StringComparison.Ordinal))
        {
            reason = "the window arrangement contains a NUL character";
            return false;
        }

        var nodes = new Dictionary<uint, Node>();
        var order = new List<Node>();
        bool inDocking = false;
        int lineNumber = 0;
        foreach (var raw in ini.Split('\n', '\r'))
        {
            lineNumber++;
            string line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                inDocking = IsDockingHeader(line);
                continue;
            }

            if (!inDocking)
                continue;
            string body = line.TrimStart();
            if (!body.StartsWith("DockNode", StringComparison.Ordinal) && !body.StartsWith("DockSpace", StringComparison.Ordinal))
                continue; // Dear ImGui ignores other lines of the section

            if (!TryParseNode(body, out var node, out reason))
            {
                reason = $"dock node line {lineNumber}: {reason}";
                return false;
            }

            if (!nodes.TryAdd(node.Id, node))
            {
                reason = $"dock node 0x{node.Id:X8} is listed twice";
                return false;
            }

            if (nodes.Count > MaxNodes)
            {
                reason = $"more than {MaxNodes} dock nodes";
                return false;
            }

            if (node.ParentId is { } parentId)
            {
                // The parent must come first (Dear ImGui writes parents first); this also rules out cycles.
                if (parentId == node.Id || !nodes.TryGetValue(parentId, out var parent) || ReferenceEquals(parent, node))
                {
                    reason = $"dock node 0x{node.Id:X8} has no parent node 0x{parentId:X8} before it";
                    return false;
                }

                if (parent.Children == 2)
                {
                    reason = $"dock node 0x{parentId:X8} has more than two children";
                    return false;
                }

                parent.Children++;
                node.Root = parent.Root;
                node.Depth = parent.Depth + 1;
                if (node.Depth > MaxDepth)
                {
                    reason = $"dock nodes nested deeper than {MaxDepth}";
                    return false;
                }
            }
            else
            {
                node.Root = node.Id;
            }

            order.Add(node);
        }

        var centralPerRoot = new HashSet<uint>();
        foreach (var node in order)
        {
            if (node.Children == 1 || (node.Children == 2) != node.Split)
            {
                reason = node.Split
                    ? $"split dock node 0x{node.Id:X8} does not have two children"
                    : $"dock node 0x{node.Id:X8} has children but no split direction";
                return false;
            }

            if (node.Central && (node.Children != 0 || !centralPerRoot.Add(node.Root)))
            {
                reason = $"dock node 0x{node.Id:X8} cannot be a central node";
                return false;
            }
        }

        return true;
    }

    private static bool IsDockingHeader(string line)
    {
        // "[Type][Name]": the type ends at the first ']', the name starts at the next '['.
        int typeEnd = line.IndexOf(']', 1);
        if (typeEnd < 0 || typeEnd == line.Length - 1)
            return false;
        int nameStart = line.IndexOf('[', typeEnd + 1);
        if (nameStart < 0)
            return false;
        return line.AsSpan(1, typeEnd - 1).SequenceEqual("Docking") && line.AsSpan(nameStart + 1, line.Length - nameStart - 2).SequenceEqual("Data");
    }

    private static bool TryParseNode(string body, out Node node, out string reason)
    {
        node = new Node();
        var m = NodeLine().Match(body);
        if (!m.Success)
        {
            reason = $"'{Shorten(body)}' is not a dock node Dear ImGui writes";
            return false;
        }

        node.Id = Hex(m.Groups["id"].Value);
        if (node.Id == 0)
        {
            reason = "a dock node has the id 0";
            return false;
        }

        if (m.Groups["parent"].Success)
        {
            uint parent = Hex(m.Groups["parent"].Value);
            if (parent == 0)
            {
                reason = $"dock node 0x{node.Id:X8} has the parent id 0";
                return false;
            }

            node.ParentId = parent;
            if (!Coordinate(m.Groups["rw"].Value, 0, out _) || !Coordinate(m.Groups["rh"].Value, 0, out _))
            {
                reason = $"dock node 0x{node.Id:X8} has an invalid size";
                return false;
            }
        }
        else
        {
            if (!Coordinate(m.Groups["px"].Value, short.MinValue, out _) || !Coordinate(m.Groups["py"].Value, short.MinValue, out _) ||
                !Coordinate(m.Groups["w"].Value, 1, out _) || !Coordinate(m.Groups["h"].Value, 1, out _))
            {
                reason = $"dock node 0x{node.Id:X8} has an invalid position or size";
                return false;
            }
        }

        node.Split = m.Groups["split"].Success;
        node.Central = m.Groups["central"].Value == "1";
        reason = "";
        return true;
    }

    private static bool Coordinate(string text, int min, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value >= min && value <= short.MaxValue;

    private static uint Hex(string text) => uint.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    // What DockSettingsHandler_WriteAll produces, field by field in its fixed order (Dear ImGui reads the fields in the
    // same order and silently stops at the first one it does not recognise, so a reordered line would be read
    // differently than it looks).
    [GeneratedRegex(
        @"^Dock(?:Node|Space)\s+ID=0x(?<id>[0-9A-Fa-f]{1,8})" +
        @"(?:\s+Parent=0x(?<parent>[0-9A-Fa-f]{1,8})\s+SizeRef=(?<rw>-?\d{1,6}),(?<rh>-?\d{1,6})" +
        @"|(?:\s+Window=0x[0-9A-Fa-f]{1,8})?\s+Pos=(?<px>-?\d{1,6}),(?<py>-?\d{1,6})\s+Size=(?<w>-?\d{1,6}),(?<h>-?\d{1,6}))" +
        @"(?:\s+Split=(?<split>[XY]))?(?:\s+NoResize=[01])?(?:\s+CentralNode=(?<central>[01]))?(?:\s+NoTabBar=[01])?" +
        @"(?:\s+HiddenTabBar=[01])?(?:\s+NoWindowMenuButton=[01])?(?:\s+NoCloseButton=[01])?(?:\s+Selected=0x[0-9A-Fa-f]{1,8})?" +
        @"\s*(?:;.*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NodeLine();

    private sealed class Node
    {
        public uint Id;
        public uint? ParentId;
        public uint Root;
        public int Depth;
        public int Children;
        public bool Split;
        public bool Central;
    }
}
