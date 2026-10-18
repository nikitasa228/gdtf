using System.Buffers.Binary;
using System.Security.Cryptography;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// The <c>VkPipelineCache</c>, persisted as <c>pipeline-&lt;vendor&gt;-&lt;device&gt;-&lt;cache uuid&gt;.bin</c> in the cache
/// directory. The file starts with a small envelope (magic, payload length, SHA-256 of the payload) so a truncated or
/// otherwise damaged file is never handed to the driver; the payload's own header (VkPipelineCacheHeaderVersionOne) is
/// then checked against the device, so a cache from another GPU or driver version is ignored rather than trusted.
/// Files are written to a unique temporary name and renamed, so concurrent writers (several contexts or processes)
/// never interleave.
/// </summary>
internal sealed unsafe class PipelineCacheStore : IDisposable
{
    private const int EnvelopeSize = 8 + 8 + 32;
    private static ReadOnlySpan<byte> Magic => "CANDPC01"u8;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly string? _path;
    private readonly Action<VulkanLogLevel, string> _log;

    public PipelineCacheStore(Vk vk, Device device, uint vendorId, uint deviceId, Guid uuid, string? cacheDirectory,
        Action<VulkanLogLevel, string> log)
    {
        _vk = vk;
        _device = device;
        _log = log;
        if (cacheDirectory is not null)
            _path = Path.Combine(cacheDirectory, $"pipeline-{vendorId:x4}-{deviceId:x4}-{uuid:N}.bin");

        byte[]? initial = TryLoad(vendorId, deviceId, uuid);
        fixed (byte* data = initial)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(initial?.Length ?? 0),
                PInitialData = data,
            };
            PipelineCache cache;
            var result = vk.CreatePipelineCache(device, &info, null, &cache);
            if (result != Result.Success && initial is not null)
            {
                info.InitialDataSize = 0;
                info.PInitialData = null;
                result = vk.CreatePipelineCache(device, &info, null, &cache);
            }

            VkUtil.Check(result, "vkCreatePipelineCache");
            Cache = cache;
        }
    }

    public PipelineCache Cache { get; private set; }

    /// <summary>Writes the cache to disk (atomically). Failures are logged, never thrown.</summary>
    public void Save()
    {
        if (_path is null || Cache.Handle == 0)
            return;
        try
        {
            nuint size;
            VkUtil.Check(_vk.GetPipelineCacheData(_device, Cache, &size, null), "vkGetPipelineCacheData");
            if (size == 0)
                return;
            var data = new byte[(int)size];
            fixed (byte* p = data)
                VkUtil.Check(_vk.GetPipelineCacheData(_device, Cache, &size, p), "vkGetPipelineCacheData");
            var payload = data.AsSpan(0, (int)size);
            var envelope = new byte[EnvelopeSize];
            Magic.CopyTo(envelope);
            BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(8), (ulong)size);
            SHA256.HashData(payload, envelope.AsSpan(16));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = File.Create(tmp))
                {
                    stream.Write(envelope);
                    stream.Write(payload);
                }

                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VulkanException)
        {
            _log(VulkanLogLevel.Warning, $"Pipeline cache not saved: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (Cache.Handle == 0)
            return;
        Save();
        _vk.DestroyPipelineCache(_device, Cache, null);
        Cache = default;
    }

    private byte[]? TryLoad(uint vendorId, uint deviceId, Guid uuid)
    {
        if (_path is null || !File.Exists(_path))
            return null;
        try
        {
            var file = File.ReadAllBytes(_path);
            if (file.Length < EnvelopeSize + 32 || !file.AsSpan(0, 8).SequenceEqual(Magic))
                return null;
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8));
            if (length != (ulong)(file.Length - EnvelopeSize))
                return null;
            var data = file.AsSpan(EnvelopeSize).ToArray();
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            if (!hash.SequenceEqual(file.AsSpan(16, 32)))
                return null;
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
            uint headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            uint vendor = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            uint device = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            var fileUuid = new Guid(data.AsSpan(16, 16));
            bool valid = headerSize >= 32 && headerVersion == (uint)PipelineCacheHeaderVersion.One &&
                         vendor == vendorId && device == deviceId && fileUuid == uuid;
            return valid ? data : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log(VulkanLogLevel.Warning, $"Pipeline cache not loaded: {e.Message}");
            return null;
        }
    }
}
