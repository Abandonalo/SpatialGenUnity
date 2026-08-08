using UnityEngine;

/// <summary>Resolves the settings asset and the backend it selects.</summary>
public static class BackendRegistry
{
    private const string SettingsResourceName = "SpatialGenerationBackendSettings";

    private static BackendSettings _settings;
    private static IGenerationBackend _current;

    public static BackendSettings Settings
    {
        get
        {
            if (_settings != null)
                return _settings;

            _settings = Resources.Load<BackendSettings>(SettingsResourceName);
            if (_settings == null)
            {
                Debug.LogError(
                    $"Spatial Generation: missing Resources/{SettingsResourceName}.asset. " +
                    "Falling back to the Mock backend until it is restored.");
                _settings = ScriptableObject.CreateInstance<BackendSettings>();
                _settings.backendKind = BackendKind.Mock;
            }

            return _settings;
        }
    }

    public static IGenerationBackend Current => _current ??= CreateBackend(Settings);

    /// <summary>Call after editing the settings asset so the next run picks up the change.</summary>
    public static void Reload()
    {
        _settings = null;
        _current = CreateBackend(Settings);
    }

    private static IGenerationBackend CreateBackend(BackendSettings settings) => settings.backendKind switch
    {
        BackendKind.Mock => new MockGenerationBackend(),
        _ => new RouterGenerationBackend(settings)
    };
}
