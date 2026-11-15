using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace SpatialGeneration.Agent
{
    public enum StudyPhase
    {
        Setup = 0,
        Generation = 1,
        Refinement = 2,
        Finished = 3
    }

    /// <summary>
    /// State of the condition a participant is currently working through: phase, generation
    /// runs used, accepted version, phase timestamps and the ideas under review.
    /// Stored in UserSettings/ so it survives domain reloads and editor restarts.
    /// </summary>
    [FilePath("UserSettings/SpatialGenerationStudySession.asset", FilePathAttribute.Location.ProjectFolder)]
    public class StudySession : ScriptableSingleton<StudySession>
    {
        public string participantId = string.Empty;
        public string targetScene = string.Empty;
        public StudyCondition condition = StudyCondition.SpatialControls;
        public StudyPhase phase = StudyPhase.Setup;

        /// <summary>Scene description typed in the text-only condition.</summary>
        public string scenePrompt = string.Empty;
        public string stylePrompt = string.Empty;
        public string editRequest = string.Empty;

        public int generationRuns;

        /// <summary>Run number shown as GeneratedContent; 0 before the first run.</summary>
        public int activeRun;
        public List<int> archivedRuns = new();
        public int acceptedRun = -1;

        public string conditionStartUtc = string.Empty;
        public string acceptedUtc = string.Empty;
        public string refinementStartUtc = string.Empty;
        public string finishedUtc = string.Empty;
        public string finishReason = string.Empty;

        public bool awaitingDecision;
        public RefinementReview review;
        public bool HasReview => !string.IsNullOrEmpty(review?.id);
        public int pendingUndoGroup = -1;
        public string pendingJobId = string.Empty;

        public int editsSubmitted;
        public int editsKept;
        public int editsUndone;
        public int editsFailed;

        public void Persist() => Save(true);

        public void DiscardReview()
        {
            if (!HasReview) return;
            if (review.ideas.Count == 0) editsFailed++;
            else editsUndone++;
            review = null;
        }

        public static string NowUtc() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        public static double SecondsSince(string utc)
        {
            if (string.IsNullOrEmpty(utc) ||
                !DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime start))
                return 0d;
            return (DateTime.UtcNow - start).TotalSeconds;
        }

        /// <summary>Clears everything but the participant and condition for a fresh start.</summary>
        public void ResetProgress()
        {
            phase = StudyPhase.Setup;
            scenePrompt = string.Empty;
            editRequest = string.Empty;
            generationRuns = 0;
            activeRun = 0;
            archivedRuns.Clear();
            acceptedRun = -1;
            conditionStartUtc = string.Empty;
            acceptedUtc = string.Empty;
            refinementStartUtc = string.Empty;
            finishedUtc = string.Empty;
            finishReason = string.Empty;
            awaitingDecision = false;
            review = null;
            pendingUndoGroup = -1;
            pendingJobId = string.Empty;
            editsSubmitted = editsKept = editsUndone = editsFailed = 0;
        }

        /// <summary>Short context string added to every study log event.</summary>
        public string LogContext =>
            $"participant={participantId}; condition={(condition == StudyCondition.TextOnly ? "A" : "B")}; " +
            $"target={targetScene}; phase={phase}";
    }
}
