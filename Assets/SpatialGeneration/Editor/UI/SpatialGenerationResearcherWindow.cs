using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SpatialGeneration.Agent;
using UnityEditor;
using UnityEngine;
using SpatialGeneration.Authoring;

namespace SpatialGeneration.UI
{
    public class SpatialGenerationResearcherWindow : EditorWindow
    {
        private static readonly string[] Tabs = { "Researcher settings", "Utilities" };

        /// <summary>Router and ComfyUI settings; registered by the legacy router package.</summary>
        public static Action<BackendSettings> RouterSettingsGUI;

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
            using (new EditorGUI.DisabledScope(StudyController.IsBusy))
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
                {
                    if (RouterSettingsGUI != null) RouterSettingsGUI(settings);
                    else EditorGUILayout.HelpBox("The router settings panel is not installed (SpatialGeneration.Legacy.Router.Editor).",
                        MessageType.Warning);
                }
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
            using (new EditorGUI.DisabledScope(StudyController.IsBusy || StudySession.instance.phase == StudyPhase.Setup))
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

            // Not a blocker, but Codex cannot be confined as tightly as Claude Code (see IsolationNote).
            bool isolated = settings.agentCli == AgentCli.ClaudeCode;
            lines.Add((isolated ? "✓ " : "⚠ ") + AgentProcessRunner.IsolationNote(settings));
            if (!isolated)
                ok = false;
            if (StudyController.DeveloperMode)
            {
                ok = false;
                lines.Add("⚠ Developer test mode is on: no run limit, and every log records dev_mode=True.");
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

        private static void DrawUtilities()
        {
            using (new EditorGUI.DisabledScope(!AgentWorkflow.IsBusy))
            {
                if (GUILayout.Button("Stop the running agent"))
                    AgentWorkflow.Cancel();
            }

            using (new EditorGUI.DisabledScope(StudyController.IsBusy))
            {
                if (GUILayout.Button("Cleanup GeneratedContent"))
                    GeneratedContentRoot.Clear();
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

        /// <summary>Saves the settings asset and rebuilds anything cached from it.</summary>
        public static void Persist(BackendSettings settings)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            BackendRegistry.Reload();
        }
    }
}
