using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Silk.NET.Shaderc;

namespace Candela.Rendering.Vulkan;

/// <summary>A GLSL file failed to compile.</summary>
public sealed class ShaderCompilationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ShaderCompilationException(string file, string message)
        : base($"Shader '{file}' failed to compile:\n{message}")
    {
        File = file;
    }

    /// <summary>The shader file.</summary>
    public string File { get; }
}

/// <summary>Shader stage, derived from the file extension.</summary>
internal enum ShaderStageKind
{
    Vertex,
    Fragment,
    Compute,
}

/// <summary>
/// GLSL 4.60 → SPIR-V 1.6 (Vulkan 1.3) at run time with shaderc. <c>#include "file"</c> is resolved through a native
/// include callback against <see cref="ShaderLibrary"/>; macro variants are passed as definitions. Results are cached
/// on disk under <c>&lt;cache&gt;/shaders/&lt;sha256&gt;.spv</c>, keyed by the source, every included file, the
/// definitions, the stage and the compiler settings — editing any include invalidates exactly the shaders using it.
/// Main thread only (the compiler object is not shared across threads).
/// </summary>
internal sealed unsafe class ShaderCompiler : IDisposable
{
    /// <summary>Bump when compiler settings change in a way the hash does not capture.</summary>
    private const string SettingsTag = "candela-shaderc-v1;vulkan1.3;spirv1.6;O";

    /// <summary>
    /// The shaderc library is loaded once and stays loaded until the process exits: glslang registers process exit
    /// handlers, so unloading it (Silk.NET's <c>Dispose</c> on the API object) makes the process crash at exit.
    /// </summary>
    private static readonly Lazy<Shaderc> SharedApi = new(Shaderc.GetApi, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Shaderc _api;
    private readonly ShaderLibrary _library;
    private readonly string? _cacheDirectory;
    private readonly Action<VulkanLogLevel, string>? _log;
    private Compiler* _compiler;
    private CompileOptions* _baseOptions;
    private GCHandle _libraryHandle;

    public ShaderCompiler(ShaderLibrary library, string? cacheDirectory, Action<VulkanLogLevel, string>? log = null)
    {
        _library = library;
        _cacheDirectory = cacheDirectory is null ? null : Path.Combine(cacheDirectory, "shaders");
        _log = log;
        _api = SharedApi.Value;
        _compiler = _api.CompilerInitialize();
        if (_compiler == null)
            throw new InvalidOperationException("shaderc_compiler_initialize failed.");
        _baseOptions = _api.CompileOptionsInitialize();
        _api.CompileOptionsSetSourceLanguage(_baseOptions, SourceLanguage.Glsl);
        _api.CompileOptionsSetTargetEnv(_baseOptions, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
        _api.CompileOptionsSetTargetSpirv(_baseOptions, SpirvVersion.Shaderc16);
        _api.CompileOptionsSetOptimizationLevel(_baseOptions, OptimizationLevel.Performance);
        _api.CompileOptionsSetWarningsAsErrors(_baseOptions);
        _libraryHandle = GCHandle.Alloc(library);
        _api.CompileOptionsSetIncludeCallbacks(_baseOptions, new PfnIncludeResolveFn(&ResolveInclude),
            new PfnIncludeResultReleaseFn(&ReleaseInclude), (void*)GCHandle.ToIntPtr(_libraryHandle));
    }

    /// <summary>Number of compilations served from the disk cache (diagnostics/tests).</summary>
    public int CacheHits { get; private set; }

    /// <summary>Number of actual shaderc compilations.</summary>
    public int Compilations { get; private set; }

    public static ShaderStageKind StageOf(string file) => Path.GetExtension(file) switch
    {
        ".vert" => ShaderStageKind.Vertex,
        ".frag" => ShaderStageKind.Fragment,
        ".comp" => ShaderStageKind.Compute,
        _ => throw new ArgumentException($"Unknown shader stage for '{file}'.", nameof(file)),
    };

    /// <summary>Compiles <paramref name="file"/> with the given macro definitions (name, value) to SPIR-V words.</summary>
    public uint[] Compile(string file, IReadOnlyList<(string Name, string Value)>? defines = null)
    {
        ObjectDisposedException.ThrowIf(_compiler == null, this);
        defines ??= Array.Empty<(string, string)>();
        string source = _library.GetSource(file);
        string key = CacheKey(file, source, defines);
        string? cachePath = _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, key + ".spv");
        if (cachePath is not null && TryReadCache(cachePath, out var cached))
        {
            CacheHits++;
            return cached;
        }

        var words = CompileUncached(file, source, defines);
        Compilations++;
        if (cachePath is not null)
            WriteCache(cachePath, words);
        return words;
    }

    public void Dispose()
    {
        if (_baseOptions != null)
        {
            _api.CompileOptionsRelease(_baseOptions);
            _baseOptions = null;
        }

        if (_compiler != null)
        {
            _api.CompilerRelease(_compiler);
            _compiler = null;
        }

        if (_libraryHandle.IsAllocated)
            _libraryHandle.Free();
    }

    private uint[] CompileUncached(string file, string source, IReadOnlyList<(string Name, string Value)> defines)
    {
        var options = _api.CompileOptionsClone(_baseOptions);
        try
        {
            foreach (var (name, value) in defines)
            {
                byte[] n = Encoding.UTF8.GetBytes(name);
                byte[] v = Encoding.UTF8.GetBytes(value);
                fixed (byte* pn = n)
                fixed (byte* pv = v)
                    _api.CompileOptionsAddMacroDefinition(options, pn, (nuint)n.Length, pv, (nuint)v.Length);
            }

            var kind = StageOf(file) switch
            {
                ShaderStageKind.Vertex => ShaderKind.VertexShader,
                ShaderStageKind.Fragment => ShaderKind.FragmentShader,
                _ => ShaderKind.ComputeShader,
            };
            byte[] text = Encoding.UTF8.GetBytes(source);
            CompilationResult* result;
            fixed (byte* src = text)
                result = _api.CompileIntoSpv(_compiler, src, (nuint)text.Length, kind, file, "main", options);
            try
            {
                var status = _api.ResultGetCompilationStatus(result);
                if (status != CompilationStatus.Success)
                    throw new ShaderCompilationException(file, $"{status}: {_api.ResultGetErrorMessageS(result)}");
                if (_api.ResultGetNumWarnings(result) > 0)
                    _log?.Invoke(VulkanLogLevel.Warning, $"Shader '{file}': {_api.ResultGetErrorMessageS(result)}");
                nuint length = _api.ResultGetLength(result);
                if (length == 0 || length % 4 != 0)
                    throw new ShaderCompilationException(file, $"invalid SPIR-V length {length}");
                var words = new uint[(int)length / 4];
                fixed (uint* dst = words)
                    System.Buffer.MemoryCopy(_api.ResultGetBytes(result), dst, (long)length, (long)length);
                return words;
            }
            finally
            {
                _api.ResultRelease(result);
            }
        }
        finally
        {
            _api.CompileOptionsRelease(options);
        }
    }

    private string CacheKey(string file, string source, IReadOnlyList<(string Name, string Value)> defines)
    {
        var sb = new StringBuilder();
        sb.Append(SettingsTag).Append('\n').Append(file).Append('\n');
        foreach (var (name, value) in defines.OrderBy(d => d.Name, StringComparer.Ordinal))
            sb.Append("-D").Append(name).Append('=').Append(value).Append('\n');
        sb.Append("@main\n").Append(source).Append('\n');
        foreach (var include in _library.IncludeClosure(file))
            sb.Append("@include ").Append(include).Append('\n').Append(_library.TryGetSource(include) ?? "<missing>").Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private bool TryReadCache(string path, out uint[] words)
    {
        words = Array.Empty<uint>();
        try
        {
            if (!File.Exists(path))
                return false;
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 20 || bytes.Length % 4 != 0)
                return false;
            words = new uint[bytes.Length / 4];
            System.Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
            return words[0] == 0x07230203u; // SPIR-V magic
        }
        catch (IOException e)
        {
            _log?.Invoke(VulkanLogLevel.Debug, $"SPIR-V cache read failed: {e.Message}");
            return false;
        }
    }

    private void WriteCache(string path, uint[] words)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[words.Length * 4];
            System.Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            string tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke(VulkanLogLevel.Debug, $"SPIR-V cache write failed: {e.Message}");
        }
    }

    /// <summary>
    /// shaderc include resolver. Returns a native <see cref="IncludeResult"/>; on failure the source name is empty and
    /// the content holds the error message (shaderc's convention).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IncludeResult* ResolveInclude(void* userData, byte* requested, int type, byte* requesting, nuint depth)
    {
        string name = Marshal.PtrToStringUTF8((nint)requested) ?? "";
        string? content = null;
        string error = $"include '{name}' not found";
        try
        {
            if (GCHandle.FromIntPtr((nint)userData).Target is ShaderLibrary library)
                content = library.TryGetSource(name);
        }
        catch (Exception e)
        {
            error = $"include '{name}': {e.Message}";
        }

        var result = (IncludeResult*)NativeMemory.AllocZeroed((nuint)sizeof(IncludeResult));
        if (content is not null)
        {
            result->SourceName = Utf8(name, out nuint nameLength);
            result->SourceNameLength = nameLength;
            result->Content = Utf8(content, out nuint contentLength);
            result->ContentLength = contentLength;
        }
        else
        {
            result->SourceName = Utf8("", out _);
            result->SourceNameLength = 0;
            result->Content = Utf8(error, out nuint errorLength);
            result->ContentLength = errorLength;
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ReleaseInclude(void* userData, IncludeResult* result)
    {
        if (result == null)
            return;
        NativeMemory.Free(result->SourceName);
        NativeMemory.Free(result->Content);
        NativeMemory.Free(result);
    }

    private static byte* Utf8(string s, out nuint length)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        var p = (byte*)NativeMemory.Alloc((nuint)n + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(p, n));
        p[n] = 0;
        length = (nuint)n;
        return p;
    }
}
