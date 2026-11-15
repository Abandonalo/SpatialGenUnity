using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialGeneration.Agent
{
    /// <summary>Study condition: A is text only, B adds spatial proxies and 3D region selection.</summary>
    public enum StudyCondition
    {
        TextOnly = 0,
        SpatialControls = 1
    }

    public enum AgentJobMode
    {
        /// <summary>A generation run: (re)builds every asset of the scene.</summary>
        Generate = 0,

        /// <summary>A refinement edit: one requested change to the accepted scene.</summary>
        Edit = 1
    }

    /// <summary>
    /// The run the agent is currently allowed to act on. The MCP tools read it to decide what
    /// the agent may touch, so it is persisted (see <see cref="AgentJobState"/>) and survives
    /// a domain reload in the middle of a run.
    /// </summary>
    [Serializable]
    public class AgentJob
    {
        public string jobId;
        public AgentJobMode mode;
        public StudyCondition condition;
        public bool running;
        public string previewToken;
        public int ideaNumber;
        public string previousIdeas;

        /// <summary>Scene description (generation, condition A) or the requested change (edit).</summary>
        public string request;
        public string stylePrompt;

        /// <summary>Edit region in world space; condition B edits only.</summary>
        public bool hasRegion;
        public Vector3 regionCenter;
        public Vector3 regionSize;
        public Quaternion regionRotation = Quaternion.identity;
        public RegionSelection selection;
        public bool HasPartScope => hasRegion && selection != null && selection.partScoped;
        public List<string> addedPartIds = new();
        public List<string> materializedSlices = new();

        /// <summary>Assets prepared for this run (condition B generation: one per proxy).</summary>
        public List<string> assetIds = new();

        /// <summary>Condition A edit: the one asset this edit may rebuild, fixed on first change.</summary>
        public string lockedAssetId;

        public int undoGroup = -1;
        public string artifactDir;
        public string startedUtc;

        public bool finishedByAgent;
        public string summary;

        public int toolCalls;
        public int rejectedCalls;
        public int partsAdded;
        public int partsUpdated;
        public int partsRemoved;
        public int assetsCreated;
    }
}
