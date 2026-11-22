using System.Threading.Tasks;

/// <summary>
/// Offline stand-in: places the proxy's own primitive instead of calling a backend.
/// Useful for exercising the authoring loop without a GPU.
/// </summary>
public sealed class MockGenerationBackend : IGenerationBackend
{
    public string Name => "Mock";

    public Task EnsureReadyAsync() => Task.CompletedTask;

    public Task<AssetGenerationResult> GenerateAssetAsync(AssetGenerationRequest request)
    {
        return Task.FromResult(new AssetGenerationResult
        {
            ProxyId = request?.ProxyId ?? string.Empty,
            FallbackPrimitive = request?.Volume?.ToPrimitive() ?? UnityEngine.PrimitiveType.Cube
        });
    }
}
