using System.Threading.Tasks;

/// <summary>
/// Produces one 3D asset for one occupy proxy. The scene-level loop lives in
/// <see cref="GenerationPipeline"/>, so a backend only ever sees a single asset request.
/// </summary>
public interface IGenerationBackend
{
    string Name { get; }

    /// <summary>
    /// One-time bootstrapping (health checks, tunnels). Safe to call repeatedly;
    /// throws with an actionable message when the backend cannot be reached.
    /// </summary>
    Task EnsureReadyAsync();

    Task<AssetGenerationResult> GenerateAssetAsync(AssetGenerationRequest request);
}
