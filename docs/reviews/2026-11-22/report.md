SpatialGenUnity implementation review — 22 November 2026

**Verdict: the main authoring path is substantially implemented and its existing tests pass, but the workflow is not yet reliable enough for a controlled study. Targeted refactoring is warranted; a rewrite is not.** Five issues were reproduced in executable checks, and two further issues were identified by code inspection. A fresh live model-generation run was not verified because neither configured local service endpoint was reachable.

Reviewed revision: `a5f1c17209a752c49dc48517341de53edbaa3cd3`, plus the pre-existing, untracked `analysis/` directory. The analysis findings therefore concern the current workspace, not necessarily a committed deliverable. Production source, settings, and scene content were not edited. Review fixtures ran in `/tmp/spatialgen-review-20261122/project`, an APFS copy of Assets, Packages, ProjectSettings, and Library, using Unity **6000.3.9f1**. This validates the current resolved environment, not a clean dependency installation.

**Proof-run results**

| Check | Observed result | What it establishes |
|---|---|---|
| Existing Unity EditMode suite | **68 passed, 0 failed, 0 skipped** | Current scripts compile; tested geometry, selection, previews, and transaction behaviors work. The rendered preview test also passed. |
| Existing Python router suite | **12 passed** | Tested capability reporting, multiview routing, cropping, masks, coordinate conversion, and fallback logic work under fixtures/mocks. |
| Existing scoring tests | **10 passed** | Current synthetic scoring expectations hold. |
| Added workflow regression checks | **3 passed, 4 failed** | Version archive/restore, invalid-input preflight, and geometry apply/undo/redo pass; four workflow expectations fail below. |
| Added real partial-removal export | **1 passed** | Unity removed 40% of a part and exported its clipped geometry. |
| Scoring that real export | **Defect reproduced** | Scorer returned no change and maximum surface deviation `0.0`. |
| Synthetic study pipeline | **Completed** | 12 synthetic participants, 48 generation snapshots, 48 edits, and a statistical report. This checks execution, not scientific validity. |
| Target-scene browser test as documented | **Blocked at startup** | `targets/test.cjs` cannot resolve its hard-coded `/opt/npm-tools/node_modules/playwright`; it also specifies Linux Chrome. No browser interaction assertions ran. |
| Current service readiness | **Unavailable** | HTTP probes could not connect to `127.0.0.1:8080/mcp` or `127.0.0.1:8001/health`. |
| Live Unity interface | **Read-only inspection** | Refinement screen visible with part selection guidance, disabled Refine for an empty prompt, applied count, Submit scene, and a prominent developer-mode badge. |

The original project venv lacked SciPy. Scoring was run in a separate temporary venv after installing the declared requirements. The resolved package versions are saved with the evidence. Existing local reports include a successful 37-part generation and two completed preview ideas, but these are historical evidence and do not certify today's end-to-end setup.

**Findings, ordered by impact**

**1. [P1] Partial geometry changes are invisible to the analysis pipeline — reproduced.**

[scene.py:154](</Users/alo/Master Thesis/SpatialGenUnity/analysis/spatialscore/scene.py:154>) reconstructs every part as its original bounding box and drops `clipped_geometry_local`. [score_edits.py:116](</Users/alo/Master Thesis/SpatialGenUnity/analysis/score_edits.py:116>) compares transforms, shape, segments, and color, so a removal that only changes fragment triangles is invisible. The Unity fixture cut a unit-box seat down to volume `0.6`; the snapshot contained three clipped cells, yet the scorer returned `[]` for change kinds and `0.0` surface deviation. This can invalidate change detection, boundary integrity, and clearance measurements after local edits. Primitive boxes also approximate spheres, wedges, and cylinders, which needs an explicit measurement policy.

Fix: preserve fragment triangles in the analysis representation, compare actual geometry, and use geometry-aware surface/contact measurements where the study claims geometric accuracy. At minimum, detect fragment changes and explicitly mark unsupported measurements unavailable rather than reporting zero. Keep the exported Unity pair as a regression fixture.

**2. [P1] Condition A does not enforce whole-object regeneration — reproduced.**

The documented baseline and [AgentPromptBuilder.cs:178](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/AgentPromptBuilder.cs:178>) require clearing and rebuilding the selected object. However, [AgentSceneOps.cs:272](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/AgentSceneOps.cs:272>) accepts `UpdateParts` in a text-only edit. The claim check only locks the operation to one asset; it does not enforce a rebuild. A test recolored the seat directly while leaving the leg intact, without calling `ClearAsset`, and the operation succeeded.

This is an experimental-condition integrity issue: an agent can perform the local edit that condition A is supposed to exclude. Fix the rule in the operation layer with a tracked rebuild state and condition-specific allowed operations. Tests should reject direct update/removal and additions to an existing asset before clearing it, while preserving the documented new-object exception.

**3. [P1] An empty or failed generation can be accepted into refinement — reproduced.**

[StudyController.cs:148](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/StudyController.cs:148>) checks only phase, a positive run number, and agent inactivity. The test removed `GeneratedContent`, set run 1 as active, and still observed `CanAccept == true`. After a process/setup failure occurring after run allocation, the participant can accept an empty run; preview creation then has no model to work on. Exhausting the run limit makes this especially awkward.

Fix: store an explicit outcome per version and require existing, usable geometry for acceptance. Distinguish infrastructure failure from a participant generation attempt. Deliberately accepting a partial result can remain possible, but should be explicit. The existing invalid-input preflight correctly preserves both the run count and the active scene; retain that behavior.

**4. [P1] Standard Unity Undo changes geometry without updating the study record — reproduced.**

[StudyController.cs:252](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/StudyController.cs:252>) clears the review and increments `editsKept` after apply. The Undo callback in [SpatialGenerationWindow.cs:44](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/UI/SpatialGenerationWindow.cs:44>) only repaints. Applying a red-seat preview and calling `Undo.PerformUndo()` restored the white seat, but `editsKept` remained 1 and `editsUndone` remained 0. No new undo snapshot is emitted by this path. The old `UndoPending` method does not handle the current preview flow.

Geometry Undo/Redo itself passes. The defect is that counters, event history, and outcome snapshots diverge from the scene. Fix: associate each applied edit with a persistent transaction identifier and track its applied/undone/redone status, writing corresponding snapshots/events. Define whether “kept” means ever applied or currently applied, then make UI and analysis agree.

**5. [P2] Discarded previews disappear from edit discovery — reproduced.**

[StudyController.cs:262](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/StudyController.cs:262>) logs a discard and clears the review, but writes no terminal edit snapshot. [score_edits.py:62](</Users/alo/Master Thesis/SpatialGenUnity/analysis/score_edits.py:62>) discovers only edits with a `before` plus `after` or `undone` file. The added test confirmed that discarding a completed idea leaves no discoverable outcome. Submitting while previews exist uses the same discard mechanism.

Fix: persist explicit outcomes such as applied, discarded, failed, and cancelled, and preserve them in analysis. Do not treat a discarded draft as a zero-change successful edit; measure draft quality separately if the study requires it. The current counter also calls a never-applied discard “undone,” which obscures the distinction.

**6. [P2] Generation success ignores backend failure and timeout — code-confirmed, not dynamically fault-injected.**

[AgentWorkflow.cs:106](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/AgentWorkflow.cs:106>) sets success to `partsAdded > 0 && !cancelled`, ignoring `timedOut`, `backendError`, nonzero exit, unfinished status, and empty assets. A process that builds a few parts and then crashes can be logged as successful; its partial scene may be useful, but that is a different outcome. The preview path already applies stricter failure checks.

Fix: use a shared outcome classifier with complete, partial, failed, cancelled, and timed-out results. Keep content availability separate from process success, and let acceptance policy use both. Add a fake-process regression test for a nonzero exit after a successful tool mutation.

**7. [P2] Setup checks prove HTTP reachability, not a usable MCP connection — code-confirmed.**

[AgentProcessRunner.cs:113](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/AgentProcessRunner.cs:113>) treats any HTTP response as success. [AgentWorkflow.cs:190](</Users/alo/Master Thesis/SpatialGenUnity/Assets/SpatialGeneration/Editor/Agent/AgentWorkflow.cs:190>) additionally checks executable existence and URL syntax, but not MCP protocol readiness, connected project identity, required tools, or CLI authentication/model access. An unrelated server returning 404 can pass, and an MCP service with no Unity editor attached can pass.

Fix: perform an MCP-aware handshake and verify the intended Unity instance and required tools, with separate CLI/authentication readiness feedback. Preserve valid MCP endpoint behavior such as HTTP GET returning 405; simply requiring GET 200 is not an adequate fix. Revalidate session/phase after asynchronous setup: the controller currently releases `_starting` before committing the run, and acceptance/version changes do not share that guard.

**Workflow completeness and smoothness**

| Stage | Assessment |
|---|---|
| Researcher setup | Present, with actionable endpoint-down guidance. Deeper readiness is missing. Current visible session is in developer mode with unlimited runs. |
| A/B generation | Implemented through agent scene operations; fresh CLI → MCP → Unity generation remains unverified this session. |
| Version browsing | Archive/restore fixture passes. Run validity and acceptance need stronger guards. |
| Selection and partial editing | Strong tested foundation, including rotated selections, preserved geometry, and partial-removal behavior. |
| Preview/regenerate/browse | Implemented with an isolated preview scene and persisted ideas; existing tests confirm regeneration from the baseline and conflict rejection. |
| Apply/Undo/Redo | Geometry behavior passes; study accounting does not follow Undo. |
| Discard and submit | UI/controller paths exist. Discard outcomes are incomplete; submission confirmation was not clicked in the user's live session. |
| Recovery | Interrupted-job clearing and preview recovery exist. A real domain reload during a live model run was not exercised. |
| Export and analysis | Synthetic pipeline executes, but actual partial geometry and some edit outcomes are misrepresented or omitted. |
| ComfyUI alternate path | Router and geometry tests pass; no live diffusion/lifting job was run. |
| Three.js proposal | The provider described in `docs/threejs-procedural-generation-plan.md` is not implemented: no `tools/threejs_generator`, HouseSpec provider, or procedural enum. `targets/` contains study reference scenes, not that provider. |

The participant interface has sensible phases, guidance, and a review-before-apply flow. Smoothness is weakest around infrastructure failures, stopping/recovering, and explaining which outcomes were accepted or discarded. The visible refinement screen is readable; this was a limited UI inspection, not a timed usability study or a complete click-through. Live latency, model quality, long-running cancellation, reconnect behavior, and real-session submission/reload persistence still require a connected integration run.

**Code quality and recommended refactoring**

The strongest design choices are worth preserving: thin MCP wrappers over a separately testable operation layer; an isolated preview scene with job tokens; validation before applying changes; one Undo group for refinement; serialized authoring data that rebuilds transient meshes; and focused geometry/transaction tests. These are substantial foundations.

The main weakness is fragmented lifecycle ownership. `StudyController`, `StudySession`, `AgentWorkflow`, `AgentJobState`, and static process flags jointly decide whether actions are valid. Outcomes are spread across booleans and semicolon-delimited log text. The older pending-edit path remains beside the preview path, and the tests emphasize geometry more than transitions and measurement contracts.

Recommended order:

1. **Correct study contracts first.** Enforce condition A, fix analysis of clipped geometry, and make every edit outcome explicit. Version snapshots/events with a schema and stable session/edit IDs; retain transform and selected-part data needed for scoring.
2. **Centralize lifecycle transitions.** Introduce a small study/run state model with explicit outcomes and guards. Make generation/preview controller operations awaitable tasks; keep `async void` only as thin UI entry points. Recheck session identity after awaits and inject backend/process/snapshot services so failures can be tested deterministically.
3. **Unify transaction accounting.** Connect apply, undo, redo, discard, cancel, and submit to a single outcome recorder. Migrate or remove the obsolete pending-decision route only after documenting compatibility expectations.
4. **Extract responsibilities where tests benefit.** Split the roughly 921-line `AgentSceneOps` into authorization/condition rules, part mutations, and read models. Separate the roughly 803-line participant window from its legacy ComfyUI UI. Refactor geometry algorithms cautiously; size alone is not evidence that they need rewriting.
5. **Make verification reproducible.** Provide one documented test command, portable target-browser paths, and declared test dependencies. Pin the MCP dependency to a reviewed revision; the manifest follows `#main` even though the current package lock resolves a particular revision. Preserve the environment lock and add repeatable CI or an equivalent release gate.

Before collecting study data, require the five reproduced regressions to be resolved, the two inspected failure paths to have fault-injection tests, and one connected acceptance run for each condition covering generation, retry/failure, version selection, refine/regenerate, apply, undo/redo, discard, submission, and reload. Verify that exported measurements agree with the actual resulting geometry. If the Three.js proposal is in scope for the claimed workflow, it remains a separate implementation project.

**Evidence and reproduction**

The [evidence folder](</Users/alo/Master Thesis/SpatialGenUnity/docs/reviews/2026-11-22/evidence>) contains NUnit XML, the added [workflow fixture](</Users/alo/Master Thesis/SpatialGenUnity/docs/reviews/2026-11-22/evidence/ReviewWorkflowTests.cs>), Python test summaries, dependency versions, a synthetic analysis report, and the real partial-edit snapshots. The four failing tests assert intended behavior; their failures are the review findings, not failures of the original 68-test suite.

To rerun the added Unity fixtures, copy `ReviewWorkflowTests.cs` into `Assets/SpatialGeneration/Tests/EditMode` **in an isolated project**, then run Unity with `-batchmode -projectPath <copy> -runTests -testPlatform EditMode -testFilter ReviewWorkflowTests -testResults <output.xml> -logFile <output.log>`. Do not add `-nographics` when checking the rendered preview test. The fixture replaces its test scene and resets its study singleton, so it belongs in the isolated copy.

From the repository root, run router tests with `python -m unittest discover -s tools/comfy_router_backend/tests -v`, scoring tests with `python analysis/tests/test_scoring.py`, and the real-geometry reproduction with `python docs/reviews/2026-11-22/evidence/check_partial_scoring.py` after installing the relevant requirements. The latter intentionally asserts the currently reproduced defect and prints the incorrect scorer result; replace that expectation with the corrected behavior when fixing it.

The full temporary project and Unity logs remain at `/tmp/spatialgen-review-20261122`. No implementation fixes or refactors were applied as part of this review.
