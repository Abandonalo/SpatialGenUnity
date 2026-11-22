using System.IO;
using UnityEngine;
using SpatialGeneration.Authoring;

namespace SpatialGeneration.Agent
{
    /// <summary>
    /// Where the study writes its records: snapshots, interaction logs and agent run folders.
    /// One root so tests can redirect everything away from real participant data.
    /// </summary>
    public static class StudyPaths
    {
        private static string _override;

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>The project's Logs folder, unless a test redirected it.</summary>
        public static string LogsRoot => _override ?? Path.Combine(ProjectRoot, "Logs");

        public static string Snapshots => Path.Combine(LogsRoot, "SpatialGenerationStudy");
        public static string InteractionLogs => Path.Combine(LogsRoot, "SpatialGenerationLogs");
        public static string AgentRuns => Path.Combine(LogsRoot, "SpatialGeneration", "Agent");

        /// <summary>Redirects every study record to <paramref name="logsRoot"/>; null restores the default.</summary>
        internal static void Redirect(string logsRoot)
        {
            _override = logsRoot;
            InteractionLogger.StartNewFile();
        }
    }
}
