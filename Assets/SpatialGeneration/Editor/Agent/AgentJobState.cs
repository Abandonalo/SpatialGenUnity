using UnityEditor;

namespace SpatialGeneration.Agent
{
    /// <summary>Persists the active <see cref="AgentJob"/> under Library/ across domain reloads.</summary>
    [FilePath("Library/SpatialGenerationAgentJob.asset", FilePathAttribute.Location.ProjectFolder)]
    public class AgentJobState : ScriptableSingleton<AgentJobState>
    {
        public AgentJob current;

        public bool HasRunningJob => current != null && current.running;

        public void Persist() => Save(true);
    }
}
