using System.Threading.Tasks;

public class RemoteGenerationBackend : IGenerationBackend
{
    private readonly BackendSettings _settings;
    public string Name => "RemoteHttp";

    public RemoteGenerationBackend(BackendSettings settings)
    {
        _settings = settings;
    }

    public Task<GenerationResult> GenerateAsync(BackendRequest request)
    {
        // Part 3: Stub for now so project compiles.
        // Later: UnityWebRequest POST to _settings.remoteUrl and parse response.
        throw new System.NotImplementedException("RemoteGenerationBackend not implemented yet. Use LocalFile backend for now.");
    }
}
