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

        private static readonly string[] ToolNames =
        {
            "spatialgen_get_task", "spatialgen_list_assets", "spatialgen_list_parts", "spatialgen_create_asset",
            "spatialgen_add_parts", "spatialgen_update_parts", "spatialgen_remove_parts", "spatialgen_clear_asset",
            "spatialgen_finish", "execute_custom_tool", "set_active_instance"
        };

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

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

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

            string arguments = settings.agentCli == AgentCli.ClaudeCode
                ? BuildClaudeArguments(settings, artifactDir)
                : BuildCodexArguments(settings);
            outcome.commandLine = $"\"{executable}\" {arguments}";
            File.WriteAllText(Path.Combine(artifactDir, "command.txt"), outcome.commandLine);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = ProjectRoot,
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

        private static string BuildClaudeArguments(BackendSettings settings, string artifactDir)
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
            args.Append($" --allowedTools {Quote(string.Join(",", allowed))}");
            args.Append(" --disallowedTools \"Bash,Edit,Write,NotebookEdit,WebFetch,WebSearch,Task\"");
            args.Append($" --max-turns {Mathf.Max(5, settings.agentMaxTurns)}");
            if (!string.IsNullOrWhiteSpace(settings.agentModel))
                args.Append($" --model {Quote(settings.agentModel.Trim())}");
            if (!string.IsNullOrWhiteSpace(settings.agentEffort))
                args.Append($" --effort {Quote(settings.agentEffort.Trim())}");
            if (!string.IsNullOrWhiteSpace(settings.claudeExtraArguments))
                args.Append(' ').Append(settings.claudeExtraArguments.Trim());
            return args.ToString();
        }

        private static string BuildCodexArguments(BackendSettings settings)
        {
            var args = new StringBuilder();
            args.Append("exec --skip-git-repo-check --sandbox read-only");
            // A dedicated server name, so an existing unityMCP entry in ~/.codex/config.toml
            // (stdio or HTTP) is neither overridden nor merged into an invalid one.
            args.Append($" -c {Quote($"mcp_servers.{CodexServerName}.url=\"{settings.mcpServerUrl.Trim()}\"")}");
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
