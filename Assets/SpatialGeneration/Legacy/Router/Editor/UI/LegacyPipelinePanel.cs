using SpatialGeneration.Agent;
using SpatialGeneration.Authoring;
using SpatialGeneration.Generation.Refinement;
using SpatialGeneration.UI;
using UnityEditor;
using UnityEngine;

namespace SpatialGeneration.Generation.Authoring
{
    /// <summary>
    /// The creation and local-refinement panel of the ComfyUI pipeline, shown in the Spatial
    /// Generation window when the backend is Router or Mock.
    /// </summary>
    [InitializeOnLoad]
    internal static class LegacyPipelinePanel
    {
        private const string StylePromptKey = "SpatialGeneration.StylePrompt";
        private const string NegativePromptKey = "SpatialGeneration.NegativePrompt";
        private const string RefinementPromptKey = "SpatialGeneration.RefinementPrompt";
        private static string _stylePrompt = EditorPrefs.GetString(StylePromptKey, string.Empty);
        private static string _negativePrompt = EditorPrefs.GetString(NegativePromptKey, string.Empty);
        private static string _refinementPrompt = EditorPrefs.GetString(RefinementPromptKey, string.Empty);
        private static GUIStyle _titleStyle;

        private static GUIStyle TitleStyle => _titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };

        static LegacyPipelinePanel()
        {
            SpatialGenerationWindow.NonAgentBackendGUI = Draw;
        }

        /// <summary>The selection rig plus the multi-view RefinementController this pipeline needs.</summary>
        private static RefinementController EnsureRig()
        {
            RegionSelectionManager manager = StudyController.EnsureRefinementRig(resetSelection: true);
            RefinementController controller = manager.GetComponent<RefinementController>();
            if (controller == null)
                controller = Undo.AddComponent<RefinementController>(manager.gameObject);
            controller.selectionManager = manager;
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static void Draw()
        {
            GUILayout.Label("Spatial Generation (ComfyUI pipeline)", TitleStyle);
            EditorGUILayout.HelpBox(
                "To run a study condition, select the Agent backend in Tools > Spatial Generation Researcher Tools.",
                MessageType.None);
            EditorGUILayout.Space(6);
            DrawCreationSection();
            EditorGUILayout.Space(10);
            DrawRefinementSection();
        }

        private static void DrawCreationSection()
        {
            GUILayout.Label("Creation", EditorStyles.boldLabel);

            _stylePrompt = DrawPersistentTextArea("Style prompt", _stylePrompt, StylePromptKey);
            EditorGUILayout.HelpBox("Appended to every proxy's own prompt so all assets share one style.", MessageType.None);

            _negativePrompt = DrawPersistentTextArea("Negative prompt", _negativePrompt, NegativePromptKey);

            SpatialGenerationWindow.DrawProxyOverview(editable: true);

            if (!GUILayout.Button("Generate"))
                return;

            GenerationRunner.Generate(_stylePrompt, _negativePrompt);
            InteractionLogger.Log(new InteractionEvent
            {
                type = "generate",
                extra = $"style_prompt={_stylePrompt}, negative_prompt={_negativePrompt}"
            });
        }

        private static void DrawRefinementSection()
        {
            GUILayout.Label("Local Region Refinement", EditorStyles.boldLabel);

            RefinementController controller = Object.FindFirstObjectByType<RefinementController>();
            if (controller == null)
            {
                EditorGUILayout.HelpBox(
                    "Create the refinement rig to select a region and run local edits.", MessageType.Info);
                if (GUILayout.Button("Setup Refinement Rig"))
                    Selection.activeGameObject = EnsureRig().gameObject;
                return;
            }

            RegionSelectionManager selectionManager = controller.selectionManager != null
                ? controller.selectionManager
                : controller.GetComponent<RegionSelectionManager>();

            if (selectionManager == null)
            {
                EditorGUILayout.HelpBox("The rig is missing its RegionSelectionManager. Recreate it.", MessageType.Error);
                return;
            }

            controller.selectionManager = selectionManager;
            if (selectionManager.CurrentSelection?.partScoped == true)
                EditorGUILayout.HelpBox("Reset Selection to place a box on the imported mesh.", MessageType.Info);
            else DrawSelectionSummary(selectionManager);

            if (GUILayout.Button("Reset Selection"))
            {
                Undo.RecordObject(selectionManager, "Reset Region Selection");
                selectionManager.ResetToDefault();
                EditorUtility.SetDirty(selectionManager);
                SceneView.RepaintAll();
            }

            _refinementPrompt = DrawPersistentTextArea("What should change here?", _refinementPrompt, RefinementPromptKey);

            bool canRefine = selectionManager.CurrentSelection != null && !selectionManager.CurrentSelection.partScoped
                             && !controller.IsRunning
                             && !string.IsNullOrWhiteSpace(_refinementPrompt);

            using (new EditorGUI.DisabledScope(!canRefine))
            {
                if (GUILayout.Button(controller.IsRunning ? "Refining…" : "Refine Selected Region"))
                {
                    controller.RunRefinement(_refinementPrompt);
                    InteractionLogger.Log(new InteractionEvent
                    {
                        type = "refine_region",
                        extra = $"selection={selectionManager.CurrentSelection?.selectionId}, prompt={_refinementPrompt}"
                    });
                }
            }

            if (!controller.IsRunning)
                return;

            if (GUILayout.Button("Clear busy flag"))
            {
                Undo.RecordObject(controller, "Clear refining status");
                controller.ClearRunningState();
                EditorUtility.SetDirty(controller);
            }

            EditorGUILayout.HelpBox(
                "Use this if a run was interrupted and the button is still stuck on Refining.", MessageType.None);
        }

        private static void DrawSelectionSummary(RegionSelectionManager selectionManager)
        {
            RegionSelection selection = selectionManager.CurrentSelection;
            if (selection == null)
            {
                EditorGUILayout.HelpBox("No region selected. Use Reset, then drag the handles in the Scene view.",
                    MessageType.Info);
                return;
            }

            string summary = $"Box {selection.size.x:0.##}×{selection.size.y:0.##}×{selection.size.z:0.##} m at " +
                             $"({selection.center.x:0.##}, {selection.center.y:0.##}, {selection.center.z:0.##})";

            if (RegionSelectionManager.TryGetGeneratedContentBounds(out Bounds meshBounds) &&
                RegionSelectionManager.SelectionSpansMostOfMesh(selection, meshBounds))
            {
                EditorGUILayout.HelpBox(
                    $"{summary}\n\nThe box covers most of the scene on several axes, so the edit will not be local. " +
                    "Shrink it around the part you want to change.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.HelpBox(summary, MessageType.None);
        }

        private static string DrawPersistentTextArea(string label, string value, string prefsKey)
        {
            GUILayout.Label(label, EditorStyles.label);
            EditorGUI.BeginChangeCheck();
            string updated = EditorGUILayout.TextArea(value, GUILayout.MinHeight(48f));
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetString(prefsKey, updated);
            return updated;
        }
    }
}
