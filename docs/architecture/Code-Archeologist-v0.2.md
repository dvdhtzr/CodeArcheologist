# Code Archeologist — Architecture & Product Design Specification

**Version:** 0.2 (technical design and approval candidate; v0.1 baseline preserved)  
**Date:** November 8, 2026  
**Status:** Technical design candidate; owner sign-off pending; **implementation not authorized**  
**Release policy:** Public previews with strict version 1.0 acceptance gates

**Revision note:** Sections 1–24 and Appendices A–B retain the v0.1 product baseline and decision states. Appendix C is the new v0.2 detailed technical design proposal. None of Appendix C’s proposed ADRs is approved merely by being included here; see C1 and C19. The original v0.1 attachment is unmodified.

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
---

# Appendix C. Version 0.2 technical architecture and approval candidate

**Status of this appendix:** Engineering recommendations for owner review; **not ratified**. It supplements, but does not silently replace, Sections 1–24. Section 2.1 remains the set of ratified product-direction decisions. Where this appendix supplies a more precise implementation proposal for an item listed in Section 2.2 or 23, it is still a **candidate decision** until the owner explicitly accepts it. In any conflict, record an ADR and seek owner approval; do not infer a superseding requirement.

**Review date:** November 8, 2026. **What has been done:** read the v0.1 specification and verify selected public technical references. **What has not been done:** no implementation, no package integration experiments, no benchmark execution, no clean-machine tests, and no GitHub modification. Thus no proposed implementation choice is described as empirically proven for Code Archeologist.

## C1. Design baseline, design controls, and proposed architecture decisions

### C1.1 Immutable agreed product baseline

Preserve all 17 design choices in Section 2.1: .NET 10 Web API, React, MAF, local Ollama by default, cloud-ready provider boundary, polyglot first wave with deep C#, multi-repository workspaces, persistent memory plus vector retrieval and a technical graph, bidirectional human-governed Obsidian knowledge, read-only analysis and approved sandbox-only source execution, justified adaptive tasks, deterministic configurable evidence quality, Mission Control, Release B scope, Windows/Linux/macOS first-release commitment, versioned independently installable plugins, hybrid evaluation laboratory, and gated public previews / strict v1.0 acceptance.

The safety and integrity invariants in Sections 3, 7, 12, 13, 16 and 20 are **non-negotiable** unless the owner expressly revises them. Proposed technology selections never override these invariants.

### C1.2 Proposed v0.2 ADR register — not approved

| ID | Question | Proposed selection | Validation needed before ratification |
|---|---|---|---|
| ADR-001 | Embedded operational persistence | SQLite for durable records, events, claims, decisions and typed graph; FTS5 for lexical full-text | Native SQLite version/patch, WAL behavior, migration/backup, representative load |
| ADR-002 | Workspace knowledge location | One separately configurable Obsidian-compatible vault per workspace, with stable note IDs | Rename/conflict/restore tests and multi-repo semantics |
| ADR-003 | Runtime deployment | Single-user, single-host local application as first implementation; no remote listen by default | Loopback threat review, browser authentication and OS packaging |
| ADR-004 | Search vectors | Versioned provider interface; select a bundled embedded engine after benchmarking; do not commit to sqlite-vec yet | Cross-OS native packaging, licensing, recall/latency, rebuild, dimensional changes |
| ADR-005 | Event transport | ASP.NET Core SSE for durable server-to-client events; same-origin HTTP commands for user actions | Reconnect/replay, buffering, idle behavior, browser/platform tests |
| ADR-006 | Investigation persistence | Application-owned event/task ledger and explicit checkpoint store adapter for MAF | .NET SDK API compatibility, restart/upgrade replay and duplicate prevention |
| ADR-007 | Imported source safety | Snapshot-first, no arbitrary project/build/tool/script execution; compiler APIs via restricted, non-executing pathways | Adversarial Roslyn/TypeScript fixtures; honest degradation |
| ADR-008 | Extension hosting | First-party analyzers as trusted built-ins; third-party code out-of-process by preference and explicitly approved | Process protocol, permissions, isolation caveats, platform packaging |
| ADR-009 | Initial platform artifacts | Windows x64, Linux x64, macOS arm64 and macOS x64 candidate release targets | Representative clean-machine installations, native dependencies; owner final matrix |
| ADR-010 | Open-source governance | Apache-2.0 as candidate license; DCO contribution sign-off and security disclosure policy | Owner selection, dependency-license audit, contributor documentation |
| ADR-011 | Source revision correctness | Immutable snapshots with content manifest and provenance; never anchor durable evidence only to mutable branches | Rebase/rename/symlink/offline and multi-repo cases |
| ADR-012 | Quality numbers | Versioned rubric with hard gates and per-profile denominators; numerical weights/thresholds stay TBD pending evaluation | Baseline and held-out calibration, error analysis, owner agreement |

**Decision discipline:** Each ADR must eventually specify status, owner approval date, alternatives, rationale, threat/operational consequences, validation evidence, reversibility, and affected sections. A proposal is not an ADR approval. Reversible infrastructure defaults may be recommended strongly but still require approval under Section 24.

## C2. Logical component ownership and dependency direction

Proposed component boundaries, written as *responsibility contracts*, not a mandated repository or C# project layout:

1. **Domain and evidence contracts:** versioned IDs, evidence/claim types, state transitions, provenance and permission types. No framework/React/MAF storage dependency.
2. **Workspace and ingestion:** source authorization, revision-scoped snapshot inventory, filesystem validation, exclusions and change manifests.
3. **Deterministic analyzers:** compiler/parser-based inspection with capability declarations and limitation reports. They cannot self-certify evidence grades.
4. **Graph and retrieval:** query projections over evidence; distinguish source-backed facts from proposed or unresolved links.
5. **Investigation application service:** task scheduling, idempotency, budget policy, event persistence, approvals, and workflow lifecycle; owns source of operational truth.
6. **MAF adapter:** executes the permitted cognitive role and agent/tool orchestration; translates back into application-owned typed proposals/events.
7. **Evidence Quality Engine:** independent deterministic evaluator consuming immutable observations, claims, denominator manifests and profiles. Never relies on a model's self-granted score.
8. **Knowledge service:** Markdown export/import, note identity, reconciliation, audit, protected human sections and pending review queue.
9. **Plugin registry and host:** manifest/compatibility checks, user consent, process lifecycle, capability exposure and failure isolation boundaries.
10. **API + live events:** same-origin UI access, revisioned queries, command authorization, event replay and diagnostics. React has no authority to finalize findings itself.

**Directional rule:** Source snapshots and durable records feed derived graph/search/vault views. Prompts, chat summaries, and vault interpretations cannot overwrite source snapshot facts. MAF agents may *propose* claims, follow-up tasks and note edits; application policy must accept/reject them before commit. The quality engine must not call an LLM to decide whether a hard gate passes.

**Transactions and side effects:** persist the requested task and its authorization before running it; persist a tool result and evidence links before publishing an event that claims success; publish to SSE from an event journal/outbox after commit. Treat graph materialization, embedding creation and vault synchronization as repeatable derived operations. Never use a frontend status indicator as a completion source.

## C3. Identity, sources, snapshots, and admissible observations

### C3.1 Proposed identity and provenance scheme

| Record | Stable identity / uniqueness recommendation | Revision-specific content |
|---|---|---|
| Workspace | generated opaque workspace ID, independent of pathname | workspace manifest and policy version |
| RepositorySource | stable ID and normalized origin/scope; credentials stored separately | requested revision, access mode, allow/deny paths |
| RepositorySnapshot | snapshot ID bound to immutable manifest digest, repository ID and resolved commit/tree identity if present | exact file set/hash, effective exclusions, acquisition method and timestamp |
| SourceArtifact | snapshot ID + normalized relative path + content digest + artifact kind | line/byte region, language, extraction characteristics |
| Symbol | analyzer/language identity + resolved language-qualified identity + snapshot + artifact association | declaration range, signature, symbol resolution status |
| GraphNode/Edge | snapshot-scoped entity/relationship identity + derivation method | evidence links, exact edge status, analyzer version |
| EvidenceItem | immutable generated ID plus dedup fingerprint of observation and source | producer, capture time, revision, source anchors, status, limitation tags |
| Claim | durable claim ID with revision-specific validity records | proposition, scope, support and counterevidence links, state |
| Investigation/Task | durable IDs; task dedup key includes purpose, scope, method and evidence gap | run attempts, state, checkpoints, terminal reason |
| KnowledgeNote | opaque note ID in frontmatter, independent of filename | vault path, hashes, ownership and reconciliation states |

Cross-revision symbol identity is *mapping*, not identity by line number: compare qualified signatures, syntax context, file motion, content and references. Report ambiguous mappings rather than merging them. Cross-repo edge identity includes both endpoint snapshot IDs and an evidence-bearing resolution method.

### C3.2 Snapshot contract

- Ingest into application-managed content-addressed or equivalent immutable storage. A branch name is a *locator*, not a revision identity. Local-folder snapshots require fingerprints even if not in Git.
- Snapshot manifest includes a reproducible set of included artifacts; recorded exclusions/ignores; symlink handling; digest algorithm; timestamps; compiler/analyzer inputs; provenance of Git/submodules or archive; and an inventory for missing/unreadable files.
- For a working directory that may change while being read, detect file changes during acquisition and retry a bounded number of times, otherwise mark snapshot inconsistent and refuse verified completeness.
- The snapshot and all evidence refer to the exact acquisition result; analysis of a later branch/head does not silently retroactively update older findings.
- Deduplicate unchanged bytes without confusing two logical source locations. Preserve archive traversal limits, size/decompression limits, path normalization and safe symlink handling.
- Keep a reversible link from an analyzed snapshot to the original location, but do not require the original path for restoration.

### C3.3 Minimum evidence-item fields (logical contract)

`evidence_id`, `workspace_id`, `repository_id`, `snapshot_id`, `subject_id`, `observation_kind`, `method`, `producer_id`, `producer_version`, `collected_at_utc`, `artifact_hash`, `anchor` (path, byte/line range, optional symbol), `verbatim_or_structured_observation`, `limitation_codes`, `integrity_hash`, `verification_status`, `independence_group`, and optional derived-from evidence IDs.

Allowed *evidence origin* classes: **static_direct**, **static_derived**, **runtime_approved**, **human_asserted**, **llm_interpreted**. Human and model input may establish a useful hypothesis or accepted *human* rationale but not a compiler-verified static fact. `static_derived` must state its inference rule and input evidence.

Citation validation checks snapshot existence, hash, allowed region, extractor version compatibility and whether the cited content actually supports the asserted relationship (type-appropriate validation; lexical presence alone is not semantic proof). Claims may have support, counterevidence, and unresolved citations simultaneously.

**Evidence independence rule:** two models repeating one parser result count as one underlying source of evidence. Independence groups represent different primary observation methods or independent artifacts, not number of agreeing agents. Duplicate statements add no score.

## C4. Claim, graph, and change-impact contracts

### C4.1 Claims are first-class records

Logical claim fields: `claim_id`, `proposition_type`, `subject_id`, `predicate`, `object_or_value`, `scope`, `snapshot_set`, `asserted_by`, `supporting_evidence_ids`, `counterevidence_ids`, `human_decision_ids`, `verification_class`, `review_state`, `last_assessed_at`, `quality_rubric_version`, `quality_assessment_id`.

Claim status axis (proposed): **candidate**, **supported**, **verified_for_stated_scope**, **disputed**, **stale**, **rejected**, **superseded**. `verified_for_stated_scope` must mean a deterministic validator can validate the *specific* claim type and stated scope; do not apply it to general descriptions of behavior merely because cited source files exist. Store provenance and human approval separately from verification class.

### C4.2 Graph schema and relationship classes

Provide a registry of node categories (repository, project, package, namespace, type, member, React component, route, API contract, data object, configuration entry, workflow, business rule, external dependency) and edge categories from Section 9. Every relationship also has:

- direction, source/target snapshot IDs, extraction method, analyzer version, evidence IDs;
- resolution grade **resolved / strongly_indicated / candidate / unresolved**;
- direct vs inferred vs transitive characterization;
- temporal validity and optional human review status;
- limitations such as reflection, environment-dependent binding, external consumers or missing source.

A `calls` edge through a directly resolved compiler symbol can be marked `resolved` *within the analyzer's stated scope*. An HTTP route match inferred from string similarity is not automatically a verified runtime call; it may be `candidate` or `strongly_indicated` depending on independently specified matching rules. Transitive paths are algorithmically calculated, not stored as if each link had been independently observed.

### C4.3 Impact reporting taxonomy

Each impact statement must be exactly categorized as:

1. **Proved structural breakage:** deterministic rules establish a structural or binding failure under stated source assumptions; identify the rule and source anchors.
2. **Supported risk:** evidence establishes a credible path or contract dependency without proving a break.
3. **Plausible hypothesis:** an inference not yet supported sufficiently by deterministic or independent evidence.
4. **Unknown impact:** missing code, dynamic behavior, external clients, runtime/environment data or unsupported language.

Impact traversal must report direction, initiating change, depth, shortest relevant paths, source versions, and explicit fan-out unknowns. A breadth count never substitutes for failure likelihood. Proposed changes are treated as *hypotheticals*, not applied to imported code. Test recommendations are clearly recommendations; test execution is explicitly absent unless separately approved and recorded.

## C5. Non-executing analyzer safety and semantic fidelity

### C5.1 C# deep analysis pathway (candidate)

**Default:** read source and project/configuration text from an immutable snapshot; parse with Roslyn syntax APIs; construct constrained compilations only from explicitly enumerated, inert source/reference inputs. Do not call `dotnet build`, `dotnet restore`, arbitrary MSBuild evaluation or custom targets as a hidden analysis step. Do not import or run arbitrary project tasks. Treat syntax and resolved semantics as different coverage grades.

A normal MSBuild project file can embed tasks, imports, property functions or other behavior; Microsoft's guidance says opening untrusted project files with MSBuild is a security concern. Therefore `MSBuildWorkspace` is **not approved for general untrusted default scanning** merely because it appears to be read-only. Fully faithful project evaluation, if ever required, belongs behind separate consent and a reviewed secure execution facility (deferred in v1.0); without it, degradation is acceptable and must be visible.

Support tiers: **inventory**, **syntax_structural**, **partial_semantic**, **resolved_semantic**. Capture per-project and per-artifact missing references, preprocessor-symbol uncertainty, generated code omissions, target framework ambiguity, external package metadata availability and unresolved symbols. Never label a syntax reference as compiler-resolved.

### C5.2 TypeScript/React, SQL and configuration

- Use compiler-backed TypeScript parsing and type resolution in a packaged, controlled worker where possible; bundle necessary runtime so end users do not need Node.js installation. Do not execute npm/pnpm/yarn scripts, package lifecycle hooks, Babel/Vite/webpack plugins, arbitrary JavaScript config, or imported repo code.
- Treat executable configuration files, plugin systems, path aliases and package-resolution uncertainty as security and coverage boundaries. Load only safe declarative configuration inputs under explicit rules.
- For SQL, declare supported dialect and parser behavior per artifact. Start with SQL Server-oriented DDL/routine/reference extraction; label dynamic SQL and ORM-generated queries unresolved unless additional evidence resolves them.
- Configuration analyzers may read approved references and deployment manifests but must avoid emitting secret *values*. Unknown formats remain inventory/lexical-only.
- Analyzers run in bounded resource settings and must reject symlinks/paths outside snapshot boundaries; a helper process is not necessarily a security sandbox unless OS isolation is actually enforced.

### C5.3 Analyzer output contract

Each analyzer must return a structured response containing `capability_id`, `analyzer_version`, `input_snapshot_id`, `scope_manifest`, `coverage_denominators`, `nodes`, `edges`, `evidence`, `warnings`, `unsupported_features`, `resource_usage`, `status` and `retryability`. Validate referential integrity and immutable snapshot anchors before accepting it. On crashes, preserve completed prior work and mark missing coverage; do not replace errors with empty success results.

### C5.4 Security acceptance probes (design only; run after implementation authorization)

- Malicious MSBuild task in a project: import cannot cause its marker side effect.
- `package.json` postinstall / `vite.config.js` / custom loader: analysis does not execute these.
- ZIP path escape, absolute path, symlink escape, archive bomb, nested Git submodules: bounded rejection with explicit diagnostics.
- README, source comments, notes and tool results containing instructions to bypass governance: model cannot turn them into authorized actions.
- Hidden secret-like inputs: no prompt, log, vector, vault or export disclosure by default.

## C6. Persistence and transaction design

### C6.1 Operational store (candidate ADR-001)

Choose one embedded SQLite database per workspace or equivalent documented, portable partitioning after a data-size study; recommend **one database per workspace** provisionally to simplify backup, export and deletion. Store immutable evidence, investigation events, claim review history, plugin permissions, quality assessments, model runs and checkpoint metadata in transactional tables. Derived graph projection, FTS index and vector chunks may be rebuilt from preserved records and snapshots. State the precise rebuild procedure and required underlying data before treating an index as disposable.

Use WAL for concurrent reading of Mission Control and background writes if performance supports it; SQLite WAL permits concurrent readers with one writer, is not intended to be shared as a WAL database over network filesystems, and requires controlled checkpointing. Verify that packaged SQLite includes published WAL corruption fixes and that the effective version is logged (as of this review, SQLite documents a fix in 3.51.3 / selected backports). **Never back up a live WAL-mode store by copying only its main `.db` file.** Use a consistent SQLite backup mechanism or transactional export procedure. [SQLite WAL documentation](https://www.sqlite.org/wal.html).

Use app-owned migrations with schema versions and rollback/restore procedures. On migration failure, preserve previous data and provide recovery instructions. Permit only a single logical writer per workspace (process lock or equivalent), reject concurrent writes from a second instance, and make readers safe. Configure reasonable busy handling and explicit disk-full behavior.

### C6.2 Persistence semantics

- **Atomicity:** Save accepted evidence, related claim updates, checkpoint references and event/outbox records transactionally where they share SQLite; for filesystem checkpoint/vault work use a durable pending operation plus reconciliation, not pretend distributed ACID across files.
- **Idempotency:** Every command carries a unique idempotency key and expected object version. Duplicate commands return the original outcome; at-least-once event delivery cannot duplicate evidence.
- **Recovery:** On start, examine unfinished tasks and external side effects; reconcile intended vs committed state; requeue safe idempotent tasks only. Never replay an action requiring new permission silently.
- **Retention:** Source snapshots, evidence, user decisions and accepted notes are durable until user-managed deletion/retention. Search indexes, embeddings and graph caches have rebuild contracts.
- **Deletion:** Provide a manifest of all locations including snapshots, vectors, logs and vault backlinks. Acknowledge backups may require separate secure retention/deletion.

## C7. Retrieval, embedding and scope-control contract

The retrieval pipeline uses exact repository/snapshot scope → identifier/symbol lookup → FTS5 text search → semantic vector candidates → graph traversal → evidence-based re-ranking → context assembly. Retrieval results must expose origin, scope, anchor, method, confidence/quality *classification* and limitations, with query diagnostics.

- No cross-workspace or cross-snapshot leakage by default. Every retrieval query is constrained by a workspace/snapshot authorization scope before ranking.
- Embedding records include chunk type, source artifact hash, source anchor, embedding model ID/version, dimensionality, normalization settings, vector index version, created time and privacy classification. Use semantic code units instead of blind length-only chunking.
- An embedding is a retrieval aid, **not verification evidence**. Similarity cannot establish a dependency or architectural fact.
- Embedding provider upgrade invalidates incompatible vectors. Missing embedding model degrades gracefully to exact/full-text/graph results.
- Candidate backends: bundled sqlite-vec (portable-looking but still pre-v1 and may break compatibility); an application-managed brute-force/approximate implementation at small scale; other embedded engine if it passes evaluation. No commitment yet. [sqlite-vec project](https://github.com/asg017/sqlite-vec).
- Benchmark queries include exact identifier collision, renamed symbol, cross-language endpoint mapping, negative/irrelevant sources, contested human notes and large repositories. Measure retrieval recall of relevant grounded chunks, latency, storage and failures on each target OS. Pick the vector backend using recorded outcomes; costless portability alone is insufficient.

## C8. Investigation orchestration and state machines

### C8.1 Investigation states (candidate)

`Created → Scoping → Queued → Running ↔ Pausing → Paused → Resuming → Running → Assessing → Complete | CompleteWithLimitations | Failed | Interrupted | Canceled`

The transition list is illustrative; the implementation must define a strict transition table and associated actor/permission rules. In particular, **Paused** requires a durable safe checkpoint, **Canceled** is a deliberate user terminal action (not a quality plateau), **Failed** is a consequential infrastructure failure, **Interrupted** is an unplanned cessation whose verified evidence can later support recovery, and **CompleteWithLimitations** is not equivalent to full completion. Completed claims and evidence survive all terminal states unless explicitly deleted by the user.

### C8.2 Task and attempt contract

A proposed task cannot be scheduled unless it has `investigation_id`, `parent_question_or_gap_or_gate_id`, `purpose`, `scope_manifest`, `capability`, `expected_evidence`, `success_criterion`, `permission_class`, `resource_limits`, `dedup_key` and `proposed_by`. Track each actual attempt separately with model/provider/version, tool call IDs, timestamps, error codes, token/context diagnostics if available, and actual newly accepted evidence.

**Planning gate:** task provenance must reference a real, open question, failed gate or explicit user direction. Merely rewording a task or asking a second model to agree does not create legitimate new work. Before scheduling, reuse existing relevant evidence, detect equivalent active tasks and bound retries. The scheduler may parallelize independent deterministic tasks but must serialize conflicting state writes and obey per-device resource budgets.

### C8.3 Agent and MAF boundary

Roles remain the Chief Archeologist, Architecture Investigator, Functional Investigator, Dependency Investigator, Impact Investigator, Skeptic/Reviewer and Archivist. They are logical roles, not a requirement to run seven concurrent models. Typed application-owned tool endpoints return evidence-bearing results. An agent cannot write a verified claim, alter permissions, create an ungrounded follow-up task or finalize a quality assessment merely by asserting it.

Use MAF workflow and role adapters, but store canonical investigation/task status outside framework-specific objects. For the .NET checkpoint adapter, test the actual `CheckpointManager` / `ICheckpointStore<T>` APIs, JSON serialization path and stable agent executor IDs for the pinned package version. MAF documentation requires compatible workflow topology and stable executor/agent identity on rehydration; a framework upgrade must have a **checkpoint compatibility policy**: restore with prior version, migrate, or explicitly terminate as incompatible without loss of independently stored evidence. [MAF .NET checkpoints](https://learn.microsoft.com/agent-framework/workflows/checkpoints); [checkpoint store contract](https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Workflows/Checkpointing/ICheckpointStore.cs).

### C8.4 Reliability policy

Separate four independent statuses: **transport**, **tool/workflow execution**, **evidence-quality outcome**, and **vault synchronization**. Never call an investigation successful solely because its model returned a final text response. Retry only transient and diagnosable failures; repeated invalid typed output may trigger a bounded correction path, after which return an explicit failure or limitation. Use configurable concurrency, elapsed time, cost-free model load/resource limits, disk limits and plateau protections. Budget exhaustion is not evidence-based completion.

## C9. Evidence Quality Engine: rubric structure and calibrated acceptance

### C9.1 Independent gate design

The quality engine evaluates source validity, relationship semantics, evidence independence, denominator-based coverage, contradictions, freshness, limitations and task lineage. It consumes persisted records and a versioned rubric profile; it does **not** receive permission to mutate their truth classifications. Evaluation results include `{assessment_id, profile_id, profile_version, engine_version, snapshot_set, metric_numerators, metric_denominators, missing_inputs, hard_gate_results, score_if_defined, terminal_recommendation, rationale}` as a logical contract, not application code.

**Mandatory proposed universal hard gates:**

- Every claim labeled verified has valid snapshot-resolvable, type-appropriate citations and a known evidence provenance method.
- No model or human assertion has been silently promoted to static proof.
- Every executed investigation task has justified lineage, valid scope, and permission authorization.
- No unauthorized imported-code execution or forbidden source/vault modification occurred.
- Unresolved critical contradictions cannot be hidden by aggregation; stale findings cannot support verified current-revision answers.
- Evidence must persist and remain replayable after a successful checkpoint/commit.
- Any missing denominator or broken validator blocks claiming the affected quality metric has passed; a model-supplied denominator cannot repair it.

### C9.2 Quantitative rubric without fabricated thresholds

For each profile (Architecture Overview, Functional Explanation, Dependency Mapping, Change Impact, Critical Change Impact), declare:

- Which claim types and repository scope must be covered.
- Numerator and denominator algorithms for structural coverage, inspected entry points, traced functional workflows, supported findings, cross-repo links, citation validity and unresolved critical gaps.
- Independent evidence and corroboration semantics; missing supported analyzers must reduce coverage explicitly.
- Hard gates, optional metrics, benchmark cohort and versioned numeric thresholds.

**No numeric 0–100 formula or pass percentage is ratified in v0.2.** Until evaluated on labeled controls and held-out repositories, the engine can report dimension-level measurements and hard-gate results but should not fabricate an aggregate score. After calibration, any score must represent *evidence sufficiency under a named rubric*, not probability of correctness; outcome thresholds remain profile-specific and subject to owner approval. Compare score bands with independently measured claim correctness as a calibration exercise, without conflating the two.

### C9.3 Evidence plateau and termination

Proposed plateau inputs: a bounded sequence of **materially different, justified** attempts; count of newly accepted independent evidence items; reduction in unresolved high-priority gaps; metric changes measured against immutable denominators; and capability availability. Repeated model narrative, duplicate evidence or multiple agent votes never reset the counter. Plateau should be reported with missing sources/analytical capabilities and suggested next actions.

Outcome meanings:

- `Complete`: all required hard gates pass and all profile quantitative thresholds are met, with source scope declared.
- `CompleteWithLimitations`: an approved termination/plateau path with explicit failed or unmeasurable requirements and no implication of full evidence completion; show the missing gates prominently.
- `Failed`: reliability or integrity fault prevents a reliable assessment.
- `Interrupted`: resumable or unplanned interruption with preserved verified work.
- `Canceled`: operator cancellation; not a successful quality outcome.

An application must not downgrade a critical failed hard gate into full success by applying a lower-quality profile silently. Changing profile or scope requires an audited user-approved action and recalculation.

## C10. Obsidian vault, two-way reconciliation and human authority

**Proposed mapping:** one Obsidian-compatible Markdown vault per workspace, optionally in a user-chosen folder. Repositories are represented within notes by stable IDs and scoped links; one workspace can contain many repositories without creating competing truth roots. Obsidian installation is optional.

### C10.1 Note identity and ownership

Every system-managed note has stable `note_id`, `workspace_id`, `note_kind`, `author_origin`, `last_accepted_revision`, `provenance_ids`, `verification_state`, `base_content_hash` and `last_reconciled_utc` in frontmatter or a paired manifest. A note rename preserves identity; duplicate IDs are a conflict. Users retain ownership of all their text by default. System-generated sections must be explicitly marked and tracked; absent markers are **not** an invitation to overwrite.

### C10.2 Reconciliation algorithm (proposed)

1. Read and fingerprint the external note plus last accepted base, using safe path and symlink checks.
2. Parse frontmatter and ownership regions without removing unfamiliar user fields.
3. Perform a three-way comparison: prior accepted base, local human-edited file, newly proposed generated version.
4. Auto-apply only changes confined to clearly system-owned regions that have not been modified by the human.
5. For overlaps, renames/ID collisions, source disagreements or invalid frontmatter, create a persistent review proposal; preserve the existing note.
6. Present base/human/proposal diff, evidence, source revisions, rationale, review decisions and consequences.
7. Commit an approved change with file atomic replacement where supported and an audited durable transaction/pending-operation record, retaining rollback information.

Vault filesystem operations and SQLite transactions cannot be treated as one atomic operation: use an explicit sync outbox, state transitions and recovery reconciliation. If the vault is offline, investigation continues; mark knowledge export as pending. A human correction is recorded as an attributed decision, not silently elevated to static source proof. An updated source snapshot may make a prior source-backed explanation stale without erasing the human note.

## C11. Plugin ABI, trust and permission model

### C11.1 Proposed manifest contract

`plugin_id`, `semver`, `publisher`, `contract_min/max`, `supported_platforms/architectures`, `entrypoint_kind`, `declared_capability_ids`, `supported_languages/dialects`, `evidence_schema_version`, `input/output_schema`, `requested_permissions`, `native_dependencies`, `resource_hints`, `package_digest`, `signature_or_integrity_metadata`, `health_probe` and `diagnostic_codes`.

Capabilities are advertised via discoverable, typed schemas. Each capability reports what questions/inputs it can handle, what admissible evidence it may produce, and where it cannot prove semantics. The host validates outputs against the evidence contract; a plugin cannot grant itself access by returning a manifest that claims extra permissions after activation.

### C11.2 Tiered trust policy (candidate)

- **Tier A — bundled built-ins:** reviewed first-party analyzers run under the application's declared privileges. Still cannot bypass application policy/quality gates.
- **Tier B — external but explicitly trusted executables:** separately installed, version-pinned, digest-verified plugins; default disabled pending consent, launched out-of-process where practical. An OS process boundary improves crash isolation but **does not by itself** limit filesystem/network access.
- **Tier C — untrusted/unknown publishers:** not auto-activated or presented as safely sandboxed. If the host cannot enforce requested OS permissions, it must clearly disclose the effective privileges and require explicit informed approval or refuse activation according to the ratified policy.

`.NET AssemblyLoadContext` is useful for version/load isolation but explicitly **does not provide security isolation**. Do not use it as the sole boundary for untrusted code. [Microsoft API note](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext?view=net-10.0).

The proposed wire protocol between out-of-process plugin and host is versioned request/response with bounded payloads, timeouts, cancellation, unique invocation IDs, schema validation, no direct database access, and mediated file access to immutable eligible snapshot artifacts where technically enforceable. Claims of least privilege require an OS-specific enforcement design and tests; otherwise the consent screen must say what is *not* isolated. Plugin output remains untrusted input to models and quality evaluation.

**v1.0 test:** separately package a benign analyzer, install and enable it on each supported target, verify stable capability discovery and typed evidence, exercise permission refusal, malicious output, crash, timeout, incompatible version and uninstall; demonstrate that it cannot bypass logical quality/approval gates. The tests must not falsely claim OS sandbox guarantees absent real isolation.

## C12. Local application API, event transport and browser safeguards

### C12.1 HTTP resource boundaries (candidate)

The API groups capabilities around workspaces, repository sources/snapshots, evidence/claims, graph/retrieval, investigations/tasks/commands, quality assessments, knowledge/reviews, plugins/permissions, exports/restores and diagnostics. Treat all command routes as authenticated and authorized; require optimistic concurrency (`expected_version`) and idempotency keys for state-changing requests. Use stable, versioned DTO schemas. Query endpoints must be side-effect free and evidence filters mandatory.

### C12.2 Durable real-time contract

Persist each meaningful event with `workspace_id`, monotonic sequence number scoped to a journal, `investigation_id` where applicable, type/schema version, timestamp, correlation/causation IDs, entity IDs, sanitized payload and committed revision. **SSE** is the provisional server-to-client transport; ASP.NET Core 10 provides native SSE results. Browser connects with a last-seen cursor and receives replay from persisted records, then live continuation. Retention/compaction policy must signal gaps and demand a resnapshot rather than invent events. HTTP commands carry pause/resume/cancel/reprioritize/redirect intentions; the client does not emit authoritative agent progress. [ASP.NET Core 10 SSE](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0).

### C12.3 Local security posture

Bind only to loopback by default; do not enable wildcard/LAN listening automatically. Browser access must use a local authenticated session/capability bootstrap, strict allowed-origin/Host behavior, anti-CSRF/Fetch Metadata protection for state-changing endpoints, narrowly scoped CORS, secure secrets handling and sanitized HTML/Markdown rendering. A loopback socket can still be contacted by other local processes and certain browser-origin attack paths; loopback alone is not authentication.

If user opts into remote access later, require deliberate TLS/auth/access-control design and separately reviewed security policy; this is not achieved by merely changing a bind address. Record user activation, local privacy implications and remote model selection consent. Do not expose Ollama or plugin management endpoints to external origins by default.

## C13. Threat model and trust-boundary matrix

| Input/actor | Assumed trust | Threat | Required containment / explicit limitation |
|---|---|---|---|
| Imported source, README, comments | untrusted | prompt injection, secrets, malicious config/build tasks | parse as data, never grant instructions/execute; redact secrets |
| Git/ZIP/local paths | untrusted | traversal, symlink escape, huge archive, races | isolated immutable snapshot, limits, canonical path validation |
| Local Ollama model outputs | untrusted reasoning | invented evidence, malformed tool calls, unsafe instructions | typed schemas, independent evidence validation, authorization checks |
| Human annotations | authoritative as human decisions only | conflict with source, accidental overwrite | attribution, versioning, reconciliation queue |
| Third-party plugin | executable untrusted-to-trusted code transition | arbitrary file/network/OS effects, output poisoning | explicit install/permission, out-of-process preference, OS isolation where available, no false sandbox claims |
| Browser accessing localhost | partially trusted | CSRF, DNS rebinding/Host abuse, script injection | app authentication, Host/Origin checks, CSP, session protections |
| Restored workspace/backup | untrusted until verified | corrupted state, malicious vault note, incompatible schema | integrity manifest, migration, safe path handling, validation before activation |
| Cloud-provider adapter | off by default | source-content exfiltration | explicit provider and per-scope consent, audit logging and redaction |

**No execution loophole:** plugin execution for language analysis is not the same thing as permission to execute *the imported repository*. The host must separately ensure a plugin tool does not turn static analysis into arbitrary imported-code execution. Because complete OS sandboxing is deferred, the owner may need to restrict plugin trust tiers until secure isolation is tested. This is a design risk, not a resolved guarantee.

## C14. Backups, exports, restore and migrations

Portable export manifest should include schema/spec version, workspace ID, repository IDs and snapshot manifests, accepted evidence/claims, decisions, task/event history, quality assessments, vault notes and ownership state, plugin settings *excluding credentials*, model/embedding identity and index rebuild recipe. Support a **metadata-and-knowledge** export and an **offline-complete** export optionally bundling source snapshots (with explicit size/privacy warning). Restoring metadata without source bytes must mark evidence re-verification as unavailable until matching snapshot content is present. Never promise full re-analysis from manifests alone.

Backup operation obtains a consistent application database view and coordinated immutable vault/snapshot manifest; calculates a complete archive checksum and per-component hashes; detects partial backups; writes to a temporary location then finalizes. Restore checks archives before extraction, displays potential overwrites, preserves existing workspace until validation succeeds, then imports as new or explicitly replaces with rollback facility. Cross-platform path case sensitivity, Unicode normalization, executable bits, symlinks, and `\`/`/` differences require tests. User secrets should be reauthorized, not carried in plaintext across machines. Preserve accepted human knowledge and audit trail on restore.

## C15. Evaluation lab, reproducibility, and preliminary gates

### C15.1 Corpus construction

The hybrid laboratory must comprise (a) small hand-designed reference systems, (b) curated real OSS repositories with license and revision pinned, (c) deliberate changes with independently recorded structural consequences, and (d) held-out cases that are not consulted in prompt/rubric tuning. Include multi-repo React→API→SQL cases, misdirecting comments, dynamic DI/reflection, unresolved packages, stale vault notes, conflicting human knowledge and malicious source/config examples.

Corpus manifests include license, clone/source digest, language and framework mix, ground-truth label author/reviewer, supported claims/edges/negative examples, allowed test execution condition, scoring exclusions and provenance. Ground truth is independently annotated where possible, with disputed/unknown labels tracked rather than treated as false.

### C15.2 Measurement definitions

| Dimension | Reported metric | Mandatory disclosure |
|---|---|---|
| Structural extraction | per-edge-class precision, recall, false positives and false negatives | ground-truth scope and unsupported categories |
| Cross-language links | resolved/indicated/candidate link precision and recall | how route/client matches were independently verified |
| Functional understanding | supported functional claim correctness and unsupported-claim rate | human-label protocol and ambiguous claims |
| Citation integrity | validity rate and invented-or-misattributed citation count | snapshot hash and validator method |
| Change impact | direct breakage precision/recall; separate supported risks/hypotheses | tests run vs not run; external unknowns |
| Evidence quality | correlation/calibration of rubric bands with independent correctness | uncertainty intervals and corpus limitations |
| Memory safety | preserved human edits, conflict detection and stale-claim behavior | cases with rename, delete, edit, restore |
| Reliability | restart/pause/resume continuity, duplicate work and evidence loss | injected failure stage and recovery result |
| Security | forbidden actions observed, injection resistance, secret disclosure | threat fixture and containment proof |
| Distribution | first-run success, platform-specific regressions, cold/incremental performance | OS/architecture, runtime model, memory and disk |

Do not hide a critical hallucinated citation, unauthorized source execution or destructive vault overwrite behind an attractive weighted average. **The first objective is a trusted baseline and explicit failure taxonomy.** Numeric targets must be proposed after the initial fixtures reveal actual behavior and be approved before v1.0; no retrospective metric manipulation on held-out cases.

### C15.3 Initial acceptance examples and independent evidence

Preserve all 12 scenarios in Section 22. Each must have `fixture`, `preconditions`, `action`, `expected observations`, `negative assertions`, `independent oracle`, `evidence artifacts`, `platform applicability`, and `pass/fail criterion`. Every preview must pass the invariant gates in Section 20. A v1.0 declaration requires the complete Section 4.1 capability matrix, held-out evaluation, platform-clean install, and independently packaged plugin tests.

## C16. First-run, OS distribution and local model capability validation

**Candidate supported release assets:** Windows x64, Linux x64, macOS arm64 and macOS x64. This matrix is a *target for evaluation*, not proof of clean-install compatibility. The Windows/Linux/macOS commitment stays intact; final CPU/OS version support is decided using test results and owner approval. Publish per-asset SHA-256, signature or release-integrity mechanism, runtime/build manifest and license notices where feasible.

First-run user path: download release → launch packaged app without SDK/Node developer setup → authenticated local dashboard → local data path choice → Ollama connection check → capability/model recommendation and explicit download permission → workspace + vault selection → import/revision summary → preflight report of unsupported semantic capabilities → start investigation with observable events. Running offline after first setup should work with installed model assets and cached/bundled analyzers; do not promise first-install offline model downloads.

Validate selected Ollama generation and embedding models for structured-tool output consistency, context management, memory/VRAM needs, throughput and error recovery; reserve the right to choose platform-specific suggestions. Model defaults are **unselected** at this stage. The Ollama provider is supported through MAF, but actual API feature sets and model behavior must be tested with pinned versions. [MAF Ollama guide](https://learn.microsoft.com/en-us/agent-framework/agents/providers/ollama).

Test clean installs without .NET SDK, Node.js, manually installed Python, a database server or Docker. Native libraries and workers must be bundled or plainly documented if unavoidable. New-user compatibility includes behavior on machines with no GPU, smaller memory configurations and unavailable Ollama—not an assumption that all analysis completes at identical speed.

## C17. Recommended engineering sequence after explicit implementation authorization

These are **future implementation steps only**, not current work authorization:

| Track | Prerequisite proof | Small verifiable exit artifact |
|---|---|---|
| 0. Ratify design | Owner accepts material ADRs and residual risks | Signed-off spec, decision log, threat model, acceptance oracle definitions |
| 1. Safe shell | Approved localhost/auth/SQLite/backup contracts | Cross-platform packaged shell, workspace creation and first-run smoke tests |
| 2. Ingestion + C# | Snapshot contract and non-execution probe | Immutable import, AST/partial semantic graph, malicious project negative tests |
| 3. Polyglot + graph/retrieval | Schema, coverage and query contract | TypeScript/React + SQL associations, scoped graph/FTS/vector benchmark |
| 4. Investigation + quality | Task state, MAF checkpoint and independent gate contracts | Task lineage, SSE journal, crash resume, skeptical review and plateau tests |
| 5. Vault and reconciliation | Three-way merge and ownership contract | Obsidian round-trip, rename/conflict preservation and recovery tests |
| 6. Mission Control + plugin | UI state projection and permission contract | Real persisted events, safe pause/redirect and external test plugin |
| 7. Release discipline | Hybrid corpus, audited metrics, supported OS matrix | Preview releases that satisfy invariant gates; full v1.0 certification later |

Parallel design or implementation is permitted only where the prerequisites and interfaces are stable. Each phase requires negative tests and a completed-evidence artifact; merging code is not proof of completion. Local test execution is acceptable; **no paid CI or mandatory hosted services** should become a prerequisite for ordinary contributors unless explicitly later agreed.

## C18. Open risk register (owners and resolutions pending)

| Risk | Severity | What is currently known | Required resolution before declaring it solved |
|---|---|---|---|
| R-01 Roslyn vs unsafe MSBuild evaluation | Critical | Normal build/project evaluation can execute repository code | Demonstrate the safe partial-semantic path and hostile fixture containment |
| R-02 Third-party plugin privilege | Critical | In-process plugin isolation is not security; subprocess alone insufficient | Explicit trust-tier policy, user consent, per-OS restrictions/disclosures |
| R-03 Invented evidence/false completion | Critical | LLM output is not source proof | Falsification tests and independent validator with hard gates |
| R-04 Vault overwrite and split-brain | High | Filesystem sync and DB are not one transaction | Three-way ownership merge and durable reconciliation recovery |
| R-05 MAF checkpoint incompatibility | High | Rehydration needs stable topology/executor IDs; SDK may evolve | Pin SDK, compatibility tests and graceful incompatible lineage handling |
| R-06 SQLite/extension native packaging | High | WAL, sqlite-vec/native binaries add compatibility constraints | Patched SQLite runtime, platform vector benchmark, consistent backup |
| R-07 Semantic gaps from safe no-execution mode | High | Some compiler semantics require project resolution | Honest tiered coverage and adequacy benchmarks on representative repos |
| R-08 Localhost/browser attack surface | High | Loopback is reachable beyond intended browser session | Host/origin/auth/XSS/CSRF test plan and local security review |
| R-09 Cross-repo and cross-revision mistaken links | High | String matching can fabricate relationships | Edge-resolution grades and independently labeled negative corpus |
| R-10 Local model resource constraints | Medium–High | Ollama cost-free does not mean unconstrained | Model/device matrix and graceful degradation/resource controls |
| R-11 Export/restore fidelity | High | Missing source bytes can prevent offline re-verification | Content-complete and metadata-only restore scenarios, explicit gaps |
| R-12 Scope/release pressure | High | Complete Foundation is ambitious for initial v1.0 | Incremental previews with non-negotiable integrity gates, no false maturity claims |

## C19. Owner approval and evidence checklist

**Status: ALL ITEMS BELOW ARE CURRENTLY PROPOSED OR PENDING. THIS DOCUMENT DOES NOT ITSELF AUTHORIZE IMPLEMENTATION.**

The following requirements are met by the written design draft, not by built-system proof: product direction preserved, layer ownership specified, stable identity/evidence proposal defined, failure states proposed, threat model developed, quality dimensions identified, acceptance laboratory specified and phase boundaries clarified.

Before final design approval, seek explicit owner disposition on:

1. ADR-001 through ADR-012, especially local process trust, third-party plugin effective privileges, one-vault-per-workspace, and initial platform matrix.
2. A formal **non-executing semantic analysis** policy: where accepted partial semantics ends and user-approved sandbox execution would begin in a later release.
3. The exact meaning of `CompleteWithLimitations` and which critical validation failures forbid even that outcome.
4. The criteria for independent evidence, source-appropriate citation validation and allowed verified claim types.
5. The plugin trust disclosure and whether a v1.0 third-party plugin may run with broad OS user privileges after explicit informed consent.
6. The licensing/contribution framework, repository governance and public security-reporting channel.
7. Which design-time decisions can be accepted with documentation alone and which require post-authorization technical spikes before public preview.

**Suggested approval wording, if and only if the owner chooses later:** “I approve Code Archeologist Architecture Specification v0.2, including explicitly accepted ADRs and noted exceptions. I authorize Phase 1 implementation under the approved security, evidence and release gates.” An approval of *design* and an authorization to *implement* should remain distinct actions, consistent with Section 24.

### C19.1 Proposed Phase 0 exit package

- Ratified architecture specification with change history and ADRs.
- Logical domain/evidence/graph/API/plugin contracts and privacy threat model.
- Quality-profile metric definitions, explicit unset numeric thresholds and calibration plan.
- Initial annotated evaluation-corpus manifests and acceptance-test specifications (no performance claims without runs).
- Release target and dependency pinning plan, plus risk register with owner disposition.
- Separate, explicit implementation authorization.

## C20. Technical-source verification notes

The following are **external technical facts**, distinct from the v0.1 product decisions and from proposed Code Archeologist architecture. Checked November 8, 2026; pin exact versions during authorized implementation:

1. **MAF Ollama integration:** Microsoft documents a local Ollama provider and client integration. https://learn.microsoft.com/en-us/agent-framework/agents/providers/ollama
2. **MAF .NET checkpoints:** Microsoft documents workflow rehydration and stable agent/executor identity; exact APIs and prerelease changes require pinned-version testing. https://learn.microsoft.com/agent-framework/workflows/checkpoints
3. **MAF checkpoint storage API:** A .NET `ICheckpointStore<TStoreObject>` contract exists in the open-source framework. https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Workflows/Checkpointing/ICheckpointStore.cs
4. **MSBuild security:** Microsoft warns that opening/processing untrusted project files can execute arbitrary behavior. https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-security-best-practices?view=visualstudio
5. **ASP.NET Core 10 SSE:** Microsoft documents `TypedResults.ServerSentEvents`. https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0
6. **SQLite WAL:** SQLite documents readers/writer restrictions, backup implications, network-filesystem warning and a 2026 WAL-reset corruption fix. https://www.sqlite.org/wal.html
7. **sqlite-vec maturity:** Its maintainer currently describes the project as pre-v1 with possible breaking changes. https://github.com/asg017/sqlite-vec
8. **AssemblyLoadContext:** Microsoft explicitly says assembly load context does not provide security features. https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext?view=net-10.0
9. **Obsidian properties:** Frontmatter properties are documented; custom note ownership/reconciliation is our proposed application design, not an Obsidian-provided guarantee. https://obsidian.md/help/properties
10. **Browser request protections:** ASP.NET Core describes defense-in-depth for CSRF and origin checks; Code Archeologist's exact local bootstrap remains an engineering decision. https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0

**Final status:** v0.2 technical design candidate prepared for owner review. **NO CODE OR GITHUB CHANGES AUTHORIZED OR PERFORMED.**