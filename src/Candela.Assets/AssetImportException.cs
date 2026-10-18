namespace Candela.Assets;

/// <summary>
/// A file could not be imported at all (unsupported or unreadable format, corrupt data). The message is written for
/// the user and can be shown as-is; recoverable problems inside a file are reported as issues instead.
/// </summary>
public sealed class AssetImportException : Exception
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public AssetImportException(string message) : base(message) { }

    /// <summary>Creates the exception with a user-facing message and the low-level cause.</summary>
    public AssetImportException(string message, Exception innerException) : base(message, innerException) { }
}
