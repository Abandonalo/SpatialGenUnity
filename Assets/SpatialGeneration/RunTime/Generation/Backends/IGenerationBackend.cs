using System.Threading.Tasks;
using NewBackendRequest = SpatialGeneration.Generation.Intent.BackendRequest;

public interface IGenerationBackend
{
    string Name { get; }
    Task<GenerationResult> GenerateAsync(NewBackendRequest request);
}
