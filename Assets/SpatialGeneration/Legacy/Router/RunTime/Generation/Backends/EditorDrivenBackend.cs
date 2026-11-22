using System;
using System.Threading.Tasks;

/// <summary>
/// Placeholder returned by <see cref="BackendRegistry"/> for <see cref="BackendKind.Agent"/>.
/// The MCP agent builds assets directly in the open scene, so it is driven from the editor
/// (SpatialGeneration.Agent.AgentWorkflow) rather than through the per-proxy backend interface.
/// </summary>
public sealed class EditorDrivenBackend : IGenerationBackend
{
    private const string Message =
        "The MCP agent backend runs from the Spatial Generation window, not through the " +
        "ComfyUI pipeline. Switch the backend to Router to use the ComfyUI path.";

    public string Name => "MCP Agent";

    public Task EnsureReadyAsync() => throw new InvalidOperationException(Message);

    public Task<AssetGenerationResult> GenerateAssetAsync(AssetGenerationRequest request) =>
        throw new InvalidOperationException(Message);
}
