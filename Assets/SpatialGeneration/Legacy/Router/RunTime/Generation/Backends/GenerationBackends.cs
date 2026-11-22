namespace SpatialGeneration.Generation.Backends
{
    /// <summary>The generation backend the legacy router pipeline runs, built from the shared settings.</summary>
    public static class GenerationBackends
    {
        private static IGenerationBackend _current;

        static GenerationBackends()
        {
            BackendRegistry.Reloaded += () => _current = null;
        }

        public static IGenerationBackend Current => _current ??= Create(BackendRegistry.Settings);

        private static IGenerationBackend Create(BackendSettings settings) => settings.backendKind switch
        {
            BackendKind.Mock => new MockGenerationBackend(),
            BackendKind.Agent => new EditorDrivenBackend(),
            _ => new RouterGenerationBackend(settings)
        };
    }
}
