# Code Archeologist — Architecture & Product Design Specification

**Version:** 0.1 (design-review draft)  
**Date:** November 8, 2026  
**Status:** Proposed for owner approval; **implementation not authorized**  
**Release policy:** Public previews with strict version 1.0 acceptance gates

## 1. Executive summary

**Code Archeologist** is a local-first, cross-platform, multi-agent software intelligence application. It ingests one or more source repositories, identifies their architecture and functionality, constructs an evidence-backed dependency and behavior model, predicts possible consequences of code changes, and develops a persistent, human-editable body of institutional knowledge.

It is not merely a documentation generator or a chat interface over source files. Its distinctive feature is **evidence-driven convergence**: deterministic analyzers establish what they can prove; specialized Microsoft Agent Framework (MAF) agents investigate harder questions; and a non-LLM Evidence Quality Engine checks provenance, completeness, contradictions, and freshness before an investigation may be declared complete.

The application will use a **React dashboard**, **C#/.NET 10 Web API**, **Microsoft Agent Framework**, **local Ollama models**, **SQLite-centered persistence**, **hybrid lexical/vector search**, a **typed knowledge graph**, and a **living, bidirectional Obsidian-compatible Markdown vault**. It will be downloadable from GitHub and usable without commercial APIs, mandatory Docker, or a database server.

### Product promise

A developer who inherits an unfamiliar system can ask:

- What does this application do, and how is it organized?
- Where does a given business rule live?
- Trace this workflow across the frontend, API, backend, and database.
- Which components depend on this class, endpoint, table, contract, or service?
- What could break if I remove or change this component?
- Why do we believe these conclusions, and what evidence is still missing?
- What changed since the last analysis, and which prior findings are now stale?

Every substantive answer must provide inspectable evidence, a repository revision or other provenance anchor, and explicit uncertainty where appropriate.

## 2. Decision record: approved versus proposed

### 2.1 Explicitly agreed in the architecture interview

| Area | Decision |
|---|---|
| Foundation | .NET 10 Web API, React, Microsoft Agent Framework |
| AI | Ollama and local LLMs by default; cloud-ready provider abstraction |
| Analysis | Polyglot from the start with deep C# analysis |
| Repository organization | Multi-repository workspaces |
| Knowledge | Living, bidirectional Obsidian vault; persistent memory, vector search, and technical graph |
| Human governance | Intelligent reconciliation with human oversight |
| Execution | Read-only analysis by default; any code execution requires explicit sandbox approval |
| Investigation | Adaptive, evidence-driven orchestration |
| Task discipline | Every investigative task traces to a question, evidence gap, or failed quality gate |
| Completion | Deterministic quality measures with thresholds configurable by investigation type |
| Dashboard | Interactive Mission Control with pause, resume, evidence inspection, and redirection |
| Initial scope | Complete Investigative Foundation (Release B) |
| Deployment | Local-first, cloud-ready; portable and easy for first-time users |
| Operating systems | Windows, Linux, macOS from the first release |
| Extensibility | Discoverable, versioned plugins; separately installed capabilities |
| Evaluation | Hybrid lab: controlled examples, real repositories, deliberate changes, held-out cases |
| Publication | Public preview builds; rigorous acceptance for version 1.0 |

### 2.2 Recommended details requiring design sign-off

The following are **recommendations**, not independently approved implementation selections: SQLite as the primary operational store, a relational node/edge graph, one Markdown vault per workspace, single-user desktop-first operation, local vector indexing technology, streaming transport, precise model defaults, release asset architecture matrix, and specific quality formulas/thresholds. Section 23 lists remaining validation points.

## 3. Principles and invariants

1. **Evidence before interpretation.** A model's assertion is not itself proof.
2. **Two authorities.** Source snapshots are authoritative for observed code; accepted, provenance-tagged human knowledge and decisions reside in the vault. The operational evidence ledger records how conclusions were obtained. Conflicts are surfaced, not silently resolved.
3. **No false certainty.** A quality score is a repeatable evidence-sufficiency measurement, *not* an uncalibrated probability that a claim is true.
4. **No unmotivated work.** Every investigation task has a documented rationale and success criterion.
5. **No useless loops.** Repeated rewording or agent agreement does not improve confidence; new, independent evidence must do so.
6. **Non-destructive by default.** Analyzed repositories and human-authored notes are not modified without appropriate permission.
7. **Portable data.** Workspaces must be backup-friendly and migratable; derived indexes must be rebuildable.
8. **Local without lock-in.** The fully functional default requires no cloud credentials, while model, analysis, storage, and integration contracts remain extensible.
9. **Honest degradation.** Missing dependencies, unsupported languages, dynamic behavior, or failed plugins reduce coverage and appear explicitly in results.
10. **Actual observability.** Dashboard activity represents real persisted events, not simulated animations.

## 4. Scope and boundaries

### 4.1 Version 1.0: Complete Investigative Foundation

- Create, reopen, back up, and restore workspaces containing one or more repositories.
- Ingest local folders, Git sources, and uploaded archives; associate findings with immutable source snapshots.
- Perform deep C# structural/semantic analysis, useful TypeScript/React and selected SQL analysis, and targeted configuration inspection.
- Discover major components, modules, public contracts, code relationships, architectural boundaries, patterns, deviations, and principal functional workflows.
- Build a queryable typed graph across repositories, with explicit uncertainty where relationships cannot be established.
- Combine lexical/symbol lookup, full-text search, vector similarity, and graph traversal for evidence retrieval.
- Run adaptive, checkpointed MAF investigations with justifications, typed outputs, skeptical review, quality gates, and evidence-plateau handling.
- Answer questions with traceable citations; explain direct versus hypothetical impacts of removing or changing code.
- Generate and synchronize Obsidian-compatible Markdown knowledge; retain human edits and use a conflict-review queue.
- Provide live React Mission Control with status, findings, evidence, quality diagnostics, pause/resume, user-directed investigation, and knowledge review.
- Discover separately installed, versioned capability plugins, subject to trust and permission controls.
- Offer prebuilt Windows, Linux, and macOS distributions and a guided local-Ollama setup.
- Include a reproducible evaluation laboratory and documented release gates.

### 4.2 Explicitly deferred beyond v1.0

- Automatic modification or refactoring of user source code.
- Proactive execution of imported programs, builds, or tests without case-specific approval.
- A complete isolated execution environment and advanced dynamic runtime instrumentation (the approval interface and architectural boundaries are designed now; implementation is later).
- Full support for every language or SQL dialect.
- Hosted multi-tenant operations, enterprise organization-wide discovery, a remote plugin marketplace, and enterprise access-control administration.
- Guarantees of identifying every dynamic, reflective, or externally implemented dependency.

**Scope note:** A user may ask for source-impact reasoning in v1.0, but outcomes that require execution must remain hypotheses or explicitly documented limitations until approved execution is available.

## 5. Logical architecture

The system is organized as independent logical subsystems behind stable contracts:

1. **React client:** Workspace explorer, investigation Mission Control, architecture and dependency explorer, impact explorer, knowledge review, conversational inquiry, settings.
2. **.NET 10 API / application services:** Coordinates users, repository snapshots, investigations, persisted events, approvals, searches, and exports; exposes local-only endpoints by default.
3. **Ingestion service:** Safely clones or snapshots inputs, fingerprints contents, identifies source revisions and languages, enforces exclusions, and constructs a repository inventory.
4. **Analyzer host:** Executes approved *non-executing* analyzers and normalizes findings into the shared evidence and graph contracts.
5. **Graph and retrieval services:** Maintain typed relationships and exact/full-text/vector search, with revision-aware query routing.
6. **MAF investigation engine:** Chief Archeologist, specialist agents, Skeptic, planning/state coordination, retry and recovery logic.
7. **Evidence Quality Engine:** Deterministic quality measures, scope denominators, hard gates, regressions, evidence plateaus, and terminal outcomes.
8. **Knowledge service:** Obsidian synchronization, note provenance, ownership sections, human edits, conflict proposals, and approved revisions.
9. **Plugin host and registry:** Manifest validation, capability discovery, version compatibility, permissions, and explicit enablement.
10. **Persistence:** Embedded operational storage, durable evidence and decisions, checkpoint state, and rebuildable graph/search views.

MAF orchestrates cognitive tasks; **it must not be the sole custodian of repository state, truth classifications, quality measurement, or authorization**.

## 6. Core domain and identity model

Minimum logical entities:

| Entity | Required meaning |
|---|---|
| Workspace | Stable logical software-system identity independent of URL or folder name |
| RepositorySource | Origin, access method, display name, allowed scope |
| RepositorySnapshot | Immutable source identity: commit/tree fingerprint or content manifest |
| SourceArtifact | File, configuration unit, generated artifact, document, or schema item |
| Symbol | Language-aware identity for type, method, property, function, endpoint, etc. |
| GraphNode / GraphEdge | Typed component or relationship with source anchors and evidence |
| EvidenceItem | Identified observation, method of collection, origin, timestamp, hashes, revision |
| Claim / Finding | An explicit proposition; status, evidence links, conflicts, and confidence measures |
| Investigation | Question, type, scope, profile, owner, state, and run identifiers |
| InvestigationTask | Parent question/gap/gate, assigned capability, acceptance criteria, result |
| QualityAssessment | Engine version, metrics, denominators, hard gates, outcome, rationale |
| KnowledgeNote | Vault file identity, ownership regions, provenance, reconciliation status |
| HumanDecision | Approval, correction, rejection, or architectural annotation with audit trail |
| PluginDescriptor | Identity, version, capabilities, permissions, compatibility |

Node/edge identity must be **stable within a revision** and support best-effort cross-revision mapping. A method's line number alone is never a durable symbol ID. Cross-repository links must retain both snapshot identities and link-derivation evidence.

Evidence records distinguish: **direct static observation**, **source-derived deduction**, **approved runtime observation**, **human assertion**, and **LLM interpretation**. The system must never promote the latter two automatically into compiler-verified facts.

## 7. Ingestion, snapshots, and source safety

### Accepted entry points

- Local folder: read files from the selected path and create an indexed snapshot without modifying the original.
- Git: clone/fetch into application-managed storage, resolve a branch/tag/commit, and record its exact immutable identity.
- ZIP or equivalent supported archive: validate, unpack into application-managed storage, inventory, and analyze.
- Private Git sources: use explicitly configured authentication; never persist raw credentials in the vault or logs.

### Ingestion phases

1. Validate origin and scope, user exclusions, size limits, and archive safety.
2. Establish isolated snapshot storage; reject archive path traversal, symlink escape, excessive expansion, and malicious paths.
3. Identify repositories, branches/commits, languages, project files, package manifests, and candidate entry points.
4. Apply ignore rules for secrets, binaries, vendor caches, generated files, and irrelevant artifacts, with override controls.
5. Calculate fingerprints and determine incremental changes since the prior snapshot.
6. Dispatch eligible analyzers and explicitly record unavailable analysis capabilities.
7. Preserve source references anchored to the snapshot, file path, symbol, and line range where possible.

**Crucial caveat:** Some compiler project loaders, package restores, build tooling, and custom scripts can execute untrusted repository content. The default non-executing analysis path must not invoke arbitrary targets or scripts. If full semantic loading cannot be achieved safely, provide degraded static results and an explicit explanation. Any execution-capable investigation requires the later, separately approved sandbox path.

## 8. Polyglot analysis contracts

### First-wave analyzers

- **C#/.NET (deep):** Roslyn syntax and symbols, projects, namespaces, types, inheritance, implementations, references, direct calls where resolvable, ASP.NET endpoints, DI registrations, selected EF Core relationships, test associations, and architecture boundaries.
- **TypeScript/React (useful semantic depth):** Modules, imports/exports, symbols, React components, props, hooks, API clients, typed contracts, and route references, using an appropriate TypeScript compiler-backed adapter where feasible.
- **SQL (dialect-qualified):** DDL schema, tables/views, SQL routines, referenced objects, queries, and database-facing relationships; SQL Server prioritized, with explicitly declared dialect coverage.
- **Configuration and manifests:** JSON/YAML, project/package manifests, selected Docker and deployment metadata, app configuration, routes, bindings, and secrets *references* (not secret values).
- **Fallback:** Unsupported source types receive inventory, lexical search, and clear limitation markers rather than fabricated semantic conclusions.

All analyzers emit a common model of nodes, relationships, anchors, evidence, limitations, and analyzer version. Deep semantic understanding must not be inferred from merely parsing syntax.

Cross-language relationships—such as React client → HTTP route → ASP.NET endpoint → service → persistence—must be labeled as **resolved**, **strongly indicated**, **candidate**, or **unresolved**, based on the discovery mechanism. Dynamic routes, reflection, runtime dependency injection, configuration overrides, and external services are not silently treated as verified links.

Compiler-backed TypeScript tooling may need a bundled helper runtime; this is a packaging validation task. End users should not be required to install Node.js merely to use a released application.

## 9. Knowledge graph and impact model

The first implementation is proposed as **typed node/edge records in SQLite**, not a standalone graph database. Nodes represent artifacts, symbols, components, APIs, database objects, workflows, and other meaningful entities. Edges represent relations such as:

- contains / defines / inherits / implements
- calls / references / injects / depends on
- exposes endpoint / invokes endpoint / reads / writes / publishes / consumes
- participates in workflow / supports business rule / documented by
- supersedes / derived from / conflicts with

Every edge records the extraction method, supporting evidence, snapshot, and status. Graph queries must distinguish directly verified dependencies from transitive impact paths and unresolved dynamic behavior.

### Change-impact investigations

Given a proposed modification or removal, the engine should identify:

1. Verified syntactic and symbol-level dependents.
2. Direct and transitive dependency paths.
3. Affected public contracts, modules, workflows, and data objects.
4. Possible behavioral or runtime consequences requiring inference.
5. Unknowns introduced by dynamic dispatch, external consumers, missing code, or unapproved execution.
6. Recommended additional evidence and tests, without claiming those tests were run.

An impact report must distinguish **proved structural breakage**, **supported risk**, **plausible hypothesis**, and **unknown impact**. A high graph reachability count is not proof that every reachable path will break.

## 10. Retrieval, memory, and storage

### Proposed storage topology

**SQLite-first:** durable workspace metadata, task and event history, evidence ledger, claims, decisions, graph records, and full-text indexes. SQLite FTS5 is the baseline for textual retrieval. Vector search must use an abstract storage contract; the exact embedded vector provider is to be validated for cross-platform packaging, reliability, and quality. PostgreSQL or a specialized engine may be introduced later without changing the domain model.

**Obsidian-compatible vault:** curated and accepted Markdown knowledge, user decisions, explanations, investigation summaries, and human edits. Obsidian itself need not be installed or running.

**Source snapshot storage:** immutable or content-addressed repository inputs and manifests.

**Derived indexes:** graph projections, embeddings, symbol indexes, and search caches; versioned and rebuildable from preserved evidence and accepted knowledge. Durable source evidence and user decisions must *not* be treated as disposable caches.

### Retrieval strategy

Use query classification and multi-stage retrieval:

1. Exact workspace/repository/snapshot scoping.
2. Symbol and filename lookup for precision.
3. Full-text lexical matching for identifiers and keywords.
4. Vector similarity on meaningful code/documentation units.
5. Typed graph expansion and evidence re-ranking.
6. Assemble an answer context with source anchors, statuses, and provenance.

Do not embed raw source by indiscriminate fixed-length splits. Preferred units are types, functions, methods, relevant blocks, endpoint contracts, documentation sections, and architecture notes. Store embedding model/version, content fingerprint, and scope; invalidate stale vectors after edits.

### Three distinct memories

- **Working memory:** the current investigation's tasks, retrieved context, and MAF checkpoint state.
- **Repository memory:** persistent, revision-aware accepted facts, structural findings, unresolved issues, and prior evidence.
- **Investigative memory:** earlier questions, decisions, contradicted theories, user corrections, and evidence plateaus.

Previous model opinions are candidate context, never automatically authoritative evidence.

## 11. MAF agents and orchestration

### Agent roles (logical responsibilities)

| Role | Responsibility |
|---|---|
| Chief Archeologist | Convert the user's goal and failed gates into a scoped, auditable plan; delegate justified tasks |
| Architecture Investigator | Establish boundaries, design patterns, dependencies, and possible deviations |
| Functional Investigator | Reconstruct workflows, entry points, business rules, and behavior |
| Dependency Investigator | Trace modules, interfaces, packages, data and cross-repository relationships |
| Impact Investigator | Analyze consequences of a proposed modification/removal and identify uncertainty |
| Skeptic / Reviewer | Challenge claims, identify conflicts, ask for missing corroboration |
| Archivist | Generate traceable knowledge notes and propose vault updates |

These are **roles**, not a requirement for seven simultaneously resident models. Early versions may reuse one local model with role-specific prompts and shared typed tools.

### Required execution protocol

1. Receive a scoped question or quality-gate failure.
2. Create a task with **exactly identified purpose**, parent investigation, missing evidence, acceptance criteria, and intended tools.
3. Validate scope and permissions; deduplicate equivalent pending work.
4. Retrieve existing evidence before launching fresh model reasoning.
5. Run deterministic analyzers and bounded specialist investigations, parallelizing independent work.
6. Require structured findings with source references and explicit unsupported claims.
7. Perform skeptical review for material or uncertain conclusions.
8. Evaluate quality in the independent engine; commit valid new evidence.
9. Generate *targeted* follow-up tasks only when a specific gap or failed gate justifies them.
10. End with verified completion, documented limitations, or a distinct failure/interruption state.

Persist tasks, checkpoints, model/tool versions, evidence, significant decisions, and event summaries. Resume without repeating already verified work. Support model context compaction without discarding durable source evidence.

### Operational safeguards

Ollama removes per-token API fees, **not** context limits, CPU/GPU contention, latency, memory consumption, or disk use. Allow long research but impose configurable concurrency, elapsed-time, disk, and zero-progress protections. No arbitrary model-call ceiling should be mistaken for an evidence-quality criterion.

## 12. Deterministic Evidence Quality Engine

The engine is **non-agent application logic**, versioned and testable independently of model outputs. It judges evidence sufficiency and compliance, not omniscient correctness.

### Claim-level measurements

- **Source validity:** citation target exists at the claimed snapshot and resolves to the correct artifact/region.
- **Provenance strength:** type of evidence and whether the stated support is direct, indirect, human-provided, or inferred.
- **Coverage:** proportion of declared relevant scope actually examined, with independently calculated denominators.
- **Corroboration:** independent supporting analysis methods, not repeated model votes.
- **Contradictions:** material inconsistent findings or counterevidence remaining unresolved.
- **Freshness:** evidence still applies to the requested repository revision.
- **Analyzer limitations:** missing projects, failed symbol binding, unavailable runtimes, unsupported languages, and externally unknown behavior.

A versioned rubric maps these features to a **deterministic evidence sufficiency score** (0–100) and hard validation gates. The precise weights must be established using benchmark evidence rather than assumed. A confidence figure must be accompanied by the rubric version and must not be presented as a probability of truth unless empirically calibrated.

### Investigation-level measurements

At minimum: structural coverage, required-finding support rate, functional workflow coverage, citation validity, unresolved critical contradictions, remaining unexamined high-impact artifacts, and plateau status. Denominators come from source inventory, graph contracts, and investigation scope—not agent self-assessment.

**Profiles:** Architecture Overview, Functional Explanation, Dependency Mapping, Change Impact, Critical Change Impact. Each profile configures required metrics, hard gates, threshold values, and permissible limitations. User-defined profiles may extend these defaults. The illustrative percentages discussed in the interview are **not ratified release thresholds**.

### Convergence and outcomes

- **Complete:** all hard gates and scope-specific numeric requirements pass.
- **Complete with limitations:** investigation has reached an evidence plateau or approved stop condition; missing coverage or disputed claims are explicitly reported. Never display this as equivalent to full completion.
- **Failed/interrupted:** infrastructure, model, tool, ingestion, or persistence failure prevents a reliable assessment.

A plateau is detected from lack of **new valid evidence or measurable gate improvement** across sufficiently different, justified attempts, not from lack of prose changes. The system reports which evidence is unavailable and what would be required to resolve the gap.

## 13. Living Obsidian vault and reconciliation

### Ownership and authority

- Source snapshots remain the authority for *what source exists*.
- Accepted, attributed Markdown notes are the durable knowledge interface for *interpretation, rationale, and human expertise*.
- The evidence ledger and human-decision history are durable operational records of *why findings were accepted or rejected*.

No single layer should overwrite an incompatible authority silently.

### Recommended organization

One vault per workspace, with folders for System Overview, Architecture, Components, Functional Behavior, Dependencies, Data Flows, Impact Investigations, Research, Architecture Decisions, and Human Knowledge. This layout may be customized while retaining stable note identities.

Notes use frontmatter for stable IDs, note type, authorship, evidence/revision anchors, verification state, and last validation. Body text uses normal Markdown and internal links. Avoid one note per trivial method; keep high-cardinality symbol detail in graph/search while generating notes for meaningful architectural and functional concepts.

### Two-way synchronization

1. Watch or rescan vault changes and record content fingerprints.
2. Identify human-modified regions and retain their ownership.
3. Automatically refresh only clearly system-owned, nonconflicting content.
4. Preserve human-authored sections, annotations, and architecture decisions.
5. When source facts diverge from a note, mark affected claims **stale/disputed**, preserve the previous text/history, and propose reconciliation.
6. Present conflict reasons and source citations in a React Knowledge Review queue.
7. Apply human decisions atomically with an audit trail and reversible history.

Never use blind whole-file regeneration. File renames and edits in external editors require stable note identity and collision handling. If vault files are unavailable, the core investigation must continue with explicit deferred synchronization status.

## 14. React experience and observability

### Major screens

- **Welcome / setup:** Ollama connectivity, model selection/download consent, data directory, platform prerequisites, and workspace creation.
- **Workspace Explorer:** multi-repository tree, snapshots, languages, capabilities, coverage, entry points.
- **Mission Control:** live agent/task lanes, evidence events, quality gates, known gaps, timers, failures, pause/resume, reprioritization, redirect and escalation actions.
- **Architecture Explorer:** navigable module boundaries and typed graphs; filtering by evidence class, repository, revision, direct/transitive edge.
- **Impact Explorer:** choose an artifact and a proposed change; distinguish verified effects, supported risks, hypotheses, and unknowns.
- **Ask the Archeologist:** scoped conversational questions with linked evidence, related notes, and persistent investigation history.
- **Knowledge Review:** inspect human edits, proposed updates, stale assertions, and conflicts; approve/reject with audit history.
- **Settings / Plugins:** local models and embeddings, quality profiles, exclusions, storage paths, plugin permissions, backups.

### Real-time contract

The backend emits stable, persisted investigation events; the UI projects them into task boards, progress indicators, and collaboration views. Use a transport appropriate for local streaming (SignalR or server-sent events; **not yet selected**). Do not animate fictional work, reveal hidden model reasoning, or treat raw token streaming as a substitute for semantic progress.

User actions must be durable and idempotent where appropriate. Pausing must reach a safe checkpoint, canceling must be distinct from pausing, and resuming must preserve all verified artifacts.

## 15. Discoverable plugin system

### Extension classes

1. **Language / framework analyzers** (e.g., additional languages, SQL dialects, framework contracts).
2. **Investigation capabilities** (e.g., security analysis, external static analyzers, test associations).
3. **Source and knowledge integrations** (e.g., alternate Git providers, MCP tools, publication destinations).
4. **Provider adapters** for models, embeddings, search, or storage where needed.

### Plugin contract

Each extension declares stable ID, semantic version, host contract range, capabilities, supported inputs, required permissions, output/evidence schema, supported platforms, native prerequisites, and diagnostic behavior. The host can enumerate and enable compatible extensions without a core rebuild; incompatible or failing plugins fail isolatedly where the chosen hosting model permits.

**Trust policy:** All third-party plugins require explicit installation and activation. In-process .NET assemblies are fully privileged application code and **must not** be misrepresented as sandboxed. Consider out-of-process plugins with explicit resource/permission boundaries as the preferred extensibility direction, but validate platform cost and packaging before finalizing the execution model. Plugin tools cannot override task justification, evidence validation, sandbox approvals, or vault ownership rules.

A plugin catalog/marketplace, hot reload, and automatic remote updates are not v1.0 requirements. Version compatibility, diagnostics, and an independently packaged test plugin **are** required.

## 16. Security, privacy, and permission model

- Bind the default dashboard/API to localhost; require a deliberate secure configuration to expose it to a network.
- Local Ollama is the default; no source content is sent to remote models unless the user explicitly configures and authorizes that provider.
- Treat repository files, README instructions, comments, tool outputs, Markdown notes, and third-party plugin output as **untrusted input**, including prompt-injection attempts.
- Validate upload archives, Git origins, symlinks, path traversal, file size and depth; avoid writing into imported sources.
- Exclude or redact common secrets and sensitive files from prompts, embeddings, exports, and logs by default; allow explicit overrides with warnings.
- Authenticate to private sources through an appropriate OS-backed credential facility where available; never embed secrets in knowledge notes.
- Record approval identity, task, purpose, expected commands, permissions, and outcome for any eventual sandbox run.
- Enforce no network access/least privilege in later execution sandboxes by default; a user approval is not proof that a sandbox is secure.
- Do not permit plugin execution to bypass imported-code execution policy.
- Support deletion of source snapshots, embeddings, investigative records, and vault references with transparent scope; backups may require separate retention management.

## 17. Packaging, installation, and portability

### First-time user goal

1. Download an OS-appropriate packaged release from GitHub.
2. Launch it without installing .NET SDK or Node.js.
3. Complete a first-run wizard that checks Ollama and requests permission before downloading missing models.
4. Select or create a workspace and vault location.
5. Import a repository and start analysis in the browser-hosted React dashboard.

Self-contained .NET publishing reduces runtime setup but **does not guarantee zero native prerequisites**; each operating-system/CPU package must be tested. Native vector libraries and compiler workers need explicit platform compatibility validation. Ollama itself may need installation, and model downloads require network access initially; normal analysis should work offline once dependencies and models are locally available.

### Portability rules

- Keep application binaries separate from user data.
- Use portable workspace manifests and relative paths where feasible.
- Support backup/export containing vault, durable findings, evidence, decisions, settings, and repository snapshot manifests; optionally include source snapshots and rebuildable indexes.
- Provide restore/import with schema migration and consistency checks.
- Do not make a user's original local repository location the only recoverable source identity.
- Ship clear release assets for Windows, Linux, and macOS, with the exact supported CPU/OS combinations stated; add platforms only after representative clean-machine testing.

## 18. Reliability, performance, and incremental analysis

- Fingerprint source artifacts, analyzer outputs, relevant graph portions, vector chunks, and vault notes.
- Reuse unchanged verified results; invalidate affected relationships, embeddings, and downstream findings on new snapshots.
- Detect repository rescans that change no relevant evidence and skip redundant analysis.
- Isolate and retry transient model/provider faults without losing completed deterministic work.
- Distinguish transport success, agent completion, quality-gate success, and vault synchronization success.
- Bound resource use with user-visible profiles and backpressure rather than token-price-based quotas.
- Support long investigations with persisted checkpoints and event histories, but surface clear stop/plateau reasons.
- Measure cold import time, incremental refresh time, retrieval latency, peak local RAM/VRAM, storage expansion, and investigation throughput on known test machines.

## 19. Hybrid Evaluation Laboratory

Evaluation must be independent of the evidence the agents use to justify their own findings.

### Corpus

- **Controlled fixtures:** small systems with ground-truth call edges, endpoints, workflows, architectural rules, and deliberately incorrect comments.
- **Curated real repositories:** diverse structures and realistic complexity; independently annotated expected findings.
- **Change experiments:** known edits or removals, with independently measured effects where safe and permitted.
- **Held-out cases:** repositories/scenarios not used to tune prompts, rubrics, or special handling.

### Core metrics

- Graph edge **precision and recall** for supported relationship categories.
- Cross-language and cross-repository relation precision/recall where ground truth exists.
- Functional-claim correctness, evidence citation validity, and unsupported-claim rate.
- Change-impact precision and recall, with separate evaluation of direct breakage and uncertain risks.
- Evidence calibration: how well quality bands predict independently established correctness, rather than model confidence alone.
- Preservation of human notes and correct stale/conflict handling after source changes.
- Recovery quality after interruption, plugin failure, missing models, and corrupt/partial indexes.
- Cross-platform installation and first-run completion on clean supported machines.

Benchmarks must be versioned, reproducible, and honest about partial ground truth. A high aggregate score cannot conceal a critical class of fabricated evidence or unauthorized execution.

## 20. Release gates

### Blocking invariants for every publicly distributed preview

- No source modification or code execution without explicit permission and the appropriate secure execution facility.
- No invented citation represented as verified; no LLM claim automatically elevated to direct evidence.
- No silent overwriting of protected human-authored knowledge.
- No falsely successful investigation when mandatory quality gates fail.
- No new investigative task lacking its parent question/gap/gate and acceptance purpose.
- No silent loss of completed evidence after pause/resume or recoverable failure.
- No mandatory paid cloud APIs or database servers for core local use.
- Clearly label incomplete capabilities, unresolved unknowns, and preview limitations.

### Additional requirements before v1.0

All section 4.1 capabilities must be demonstrated against the acceptance suite on representative supported platforms. The independently distributed plugin test must work, first-run onboarding must be validated, exported workspaces must restore, and minimum quantitative metrics must be established from baseline and held-out evaluation results before final release. Set numeric targets through benchmark calibration and owner approval, not arbitrary aspirations.

## 21. Phased implementation plan (proposed; no implementation authorized)

| Phase | Objective | Verifiable exit artifact |
|---|---|---|
| 0. Design baseline | Ratify architecture, contracts, quality rubric, test corpus and release matrix | Approved specification and architectural decision records |
| 1. Portable shell | .NET API + bundled React + setup wizard + SQLite workspace state | Clean launch on supported OS targets, baseline import |
| 2. Structural foundation | Safe ingestion, C# deep analyzer, TypeScript/SQL inventory and semantic adapters | Ground-truth structural comparison and source citations |
| 3. Shared intelligence | Typed graph, hybrid retrieval, cross-repository relationships, incremental snapshots | Measured retrieval and graph benchmark results |
| 4. Investigative engine | MAF agents, task lineage, evidence engine, quality profiles, checkpoints | Quality gate, plateau, and crash/recovery tests |
| 5. Institutional memory | Bidirectional Obsidian vault, ownership and conflict review | Human-edit preservation and reconciliation tests |
| 6. User experience and extensibility | Interactive Mission Control, impact explorer, plugin loader | End-to-end investigation and independently packaged plugin |
| 7. Hardening and release | Cross-platform distribution, hybrid evaluation lab, migration/backup, accessibility, docs | Preview gates and ultimately full v1.0 acceptance |

Phases may overlap internally but must not be marked complete without their stated independent evidence. Public previews begin only when the relevant blocker gates pass.

## 22. Illustrative v1.0 acceptance scenarios

1. **Cold import:** A new user installs the app, configures Ollama, ingests a .NET/React application, and reaches an architecture overview without developer tooling.
2. **Multi-repo trace:** A React UI in one repo calls an API in another; the graph provides the source-backed route and clearly flags any guessed segment.
3. **Change-impact analysis:** Removing a registered service yields a list of direct references, probable affected workflows, unknown dynamic consumers, and exact evidence.
4. **Unsupported behavior:** Reflection or an unavailable external service causes an explicit limitation instead of a confident invented dependency.
5. **Quality iteration:** A failing gate creates a traceable, targeted follow-up task; only new supporting evidence improves the assessment.
6. **Plateau:** After materially distinct attempts fail to resolve missing evidence, the run ends with a clear limitations report and no false completion.
7. **Vault edit:** A developer corrects a business explanation in Obsidian; subsequent scans preserve it and open review if the source appears to conflict.
8. **Incremental update:** Only changed artifacts and affected derived knowledge require recomputation; unchanged accepted findings remain available.
9. **Interruption/restart:** An investigation is paused and restarted without discarding completed evidence or rerunning completed tasks unnecessarily.
10. **Plugin extension:** An independently packaged analyzer is discovered, enabled with permission, supplies typed evidence, and cannot bypass governance.
11. **Portable migration:** Export a workspace and restore it on a different supported OS, preserving vault knowledge and durable investigation history.
12. **Held-out test:** A previously unseen repository is analyzed with independently scored relationship, citation, and impact accuracy.

## 23. Final validation items before implementation approval

**No new product-direction interview is required.** These are design choices or engineering spikes to resolve in the implementation plan:

1. Confirm SQLite as the operational source of durable evidence and graph records; choose a cross-platform embedded vector option only after a packaging/retrieval benchmark.
2. Ratify the workspace/vault mapping (recommended: one vault per workspace) and full backup/restore contents.
3. Define the exact typed evidence and plugin manifests, including plugin process isolation and trust boundaries.
4. Validate the safe non-executing Roslyn and TypeScript pathways for arbitrary repositories, and how degraded semantic resolution is reported.
5. Choose live-event transport and checkpoint persistence mechanisms consistent with the current MAF .NET SDK.
6. Establish target OS/architecture combinations and model/minimum-memory guidance based on actual clean-install tests.
7. Agree on rubric dimensions, hard gates, calibrated threshold profiles, and plateau criteria using initial benchmark evidence.
8. Define licensing, contribution policy, and public repository documentation for the GitHub release.

These are **not authorization to begin building**. The project owner should approve the specification and any material amendments first.

## 24. Definition of approval

Approval of the design means that Code Archeologist will be developed against the agreed decisions in section 2.1 and the accepted portions of this proposal. It does **not** authorize unrelated capabilities, source-changing agents, unapproved repository execution, or silent weakening of evidence/integrity gates.

Any substantial change in first-release scope, knowledge authority, security model, platform commitments, or completion rules requires an updated design decision before implementation.

---

## Appendix A. Current technical reference points

These references validate feasibility but do not determine the precise dependency versions or final package selections:

- Microsoft Agent Framework overview and runtime concepts: https://learn.microsoft.com/en-us/agent-framework/overview
- MAF .NET workflow graphs, events and checkpointing: https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/
- MAF human-in-the-loop approvals: https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop
- MAF Ollama provider integration: https://learn.microsoft.com/en-us/agent-framework/agents/providers/ollama
- Obsidian local Markdown vault model: https://obsidian.md/help/data-storage
- Obsidian YAML properties: https://obsidian.md/help/properties
- Obsidian internal linking: https://obsidian.md/help/links
- SQLite FTS5: https://www.sqlite.org/fts5.html
- .NET self-contained and platform-specific distribution: https://learn.microsoft.com/en-us/dotnet/core/deploying/

## Appendix B. Decision summary

**Approved direction:** Local-first, cloud-ready; Windows/Linux/macOS; multiple repositories per workspace; polyglot analysis with deep C#; MAF adaptive investigation; justified tasks; deterministic and configurable quality gates; evidence-driven convergence; Obsidian bidirectional knowledge with protected human ownership; memory, vector retrieval, typed graph; discoverable/versioned plugins; read-only default and explicit sandbox approval; interactive Mission Control; hybrid evaluation; complete investigative foundation; public previews with strict v1.0 requirements.

**Next action:** Review and revise this specification. **Do not create application code or modify GitHub repositories until explicit owner approval.**