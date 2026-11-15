using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SpatialGeneration.Agent;
using SpatialGeneration.Generation.Backends;
using UnityEditor;
using UnityEngine;

public class SpatialGenerationResearcherWindow : EditorWindow
{
    private static readonly string[] Tabs = { "Researcher settings", "Utilities" };
    private static readonly GUIContent GenerationModelLabel = new(
        "Asset Generation Model",
        "Creates a complete 3D asset for a spatial proxy.");
    private static readonly GUIContent RefinementLifterLabel = new(
        "Region Refinement Lifter",
        "Reconstructs only the selected refinement region. Hunyuan uses four views; " +
        "TripoSR lifts the refined Front view.");

    private int _tab;
    private Vector2 _scroll;
    private string _setupCheckResult = string.Empty;
    private MessageType _setupCheckType = MessageType.None;

    [MenuItem("Tools/Spatial Generation Researcher Tools")]
    private static void Open()
    {
        var window = GetWindow<SpatialGenerationResearcherWindow>("Researcher Tools");
        window.minSize = new Vector2(360f, 300f);
        window.Show();
    }

    private void OnEnable() => AgentWorkflow.Changed += Repaint;
    private void OnDisable() => AgentWorkflow.Changed -= Repaint;

    private void OnGUI()
    {
        int tab = GUILayout.Toolbar(_tab, Tabs);
        if (tab != _tab)
        {
            _tab = tab;
            _scroll = Vector2.zero;
            GUI.FocusControl(null);
        }

        using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
        {
            _scroll = scroll.scrollPosition;
            if (_tab == 0)
                DrawResearcherSettings(BackendRegistry.Settings);
            else
                DrawUtilities();
        }
    }

    private void DrawResearcherSettings(BackendSettings settings)
    {
        using (new EditorGUI.DisabledScope(AgentWorkflow.IsBusy))
        {
            EditorGUI.BeginChangeCheck();
            BackendKind kind = (BackendKind)EditorGUILayout.EnumPopup("Backend", settings.backendKind);
            if (EditorGUI.EndChangeCheck())
            {
                settings.backendKind = kind;
                Persist(settings);
            }

            if (settings.backendKind == BackendKind.Agent)
                DrawAgentSettings(settings);
            else if (settings.backendKind == BackendKind.Router)
                DrawRouterSettings(settings);
        }
    }

    private void DrawAgentSettings(BackendSettings settings)
    {
        EditorGUI.BeginChangeCheck();
        AgentCli cli = (AgentCli)EditorGUILayout.EnumPopup("Agent CLI", settings.agentCli);
        string exe = EditorGUILayout.TextField(
            new GUIContent("Agent executable", "Empty = search the usual install locations."),
            cli == AgentCli.ClaudeCode ? settings.claudeExecutable : settings.codexExecutable);
        string model;
        using (new EditorGUILayout.HorizontalScope())
        {
            model = EditorGUILayout.TextField(
                new GUIContent("Model", "Empty = the CLI's default. Use a full model ID and freeze it for the study."),
                settings.agentModel);
            if (GUILayout.Button(new GUIContent("▾", "Choose a model"), EditorStyles.miniButton, GUILayout.Width(22f)))
                ShowModelMenu(settings, cli);
        }

        int effortIndex = Mathf.Max(0, Array.IndexOf(EffortValues, settings.agentEffort?.Trim() ?? string.Empty));
        effortIndex = EditorGUILayout.Popup(
            new GUIContent("Effort", "Reasoning effort per run. Higher is slower and uses more of your plan."),
            effortIndex, EffortLabels);
        string url = EditorGUILayout.DelayedTextField("MCP server URL", settings.mcpServerUrl);
        int timeout = EditorGUILayout.IntField("Timeout per run (s)", settings.agentTimeoutSeconds);
        int turns = EditorGUILayout.IntField("Max turns (Claude)", settings.agentMaxTurns);
        int parts = EditorGUILayout.IntField("Max parts per asset", settings.maxPartsPerAsset);
        int runs = EditorGUILayout.IntSlider("Generation runs", settings.maxGenerationRuns, 1, 10);
        if (EditorGUI.EndChangeCheck())
        {
            settings.agentCli = cli;
            if (cli == AgentCli.ClaudeCode)
                settings.claudeExecutable = exe.Trim();
            else
                settings.codexExecutable = exe.Trim();
            settings.agentModel = model.Trim();
            settings.agentEffort = EffortValues[effortIndex];
            // Keep the last working address if the field is cleared or holds stray typing.
            if (AgentProcessRunner.IsValidMcpUrl(url))
                settings.mcpServerUrl = url.Trim();
            else if (string.IsNullOrWhiteSpace(url))
                settings.mcpServerUrl = AgentProcessRunner.DefaultMcpUrl;
            settings.agentTimeoutSeconds = Mathf.Max(30, timeout);
            settings.agentMaxTurns = Mathf.Max(5, turns);
            settings.maxPartsPerAsset = Mathf.Clamp(parts, 4, 400);
            settings.maxGenerationRuns = runs;
            Persist(settings);
        }

        bool developerMode = EditorGUILayout.ToggleLeft(
            new GUIContent("Developer test mode (unlimited generation runs, optional ID)",
                "Only on this machine. Turn it off before running participants; study logs record it."),
            StudyController.DeveloperMode);
        if (developerMode != StudyController.DeveloperMode)
            StudyController.DeveloperMode = developerMode;

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Check setup"))
                _ = CheckAgentSetupAsync(settings);
            if (GUILayout.Button("Open MCP for Unity") &&
                !EditorApplication.ExecuteMenuItem("Window/MCP for Unity/Toggle MCP Window"))
                Debug.LogWarning("Spatial Generation: MCP for Unity is not installed or its menu moved.");
        }

        if (!string.IsNullOrEmpty(_setupCheckResult))
            EditorGUILayout.HelpBox(_setupCheckResult, _setupCheckType);

        EditorGUILayout.Space(4);
        using (new EditorGUI.DisabledScope(AgentWorkflow.IsBusy || StudySession.instance.phase == StudyPhase.Setup))
        {
            if (GUILayout.Button("Reset study session (back to setup)") &&
                EditorUtility.DisplayDialog("Reset session", "Return to setup? Generated content stays in the scene.", "Reset", "Cancel"))
            {
                StudyController.ResetSession();
                GUIUtility.ExitGUI();
            }
        }
    }

    private static readonly string[] EffortValues = { "", "low", "medium", "high", "xhigh", "max" };
    private static readonly GUIContent[] EffortLabels =
    {
        new("CLI default"), new("low"), new("medium"), new("high"), new("xhigh"), new("max")
    };

    private static readonly (string id, string label)[] ClaudeModels =
    {
        ("", "CLI default"),
        ("claude-opus-5-5", "Claude Opus 5.5"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5"),
        ("claude-haiku-5-5", "Claude Haiku 5.5"),
        ("opus", "Alias: opus (latest Opus, changes over time)"),
        ("sonnet", "Alias: sonnet (latest Sonnet, changes over time)")
    };

    private static void ShowModelMenu(BackendSettings settings, AgentCli cli)
    {
        var menu = new GenericMenu();
        if (cli == AgentCli.ClaudeCode)
        {
            foreach ((string id, string label) in ClaudeModels)
            {
                string chosen = id;
                menu.AddItem(new GUIContent(label), (settings.agentModel ?? string.Empty) == chosen, () =>
                {
                    settings.agentModel = chosen;
                    Persist(settings);
                });
            }
        }
        else
        {
            menu.AddItem(new GUIContent("CLI default"), string.IsNullOrEmpty(settings.agentModel), () =>
            {
                settings.agentModel = string.Empty;
                Persist(settings);
            });
            menu.AddDisabledItem(new GUIContent("Type a Codex model name (see `codex --help` / your config)"));
        }

        menu.ShowAsContext();
    }

    private async Task CheckAgentSetupAsync(BackendSettings settings)
    {
        _setupCheckResult = "Checking…";
        _setupCheckType = MessageType.None;
        Repaint();

        var lines = new List<string>();
        bool ok = true;

        string exe = AgentProcessRunner.ResolveExecutable(settings, out string problem);
        if (exe != null)
        {
            lines.Add($"✓ {settings.agentCli}: {exe}");
        }
        else
        {
            ok = false;
            lines.Add($"✗ {problem}");
        }

        if (await AgentProcessRunner.IsMcpServerReachableAsync(settings.mcpServerUrl))
        {
            lines.Add($"✓ MCP server answers at {settings.mcpServerUrl}");
        }
        else
        {
            ok = false;
            lines.Add($"✗ No MCP server at {settings.mcpServerUrl}. Open MCP for Unity and start the server.");
        }

#if SPATIALGEN_MCP
        lines.Add("✓ Spatial Generation tools are compiled for MCP for Unity.");
#else
        ok = false;
        lines.Add("✗ MCP for Unity package not found, so the Spatial Generation tools are not compiled.");
#endif

        _setupCheckResult = string.Join("\n", lines);
        _setupCheckType = ok ? MessageType.Info : MessageType.Warning;
        Repaint();
    }

    private static void DrawRouterSettings(BackendSettings settings)
    {
        EditorGUI.BeginChangeCheck();
        BackendPreset preset = (BackendPreset)EditorGUILayout.EnumPopup("Preset", settings.backendPreset);
        string routerUrl = preset == BackendPreset.Colab
            ? EditorGUILayout.TextField("Colab Router URL", settings.colabRouterUrl)
            : EditorGUILayout.TextField("Local Router URL", settings.localRouterUrl);
        GenerationModel model =
            (GenerationModel)EditorGUILayout.EnumPopup(GenerationModelLabel, settings.generationModel);
        RefinementLifter refinementLifter =
            (RefinementLifter)EditorGUILayout.EnumPopup(RefinementLifterLabel, settings.refinementLifter);

        if (EditorGUI.EndChangeCheck())
        {
            settings.backendPreset = preset;
            settings.generationModel = model;
            settings.refinementLifter = refinementLifter;
            if (preset == BackendPreset.Colab)
                settings.colabRouterUrl = BackendSettings.NormalizeOrigin(routerUrl);
            else
                settings.localRouterUrl = BackendSettings.NormalizeOrigin(routerUrl);
            Persist(settings);
        }

        if (preset == BackendPreset.Colab)
            DrawColabControls(settings);
        else
            DrawLocalControls(settings);

        if (GUILayout.Button("Check Backend Health"))
            _ = CheckHealthAsync(settings, showDialog: preset == BackendPreset.Colab);

        EditorGUILayout.HelpBox($"Requests go to {settings.RouterBaseUrl}", MessageType.None);
    }

    private static void DrawUtilities()
    {
        using (new EditorGUI.DisabledScope(!AgentWorkflow.IsBusy))
        {
            if (GUILayout.Button("Stop the running agent"))
                AgentWorkflow.Cancel();
        }

        using (new EditorGUI.DisabledScope(AgentWorkflow.IsBusy))
        {
            if (GUILayout.Button("Cleanup GeneratedContent"))
                GenerationRunner.CleanupGeneratedContent();
        }

        if (GUILayout.Button("Open Interaction Log Folder"))
            InteractionLogger.RevealLogFolder();

        AgentRunReport last = AgentWorkflow.LastReport;
        using (new EditorGUI.DisabledScope(last == null || string.IsNullOrEmpty(last.artifactDir)))
        {
            if (GUILayout.Button("Reveal last agent run files"))
                EditorUtility.RevealInFinder(last.artifactDir);
        }
    }

    private static void DrawLocalControls(BackendSettings settings)
    {
        EditorGUI.BeginChangeCheck();
        bool autoStart = EditorGUILayout.Toggle("Auto Start ComfyUI", settings.autoStartComfy);
        if (EditorGUI.EndChangeCheck())
        {
            settings.autoStartComfy = autoStart;
            Persist(settings);
        }

        EditorGUILayout.HelpBox(
            autoStart
                ? "Generate starts ComfyUI itself if it is not already running. The router is " +
                  "separate: start it with ./tools/start_backend.sh."
                : "ComfyUI must already be running. Enable Auto Start to have Generate launch it.",
            MessageType.None);
    }

    private static void DrawColabControls(BackendSettings settings)
    {
        EditorGUI.BeginChangeCheck();
        string notebookUrl = EditorGUILayout.TextField("Notebook URL", settings.colabNotebookUrl);
        if (EditorGUI.EndChangeCheck())
        {
            settings.colabNotebookUrl = notebookUrl;
            Persist(settings);
        }

        if (GUILayout.Button("Open Notebook in Colab"))
            Application.OpenURL(settings.colabNotebookUrl);

        EditorGUILayout.HelpBox(
            "Open the notebook, pick a GPU runtime, and run every cell until the zrok share is serving " +
            "the router. Then check backend health here before generating.",
            MessageType.Info);
    }

    private static async Task CheckHealthAsync(BackendSettings settings, bool showDialog)
    {
        RouterClient.BackendHealth health = await RouterClient.CheckHealthAsync(settings);
        if (health.IsReady)
        {
            Debug.Log($"Spatial Generation: backend ready at {settings.RouterBaseUrl} (router + ComfyUI).");
            if (settings.refinementLifter != RefinementLifter.TripoSR &&
                !health.HunyuanRefinementAvailable)
            {
                Debug.LogWarning(
                    "Spatial Generation: Hunyuan3D-2mv refinement is unavailable. " +
                    (string.IsNullOrWhiteSpace(health.HunyuanRefinementDetail)
                        ? "Run ./tools/setup_hunyuan2mv.sh and restart ComfyUI."
                        : health.HunyuanRefinementDetail));
            }
            if (showDialog)
                EditorUtility.DisplayDialog(
                    "Backend Ready", "The router and ComfyUI are both up. You can generate and refine.", "OK");
            return;
        }

        string problem = RouterClient.DescribeProblem(settings, health);
        Debug.LogError($"Spatial Generation: {problem}");
        if (showDialog)
            EditorUtility.DisplayDialog("Backend Not Ready", problem, "OK");
    }

    private static void Persist(BackendSettings settings)
    {
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        BackendRegistry.Reload();
    }
}
