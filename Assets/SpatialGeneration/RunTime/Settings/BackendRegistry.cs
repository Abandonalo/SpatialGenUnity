using System;
using UnityEngine;

namespace SpatialGeneration
{
    /// <summary>
    /// Resolves the settings asset shared by every backend. The legacy router pipeline builds its
    /// generation backend from these settings (SpatialGeneration.Generation.Backends.GenerationBackends).
    /// </summary>
    public static class BackendRegistry
    {
        private const string SettingsResourceName = "SpatialGenerationBackendSettings";

        private static BackendSettings _settings;

        /// <summary>Raised after <see cref="Reload"/>, so cached backends rebuild from the new settings.</summary>
        public static event Action Reloaded;

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

        /// <summary>Call after editing the settings asset so the next run picks up the change.</summary>
        public static void Reload()
        {
            _settings = null;
            Reloaded?.Invoke();
        }
    }
}
