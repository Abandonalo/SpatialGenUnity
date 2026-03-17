using UnityEngine;

public static class BackendRegistry
{
    private static BackendSettings _settings;
    private static IGenerationBackend _current;

    public static BackendSettings Settings
    {
        get
        {
            if (_settings == null)
            {
                _settings = Resources.Load<BackendSettings>("SpatialGenerationBackendSettings");
                if (_settings == null)
                {
                    Debug.LogError(
                        "Spatial Generation: Missing Resources/SpatialGenerationBackendSettings.asset. " +
                        "Using a temporary Mock backend until the asset is restored.");
                    _settings = ScriptableObject.CreateInstance<BackendSettings>();
                    _settings.backendKind = BackendKind.Mock;
                }
            }
            return _settings;
        }
    }

    public static IGenerationBackend Current
    {
        get
        {
            if (_current == null) _current = CreateBackend(Settings);
            return _current;
        }
    }

    public static void Reload() => _current = CreateBackend(Settings);

    private static IGenerationBackend CreateBackend(BackendSettings s)
    {
        return s.backendKind switch
        {
            BackendKind.LocalFile => new LocalFileGenerationBackend(s),
            BackendKind.RemoteHttp => new RemoteGenerationBackend(s), // later
            _ => new MockGenerationBackend()
        };
    }
}
