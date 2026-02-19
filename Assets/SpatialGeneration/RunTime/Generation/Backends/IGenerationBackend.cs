using System.Threading.Tasks;

public interface IGenerationBackend
{
    string Name { get; }
    Task<GenerationResult> GenerateAsync(BackendRequest request);
}
