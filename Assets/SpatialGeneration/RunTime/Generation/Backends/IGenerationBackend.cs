using System.Threading.Tasks;
using NewBackendRequest = SpatialGeneration.Generation.Intent.BackendRequest;

public interface IGenerationBackend
{
    string Name { get; }
    Task<GenerationResult> GenerateAsync(NewBackendRequest request);

    // Performs any one-time / on-demand bootstrapping the backend needs before
    // requests can be served (e.g. launching ComfyUI for the remote backend).
    // Safe to call repeatedly; subsequent calls should be no-ops once ready.
    Task EnsureReadyAsync();
}
