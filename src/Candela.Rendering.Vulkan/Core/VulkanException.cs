using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>A Vulkan call failed with an error <see cref="Silk.NET.Vulkan.Result"/>.</summary>
public class VulkanException : Exception
{
    /// <summary>Creates the exception for a failed call.</summary>
    /// <param name="result">The Vulkan result code.</param>
    /// <param name="message">What was being done.</param>
    public VulkanException(Result result, string message)
        : base($"{message} ({result}).")
    {
        Result = result;
    }

    /// <summary>Creates the exception without a result code (a missing feature, format or extension).</summary>
    public VulkanException(string message)
        : base(message)
    {
        Result = Result.ErrorUnknown;
    }

    /// <summary>The result code of the failed call.</summary>
    public Result Result { get; }
}

/// <summary>
/// No usable Vulkan implementation: the loader library is missing, no device supports Vulkan 1.3 with the required
/// features, or instance creation failed. Callers (CLI, tests, editor) report it instead of crashing.
/// </summary>
public sealed class VulkanUnavailableException : VulkanException
{
    /// <summary>Creates the exception.</summary>
    public VulkanUnavailableException(string message)
        : base(message)
    {
    }
}
