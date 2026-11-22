using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpatialGeneration.Agent
{
    public sealed class AgentRunOutcome
    {
        public bool started;
        public int exitCode = -1;
        public bool timedOut;
        public bool cancelled;
        public double seconds;
        public string commandLine = string.Empty;
        public string stdout = string.Empty;
        public string stderr = string.Empty;
        public string error = string.Empty;

        /// <summary>Final answer text, when the CLI reports one (Claude Code JSON output).</summary>
        public string resultText = string.Empty;
        public int turns = -1;
        public bool reportedError;
    }

    /// <summary>
    /// Launches Claude Code (<c>claude -p</c>) or Codex (<c>codex exec</c>) headless with the
    /// prompt on stdin. The agent then works on the scene through MCP for Unity while this
    /// editor keeps running: the process is awaited off the main thread so Unity can keep
    /// serving the MCP tool calls it makes.
    /// </summary>
    public static class AgentProcessRunner
    {
        public const string ClaudeServerName = "UnityMCP";
        public const string CodexServerName = "spatialgenUnity";

        /// <summary>The only MCP tools an agent run may call (MCP for Unity also serves general editor tools).</summary>
        internal static readonly string[] ToolNames =
        {
            "spatialgen_get_task", "spatialgen_list_assets", "spatialgen_list_parts", "spatialgen_create_asset",
            "spatialgen_add_parts", "spatialgen_update_parts", "spatialgen_remove_parts", "spatialgen_clear_asset",
            "spatialgen_finish", "execute_custom_tool", "set_active_instance"
        };

        /// <summary>
        /// Claude Code's whole built-in tool set for a run (<c>--tools</c>). No file, shell, web or
        /// sub-agent tools exist in the session, so the agent cannot read the target scenes or any
        /// other file; ToolSearch loads the MCP tools when Claude Code defers them.
        /// </summary>
        internal const string ClaudeBuiltInTools = "ToolSearch,ListMcpResourcesTool,ReadMcpResourceTool";

        /// <summary>Denied on top of <see cref="ClaudeBuiltInTools"/>, in case a CLI version ignores --tools.</summary>
        internal const string ClaudeDeniedTools =
            "Bash,Edit,Write,NotebookEdit,WebFetch,WebSearch,Task,Agent,Read,Glob,Grep,LS";

        /// <summary>
        /// Codex features that give the model tools besides MCP (shell, browser, computer use, apps,
        /// plugins, sub-agents, images). Set through <c>-c features.*=false</c>, which a Codex version
        /// without the feature ignores, rather than <c>--disable</c>, which rejects unknown names.
        /// </summary>
        internal static readonly string[] CodexDisabledFeatures =
        {
            "shell_tool", "unified_exec", "apps", "plugins", "browser_use", "browser_use_external",
            "in_app_browser", "computer_use", "image_generation", "view_image", "multi_agent", "multi_agent_v2",
            "memories"
        };

        /// <summary>
        /// What the setup check reports about how far the agent is confined. Claude Code is verified
        /// to run with no file tools; Codex keeps a read-only command runner in current versions.
        /// </summary>
        public static string IsolationNote(BackendSettings settings) => settings.agentCli == AgentCli.ClaudeCode
            ? "Claude Code runs with no file, shell or web tools, from an empty folder outside the project."
            : "Codex runs from an empty folder with your config, shell tool and plugins off, but current versions " +
              "keep a read-only command runner, so the agent could still read files on this machine. " +
              "Use Claude Code for study sessions.";

        private static Process _process;
        private static bool _cancelRequested;

        public static bool IsRunning
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public static void Cancel()
        {
            _cancelRequested = true;
            TryKill(_process);
        }

        public static string ProjectRoot => StudyPaths.ProjectRoot;

        // ------------------------------------------------------------------ setup checks

        public static string ResolveExecutable(BackendSettings settings, out string problem)
        {
            problem = null;
            bool claude = settings.agentCli == AgentCli.ClaudeCode;
            string configured = (claude ? settings.claudeExecutable : settings.codexExecutable)?.Trim();
            string name = claude ? "claude" : "codex";

            if (!string.IsNullOrEmpty(configured))
            {
                if (File.Exists(configured))
                    return configured;
                problem = $"The configured {name} path does not exist: {configured}";
                return null;
            }

            foreach (string dir in SearchDirectories())
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    foreach (string ext in new[] { ".cmd", ".exe" })
                    {
                        if (File.Exists(candidate + ext))
                            return candidate + ext;
                    }
                }
            }

            problem = $"Could not find '{name}'. Run `which {name}` in a terminal and paste the path into " +
                      "Researcher settings > Agent executable.";
            return null;
        }

        public const string DefaultMcpUrl = "http://127.0.0.1:8080/mcp";

        /// <summary>True for an absolute http(s) URL; anything else cannot reach the MCP server.</summary>
        public static bool IsValidMcpUrl(string url) =>
            Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        /// <summary>True when something answers at the MCP URL; any HTTP status counts as up.</summary>
        public static async Task<bool> IsMcpServerReachableAsync(string url)
        {
            if (!IsValidMcpUrl(url))
                return false;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using HttpResponseMessage response = await client.GetAsync(url);
                return true;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ run

        public static async Task<AgentRunOutcome> RunAsync(BackendSettings settings, string prompt, string artifactDir)
        {
            var outcome = new AgentRunOutcome();
            _cancelRequested = false;

            string executable = ResolveExecutable(settings, out string problem);
            if (executable == null)
            {
                outcome.error = problem;
                return outcome;
            }

            // An empty folder outside the project: nothing to read there, and no CLAUDE.md / AGENTS.md
            // of the project or its parents is picked up as instructions.
            string workingDir = CreateIsolatedWorkingDirectory(artifactDir);
            string arguments = settings.agentCli == AgentCli.ClaudeCode
                ? BuildClaudeArguments(settings, artifactDir)
                : BuildCodexArguments(settings, workingDir);
            outcome.commandLine = $"\"{executable}\" {arguments}";
            File.WriteAllText(Path.Combine(artifactDir, "command.txt"), outcome.commandLine);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.EnvironmentVariables["PATH"] = BuildPath(startInfo.EnvironmentVariables["PATH"]);
            startInfo.EnvironmentVariables["NO_COLOR"] = "1";
            // The environment variable takes precedence over every other effort setting in
            // Claude Code, including a value exported in ~/.zshrc, so set it alongside --effort.
            string effort = settings.agentEffort?.Trim();
            if (settings.agentCli == AgentCli.ClaudeCode && !string.IsNullOrEmpty(effort))
                startInfo.EnvironmentVariables["CLAUDE_CODE_EFFORT_LEVEL"] = effort;

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var clock = Stopwatch.StartNew();

            try
            {
                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

                if (!_process.Start())
                {
                    outcome.error = "The agent process did not start.";
                    return outcome;
                }

                outcome.started = true;
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                Process process = _process;
                await Task.Run(() =>
                {
                    process.StandardInput.Write(prompt);
                    process.StandardInput.Close();
                });

                int timeoutMs = Mathf.Max(30, settings.agentTimeoutSeconds) * 1000;
                bool exited = await Task.Run(() => process.WaitForExit(timeoutMs));
                if (!exited)
                {
                    outcome.timedOut = true;
                    TryKill(process);
                }

                await Task.Run(() => process.WaitForExit());
                outcome.exitCode = SafeExitCode(process);
            }
            catch (Exception ex)
            {
                outcome.error = $"Could not run the agent: {ex.Message}";
            }
            finally
            {
                clock.Stop();
                outcome.seconds = clock.Elapsed.TotalSeconds;
                outcome.cancelled = _cancelRequested;
                _process?.Dispose();
                _process = null;
                TryRemoveEmptyDirectory(workingDir);
            }

            lock (stdout) outcome.stdout = stdout.ToString();
            lock (stderr) outcome.stderr = stderr.ToString();
            WriteLogs(artifactDir, outcome);

            if (settings.agentCli == AgentCli.ClaudeCode)
                ParseClaudeResult(outcome);
            else
                outcome.resultText = LastLines(outcome.stdout, 12);

            return outcome;
        }

        internal static string BuildClaudeArguments(BackendSettings settings, string artifactDir)
        {
            string mcpConfigPath = Path.Combine(artifactDir, "mcp-config.json");
            File.WriteAllText(mcpConfigPath,
                "{\n  \"mcpServers\": {\n    \"" + ClaudeServerName + "\": {\n      \"type\": \"http\",\n      \"url\": \"" +
                settings.mcpServerUrl.Trim() + "\"\n    }\n  }\n}\n");

            var allowed = new List<string>();
            foreach (string tool in ToolNames)
                allowed.Add($"mcp__{ClaudeServerName}__{tool}");
            allowed.Add("ListMcpResourcesTool");
            allowed.Add("ReadMcpResourceTool");
            allowed.Add("ToolSearch");

            var args = new StringBuilder();
            args.Append("-p --output-format json");
            args.Append($" --mcp-config {Quote(mcpConfigPath)} --strict-mcp-config");
            args.Append($" --tools {Quote(ClaudeBuiltInTools)}");
            args.Append($" --allowedTools {Quote(string.Join(",", allowed))}");
            args.Append($" --disallowedTools {Quote(ClaudeDeniedTools)}");
            args.Append($" --max-turns {Mathf.Max(5, settings.agentMaxTurns)}");
            if (!string.IsNullOrWhiteSpace(settings.agentModel))
                args.Append($" --model {Quote(settings.agentModel.Trim())}");
            if (!string.IsNullOrWhiteSpace(settings.agentEffort))
                args.Append($" --effort {Quote(settings.agentEffort.Trim())}");
            if (!string.IsNullOrWhiteSpace(settings.claudeExtraArguments))
                args.Append(' ').Append(settings.claudeExtraArguments.Trim());
            return args.ToString();
        }

        internal static string BuildCodexArguments(BackendSettings settings, string workingDir)
        {
            var args = new StringBuilder();
            // --ignore-user-config drops the MCP servers, plugins and features of ~/.codex/config.toml
            // (sign-in still comes from CODEX_HOME); --ephemeral keeps runs out of the session history.
            args.Append("exec --skip-git-repo-check --sandbox read-only --ephemeral --ignore-user-config");
            args.Append($" -C {Quote(workingDir)}");
            foreach (string feature in CodexDisabledFeatures)
                args.Append($" -c features.{feature}=false");
            args.Append($" -c {Quote($"mcp_servers.{CodexServerName}.url=\"{settings.mcpServerUrl.Trim()}\"")}");
            string tools = string.Join(",", Array.ConvertAll(ToolNames, t => $"\"{t}\""));
            args.Append($" -c {Quote($"mcp_servers.{CodexServerName}.enabled_tools=[{tools}]")}");
            if (!string.IsNullOrWhiteSpace(settings.agentModel))
                args.Append($" -m {Quote(settings.agentModel.Trim())}");
            if (!string.IsNullOrWhiteSpace(settings.agentEffort))
            {
                string effort = settings.agentEffort.Trim() == "max" ? "xhigh" : settings.agentEffort.Trim();
                args.Append($" -c {Quote($"model_reasoning_effort=\"{effort}\"")}");
            }
            if (!string.IsNullOrWhiteSpace(settings.codexExtraArguments))
                args.Append(' ').Append(settings.codexExtraArguments.Trim());
            args.Append(" -");
            return args.ToString();
        }

        [Serializable]
        private class ClaudeJsonResult
        {
            public string type;
            public string subtype;
            public bool is_error;
            public string result;
            public int num_turns = -1;
        }

        private static void ParseClaudeResult(AgentRunOutcome outcome)
        {
            string text = outcome.stdout?.Trim() ?? string.Empty;
            int start = text.LastIndexOf("{\"type\":\"result\"", StringComparison.Ordinal);
            if (start < 0)
                start = text.IndexOf('{');
            if (start < 0)
            {
                outcome.resultText = LastLines(text, 12);
                return;
            }

            try
            {
                ClaudeJsonResult parsed = JsonUtility.FromJson<ClaudeJsonResult>(text.Substring(start));
                outcome.resultText = parsed?.result ?? string.Empty;
                outcome.turns = parsed?.num_turns ?? -1;
                outcome.reportedError = parsed != null && (parsed.is_error ||
                                                           (parsed.subtype != null && parsed.subtype.StartsWith("error")));
            }
            catch (Exception)
            {
                outcome.resultText = LastLines(text, 12);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void TryRemoveEmptyDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string CreateIsolatedWorkingDirectory(string artifactDir)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SpatialGenerationAgent", Path.GetFileName(artifactDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(artifactDir, "working_directory.txt"), dir);
            return dir;
        }

        private static IEnumerable<string> SearchDirectories()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string>
            {
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".claude", "local"),
                "/opt/homebrew/bin",
                "/usr/local/bin",
                "/usr/bin",
                Path.Combine(home, ".npm-global", "bin"),
                Path.Combine(home, ".bun", "bin"),
                Path.Combine(home, ".volta", "bin"),
                Path.Combine(home, "AppData", "Roaming", "npm")
            };

            string nvm = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvm))
            {
                string[] versions = Directory.GetDirectories(nvm);
                Array.Sort(versions);
                for (int i = versions.Length - 1; i >= 0; i--)
                    dirs.Add(Path.Combine(versions[i], "bin"));
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            dirs.AddRange(path.Split(Path.PathSeparator));
            return dirs;
        }

        /// <summary>
        /// Unity started from the Dock gets a minimal PATH, but both CLIs need node and git.
        /// Put the usual install locations in front of whatever PATH there is.
        /// </summary>
        private static string BuildPath(string existing)
        {
            var parts = new List<string>();
            foreach (string dir in SearchDirectories())
            {
                if (!string.IsNullOrWhiteSpace(dir) && !parts.Contains(dir) && Directory.Exists(dir))
                    parts.Add(dir);
            }

            if (!string.IsNullOrEmpty(existing))
            {
                foreach (string dir in existing.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(dir) && !parts.Contains(dir))
                        parts.Add(dir);
                }
            }

            return string.Join(Path.PathSeparator.ToString(), parts);
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Spatial Generation: could not stop the agent process: {ex.Message}");
            }
        }

        private static int SafeExitCode(Process process)
        {
            try { return process.ExitCode; }
            catch (InvalidOperationException) { return -1; }
        }

        private static void WriteLogs(string artifactDir, AgentRunOutcome outcome)
        {
            try
            {
                File.WriteAllText(Path.Combine(artifactDir, "agent_stdout.txt"), outcome.stdout ?? string.Empty);
                File.WriteAllText(Path.Combine(artifactDir, "agent_stderr.txt"), outcome.stderr ?? string.Empty);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Spatial Generation: could not write agent logs: {ex.Message}");
            }
        }

        private static string LastLines(string text, int count)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            string[] lines = text.TrimEnd().Split('\n');
            int start = Math.Max(0, lines.Length - count);
            return string.Join("\n", lines, start, lines.Length - start).Trim();
        }
    }
}
