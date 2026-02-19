using System.Threading.Tasks;

public static class GenerationPipeline
{
    public static async Task<GenerationResult> GenerateAsync(SceneIntent intent)
    {
        BackendRequest request = ConstraintTranslator.Build(intent);
        IGenerationBackend backend = BackendRegistry.Current;
        return await backend.GenerateAsync(request);
    }
}
