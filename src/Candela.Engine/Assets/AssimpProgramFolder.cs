using Assimp.Unmanaged;

namespace Candela.Engine;

/// <summary>
/// Loads the native Assimp library of an installed program whose folder name the Windows ANSI code page cannot spell.
/// AssimpNet loads <c>assimp.dll</c> through <c>LoadLibraryA</c> with the full path of the program folder: on a Windows
/// whose ANSI code page has no Cyrillic letters, a per-user install under <c>C:\Users\Иван\AppData\Local\Programs</c> or
/// a folder like <c>C:\Program Files\Свет\Candela</c> reaches the loader as question marks and fails with "module not
/// found" — every OBJ, FBX, COLLADA and 3DS import then fails. Loaded by its bare name instead, the library is found by
/// Windows itself in the program's folder (the first place of the DLL search order, looked up in Unicode), and
/// <see cref="Candela.Assets.AssimpNative"/> finds it already loaded. Does nothing elsewhere, for an ASCII folder (the
/// usual way works there), or when the library is not next to the program (the development layout). Never throws.
/// </summary>
public static class AssimpProgramFolder
{
    /// <summary>File name of the native library on Windows.</summary>
    public const string LibraryFileName = "assimp.dll";

    /// <summary>
    /// True when the library must be loaded by its bare name: Windows, a program folder (<paramref name="programDirectory"/>)
    /// with characters outside ASCII, and <see cref="LibraryFileName"/> in that folder.
    /// </summary>
    public static bool NeedsBareNameLoad(string programDirectory, bool windows)
    {
        if (!windows || string.IsNullOrEmpty(programDirectory) || programDirectory.All(char.IsAscii))
            return false;
        try
        {
            return File.Exists(Path.Combine(programDirectory, LibraryFileName));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the library by its bare name when <see cref="NeedsBareNameLoad"/> says so (once; later calls see it
    /// loaded). True when the library is loaded afterwards; false when nothing was needed or the load failed (the usual
    /// loading then runs and reports its own error).
    /// </summary>
    public static bool Prepare()
    {
        if (!NeedsBareNameLoad(AppContext.BaseDirectory, OperatingSystem.IsWindows()))
            return false;
        try
        {
            var library = AssimpLibrary.Instance;
            return library.IsLibraryLoaded || library.LoadLibrary(LibraryFileName);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // AssimpNet reports a failed load with its own exception types; the usual loading reports the reason.
            return false;
        }
    }
}
