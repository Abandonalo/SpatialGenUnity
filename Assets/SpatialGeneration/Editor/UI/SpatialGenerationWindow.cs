using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SpatialGeneration.Agent;

/// <summary>
/// Guides participants through setup, generation, refinement and scene submission.
/// Also hosts the creation and refinement workflow for the ComfyUI pipeline.
/// </summary>
public class SpatialGenerationWindow : EditorWindow
{
    private const string StylePromptKey = "SpatialGeneration.StylePrompt";
    private const string NegativePromptKey = "SpatialGeneration.NegativePrompt";
    private const string RefinementPromptKey = "SpatialGeneration.RefinementPrompt";
    private string _stylePrompt = string.Empty;
    private string _negativePrompt = string.Empty;
    private string _refinementPrompt = string.Empty;
    private Vector2 _scroll;
    private bool _showParts;
    private readonly RefinementSceneInteraction _sceneSelection = new();
    private readonly RefinementModelPreview _modelPreview = new();
    private GUIStyle _titleStyle;
    private GUIStyle _bigButton;

    private static StudySession Session => StudySession.instance;

    [MenuItem("Tools/Spatial Generation")]
    public static void Open()
    {
        SpatialGenerationWindow window = GetWindow<SpatialGenerationWindow>();
        window.titleContent = new GUIContent("Spatial Generation");
        window.minSize = new Vector2(360f, 420f);
        window.Show();
    }

    private void OnEnable()
    {
        _stylePrompt = EditorPrefs.GetString(StylePromptKey, string.Empty);
        _negativePrompt = EditorPrefs.GetString(NegativePromptKey, string.Empty);
        _refinementPrompt = EditorPrefs.GetString(RefinementPromptKey, string.Empty);
        AgentWorkflow.Changed += Repaint;
        SceneView.duringSceneGui += _sceneSelection.Draw;
        Undo.undoRedoPerformed += Repaint;
    }

    private void OnDisable()
    {
        AgentWorkflow.Changed -= Repaint;
        SceneView.duringSceneGui -= _sceneSelection.Draw;
        Undo.undoRedoPerformed -= Repaint;
        _sceneSelection.Cancel();
        _modelPreview.Dispose();
    }

    private void OnInspectorUpdate()
    {
        if (AgentWorkflow.IsBusy || Session.phase == StudyPhase.Refinement)
            Repaint();
    }

    private void OnGUI()
    {
        if (!Session.HasReview || Session.phase != StudyPhase.Refinement) _modelPreview.Dispose();
        EnsureStyles();
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        BackendSettings settings = BackendRegistry.Settings;
        if (settings != null && settings.backendKind == BackendKind.Agent)
        {
            DrawStudyHeader();
            DrawStaleJobWarning();

            switch (Session.phase)
            {
                case StudyPhase.Setup:
                    DrawSetupPhase();
                    break;
                case StudyPhase.Generation:
                    DrawGenerationPhase();
                    break;
                case StudyPhase.Refinement:
                    DrawRefinementPhase();
                    break;
                case StudyPhase.Finished:
                    DrawFinishedPhase();
                    break;
            }

            DrawAgentActivity();
            DrawStudyMessage();
        }
        else
        {
            DrawLegacyPipeline();
        }

        EditorGUILayout.EndScrollView();
    }

    // ================================================================== study flow

    private void DrawStudyHeader()
    {
        string condition = Session.condition == StudyCondition.TextOnly ? "A · text only" : "B · spatial controls";
        string participant = string.IsNullOrWhiteSpace(Session.participantId) ? "no participant" : Session.participantId;
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(PhaseTitle(), _titleStyle);
            GUILayout.FlexibleSpace();
            if (StudyController.DeveloperMode)
            {
                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.6f, 0.2f);
                GUILayout.Label("DEV MODE · no limits", EditorStyles.miniButton);
                GUI.backgroundColor = previous;
            }
        }
        EditorGUILayout.LabelField($"{participant}   ·   condition {condition}" +
                                   (string.IsNullOrWhiteSpace(Session.targetScene) ? string.Empty : $"   ·   target {Session.targetScene}"),
            EditorStyles.miniLabel);
        DrawPhaseSteps();
        EditorGUILayout.Space(6);
    }

    private static string PhaseTitle() => Session.phase switch
    {
        StudyPhase.Generation => "Generation",
        StudyPhase.Refinement => "Refinement",
        StudyPhase.Finished => "Finished",
        _ => "Spatial Generation"
    };

    private static void DrawPhaseSteps()
    {
        string[] steps = { "1 Setup", "2 Generate", "3 Refine", "4 Done" };
        int current = (int)Session.phase;
        using (new EditorGUILayout.HorizontalScope())
        {
            for (int i = 0; i < steps.Length; i++)
            {
                GUIStyle style = i == current ? EditorStyles.boldLabel : EditorStyles.miniLabel;
                Color previous = GUI.contentColor;
                if (i < current)
                    GUI.contentColor = new Color(0.55f, 0.8f, 0.55f);
                GUILayout.Label(steps[i], style);
                GUI.contentColor = previous;
            }

            GUILayout.FlexibleSpace();
        }
    }

    private void DrawSetupPhase()
    {
        EditorGUILayout.HelpBox(
            "Researcher: enter the participant and condition, then start. Starting hides any generated content " +
            "so the condition begins from an empty scene.", MessageType.None);

        EditorGUI.BeginChangeCheck();
        string participant = EditorGUILayout.TextField("Participant ID", Session.participantId);
        string target = EditorGUILayout.TextField("Target scene", Session.targetScene);
        int condition = EditorGUILayout.Popup("Condition", (int)Session.condition,
            new[] { "A — text only", "B — text + spatial controls" });
        if (EditorGUI.EndChangeCheck())
        {
            Session.participantId = participant.Trim();
            Session.targetScene = target.Trim();
            Session.condition = (StudyCondition)condition;
            Session.Persist();
        }

        EditorGUILayout.Space(6);
        if (StudyController.DeveloperMode)
            EditorGUILayout.LabelField("Developer mode: no participant ID needed; logs use \"dev\".", EditorStyles.miniLabel);

        bool needsId = !StudyController.DeveloperMode && string.IsNullOrWhiteSpace(Session.participantId);
        using (new EditorGUI.DisabledScope(AgentWorkflow.IsBusy || needsId))
        {
            if (GUILayout.Button("Start condition", _bigButton))
                StudyController.StartCondition();
        }
    }

    private void DrawGenerationPhase()
    {
        int max = StudyController.MaxRuns;
        bool busy = AgentWorkflow.IsBusy;

        GUILayout.Label("Scene style", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Set once for the whole scene, e.g. \"low poly, warm colours\". Every object uses it.",
            EditorStyles.wordWrappedMiniLabel);
        using (new EditorGUI.DisabledScope(busy))
            PersistentSessionArea(ref Session.stylePrompt, 36f);
        EditorGUILayout.Space(8);

        if (Session.condition == StudyCondition.TextOnly)
        {
            GUILayout.Label("Describe the scene", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Objects, their sizes, where they stand and which way they face.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(busy))
                PersistentSessionArea(ref Session.scenePrompt, 96f);
        }
        else
        {
            DrawProxyOverview(editable: !busy);
        }

        EditorGUILayout.Space(8);
        DrawRunCounter(max);

        using (new EditorGUI.DisabledScope(!StudyController.CanGenerate))
        {
            string ofMax = StudyController.DeveloperMode ? string.Empty : $" of {max}";
            string label = Session.generationRuns == 0
                ? $"Generate (run 1{ofMax})"
                : $"Generate again (run {Session.generationRuns + 1}{ofMax})";
            if (Session.generationRuns < max && GUILayout.Button(busy ? "Generating…" : label, _bigButton))
                StudyController.RunGeneration();
        }

        if (Session.generationRuns >= max)
            EditorGUILayout.HelpBox("All generation runs are used. Choose the version to continue from and accept it.",
                MessageType.Info);

        if (Session.generationRuns == 0)
            return;

        EditorGUILayout.Space(8);
        GUILayout.Label("Continue from", EditorStyles.boldLabel);
        DrawVersionPicker(busy);

        using (new EditorGUI.DisabledScope(!StudyController.CanAccept))
        {
            if (GUILayout.Button($"Accept run {Session.activeRun} and start refinement", _bigButton) &&
                EditorUtility.DisplayDialog("Accept this version?",
                    $"Finish generation and start refining run {Session.activeRun}?", "Accept", "Not yet"))
            {
                StudyController.AcceptActiveVersion();
                GUIUtility.ExitGUI();
            }
        }

        EditorGUILayout.LabelField("Accepting means: worth refining rather than regenerating — not finished.",
            EditorStyles.wordWrappedMiniLabel);
    }

    private static void DrawRunCounter(int max)
    {
        if (StudyController.DeveloperMode)
        {
            EditorGUILayout.LabelField("Generation runs", $"{Session.generationRuns} used (no limit in developer mode)");
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Generation runs", GUILayout.Width(110f));
            for (int i = 1; i <= max; i++)
            {
                bool used = i <= Session.generationRuns;
                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = used ? new Color(0.45f, 0.7f, 1f) : Color.white;
                GUILayout.Box(used ? $"{i} ✓" : i.ToString(), GUILayout.Width(36f), GUILayout.Height(18f));
                GUI.backgroundColor = previous;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"{Session.generationRuns} of {max} used", EditorStyles.miniLabel);
        }
    }

    private static void DrawVersionPicker(bool busy)
    {
        using (new EditorGUI.DisabledScope(busy))
        using (new EditorGUILayout.HorizontalScope())
        {
            for (int run = 1; run <= Session.generationRuns; run++)
            {
                bool active = run == Session.activeRun;
                bool available = active || Session.archivedRuns.Contains(run);
                using (new EditorGUI.DisabledScope(!available))
                {
                    bool pressed = GUILayout.Toggle(active, $"Run {run}", EditorStyles.miniButtonMid, GUILayout.MinWidth(56f));
                    if (pressed && !active)
                        StudyController.ShowVersion(run);
                }
            }

            GUILayout.FlexibleSpace();
        }
    }

    private void DrawRefinementPhase()
    {
        bool busy = AgentWorkflow.IsBusy;

        if (Session.awaitingDecision)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("Look at the change in the Scene view. Keep it, or undo it to return to the scene before the edit.",
                MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Keep change", _bigButton))
                    StudyController.KeepPending();
                if (GUILayout.Button("Undo change", _bigButton))
                    StudyController.UndoPending();
            }

            EditorGUILayout.Space(8);
        }

        if (Session.HasReview)
            DrawPreviewReview(Session.review, busy);
        else
            DrawEditRequest(busy);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField($"Refinements applied: {Session.editsKept}", EditorStyles.miniLabel);
        using (new EditorGUI.DisabledScope(busy))
        {
            if (GUILayout.Button("Submit scene"))
            {
                StudyController.SubmitScene();
                GUIUtility.ExitGUI();
            }
        }
    }

    private void DrawEditRequest(bool busy)
    {
        bool hasTarget = true;
        if (Session.condition == StudyCondition.SpatialControls)
        {
            hasTarget = DrawRegionControls(busy || Session.awaitingDecision);
            EditorGUILayout.Space(4);
            GUILayout.Label("What should change here?", EditorStyles.boldLabel);
        }
        else
        {
            GUILayout.Label("Describe the change", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Name the object you mean and what should be different about it.",
                EditorStyles.wordWrappedMiniLabel);
        }

        using (new EditorGUI.DisabledScope(busy || Session.awaitingDecision))
            PersistentSessionArea(ref Session.editRequest, 56f);

        using (new EditorGUI.DisabledScope(!hasTarget || !StudyController.CanEdit || string.IsNullOrWhiteSpace(Session.editRequest)))
        {
            if (GUILayout.Button("Refine", _bigButton))
            {
                StudyController.SubmitEdit();
                GUIUtility.ExitGUI();
            }
        }
    }

    private void DrawPreviewReview(RefinementReview review, bool busy)
    {
        EditorGUILayout.LabelField("Refinement preview", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(review.request, EditorStyles.wordWrappedLabel);
        bool hasIdeas = review.ideas.Count > 0;
        if (!hasIdeas && busy)
        {
            EditorGUILayout.LabelField("Generating your preview…", EditorStyles.miniLabel);
            return;
        }
        if (hasIdeas)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(review.selected == 0))
                    if (GUILayout.Button("Previous", GUILayout.Width(72))) StudyController.ShowIdea(review.selected - 1);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Idea {review.selected + 1} of {review.ideas.Count}");
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(review.selected == review.ideas.Count - 1))
                    if (GUILayout.Button("Next", GUILayout.Width(58))) StudyController.ShowIdea(review.selected + 1);
            }
            _modelPreview.Draw(review);
            EditorGUILayout.LabelField("Drag to orbit · Scroll to zoom", EditorStyles.miniLabel);
        }
        using (new EditorGUI.DisabledScope(busy || AgentJobState.instance.HasRunningJob))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(busy ? "Generating…" : hasIdeas ? "Regenerate" : "Retry"))
                {
                    StudyController.GenerateIdea();
                    GUIUtility.ExitGUI();
                }
                using (new EditorGUI.DisabledScope(!hasIdeas))
                    if (GUILayout.Button("Apply this idea", _bigButton))
                    {
                        StudyController.ApplyIdea();
                        GUIUtility.ExitGUI();
                    }
            }
            if (GUILayout.Button("Discard previews"))
            {
                StudyController.DiscardIdeas();
                GUIUtility.ExitGUI();
            }
        }
    }

    private bool DrawRegionControls(bool disabled)
    {
        GUILayout.Label("Refinement selection", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Click a part, Shift-click to add or remove, or drag a box. Drag its handles to adjust. W moves the box, E rotates it.",
            EditorStyles.wordWrappedMiniLabel);
        var manager = FindFirstObjectByType<RegionSelectionManager>();
        if (manager == null)
        {
            using (new EditorGUI.DisabledScope(disabled))
                if (GUILayout.Button("Set up refinement")) StudyController.EnsureRefinementRig(true);
            return false;
        }

        RegionSelection region = manager.CurrentSelection;
        List<AgentAsset> assets = AgentSceneOps.AllAssets();
        AgentAsset owner = assets.Find(a => a.assetId == region?.assetId);
        bool valid = region?.partScoped == true && (string.IsNullOrEmpty(region.assetId) || owner != null);
        string summary = "No selection";
        if (valid)
        {
            int whole = 0, partial = 0, outside = 0;
            foreach (AgentPart part in AgentSceneOps.PartsOf(owner))
            {
                if (region.wholeParts.Contains(part.partId)) whole++;
                if (region.partialParts.Contains(part.partId)) partial++;
                if (region.outsideParts.Contains(part.partId)) outside++;
            }
            var counts = new List<string>();
            if (whole > 0) counts.Add($"{whole} whole");
            if (partial > 0) counts.Add($"{partial} partial");
            if (outside > 0) counts.Add($"{outside} outside box");
            summary = (owner != null ? owner.label + " · " : "") +
                      (counts.Count > 0 ? string.Join(", ", counts) : "Empty region");
            if (whole + partial == 0 && region.wholeParts.Count + region.partialParts.Count + region.outsideParts.Count > 0)
                valid = false;
        }
        EditorGUILayout.LabelField(summary, EditorStyles.boldLabel);
        _showParts = EditorGUILayout.Foldout(_showParts, "Parts", true);
        if (_showParts)
        {
            using (new EditorGUI.DisabledScope(disabled))
            foreach (AgentAsset asset in assets)
            {
                EditorGUILayout.LabelField(asset.label, EditorStyles.miniBoldLabel);
                foreach (AgentPart part in AgentSceneOps.PartsOf(asset))
                {
                    bool selected = owner == asset && RefinementPartSelection.Has(region, part.partId);
                    string name = part.name;
                    string suffix = "_" + part.partId;
                    if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name.Substring(0, name.Length - suffix.Length);
                    if (selected) name += region.wholeParts.Contains(part.partId) ? "  ·  Whole" :
                        region.partialParts.Contains(part.partId) ? "  ·  Partial" : "  ·  Outside box";
                    if (EditorGUILayout.ToggleLeft(name, selected) == selected) continue;
                    var picked = RefinementPartSelection.Pick(region, asset, part, owner == asset);
                    RefinementPartSelection.Commit(manager, picked, "Select refinement part");
                    GUIUtility.ExitGUI();
                }
            }
        }
        return valid;
    }

    private void DrawFinishedPhase()
    {
        EditorGUILayout.HelpBox("This condition is finished.\n" +
                                $"Generation runs: {Session.generationRuns} (accepted run {Session.acceptedRun}). " +
                                $"Edits: {Session.editsSubmitted} submitted, {Session.editsKept} kept, {Session.editsUndone} undone.",
            MessageType.Info);

        if (GUILayout.Button("Researcher: set up the next condition"))
        {
            StudyController.ResetSession();
            GUIUtility.ExitGUI();
        }
    }

    private void DrawAgentActivity()
    {
        if (!AgentWorkflow.IsBusy)
            return;

        EditorGUILayout.Space(6);
        string status = string.IsNullOrEmpty(AgentWorkflow.Status) ? "The agent is working…" : AgentWorkflow.Status;
        int dots = (int)(EditorApplication.timeSinceStartup * 2d) % 4;
        EditorGUILayout.HelpBox($"{status}{new string('.', dots)}", MessageType.None);
    }

    private static void DrawStudyMessage()
    {
        if (string.IsNullOrEmpty(StudyController.LastMessage) || AgentWorkflow.IsBusy)
            return;
        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(StudyController.LastMessage, StudyController.LastMessageType);
    }

    private static void DrawStaleJobWarning()
    {
        if (!AgentJobState.instance.HasRunningJob || AgentWorkflow.IsBusy)
            return;

        EditorGUILayout.HelpBox(
            "An agent run was interrupted (for example by a script reload). Its changes stay in the scene; " +
            "clear the run to continue.", MessageType.Warning);
        if (GUILayout.Button("Clear interrupted run"))
            AgentWorkflow.ClearStaleJob();
    }

    private static GUIStyle _wrapArea;

    /// <summary>Wrapped text area bound to a session field; saved as soon as it changes.</summary>
    private static void PersistentSessionArea(ref string field, float height)
    {
        _wrapArea ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true };
        EditorGUI.BeginChangeCheck();
        string updated = EditorGUILayout.TextArea(field ?? string.Empty, _wrapArea, GUILayout.MinHeight(height));
        if (!EditorGUI.EndChangeCheck())
            return;

        field = updated;
        Session.Persist();
    }

    // ================================================================== proxy overview

    private static void DrawProxyOverview(bool editable)
    {
        List<SpatialProxy> proxies = AllProxies();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label($"Spatial proxies ({proxies.Count})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!editable))
            {
                if (GUILayout.Button("+ Add proxy", GUILayout.Width(96f)))
                    SpatialProxyFactory.CreateProxy();
            }
        }

        if (proxies.Count == 0)
        {
            EditorGUILayout.HelpBox("Add a proxy for each object, then move, rotate and resize it in the Scene view. " +
                                    "The arrow on a selected proxy marks its front.", MessageType.Info);
            return;
        }

        Dictionary<string, AgentAsset> assets = AssetsByProxy();
        foreach (SpatialProxy proxy in proxies)
            DrawProxyRow(proxy, assets, editable);
    }

    private static void DrawProxyRow(SpatialProxy proxy, Dictionary<string, AgentAsset> assets, bool editable)
    {
        bool selected = Selection.activeGameObject == proxy.gameObject;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!editable))
                {
                    EditorGUI.BeginChangeCheck();
                    string label = EditorGUILayout.TextField(proxy.label, GUILayout.MinWidth(80f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(proxy, "Edit Proxy Label");
                        proxy.label = label;
                        EditorUtility.SetDirty(proxy);
                        LogProxy(proxy, "proxy_label_change");
                    }
                }

                Vector3 size = Vector3.Scale(proxy.size, proxy.transform.lossyScale);
                GUILayout.Label($"{size.x:0.##}×{size.y:0.##}×{size.z:0.##} m", EditorStyles.miniLabel, GUILayout.Width(96f));

                if (GUILayout.Button(selected ? "Selected" : "Select", EditorStyles.miniButton, GUILayout.Width(62f)))
                {
                    Selection.activeGameObject = proxy.gameObject;
                    SceneView.lastActiveSceneView?.FrameSelected();
                }
            }

            using (new EditorGUI.DisabledScope(!editable))
            {
                EditorGUI.BeginChangeCheck();
                string prompt = EditorGUILayout.TextField(proxy.assetPrompt);
                if (string.IsNullOrEmpty(proxy.assetPrompt))
                {
                    Rect last = GUILayoutUtility.GetLastRect();
                    GUI.Label(new Rect(last.x + 4f, last.y, last.width, last.height), "What should this become?",
                        EditorStyles.centeredGreyMiniLabel);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(proxy, "Edit Proxy Prompt");
                    proxy.assetPrompt = prompt;
                    EditorUtility.SetDirty(proxy);
                    LogProxy(proxy, "proxy_asset_prompt_change");
                }
            }

            string status = proxy.role != SpatialProxyRole.Occupy
                ? $"{proxy.role} proxy — not generated"
                : assets.TryGetValue(proxy.ProxyId ?? string.Empty, out AgentAsset asset)
                    ? $"Generated: {AgentSceneOps.PartsOf(asset).Count} parts"
                    : "Not generated yet";
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);
        }
    }

    private static List<SpatialProxy> AllProxies()
    {
        var proxies = new List<SpatialProxy>(FindObjectsByType<SpatialProxy>(FindObjectsSortMode.None));
        proxies.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return proxies;
    }

    private static Dictionary<string, AgentAsset> AssetsByProxy()
    {
        var map = new Dictionary<string, AgentAsset>(StringComparer.Ordinal);
        foreach (AgentAsset asset in AgentSceneOps.AllAssets())
        {
            if (!string.IsNullOrEmpty(asset.proxyId))
                map[asset.proxyId] = asset;
        }

        return map;
    }

    private static void LogProxy(SpatialProxy proxy, string type)
    {
        InteractionLogger.Log(new InteractionEvent
        {
            type = type,
            proxy_id = proxy.ProxyId,
            position = proxy.transform.position,
            size = proxy.size,
            role = proxy.role.ToString(),
            shape = proxy.Shape.ToString(),
            label = proxy.label,
            strength = proxy.strength,
            priority = proxy.priority,
            extra = "via_overview"
        });
    }

    // ================================================================== legacy ComfyUI pipeline

    private void DrawLegacyPipeline()
    {
        GUILayout.Label("Spatial Generation (ComfyUI pipeline)", _titleStyle);
        EditorGUILayout.HelpBox(
            "To run a study condition, select the Agent backend in Tools > Spatial Generation Researcher Tools.",
            MessageType.None);
        EditorGUILayout.Space(6);
        DrawCreationSection();
        EditorGUILayout.Space(10);
        DrawRefinementSection();
    }

    private void DrawCreationSection()
    {
        GUILayout.Label("Creation", EditorStyles.boldLabel);

        _stylePrompt = DrawPersistentTextArea("Style prompt", _stylePrompt, StylePromptKey);
        EditorGUILayout.HelpBox("Appended to every proxy's own prompt so all assets share one style.", MessageType.None);

        _negativePrompt = DrawPersistentTextArea("Negative prompt", _negativePrompt, NegativePromptKey);

        DrawProxyOverview(editable: true);

        if (!GUILayout.Button("Generate"))
            return;

        GenerationRunner.Generate(_stylePrompt, _negativePrompt);
        InteractionLogger.Log(new InteractionEvent
        {
            type = "generate",
            extra = $"style_prompt={_stylePrompt}, negative_prompt={_negativePrompt}"
        });
    }

    private void DrawRefinementSection()
    {
        GUILayout.Label("Local Region Refinement", EditorStyles.boldLabel);

        RefinementController controller = FindFirstObjectByType<RefinementController>();
        if (controller == null)
        {
            EditorGUILayout.HelpBox(
                "Create the refinement rig to select a region and run local edits.", MessageType.Info);
            if (GUILayout.Button("Setup Refinement Rig"))
                Selection.activeGameObject = StudyController.EnsureRefinementRig(resetSelection: true).gameObject;
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

    // ================================================================== shared

    private void EnsureStyles()
    {
        _titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
        _bigButton ??= new GUIStyle(GUI.skin.button) { fixedHeight = 28f, fontStyle = FontStyle.Bold };
    }
}
