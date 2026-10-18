using System.IO.Compression;

namespace Candela.Project.Internal;

/// <summary>
/// Cheap structural check of a GDTF file: a zip archive whose directory can be read and that contains
/// <c>description.xml</c>. Only the central directory is read, not the XML.
/// </summary>
internal static class GdtfCheck
{
    /// <summary>Null when the bytes look like a GDTF file, otherwise a short reason for the user.</summary>
    public static string? Validate(byte[] data)
    {
        if (data.Length < 22 || data[0] != (byte)'P' || data[1] != (byte)'K') return "not a zip archive";
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            if (zip.GetEntry("description.xml") is not null) return null;
            foreach (var e in zip.Entries)
                if (string.Equals(e.FullName, "description.xml", StringComparison.OrdinalIgnoreCase)) return null;
            return "no description.xml";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            return "damaged zip archive";
        }
    }
}
