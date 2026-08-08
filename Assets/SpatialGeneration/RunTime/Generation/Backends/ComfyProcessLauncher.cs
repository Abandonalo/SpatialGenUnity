using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace SpatialGeneration.Generation.Backends
{
    /// <summary>
    /// Starts a local ComfyUI on demand, so Generate works from a cold editor without the
    /// user having to remember a second process.
    ///
    /// Only relevant to the Local preset: on Colab, ComfyUI lives in the notebook.
    /// </summary>
    public static class ComfyProcessLauncher
    {
        /// <summary>The Comfy Desktop app. It manages its own ComfyUI server.</summary>
        private const string DesktopAppPath = "/Applications/Comfy Desktop.app";

        /// <summary>Pre-1.0 desktop app, which bundled ComfyUI inside the .app.</summary>
        private const string LegacyDesktopBinary = "/Applications/ComfyUI.app/Contents/MacOS/ComfyUI";
        private const string LegacyDesktopMainScript = "/Applications/ComfyUI.app/Contents/Resources/ComfyUI/main.py";

        /// <summary>Survives domain reloads so a second Generate does not spawn a second server.</summary>
        private const string ProcessIdKey = "SpatialGeneration.ComfyProcessId";

        private static Process _process;

        /// <summary>True when this editor session already started a ComfyUI that is still alive.</summary>
        public static bool IsOwnedProcessRunning => _process is { HasExited: false } || TryGetPersistedProcess(out _);

        /// <summary>
        /// Launches ComfyUI bound to <paramref name="baseUrl"/>'s host and port.
        /// Throws with an actionable message when no usable launch command can be found.
        /// </summary>
        public static void Start(string baseUrl, string launchCommandOverride, string workingDirectoryOverride)
        {
            if (_process is { HasExited: false })
                return;

            LaunchCommand command = ResolveCommand(baseUrl, launchCommandOverride, workingDirectoryOverride);
            var startInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
                startInfo.WorkingDirectory = command.WorkingDirectory;

            try
            {
                _process = Process.Start(startInfo);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Could not launch ComfyUI with '{command.FileName}'. " +
                    "Set BackendSettings.comfyLaunchCommand to a working interpreter or binary.", ex);
            }

            if (_process == null)
                throw new InvalidOperationException("Could not launch ComfyUI: the process did not start.");

            PersistProcessId(_process.Id);

            // Draining both pipes prevents ComfyUI blocking once its output buffer fills.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Debug.Log($"Spatial Generation: started ComfyUI (pid {_process.Id}) for {baseUrl}.");
        }

        /// <summary>Exit code if the process we started has already died, otherwise null.</summary>
        public static int? ExitCodeIfDead =>
            _process is { HasExited: true } ? _process.ExitCode : null;

        /// <summary>Stops only a ComfyUI this editor session started; never someone else's.</summary>
        public static void StopOwnedProcess()
        {
            Process target = _process;
            if (target == null && !TryGetPersistedProcess(out target))
                return;

            try
            {
                if (!target.HasExited)
                {
                    target.Kill();
                    target.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Already gone, or owned by another user; nothing useful to do.
            }
            finally
            {
                target.Dispose();
                ClearPersistedProcessId();
                _process = null;
            }
        }

        private readonly struct LaunchCommand
        {
            public readonly string FileName;
            public readonly string Arguments;
            public readonly string WorkingDirectory;

            public LaunchCommand(string fileName, string arguments, string workingDirectory)
            {
                FileName = fileName;
                Arguments = arguments;
                WorkingDirectory = workingDirectory;
            }
        }

        private static LaunchCommand ResolveCommand(string baseUrl, string commandOverride, string workingDirectoryOverride)
        {
            // An explicit command always wins: it is the escape hatch for installs that do
            // not match any layout below.
            string configured = (commandOverride ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(configured) && configured != LegacyDesktopBinary)
                return new LaunchCommand(configured, BuildServerArguments(baseUrl, string.Empty), workingDirectoryOverride);

            string baseDirectory = ResolveDesktopBaseDirectory();
            var tried = new List<string>();

            foreach (string mainScript in CandidateMainScripts(baseDirectory))
            {
                tried.Add(mainScript);
                if (TryBuildPythonCommand(baseUrl, mainScript, baseDirectory, out LaunchCommand command))
                    return command;
            }

            // Comfy Desktop 1.x keeps its ComfyUI outside the bundle in a per-install
            // folder we cannot reliably guess. Opening the app is the most we can automate:
            // it then asks the user which mode to start, which is a GUI choice.
            if (Directory.Exists(DesktopAppPath))
            {
                OpenDesktopApp();
                throw new InvalidOperationException(
                    "ComfyUI was not running, so Comfy Desktop has been opened for you.\n" +
                    "Choose ComfyUI mode in the app, wait for it to finish starting, then press Generate again.");
            }

            throw new InvalidOperationException(
                "ComfyUI is not running and no way to launch it was found.\n" +
                $"Looked for main.py at:\n  {string.Join("\n  ", tried)}\n" +
                "Set BackendSettings.comfyLaunchCommand to your ComfyUI python, " +
                "or start ComfyUI yourself and turn off Auto Start ComfyUI.");
        }

        /// <summary>
        /// Install layouts worth trying, in order: a checkout in the configured base
        /// directory, the folder Comfy Desktop installs into, then the legacy app bundle.
        /// </summary>
        private static IEnumerable<string> CandidateMainScripts(string baseDirectory)
        {
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                yield return Path.Combine(baseDirectory, "main.py");

                // Comfy Desktop installs alongside the base directory, e.g. a base of
                // ~/ComfyUI pairs with ~/ComfyUI-Installs/ComfyUI/ComfyUI/main.py.
                string parent = Path.GetDirectoryName(baseDirectory.TrimEnd('/'));
                string installName = Path.GetFileName(baseDirectory.TrimEnd('/'));
                if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(installName))
                {
                    yield return Path.Combine(parent, $"{installName}-Installs", "ComfyUI", "ComfyUI", "main.py");
                    yield return Path.Combine(parent, $"{installName}-Installs", "ComfyUI", "main.py");
                }
            }

            yield return LegacyDesktopMainScript;
        }

        private static void OpenDesktopApp()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = $"-a \"{DesktopAppPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Spatial Generation: could not open Comfy Desktop. {ex.Message}");
            }
        }

        private static bool TryBuildPythonCommand(
            string baseUrl, string mainScript, string baseDirectory, out LaunchCommand command)
        {
            command = default;
            if (!File.Exists(mainScript))
                return false;

            string python = ResolvePython(baseDirectory, Path.GetDirectoryName(mainScript));
            if (string.IsNullOrEmpty(python))
                return false;

            // main.py has to run as __main__ with argument parsing enabled before anything
            // else imports comfy.options; that is what ComfyUI's own launcher does.
            string bootstrap =
                "import sys, runpy, comfy.options; " +
                "comfy.options.enable_args_parsing(); " +
                "import comfy.utils; " +
                "comfy.utils.set_progress_bar_enabled(False); " +
                "sys.argv=['main.py'] + sys.argv[1:]; " +
                $"runpy.run_path(r'{mainScript}', run_name='__main__')";

            command = new LaunchCommand(
                python,
                $"-c \"{bootstrap}\" {BuildServerArguments(baseUrl, baseDirectory)}",
                Path.GetDirectoryName(mainScript));
            return true;
        }

        private static string BuildServerArguments(string baseUrl, string baseDirectory)
        {
            (string host, int port) = ParseHostPort(baseUrl);
            string args = $"--listen {host} --port {port} --disable-auto-launch --dont-print-server";
            return string.IsNullOrWhiteSpace(baseDirectory) ? args : $"{args} --base-directory \"{baseDirectory}\"";
        }

        private static (string host, int port) ParseHostPort(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
                return ("127.0.0.1", 8188);

            string host = string.IsNullOrWhiteSpace(uri.Host) ? "127.0.0.1" : uri.Host;
            int port = uri.IsDefaultPort ? 8188 : uri.Port;
            return (host, port);
        }

        /// <summary>Model and output root the desktop app was configured with.</summary>
        private static string ResolveDesktopBaseDirectory()
        {
            string[] candidates =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            };

            foreach (string appSupport in candidates)
            {
                if (string.IsNullOrWhiteSpace(appSupport))
                    continue;

                string configPath = Path.Combine(appSupport, "ComfyUI", "config.json");
                if (!File.Exists(configPath))
                    continue;

                Match match = Regex.Match(File.ReadAllText(configPath), "\"basePath\"\\s*:\\s*\"([^\"]+)\"");
                string basePath = match.Success ? match.Groups[1].Value : string.Empty;
                return Directory.Exists(basePath) ? basePath : string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// ComfyUI's own interpreter. The venv beside the base directory or beside main.py
        /// is the one with torch and the custom-node dependencies installed; a system
        /// python would start and then fail on the first import.
        /// </summary>
        private static string ResolvePython(string baseDirectory, string mainScriptDirectory)
        {
            foreach (string root in new[] { baseDirectory, mainScriptDirectory })
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;

                foreach (string name in new[] { "python3", "python" })
                {
                    string candidate = Path.Combine(root, ".venv", "bin", name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return string.Empty;
        }

        private static bool TryGetPersistedProcess(out Process process)
        {
            process = null;
            int processId = GetPersistedProcessId();
            if (processId <= 0)
                return false;

            try
            {
                Process candidate = Process.GetProcessById(processId);
                if (candidate.HasExited)
                {
                    candidate.Dispose();
                    ClearPersistedProcessId();
                    return false;
                }

                process = candidate;
                return true;
            }
            catch (ArgumentException)
            {
                ClearPersistedProcessId();
                return false;
            }
        }

        private static int GetPersistedProcessId()
        {
#if UNITY_EDITOR
            return SessionState.GetInt(ProcessIdKey, 0);
#else
            return 0;
#endif
        }

        private static void PersistProcessId(int processId)
        {
#if UNITY_EDITOR
            SessionState.SetInt(ProcessIdKey, processId);
#endif
        }

        private static void ClearPersistedProcessId()
        {
#if UNITY_EDITOR
            SessionState.EraseInt(ProcessIdKey);
#endif
        }
    }
}
