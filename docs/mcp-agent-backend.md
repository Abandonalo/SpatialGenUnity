# MCP agent backend

The ComfyUI pipeline (ControlNet image → Hunyuan3D/TripoSR mesh) is replaced, as the default
backend, by a coding agent (Claude Code or Codex) that builds low-poly assets directly in the
open scene through **MCP for Unity** (CoplayDev). The ComfyUI path is still there: set
*Backend* to `Router` in **Tools > Spatial Generation Researcher Tools**.

## One-time setup (macOS)

1. Open the project. Unity resolves `com.coplaydev.unity-mcp` from GitHub and drops the old
   `com.unity.ai.assistant` package.
2. MCP for Unity needs Python 3.10+ and `uv` (`brew install uv`).
3. `Window > MCP for Unity > Toggle MCP Window`: keep the transport on **HTTP**
   (`http://127.0.0.1:8080/mcp`), start the server, and check that Unity shows as connected.
   You do **not** need to click *Configure* for Claude Code: the window passes its own MCP
   config to the CLI on every run.
4. Sign in to the CLI once in a terminal (`claude` or `codex`), so headless runs use your
   subscription.
5. Open **Tools > Spatial Generation Researcher Tools** and choose **Check setup** on the
   **Researcher settings** tab. If the CLI is not found,
   paste the output of `which claude` (or `which codex`) into *Agent executable*: Unity started
   from the Dock does not inherit your shell `PATH`.
6. Freeze the model for the study in *Researcher settings → Model*.

## How a run works

```
Spatial Generation window ──launches──▶ claude -p / codex exec  (prompt on stdin)
        ▲                                        │ MCP (HTTP)
        │ job state, logs, snapshots             ▼
   AgentWorkflow ◀──── spatialgen_* tools ◀── MCP for Unity server ◀── Unity Editor
```

* The agent may call only the project's `spatialgen_*` tools, and it starts in an empty folder
  outside the project, so it cannot read the target scenes or any project file.
  Claude Code: `--tools` limits the built-in tools to `ToolSearch` and the MCP resource tools,
  so no file, shell, web or sub-agent tools exist in the session; `--allowedTools` admits only the
  `spatialgen_*` MCP tools. Codex: `--ignore-user-config`, the shell, browser, apps and plugin
  features off, and `enabled_tools` on the MCP server. Current Codex versions keep a read-only
  command runner, so *Check setup* warns: **use Claude Code for study sessions.** The tools build
  assets from primitives (box, wedge, pyramid, cylinder, cone, sphere) with flat colours.
* Agent tool calls are not Unity Undo steps. Edits run on a private preview copy, and a
  generation run is a version the window manages (switch, regenerate).
* Duplicating a proxy (Ctrl+D, copy/paste) gives the copy a new proxy ID. Copies made by
  scripts are repaired before each generation run.
* **Condition B generation:** one empty asset per Occupy proxy is prepared at the proxy's
  footprint, rotation and volume; the agent models inside it. The proxy's local **+X is the
  front** (shown as an arrow on the selected proxy). Parts outside the volume only produce a
  warning, so spatial fit is not forced by post-processing.
* **Condition A generation:** the agent reads the scene description, creates each asset
  (position, yaw, volume) and builds it.
* **Condition B edit:** click a part on the model, Shift-click to toggle additional parts,
  or drag a box in the Scene view. Drawing a box selects a part in full at 85% coverage.
  Adjust its handles to refine the exact region. For a partial selection, the tools
  preserve the geometry outside the box.
* **Condition A edit:** the agent picks one asset and rebuilds it as a whole
  (`clear_asset` + `add_parts`), which is the text-only baseline's whole-object regeneration.
  This is enforced: adding to, updating or removing parts of an existing object is rejected until
  that object has been cleared in the same edit. An object the edit creates is built directly.
  The rebuilt object may grow or shrink: its `volume_size` is refitted to the parts it was rebuilt
  from (carried by Apply, reverted by Ctrl+Z). Condition B volumes are the designer's proxies and
  never change.

## Study flow in the window

Open **Tools > Spatial Generation**. After setup, generate and accept a version, then enter a refinement prompt and choose
**Refine**. The whole-model preview appears automatically when the refinement is ready.
Drag the preview to orbit the object and scroll to zoom.
**Regenerate** creates another idea from the same model, prompt and selection. Use
**Previous** and **Next** to browse. **Apply this idea** commits the displayed version
as one Unity Undo action. Ctrl+Z / Ctrl+Y on it are recorded (counters, `study_edit_undo` /
`study_edit_redo`, an `edit<n>_undone` or new `edit<n>_after` snapshot). The Undo history is
cleared at acceptance and at submission, so neither generation-phase edits nor the submitted
scene can be undone. **Discard previews** returns to the prompt. Participants can keep
refining until they choose **Submit scene**. Submitting discards any unapplied ideas.
Each run ends as *complete*, *partial* (built something, but the CLI then failed or some
objects stayed empty), *timed out*, *failed* or *cancelled*; the outcome is logged
(`study_generation_run_end`) and marked on the version buttons (⚠). A run that built nothing does
not count against the limit (`study_generation_run_refunded`): its empty version is removed and
the previous one is shown again. Only a version with geometry can be accepted; accepting one with
empty objects or an unfinished run asks for confirmation and names them. While a run is starting
(the setup check waits for the MCP server), Accept, version switching and session reset wait.
**← Back to …** (right of the step row) returns to the previous step after a confirmation:
refinement → generation un-accepts the version (applied refinements stay in it, open previews are
discarded), finished → refinement reopens the submitted scene, and generation → setup is possible
only before a run is used. Outside developer mode each step back is logged (`study_step_back`,
with from/to, runs and edits) and snapshotted (`back_to_<step>`). Accepting or submitting again
writes newer `accepted_run*` / `final` snapshots, which the scoring scripts use.
Set the generation run limit in **Researcher Tools > Researcher settings**. The **Utilities**
tab contains agent cancellation, scene cleanup, and access to logs and run files.

## Logs

* `Logs/SpatialGenerationLogs/interaction_*.jsonl|csv`: all events (`study_*`, `agent_tool`,
  proxy edits).
* `Logs/SpatialGenerationStudy/<participant>/condition_<A|B>/*.json`: world-space snapshots
  after each run, at acceptance, before/after each edit, after an undo or redo of an applied
  edit, and at the end. A part clipped by a partial region edit also carries `pieces` (one box
  per remaining convex piece), `geometry_center_world` / `geometry_size_world`, and on the
  unselected remainder `source_part_id`; the scoring scripts use these instead of the full
  primitive.
* `Logs/SpatialGeneration/Agent/<run>/`: prompt, task JSON, exact command, CLI stdout/stderr,
  the working folder used, and a report per agent run.

## Known limits

* A script reload interrupts an in-progress preview. Completed ideas remain available
  for review. For an interrupted initial generation, use *Clear interrupted run*.
* If Codex does not call MCP tools in `exec` mode on your version, add the needed approval
  flag under *Researcher settings → Codex extra arguments*.
