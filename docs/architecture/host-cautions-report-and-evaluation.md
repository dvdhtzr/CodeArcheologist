# Host-written cautions — report persistence and evaluation (PR 2)

Status: implementation for Windows owner verification; no runtime outcome is claimed.

## Report version and provenance

Gate C persisted reports use gate-c5-investigation-report-v2. The three
agent roles, their evidence handles and the review reconciliation are
unchanged. A separate report section contains:

- hostCautionStatus: verified or not-evaluated.
- hostCautionAnalyzerVersion: roslyn-host-cautions-v1.
- hostCautions: zero to five host-authored cautions with their own source
  anchors, source-file SHA-256 values, status and exact analyzer version.

The production worker adds these cautions AFTER model execution but BEFORE
the single fenced terminal report write. It reads the selected entry-point
evidence again from the immutable snapshot. Missing or invalid host evidence
prevents publication of a supposedly successful report. If the source was
rejected and therefore cannot be inspected, the report does not pretend an
empty list is a verified result.

Reports for role validation failures can still carry verified host cautions
when a terminal report exists. Attempts that remain interrupted instead
of writing a report keep existing C6b recovery/checkpoint behavior; no
synthetic report is created solely to carry cautions.

Old v1 persisted reports continue to deserialize and remain viewable in
Mission Control. They carry no retrospectively invented host cautions.
PR 3 will add the separated clickable host-caution Findings section.
PR 2 updates only the web client's contract compatibility.

## Evaluation lab

New report/score schemas are c6c-lab-report-v2 and c6c-lab-score-v2;
corpus, labels, budgets and evidence thresholds remain unchanged.

Each report carries TWO distinct lists:

- claims: statements made by the evaluated comparator. Citation-integrity
  and anchor-recall logic is unchanged and cannot use host caution citations.
- hostCautions: a separately versioned host-only ledger built from the
  freshly verified snapshot for each comparator, including incomplete
  agent outcomes. Kind=host-caution and role=host. It is never an agent claim.

Scoring re-derives the expected caution ledger and rejects any changed,
forged, missing, or moved host caution. It also flags claims that try to
impersonate the host. Summaries count host cautions, comparator claims
and verified host inspections separately; host findings do not enter
Gate C objection coverage, anchor recall or model completion.

Material-issue recognition stays PENDING external judgment for
agent-only, host-only and combined views. No string matching against the
50 owner-approved labels is scored as automatic detection.

## Attributable owner checklists

The existing single-comparator command judges only agent/comparator claims.

- --checklist --runs <dir> --single gate-c
- --checklist --runs <dir> --single host-cautions
- --checklist --runs <dir> --single gate-c+host

Host-only uses completed deterministic-baseline reports, so it retains
all captured host cautions regardless of Gate C agent completion. Combined
uses completed Gate C reports and shows both channels with explicit attribution.
Answer ledgers cannot be reused between modes. Empty verified host
lists may be judged; not-evaluated lists cannot be judged as empty.
The existing blinded external A/B export still compares agent/comparator
claims only, never host cautions.

No evaluation results have been generated or claimed by this PR. Do not
alter the approved labels or tune triggers to known issue descriptions.
Follow the pre-registered comparison only after Windows GREEN and the
report/UI milestones.
