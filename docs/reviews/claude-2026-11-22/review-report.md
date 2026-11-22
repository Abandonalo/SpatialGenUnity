# SpatialGenUnity: workflow proof run and code review

*22 November 2026, at commit `a5f1c17` ("include the new MCP backend, redesign window interface").
Scope: the study workflow in `docs/mcp-agent-backend.md` (MCP agent backend, conditions A and B,
generation → accept → refine with previews → submit), the snapshot → analysis contract, and overall
code quality.*

## 1. Verdict

The workflow is **complete**. Every step in the docs exists in code, and a scripted run went
through both conditions without a functional break. The agent-facing rules in `AgentSceneOps`
hold: out-of-scope assets, out-of-region geometry and a second asset in a condition A edit were all
rejected. The Unity-to-analysis contract is also right for whole parts: a perfect stand-in agent
scores 1.0 coverage, 14/14 relations and 0 error in both conditions.

It is **not yet safe for data collection**. Four problems would change or contaminate study
measures (section 3, items C1–C4). Two of them were reproduced in the proof run. None is large to
fix.

## 2. What was run

| Check | How | Result |
|---|---|---|
| Router backend unit tests | `python -m unittest` in `tools/comfy_router_backend/tests` | **12/12 pass** |
| Analysis self-test + synthetic study | `analysis/tests/test_scoring.py`; `make_synthetic_study.py` → `score_generation` → `score_edits` → `analyze_generation` | **10/10 pass**, all four scripts produce output |
| Unity compile + EditMode suite | Isolated clone of the project, Unity 6000.3.9f1 `-batchmode -runTests` (the open editor holds the project lock) | **0 compile errors; 66 pass, 0 fail, 2 skipped** (both need a GPU / Scene view) |
| End-to-end study flow | New harness `proof-run/ProofRunWorkflowTests.cs`, details below | Full flow runs, findings below |
| Proof-run snapshots → analysis | `score_generation.py`, `score_edits.py` on the harness's logs | Contract works; two scoring defects reproduced |

**Proof-run harness.** It calls the real `StudyController` → `AgentWorkflow` →
`AgentProcessRunner` path. The CLI is a stub (`proof-run/fake_claude.sh`) and the MCP endpoint is a
stub HTTP listener. While the stub runs, the harness plays the agent by calling the same
`AgentSceneOps` functions the `spatialgen_*` MCP wrappers call. Proxies are placed exactly on target
scene X. Log: `proof-run/proof_report.txt`.

Steps covered: StartCondition → 11 proxies (one duplicated as Ctrl+D would) → run 1 (complete) →
run 2 (agent stops part-way) → switch back to run 1 → Accept → edit 1 (click a part, 2 ideas,
apply idea 1) → edit 2 (drag a box over half a part, remove it, apply) → edit 3 (preview, discard)
→ Ctrl+Z → Submit. Condition A: empty-prompt check → text generation → text edit → apply.

**Not covered.** A real Claude Code or Codex run over the live MCP for Unity server (the server
was not running on port 8080, and a real run would spend your subscription). The Scene-view click
and drag gestures (simulated through `RefinementPartSelection.Pick/Draw`). The orbit preview
rendering. Submit itself: batch mode auto-cancels the modal `DisplayDialog`, so no `final`
snapshot was written.

## 3. Findings

### Critical: study validity

**C1. Partial-part edits are invisible to edit scoring (reproduced).**
`PartialPartEdit` stores the clipped geometry in `AgentPart.fragments` and leaves the part's
transform unchanged (`RetainOutside`), or it creates a `_remainder_` part with the *full* original
transform (`IsolateInside`). `SceneSnapshot` exports this as `clipped_geometry_local`, but
`analysis/spatialscore/scene.py:snapshot_objects` never reads it: every part is scored as the full
box `center_world`/`size_world`.
*Proof run, edit 2* ("remove the left half of the bench"): half the bench was really removed, and
`score_edits` reports `changed_parts_in_target = 0` and `boundary_integrity = no_boundary`, i.e.
"nothing happened". A partial *update* fails the other way: the remainder part looks like a new
full-size part. Box-drag edits are the core condition B refinement, so this hits the H2 measures
directly.
*Fix:* export per-part world-space geometry (fragment triangles transformed to world, or at least
renderer bounds) and build each part's shape from it in `snapshot_objects`. Add a `test_scoring.py`
case with a fragmented part.

**C2. Duplicated proxies share one `proxyId` (reproduced).**
`SpatialProxy.EnsureProxyId` only fills an empty ID, so Ctrl+D copies the serialized GUID.
*Proof run:* 11 proxies, 10 distinct IDs. In the condition B snapshot, the right stall's asset
carried the *left* stall's proxy label, and the analysis matched it by position only (flagged
`check`). The window's `AssetsByProxy` map also shows one asset under both proxies.
*Fix:* re-issue the ID when it collides with another proxy in the scene (in `OnValidate`, or
`ObjectChangeEvents` on create/duplicate). Also have `ValidateProxies` refuse to generate with
duplicate IDs.

**C3. The agent can read the target scenes (from the code; no real CLI was run).**
Claude Code is launched with the project root as working directory. `--disallowedTools` blocks
`Bash, Edit, Write, NotebookEdit, WebFetch, WebSearch, Task`, but **not `Read`, `Glob`, `Grep`**,
which need no permission in `-p` mode. The project root contains the exact target layouts
(`analysis/targets/target_X.json`, `targets/src/market.js` → `LAYOUT`). Only the prompt line
"Do not … read … files" stands between the condition A agent and the answer key. Codex's
`--sandbox read-only` still allows reading. Codex also gets **no MCP tool allow-list**, so it can
call MCP for Unity's general tools (`manage_gameobject`, …) and bypass every `AgentSceneOps` rule.
*Fix:* add `Read,Glob,Grep,LS,Agent` to the disallowed list (or restrict the built-in tool set),
run the CLI from the empty run folder (`artifactDir`) instead of `ProjectRoot`, and limit Codex to
the `spatialgen_*` tools in its MCP server config. Then read one run's `agent_stdout.txt` to
confirm no file tools were called.

**C4. Ctrl+Z bypasses the study state (reproduced).**
*Proof run:* after edit 2 was applied, one `Undo.PerformUndo()` reverted it. The study counters
(`kept=2`) didn't change, nothing was logged, and the last `edit2_after` snapshot no longer matches
the scene. Related: generation registers one Undo step *per part* (`AgentJob.undoGroup` and
`AgentRunReport.undoGroup` exist but nothing sets them), `EnsureGeneratedRoot` is undoable, and
`GenerationVersions.ArchiveActive/Activate` are not. Ctrl+Z during generation can therefore leave
half-built or inconsistent versions.
*Fix:* collapse each generation run into one Undo group, call `Undo.ClearAll()` at phase
boundaries (accept, submit), and hook `Undo.undoRedoPerformed` in the study to log and snapshot
(`edit<n>_undone`, which the analysis already accepts).

### Major: workflow and data

**M1. Condition A edits are not "whole-object regeneration".** The docs and the A prompt say
`clear_asset` + `add_parts`. The code only enforces one asset per edit. *Proof run:* a part-level
`update_parts` without clearing was accepted in A. If the thesis describes the A baseline as
whole-object regeneration, enforce it (reject update/remove until the locked asset is cleared) or
change the description.

**M2. Region info is missing from the edit log.** `study_edit_submit` logs no region, so the
`region_center`/`region_size` columns of `edit_coding_template.csv` are empty (reproduced). Coders
of B edits lose the spatial intent. Log `selection.center/size/rotation` and the part IDs.

**M3. Discarded previews are counted as "undone".** `StudySession.DiscardReview` increments
`editsUndone` (edit 3 → `undone=1`) and no closing snapshot is written. Keep a separate
`editsDiscarded` and log the discard reason. The final log line reads as if edits were reverted.

**M4. Failed runs still use up a generation run, and an empty run can be accepted.**
`generationRuns` is incremented before the agent starts. A CLI crash or timeout with
`toolCalls == 0` costs the participant one of their three runs. Run 2 in the proof run (8 of 11
assets empty) became the active version, and `CanAccept` would have accepted it. Decide the policy
on purpose: refund backend failures, and warn on accepting a version with empty assets.

**M5. Pre-study checklist items found on this machine.**
- **Developer mode is ON.** It is stored in EditorPrefs (machine-wide). Every proof-run event was
  logged with `dev_mode=True`, and the run limit is off. Turn it off before sessions.
- `com.coplaydev.unity-mcp` is pinned to `#main`. Pin a commit or tag so the tool surface can't
  change between participants.
- The MCP for Unity server was not running. `Check setup` catches that, but it is the first thing
  that will fail on the day.

### Minor

- **Dead code:** `awaitingDecision`, `pendingUndoGroup`, `pendingJobId`, `KeepPending()`,
  `UndoPending()` and the "Keep / Undo change" UI branch are never set since previews replaced
  live edits. Remove them, or the `_undone` path looks supported when it isn't.
- **Fragile log format:** `extra` is free text `k=v; k=v` that includes participant prompts.
  `score_edits.read_requests` splits on `; key=`, so a request like "taller; size=2 m" gets cut up.
  Write the structured fields as a JSON object in the JSONL.
- `TryParseShape` falls back to `Enum.TryParse`, which accepts numeric strings ("7") and yields an
  undefined `AgentPartShape`.
- `StudyController.RunGeneration/GenerateIdea` are `async void`. An exception in
  `SceneSnapshot.Write` after the await would go unhandled.
- `AgentProcessRunner.Cancel` kills only the CLI process, not its children. `--disallowedTools`
  names `Task`; the current Claude Code sub-agent tool is `Agent`.
- `SceneSnapshot` writes `null` arrays through `JsonUtility`, which turns them into `[]` (empty
  `bounds_*` for empty assets). The analysis copes today by skipping part-less assets.
- `AgentSceneOps.Count` saves the job singleton to disk on every tool call. Harmless now; batch it
  if runs get long.

## 4. Code quality and refactoring

**Overall.** The new agent path is careful code. Rules are enforced server-side rather than
trusted to the prompt. Preview work happens in a private preview scene, so stale tool calls cannot
reach live content. Apply is a validated, single-Undo transaction. Comments explain *why*. The
issues are structural, not sloppy.

**Refactoring is worth doing, but not all of it before the study.** In priority order:

1. **Test the default backend (before the study).** All 66 EditMode tests cover geometry and the
   legacy ComfyUI pipeline. There are **no tests** for `AgentSceneOps` rules, the `StudyController`
   state machine, `SceneSnapshot`'s schema or the CLI argument building, i.e. the path the study
   runs on. The proof-run harness shows this path can be tested headlessly. Promote it to a real
   fixture (with `DisplayDialog` behind a small interface so Submit/Accept can run in batch).
2. **Split the two pipelines.** About 7.3k lines of C# (MeshRegionSplitter, MeshSeamWelder,
   RefinementMeshLoader, multi-view refinement, intent/constraints, router client) plus 2.4k lines
   of Python serve the legacy Router backend. The agent path is about 4k lines. They share one
   assembly and one window (`DrawLegacyPipeline`). `BackendKind.Agent` maps to an
   `IGenerationBackend` that only throws, so the abstraction no longer fits. Move the router path
   into its own asmdef (e.g. `SpatialGeneration.Legacy.Router`), and make the participant window
   study-only.
3. **Break up `AgentSceneOps` (921 lines).** It mixes scope rules, scene mutation, response
   shaping and parsing. The "is this asset in scope" expression is written three times
   (`TryClaimAsset`, `IsPartEditable`, `AssetData.inScope`), and they will drift. Extract a
   `JobScope` (rules, pure, easy to test), keep mutation in `AgentSceneOps`, and move the DTOs out.
4. **Make state explicit.** The study state is spread across static flags (`_busy`, `_starting`),
   two `ScriptableSingleton`s (`StudySession`, `AgentJobState`) and the scene hierarchy found by
   name (`GameObject.Find("GeneratedContent")`). A small explicit state machine for
   `StudySession` (allowed transitions in one place) would remove the dead pending-decision states
   and make C4 easier to fix.
5. **Consistency.** 31 of 70 C# files are in the global namespace (`BackendSettings`,
   `SpatialProxy`, `RegionSelection`, `InteractionLogger`…) while the rest use
   `SpatialGeneration.*`. Other large files: `MeshRegionSplitter` (872 lines),
   `SpatialGenerationWindow` (803 lines).

## 5. Suggested order before data collection

1. C1 (analysis reads fragments) and C2 (unique proxy IDs): small, and both change scores.
2. C3 (tool lock-down and working directory), then one real smoke run per CLI, checking
   `agent_stdout.txt`.
3. C4 (Undo grouping, clearing and logging) and M2/M3 (log the region; separate discards).
4. Decide M1 and M4 on purpose and write the decision into the method section.
5. Turn developer mode off, pin MCP for Unity, start its server, run *Check setup*.
6. Promote the proof-run harness to a regression test, and rerun it after each fix.

*Files: `proof-run/ProofRunWorkflowTests.cs` (harness; it runs in a copy of the project with
`PROOF_SCRATCH` set), `proof-run/fake_claude.sh` (stub CLI, with paths for this machine),
`proof-run/proof_report.txt` (run log).*

## 6. Follow-up: fixes applied (same day)

Each fix has a test that fails on the old code. Every stage was checked with the full Unity
EditMode suite in an isolated clone, the router tests, the analysis tests and the end-to-end proof
run. Final run: **Unity 85 passed, 0 failed, 2 skipped (GPU only); router 12/12; analysis 14/14.**

| Finding | Change | Test |
|---|---|---|
| Quality | `AgentSceneOps` split into operations / `.Rules` / `.Data`; the three copies of the asset-scope rule replaced by one `InScope`; dead keep/undo states removed; `async void` entry points wrapped; numeric shape names rejected; `StudyPaths` (one log root) and a `StudyController.Confirm` seam make the study flow testable headlessly | `AgentStudyWorkflowTests` (new, 17 tests, stub CLI + stub MCP endpoint, runs the real study flow) |
| C1 | `SceneSnapshot` writes `pieces`, `geometry_center/size_world` and `source_part_id` for clipped parts; the analysis builds clipped parts from their pieces (`solids`), adds a `clipped` change kind, and treats a remainder lying inside its source as preserved material | 3 analysis tests (both fail on the old code); 2 Unity snapshot tests |
| C2 | Editor hook re-issues the ID of a duplicated or pasted proxy; a pre-generation repair keeps the ID on the proxy standing where its asset was generated | Ctrl+D via the real duplicate command, same-frame create + duplicate, scripted copies, keeper rule |
| C3 | Claude Code: `--tools "ToolSearch,ListMcpResourcesTool,ReadMcpResourceTool"` (live check: the session's only built-in tool is `ToolSearch`, and a read of the target file failed), more names on the deny list. Both CLIs start in an empty temp folder outside the project. Codex: `--ignore-user-config --ephemeral`, tool features off, `enabled_tools` (checked with `codex mcp get`). *Check setup* warns that Codex keeps a read-only command runner, and when developer mode is on | Argument and working-folder tests |
| C4 | Agent operations add no Undo steps. Ctrl+Z / Ctrl+Y on an applied idea update the counters, log `study_edit_undo/redo` and write `edit<n>_undone` / a new `edit<n>_after`. Undo history is cleared at accept and submit. Discards count as `editsDiscarded`. `score_edits` scores the latest after/undone snapshot | Generation not undoable, undo/redo recorded, submitted scene not undoable, partial edit undone as a whole, latest-snapshot rule |

Still open from section 3: M1 (condition A whole-object rule), M2 (region in the edit log), M4
(failed runs use up a run), M5 (developer mode is on, MCP for Unity pinned to `#main`), and the
minor items. The structural refactors (moving the legacy pipeline into its own assembly,
namespaces) were left for after the study.

## 7. Structural refactor (same day)

- **Assemblies.** The ComfyUI/router pipeline now lives in `Legacy/Router/` as
  `SpatialGeneration.Legacy.Router` (runtime), `…Legacy.Router.Editor`, and
  `…Legacy.Router.Tests.EditMode`. Dependencies point one way, legacy → core.
  The core reaches the legacy UI only through `SpatialGenerationWindow.NonAgentBackendGUI` and
  `SpatialGenerationResearcherWindow.RouterSettingsGUI`, which the legacy panels register.
- **Seams moved to the core.** Settings: `BackendRegistry` now only resolves settings; the backend
  factory became `GenerationBackends` in the legacy assembly. Selection: `RegionSelection` /
  `RegionSelectionManager`. Proxies: `GeneratedMeshMetadata`, and `GeneratedContentRoot`, which
  replaces the root helpers on `GenerationRunner`. The agent's refinement rig is now just a
  `RegionSelectionManager`; the legacy panel adds its `RefinementController` when it needs one.
- **Namespaces.** The 31 global-namespace scripts now have namespaces (tests stay global):
  - `SpatialGeneration`: shared runtime types
  - `SpatialGeneration.Authoring`: core editor tools
  - `SpatialGeneration.UI`: the two windows
  - `SpatialGeneration.Generation.*`: legacy code
  No namespace is named `…Editor` or `…Tools`, because those would shadow
  `UnityEditor.Editor` / `UnityEditor.Tools`.
- **Safety.** All 111 moves kept their `.meta` GUIDs, so scene and asset references are
  unchanged. There are no `[SerializeReference]` fields whose type names could break.
- **Verified.** Full suite: 85 passed, 0 failed (2 skipped, GPU only). With `Legacy/` deleted from
  the clone, the core still compiles and every core test passes. That proves the study path no
  longer depends on the ComfyUI code.

## 8. Codex review (`docs/reviews/2026-11-22/report.md`): status

Checked against the current code, and by rerunning Codex's own fixture
(`evidence/ReviewWorkflowTests.cs`, only renamed APIs adapted) in the clone:
**5 passed, 3 failed** (it was 3 passed, 4 failed on the original code).

| # | Codex finding | Status now | Worth solving? |
|---|---|---|---|
| 1 | Partial geometry invisible to analysis | **Solved.** Their notch cut, re-exported by the new code, scores `['clipped']`, 0.375 m deviation (was `[]`, 0.0). Their point that boxes stand in for spheres, wedges and cylinders is still open | Yes for the fix (done). For the box approximation: write the measurement policy into `analysis/README.md` (cheap), no new geometry code |
| 2 | Condition A doesn't enforce whole-object regeneration | **Solved (§9)**; was open | **Yes, before data collection**, or change the thesis wording. It decides what the A baseline is |
| 3 | Empty/failed run can be accepted | **Solved (§9)**; was open | **Yes**: small. Require geometry for Accept and warn on empty assets |
| 4 | Undo bypasses the study record | **Solved** (their test passes) | — |
| 5 | Discarded previews leave no outcome snapshot | **Partly.** Counted as `editsDiscarded` and logged (`study_preview_discard`), but no snapshot, so `score_edits` never lists them (their test still fails) | Worth a small fix only if the thesis reports discard or failure rates: list discarded/failed edits from the log in the coding template. A discard leaves the scene unchanged, so there is nothing geometric to score |
| 6 | Generation success ignores timeout/crash | **Solved (§9)**; was open | **Yes**, together with #3: one outcome classifier (complete/partial/failed/timed-out), and a run is not counted when the backend failed before any tool call |
| 7a | Setup check only proves HTTP reachability | **Open** | Medium. Fixing #6 makes a wrong pass cost no run; a smoke run before each session covers the rest. An MCP handshake is nice to have |
| 7b | Async guard: `_starting` not shared by Accept / Show version | **Solved (§9)**; was open, confirmed by reading. During the setup check (up to 3 s) the participant can press Accept. The pending run then archives the accepted version and generates in the Refinement phase | **Yes**: small, real bug. Check `_starting` in `CanAccept`/`ShowVersion` and recheck the phase after the await |
| — | MCP for Unity pinned to `#main` | Open | Yes: 5 minutes, freezes the tool surface for the study |
| — | `targets/test.cjs` hard-codes a Linux Chrome and an `/opt` path | Open | Low: only if you rerun the target-scene tests |
| — | Three.js provider not implemented | Out of scope (a plan document) | Only if the thesis claims it |
| — | One documented test command | Open (this review used a script in the scratch folder) | Yes: add it as `tools/run_tests.sh` |
| — | Connected acceptance run per condition | Not possible here (MCP server off) | **Required** before data collection |

Suggested next fixes, in order: 7b, 3 and 6 (one change to the run lifecycle), 2, then the MCP pin
and the test script. Then do one connected acceptance run per condition.

## 9. Codex findings 7b, 3, 6 and 2: fixed

| Finding | Change | Tests |
|---|---|---|
| 7b | `StudyController.IsBusy` (agent running **or** a run starting) gates Generate, Accept, version switching, start/reset, edit, apply and discard. After the setup check's `await`, the run re-checks the phase, condition, active version and run limit. The starting flag is held until the agent run has taken its own busy flag | `StudyActionsWaitWhileARunIsStarting` (setup check delayed 1.5 s; Accept and Reset attempted meanwhile) |
| 6 | `RunOutcome` (Complete / Partial / TimedOut / Failed / Cancelled) decided in one place, `AgentWorkflow.Classify`. Content first, then how the process ended and whether assets stayed empty (condition A included). `success` = Complete. Logged per run, kept in `StudySession.runRecords`, shown on the version buttons | `OutcomeClassification`, `CrashAfterBuildingIsPartialAndNeedsAnExplicitAccept` (stub CLI exits 1 after building) |
| 3 | A run that built nothing is refunded: its empty version is removed, the previous one restored, the run count unchanged, `study_generation_run_refunded` logged. Accept needs geometry. Accepting a partial version shows a warning naming the empty objects, and the confirmation repeats it | `RunThatBuildsNothingDoesNotCount`, `FirstEmptyRunLeavesNothingToAccept`, `VersionWithoutGeometryCannotBeAccepted` |
| 2 | `MustClearFirst`: in a condition A edit, add/update/remove on an existing object is rejected until `spatialgen_clear_asset` on it. `list_parts` reports its parts as not editable, and the prompt says so. Objects the edit creates are exempt | `ConditionA_AnExistingObjectIsRebuiltNotPatched` (incl. the new-object exception) |

Verification: Unity **92 passed, 0 failed** (2 skipped, GPU only), router 12/12, analysis 14/14.
Codex's fixture: **7 of 8 pass**; only `DiscardedIdeaMustWriteAnEditOutcomeSnapshot` (#5, not
in this batch) still fails. End-to-end proof run scored: unchanged generation scores; the
half-built run is now logged as `Partial` instead of a success.

Policy decision to note in the method section: a run counts against the participant's limit
exactly when it produced geometry. Partial runs count and can be accepted, but only after an
explicit confirmation.

## 10. Live acceptance run (MCP for Unity connected)

Run in the open editor through MCP for Unity, with the real Claude Code CLI (`claude-opus-5-5`,
effort medium). It used a temporary scene, a participant called `live_acceptance`, and study logs
redirected to a scratch folder. The study session, job state and `samplescene` were restored
afterwards, and nothing was written to `Logs/`.

**Full EditMode suite in the live editor: 92 passed, 0 failed, 0 skipped.** The two tests that
batch mode always skipped (Scene-view click/drag picking and the review camera render) pass with
a GPU.

| Step | Result |
|---|---|
| B generation (stall + barrel proxies) | Complete: 45 parts, 51 s, 5 turns, ≈ $0.25. Only `spatialgen_*` tools called; no CLI permission denials. Each object fills its proxy |
| C3 lock-down, real run | `command.txt` shows `--tools "ToolSearch,ListMcpResourcesTool,ReadMcpResourceTool"`, strict MCP config, the deny list, and an empty working folder in the system temp directory |
| B refinement (8 awning parts clicked, "blue and white stripes") | Complete in 18 s. Only the selected parts changed (the agent noted the unselected valance stayed red). Live scene unchanged until Apply |
| Apply → Ctrl+Z → Ctrl+Y | Colours follow; kept 1 → 0 → 1, undone 0 → 1 → 0; snapshots `edit1_after`, `edit1_undone`, `edit1_after` |
| B submit | Finished; `final` snapshot written |
| A generation (text only) | Complete: 40 parts, 54 s; stall measured at 1.5 × 2.57 × 2.4 m for a requested 1.5 × 2.6 × 2.4 m; barrel placed 2.5 m to the stall's left |
| A text edit ("barrel 1 m tall, black bands") | Complete in 38 s. The agent followed the new whole-object rule on its own: `list_parts` → `clear_asset` → `add_parts`, zero rejected calls. Barrel 0.60 m → 1.00 m after Apply |
| A submit | Finished; `final` snapshot written |
| Analysis | All live snapshots load; object boxes match what was built |

**New finding (minor).** In condition A an asset's `volume_size` is fixed when the agent creates
it, and no tool can change it. A text edit that resizes an object (barrel 0.6 → 1.0 m) therefore
draws out-of-volume warnings, and the stored volume stays 0.6 m. Geometry and scoring are
unaffected (scores use the parts), but the stale volume is shown to the agent in later edits.
Fix: let `spatialgen_clear_asset` + rebuild in a condition A edit update the volume, or recompute
it from the parts at the end of the job.

**Still open:** Codex #5 (no outcome snapshot for discards), #7a (MCP handshake in *Check
setup*), the MCP for Unity pin, the condition A volume (above), and a Codex CLI run, not tried
because the study uses Claude Code.

## 11. Condition A volume: fixed

- **Change.** `AgentSceneOps.RefitRebuiltVolumes` runs before an idea is captured. In a condition A
  edit, every object the agent cleared or created gets the volume that just encloses its parts
  (`TryFitVolume`: [2·max|x|, max y, 2·max|z|] in the asset frame). `PreviewAsset.ApplyParts` now
  also applies asset-level data, in the same Undo group, so Ctrl+Z restores the old volume. There
  are no out-of-volume warnings in condition A edits, and the prompt says the volume follows the
  rebuilt object. Condition B is unchanged: its volumes are the proxies.
- **Tests.** `ConditionA_RebuiltObjectGetsItsNewVolume` covers no warnings, the volume after
  Apply, untouched objects, the snapshot and Undo. `FittedVolumeIsCentredOnTheFootprint` checks
  the convention on a rotated asset. The condition B preview test now asserts its volume is
  unchanged. Batch: 94 passed (2 GPU skips); live editor: **94/94**.
- **Live, real agent.** "Barrel 0.6 m → 1 m tall": clear and rebuild with no volume warnings.
  Volume 0.50 × 0.60 × 0.50 → 0.49 × 1.00 × 0.49 after Apply (the built extent), back to
  0.50 × 0.60 × 0.50 on Ctrl+Z; the `final` snapshot records 1.0 m.

## 12. "Back to the previous step" in the Spatial Generation window

- **What it does.** `StudyController.StepBack` / `PreviousStep` / `CanStepBack`, plus a
  "← Back to …" button at the right of the step row.
  - Refinement → generation: un-accepts the version. Applied refinements stay; open previews are
    discarded and counted as discarded; the Undo history is cleared.
  - Finished → refinement: reopens the submitted scene.
  - Generation → setup: only before any run is used. Afterwards the button is disabled, and its
    tooltip points to the researcher reset.
- **Safety.** Each step back needs a confirmation and is blocked while a run is active.
- **Logging.** Outside developer mode: a `study_step_back` event and a `back_to_<step>` snapshot.
  In developer mode neither is written.
- **Analysis.** No change needed: it already uses the latest `accepted_run*` and `final`
  snapshots, and edit numbers continue across a step back.
- **Tests.** Five new tests: back from refinement (logged, previews discarded, accept again), not
  logged in developer mode, reopen and resubmit, setup only before a run, and declining the
  confirmation. Batch: **99 passed, 0 failed** (2 GPU skips); the developer-mode preference was
  restored. Live editor (MCP back on): **99/99 passed, 0 skipped**; afterwards there were no
  console errors, and the session, developer mode, log root and scene were as before.
