# SDD ledger — plan: docs/superpowers/plans/2026-10-31-phase-3.4-minimal-apis-plan.md

Spec: docs/superpowers/specs/2026-10-31-phase-3.4-minimal-apis-design.md; impact analysis
docs/superpowers/specs/2026-10-31-phase-3.4-minimal-apis-impact-analysis.md wins where they differ.
Branch: worktree branch tracks origin/phase/3.4-minimal-apis; push with git push origin HEAD:phase/3.4-minimal-apis.
Start: 39154106.

## Pre-flight scan

| Rows | Produces / consumes | Finding |
|---|---|---|
| T1 -> T9, T17, T20 | Render(IServiceProvider) used by T9; snapshot read after MVC removal | Renderer must be stack-neutral: read policies, anonymous, rate limit, versions and content type from ApiDescription.ActionDescriptor.EndpointMetadata, not controller attributes. AddOpenApi keeps ApiExplorer for endpoints after T20, so the provider survives. Ruling P1. |
| T2/T3 -> T5 | pinned shapes -> ApiProblem, ApiBody, ApiQuery | consistent |
| T4 <-> T5 | ErrorBody added to the context | T4 says add in T5 if T4 lands first: ErrorBody lands in T5. Ruling P2. |
| T4 <-> T16, GlobalExceptionHandler | context reworked to DefaultIgnoreCondition.Never | CONFLICT with "No contract change": the SSE writer serializes AgentEventDto through ApiJsonSerializerContext with WhenWritingNull today, so 8 optional members would start appearing as null in every stream event. GlobalExceptionHandler also writes ProblemDetails through it; ProblemDetails carries its own WhenWritingNull attributes, verify. Ruling P3. |
| T4 <-> T20 | Browser fixture: AddDaedalusEndpoints beside AddControllers, removed in T20 | consistent |
| T5 <-> T15/T16 | ToProblem beside ToActionResult; MVC overload removed in T16 | consistent |
| T8 <-> T9 | T8 drops TaskExecutionsController from ApiAuthorizationTests' list, T9 rewrites | consistent |
| T12/T14/T17/T18/T16 <-> T3 | Location per 201 | consistent; ApiLocation follows T3 |
| T17 <-> T1 | line-242 reflection -> snapshot metadata or ruling | consistent |
| T19 <-> T5 | WorkflowEngineEnabledEndpointFilter | consistent |
| T20 grep gate <-> tests | AddMvcCore forbidden in tests | Unit tests that use AddMvcCore().AddApiExplorer() are removed by the port tasks; consistent |
| Each task internally | | T1 says 61 lines; impact analysis says 61 endpoints: consistent. T4 step 1 expects FAIL then PASS: consistent. Others self-consistent. |

Rulings:
- P1: snapshot renderer reads endpoint metadata, stack-neutral — T9 and the whole port depend on it — if wrong,
  the snapshot changes when a controller is ported and the check becomes a re-baseline every task.
- P2: ErrorBody registered in the context in T5 — the plan allows it — none.
- P3: T4 keeps SSE event bytes unchanged: nulls stay omitted in stream events, e.g. through a dedicated
  source-generated context for AgentEventDto with WhenWritingNull, pinned by a unit test; ProblemDetails from
  GlobalExceptionHandler verified unchanged — the spec's no-contract-change rule outranks the plan's single-context
  wording — if wrong, the Web's SSE parser sees extra null members, which it would likely tolerate.
  Goes into the rulings doc as R12 in T4.

## Tasks
- P4: rulings numbering — T1 writes R1-R10 as the plan lists them plus P1 as R11; later rulings, including T6's
  endpoint pattern and P3, take the next free number — the plan's "R11" for T6 is a label, not a reserved slot —
  if wrong, only numbering differs from the plan text.
Task 1: dispatched (BASE 39154106, implementer a68171fd73ad6252f, opus)
Task 1: implementer DONE_WITH_CONCERNS 74908dc2, 0ae0e8bc. Review a363477f: spec ✅, Approved, 3 Minor.
OWNER DECISION 2026-10-31 (AskUserQuestion): versioning is inert on MVC today (no .AddMvc()); keep wire, drop dead
  code. Plan impact: Global constraint "api-supported-versions stays" now means "stays absent"; Review Focus 3 =
  unsupported version served like any other, as today; Task 3 pins absence of header and 200 on ?api-version=2.0;
  Task 4 adds NO Asp.Versioning.Http and no Mvc bump (R8 withdrawn), AddDaedalusEndpoints carries no versioning;
  recipe drops .WithApiVersionSet/.HasApiVersion, Map…Endpoints takes no ApiVersionSet; Task 20 removes
  AddApiVersioning, [ApiVersion] x13, Asp.Versioning.Mvc, global using, Browser fixture copy, snapshot versions column.
Task 1 fix round 1: resumed implementer with owner decision + Minor 1 (content column = last content-type
  metadata, R12) + Minor 2 (whitespace-only diff note); Minor 3 dropped with reason in R11.
Concerns carried: TasksController responses list application/json,text/json,text/plain -> its port declares all
  three (T17). Stream endpoint content -> endpoint-level metadata (T16).
Task 1: fix round 1/5 (4 addressed, 0 open; commits 0ae0e8bc..85c8f66b). Re-review ae7556ea: all addressed.
Task 1: complete (commits 3915410..85c8f66, review clean). Plan amended for versioning: 0e2e2f54.
Carry to T16: on minimal APIs an endpoint-level ProducesAttribute adds "200 - text/event-stream" to the responses
  column where MVC has "200 - -": T16 needs another declaration or a named snapshot ruling.
Carry: rulings append from R13 (P3 SSE null omission becomes the next free R in T4).
Task 2: dispatched (BASE 0e2e2f54, opus). Draft PR #427 opened with owner OK for CI.
Task 2: implementer DONE_WITH_CONCERNS d9e3b689 (rulings R13-R19: projects 404 has {error} body so bodiless 404
  pinned on workflow-runs; no bodiless 409; query message "The value 'abc' is not valid." without "for take";
  401 reachable over HTTP; malformed-JSON key $.name; charset=utf-8 + relaxed escaping). Review dispatched a72b6751.
Env: local test runs need DOTNET_USE_POLLING_FILE_WATCHER=1 (inotify limit held by other sessions). Tell every
  implementer.
Task 2: review a72b6751 spec ✅ Approved, 3 Minor -> fix round 1 dispatched to implementer (plan/R7 wording, misplaced Red line, pin action-level 400 bodies).
Task 2: fix round 1/5 (3 addressed, 0 open; commits d9e3b689..610ad275). Re-review a42aabbf: all addressed.
  Minor dropped: R7 line wider than the doc's wrap, cosmetic.
Task 2: complete (commits 0e2e2f5..610ad27, review clean).
CI run 41230471628 on d9e3b689: aot-analysis all 5 success (no baseline move); Build & Test cancelled by next push.
Task 3: dispatched (BASE 610ad275, sonnet->opus? chose opus: exact header/pipeline pins).
Task 3: implementer DONE_WITH_CONCERNS 854e9b13 (R20-R24; Location absolute + class-name case via CreatedAtAction,
  manufacture relative /api/workflow-runs/{id}). Review dispatched a452c043.
  Concern 1 verified and FILED: #428 POST /api/codeanalysis with [] requirements -> 500 (jsonb ""), Domain fix,
  outside the port. Tell T18: its port keeps the 500 (no contract change) and names #428 in a remark.
  Concern 2: path base untested (TestServer has none) -> T5 ApiLocation.For must use request.PathBase like MVC's
  LinkGenerator; carry to T5 brief.
  Local ~Api run: 2 ManufactureSandboxEndToEndTests failures, "Tool source 'roslyn' is unavailable" in Docker
  sandbox, VM-environmental; CI is the check.
Task 3: review a452c043 spec ✅ Approved; Important = #428 filed (+comment: omitted field too); 3 Minor -> fix round 1 dispatched.
Task 3: fix round 1/5 (3 addressed, 0 open; commits 854e9b13..2f04e2a7). Re-review a2fd49bd: all addressed.
Task 3: complete (commits 610ad27..2f04e2a, review clean). CI 610ad275 green incl. Build & Test + aot x5.
Task 4: dispatched (BASE 2f04e2a7, sonnet). Carries P3 (SSE null omission) as next free R; ErrorBody in T5 (P2).
Task 4: NEEDS_CONTEXT — clearing Http JsonOptions chain to context-only breaks Development /openapi/v1.json (500:
  OpenAPI reads Http JsonOptions for MVC action types not in the context).
  Ruling: option C — register every MVC action response + [FromBody] request type in the context now and add a
  Development OpenAPI 200 integration test as a standing guard — spec demands every type in the context and no
  reflection fallback; A is a known breakage, B the forbidden fallback — if wrong, the context grows a few tasks
  early, work the ports needed anyway. Resumed implementer.
Task 4: implementer DONE b2cb0a96 (R25 SSE context, R26 all types + OpenApiDocumentTests). CI 2f04e2a7 green. Review dispatched a60dabf9. Read aot-analysis on b2cb0a96 (RDG on) before completing.
Task 4: review a60dabf9 Needs fixes (2 Important: false Red line on null pin; comments claim context options govern endpoints — Http JsonOptions do; 4 Minor). Fix round 1 dispatched.
Task 4: fix commit ba265cab; aot-analysis Api/Cli/Migrations/Sandbox success on b2cb0a96 (no baseline move with RDG on). Re-review dispatched a2416e42.
Task 4: fix round 1/5 (6 addressed, 0 open; commits b2cb0a96..ba265cab). Re-review a2416e42: all addressed.
  Minor dropped: R25 line width, cosmetic. Out-of-scope stale R20 ("context declares WhenWritingNull") -> T5 fixes.
Task 4: complete (commits 2f04e2a..ba265ca, review clean).
FACT for all later tasks: endpoint JSON settings come from Http JsonOptions (AddDaedalusEndpoints); the context
  only decides which types resolve. New request/response types must be added to ApiJsonSerializerContext or
  OpenApiDocumentTests goes red.
Task 5: dispatched (BASE ba265cab, opus).
Task 5: implementer DONE_WITH_CONCERNS 78341751 (R27-R36: ContentType member, UnsupportedMediaType 415, ApiQuery class + ValueOr/HasValue, MapApiGroup helper, PrdRequestDto changed in Application R34). Carry to T19: ?status= null vs ""; empty ?take= on int?; FromServices gateway before 503 filter.
Task 5: review a46063ad Needs fixes (Important: 415 not pinned on MVC; R37 accepted hex/empty/repeated query
  deviations). Ruling: reproduce MVC's hex prefixes + pin empty/repeated — no-contract-change rule; a small parse
  branch — if wrong, a few lines of parsing nobody sends. Fix round 1 dispatched. After it: update the plan's
  shared-definitions table per reviewer's list (ContentType, UnsupportedMediaType, ApiQuery class + ValueOr,
  MapApiGroup row, Http JsonOptions, filter order, WorkflowRuns notes).
Task 5: fix round 1/5 (4 addressed, 0 open; 78341751..8d9be4ba, commit typed fix(api) by mistake — note for squash). Re-review af2e1d12 yes. Fix round 2 dispatched for 2 minors + 2 observations (MVC-side Reds, ucs-2/utf8/quoted charset pins, Guid parity). Plan shared-definitions edited, uncommitted, commit after T5.
Task 5: fix round 2/5 (3 addressed, 0 open; commits 8d9be4ba..6536e574). Re-review ac272fe3: yes. Plan table
  8e64b0f4 verified against code. Minor dropped: plan step-4 line width, cosmetic.
Task 5: complete (commits ba265ca..6536e57, review clean).
Task 6: dispatched (BASE 6536e574, opus) — first port, sets the pattern; record pattern as next free R.
Task 6: NEEDS_CONTEXT — group-root endpoints render "api/agents/" (RoutePatternFactory.Combine adds "/"); 12 lines.
  Ruling R39: renderer trims one trailing "/" — MVC never emits one, routing and OpenAPI paths equal — if wrong,
  a real trailing-slash route renders the same, no client impact. Return type IReadOnlyList kept (matches MVC);
  OpenAPI tags order change accepted (dev doc, same set). Resumed implementer.
OWNER 2026-10-31: weekly token budget at 90% — pause after Task 6 commit, no Task 6 review now; review is first step on resume.
Task 6: committed eb148832 (pushed); implementer stopped while waiting on aot-analysis; NOT reviewed. Paused for owner's token budget.
RESUME 2026-11-04: worktree .claude/worktrees/phase-3.4-resume, detached at origin 01d05153.
Task 6: CI 41244031846 on 01d05153 green; aot-analysis Api "all counts match" 1502, no baseline move.
Task 6: review dispatched (BASE 6536e574, HEAD eb148832, opus).
Task 6: review a0f423b4 spec ✅, Needs fixes: Important = WithTags in R38 pattern unguarded by any test,
  recipe text lacks it. Minor 1 ApiAuthorizationTests loses Agents: dropped, Task 9 rewrites the test over the
  endpoint table. Minor 2 ThrowIfNull on RDG-resolved service: Ruling: decide in this round, keep only if an
  analyzer such as CA1062 requires it, record in R38 — the pattern propagates — if wrong, one line per handler.
Task 6: fix round 1 dispatched to fresh implementer, original from 10-31 session gone (sonnet).
Task 6: fix round 1/5 (2 addressed, 0 open; commits 01d05153..b84c28cd). Re-review a30257bb: all addressed.
Task 6: minor (deferred): tag test pins only api/agents via Single(StartsWith "api/agents"), which breaks
  when AgentSessions maps api/agents/sessions. Ruling: Task 7 turns the pin into an exact group-route -> tag
  table and each port adds its row — the guard must catch a wrong tag, not only a missing one — if wrong,
  a few table rows.
Task 6: complete (commits 6536e57..b84c28c, review clean). Plan recipe amended for WithTags + null-check rule.
Env: solution-wide Release build fails locally on NU1903 for WebAssembly.Server 10.0.13 in
  Playwright.Browser; #399, the Renovate bump to 10.0.14, is red on ZeroAlloc.TestHelpers 1.8.0 contentFiles
  tripping Sonar S125/S2234. Checking branch CI on b84c28cd.
Task 7: dispatched (BASE 62f3ab68, sonnet).
Task 7: implementer DONE c472bb8f (R40 tag table, R41 Resend IResult + DefaultTake operation transformer;
  unbound {scheduleid} rendered without :guid so Resend binds scheduleId and discards it). Review dispatched.
CI 41781446870 on 62f3ab68: aot x5 success; Build & Test FAIL NU1903 WebAssembly.Server 10.0.13,
  GHSA-g27p-qf6j-c96c, warning as error. Blocks every push; asking owner.
Task 7: review ac874729 spec ✅, Needs fixes: Important = inline OpenAPI query-param lambda is pattern-level,
  make a shared tested helper. Minors: take test lacks type/pattern; 3 reworded Reds in MvcBehaviour not
  seen failing; grammar; split a double Red. All sent in fix round 1 to the implementer.
OWNER 2026-11-04: NU1903 fix = separate fix(deps) PR to main, ASP.NET Core family 10.0.14; then merge main into
  the phase branch. Worktree .claude/worktrees/aspnetcore-10.0.14, branch fix/aspnetcore-10.0.14.
Task 7: fix round 1/5 (5 addressed, 0 open; commits c472bb8f..8fdae425). Re-review a39c299f: all addressed.
Task 7: minor (deferred): Declare adds the query parameter without a same-name check, harmless while ApiQuery<T>
  contributes none; T : struct overload has no string default, consistent today.
Task 7: complete (commits 62f3ab6..8fdae42, review clean). Recipe points at WithQueryParameter.
Task 8: dispatched (BASE f4e6c56f, sonnet). Schedules aot Api unchanged, CI 41782966375.
Task 8: implementer DONE 11f3a1c9 (R42 ILoggerFactory, log category becomes TaskExecutionEndpoints). Review dispatched.
Task 8: review a3c1564a spec ✅ Approved, 4 Minor (CreateLogger Type overload; pin category+event ids; 503 Red wording; shared binding test helper) -> fix round 1 to implementer, owner rule fixes minors in the loop.
Task 8: fix round 1/5 (4 addressed, 0 open; commits 11f3a1c9..92cdc1de). Re-review ae3955ad: all addressed.
  Minor dropped: stray blank line in Unit csproj, cosmetic.
Task 8: complete (commits f4e6c56..92cdc1d, review clean). Recipe: logging pattern + PagingQueryBinding.
Task 9: dispatched (BASE 9c6eda73, sonnet).
Task 9: implementer DONE_WITH_CONCERNS c140f83e. Ruling: Red 2 on AgentMemoriesController instead of
  ProjectsController, whose actions carry their own policies — the Red must prove the MVC side fails, which the
  plan's choice cannot — if wrong, none. Guard test non-empty table: Red not applied, sent to review.
Task 9: review aebddea1 ❌ guard test Red never applied (Important); 3 Minor (shared predicate, partial-table assertion, line split) -> fix round 1.
Task 9: fix round 1/5 (4 addressed, 0 open; commits c140f83e..ac306f61). Re-review a5bec973: all addressed.
Task 9: minor (deferred): Route helper now Trim('/') not TrimStart, both sides consistent; Red commands not logged.
Task 9: complete (commits 9c6eda7..ac306f6, review clean).
Task 10: dispatched (BASE ac306f61, sonnet).
Task 10: implementer DONE e1c96deb (R43 ApiProblem.AsGiven for explicit 404 ProblemDetails; modelId required 400 in handler, combined-error case differs from MVC). Integration count 801 vs 835 earlier: flagged to review.
Task 10: review a28f3c82 ❌ Important: required modelId checked in handler, not merged with binding problems,
  400 body differs from MVC -> required mode in ApiQuery via [Required]. Check: OpenAPI required for modelId.
  Minors: R43(c) IResult rationale doubtful, missing Reds, AsGiven doc. Fix round 1 sent to implementer.
  Integration count drop 835->801 = Task 9's planned theory-to-table rewrite, not lost cases. Whole-branch review: note.
Task 10: fix round 1/5 (5 addressed, 0 open; commits e1c96deb..0b1ebae3). Re-review a0ff4796: all addressed.
Task 10: fix round 2/5 (1 addressed: stale Red comment; commit babc8335), verified by controller via git show.
Task 10: minor (deferred): [Required] ApiQuery<int> with empty value answers "The value '' is invalid." as MVC
  does for value types; no current port needs it.
Task 10: complete (commits ac306f6..babc833, review clean). Recipe: [Required] + AsGiven.
Task 11: dispatched (BASE 8e2ee910, sonnet).
Ruling T11: brief says llm-operations on both Prd endpoints; snapshot has convert-to-tasks write-operations — snapshot is the target, no-contract-change outranks the plan sentence — if wrong, none: the wire stays as MVC serves it.
Task 11: implementer DONE 2489e1ab (R44 ErrorDetailBody keeps Prd's {error,details} 500 body). Review dispatched.
Task 11: review a6899736 Needs fixes: Important ErrorDetailBody never serialized in a test; Minor missing 500 Reds, long lines; architecture-diagrams dropped -> Task 21 owns docs. Fix round 1 sent.
Task 11: fix round 1/5 (3 addressed, 0 open; commits 2489e1ab..d2fb4316). Re-review a2a83760: all addressed.
  Dropped: two test lines over 120 columns, cosmetic, the file already has such lines.
Task 11: complete (commits 8e2ee91..d2fb431, review clean).
AOT: CI 41793484837 on d2fb4316: Api 1514 vs baseline 1502, rose by 12. Keys diff vs 41784541664 (f4e6c56f):
  all 12 are IL2026/IL3050 on TypedResults.Json<T>(value, JsonSerializerOptions?, ...) in the catch-alls and
  Prd's 400 helper — TaskExecutionEndpoints:85, CostAnalyticsEndpoints:153, PrdEndpoints:112,115.
  Ruling: shared ApiJsonResult<T> in Endpoints/, like ApiProblemResult: resolves JsonTypeInfo from Http
  JsonOptions at execution, no endpoint metadata, typed value for tests; replaces every TypedResults.Json — the
  constraint forbids new AOT warnings and suppressions, and JsonHttpResult has no options-free AOT-safe overload
  that keeps Http JsonOptions — if wrong, one small result type. Cross-task fix dispatched before Task 12.
AOT fix: DONE c1220e39 (R45 ApiJson.Of / ApiJsonResult<T>). Review dispatched; waiting on CI AOT count with no pushes.
AOT fix: review a870ce5f spec ✅ Approved, 3 Minor (stale JsonHttpResult in R42-R44, lost escape in a Red, one
  Red over three assertions) -> fix round 1 to implementer.
AOT: CI 41795247052 on c1220e39: Api 1502, all counts match; Build & Test still red on NU1903 only (#430).
  TaskExecutions, CostAnalytics and Prd add no warnings after R45. No baseline move.
AOT fix: fix round 1/5 (3 addressed, 0 open; commits c1220e39..18e445b5), controller-checked via git show.
AOT fix: complete (commits d2fb431..18e445b, review clean).
PAUSE 2026-11-04 for context: handoff to a fresh session; next is Task 12.
