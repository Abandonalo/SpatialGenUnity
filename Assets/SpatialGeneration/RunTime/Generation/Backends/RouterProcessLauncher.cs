using System;
using System.ComponentModel;
using System.IO;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace SpatialGeneration.Generation.Backends
{
    /// <summary>
    /// Starts the local FastAPI router via <c>tools/start_backend.sh</c>.
    ///
    /// Only for the Local preset: on Colab the router runs in the notebook. Unlike
    /// ComfyUI, there is nothing to discover here — the script ships with the repo and is
    /// the documented way to run it, so leaving this to the user just meant hitting the
    /// same "not reachable" error once per session.
    /// </summary>
    public static class RouterProcessLauncher
    {
        private const string ScriptRelativePath = "tools/start_backend.sh";

        /// <summary>Survives domain reloads so a second Generate does not spawn a second router.</summary>
        private const string ProcessIdKey = "SpatialGeneration.RouterProcessId";

        private static Process _process;

        /// <summary>Launches the router. Throws with an actionable message if it cannot.</summary>
        public static void Start()
        {
            if (_process is { HasExited: false })
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string script = Path.Combine(projectRoot, ScriptRelativePath);

            if (!File.Exists(script))
            {
                throw new InvalidOperationException(
                    $"Cannot start the router: {ScriptRelativePath} is missing from {projectRoot}.");
            }

            // The script refuses to run without it, and the resulting message is easy to
            // miss once output is redirected, so check here where we can be specific.
            if (!Directory.Exists(Path.Combine(projectRoot, ".venv")))
            {
                throw new InvalidOperationException(
                    "Cannot start the router: no .venv in the project root.\n" +
                    "Create it with:\n" +
                    "    python3 -m venv .venv && .venv/bin/pip install -r requirements.txt");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"\"{script}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                _process = Process.Start(startInfo);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException($"Could not run {ScriptRelativePath}: {ex.Message}", ex);
            }

            if (_process == null)
                throw new InvalidOperationException($"Could not run {ScriptRelativePath}: the process did not start.");

            PersistProcessId(_process.Id);

            // Draining both pipes stops uvicorn blocking once its output buffer fills.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Debug.Log($"Spatial Generation: started the router (pid {_process.Id}).");
        }

        /// <summary>Exit code if the router we started has already died, otherwise null.</summary>
        public static int? ExitCodeIfDead => _process is { HasExited: true } ? _process.ExitCode : null;

        /// <summary>Stops only a router this editor session started.</summary>
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
                // Already gone, or not ours to stop.
            }
            finally
            {
                target.Dispose();
                ClearPersistedProcessId();
                _process = null;
            }
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
