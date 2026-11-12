/**
 * MC3 typed REST client for the Code Archeologist white-label API v1
 * (docs/api/README.md, ADR 0021). The bearer token lives only in this
 * object's memory and is never persisted in browser storage or cookies.
 * REST sends it in the Authorization header. (Browsers cannot set headers on
 * WebSockets, so the SignalR client sends it as the access_token query value
 * of the hub connection only, as the realtime contract specifies.)
 * Every mutation carries a fresh Idempotency-Key.
 * Responses are host records, not proof of runtime behavior.
 */

import type { EvidenceGraph } from './evidenceGraph.ts';

export type Page<T> = { items: T[]; totalCount: number; hasMore: boolean };

export type Workspace = { id: string; name: string; createdAtUtc: string; version: number };
export type Repository = { id: string; workspaceId: string; localPath: string; sourceKind: string; addedAtUtc: string; version: number };
export type Snapshot = {
  id: string; workspaceId: string; repositoryId: string; fingerprintSha256: string; manifestSha256: string;
  capturedAtUtc: string; fileCount: number; totalBytes: number; exclusionCount: number; policyVersion: string;
};
export type SnapshotFile = { path: string; sizeBytes: number; sha256: string };
export type SnapshotExclusion = { path: string; reason: string };
export type SnapshotManifest = {
  formatVersion: number; policyVersion: string; workspaceId: string; repositoryId: string; snapshotId: string;
  sourceKind: string; sourceLocator: string; revisionKind: string; fingerprintSha256: string;
  capturedAtUtc: string; files: SnapshotFile[]; exclusions: SnapshotExclusion[];
};
export type SourceInventory = {
  workspaceId: string; repositoryId: string; snapshotId: string; fingerprintSha256: string;
  analyzerVersion: string; detectionMethod: string; includedFileCount: number; includedBytes: number;
  excludedEntryCount: number; languages: Array<{ name: string; fileCount: number; totalBytes: number }>;
  roles: Array<{ name: string; fileCount: number; totalBytes: number }>;
  files: Array<{ path: string; sha256: string; sizeBytes: number; language: string; role: string }>;
  exclusions: SnapshotExclusion[]; limitations: string[];
};

export type Anchor = { filePath: string; fileSha256: string; line: number; column: number };
export type EntryPoint = { id: string; kind: string; evidenceStatus: string; label: string; detail: string; anchor: Anchor };
export type EntryPointList = {
  contractVersion: string; snapshotId: string; fingerprintSha256: string; count: number; truncated: boolean; items: EntryPoint[]; caveat: string;
};
export type RunSummary = {
  runId: string; status: string; diagnostic: string; startedAtUtc: string; finishedAtUtc: string | null;
  scope: { workspaceId: string; repositoryId: string; snapshotId: string; fingerprintSha256: string;
    entryPointId: string; evidenceContractVersion: string };
};
export type LedgerEvent = {
  eventId: string; attemptId: string; runId: string; sequenceNumber: number; eventType: string; diagnostic: string;
  fromStatus: string | null; toStatus: string | null; occurredAtUtc: string; stage: string | null;
  ownershipEpoch: number | null; checkpointId: string | null;
};
export type ExecutionStage = { role: string; status: string; checkpoint: { checkpointId: string; contentSha256: string } | null };
export type ExecutionAttempt = { attemptId: string; number: number; status: string; terminalCause: string | null; diagnostic: string };
export type RecoveryEligibility = { eligible: boolean; diagnostic: string; expectedAttemptNumber: number | null };
export type Execution = {
  projectionVersion: string; runId: string; runStatus: string; runDiagnostic: string; legacy: boolean;
  currentAttempt: ExecutionAttempt | null; attempts: ExecutionAttempt[]; stages: ExecutionStage[];
  recovery: RecoveryEligibility; cancellable: boolean; executionOwner: boolean; ownershipDiagnostic: string; note: string;
};
/** Recovery is a host-readiness decision, not inferred from the run's status. */
export function eligibleContinuation(execution: Execution | null): number | null {
  const recovery = execution?.recovery;
  return execution?.projectionVersion === 'gate-c6b-execution-projection-v1' &&
    execution.legacy === false && recovery?.eligible === true &&
    Number.isInteger(recovery.expectedAttemptNumber) && (recovery.expectedAttemptNumber ?? 0) > 0
    ? recovery.expectedAttemptNumber : null;
}
export type Scope = { workspaceId: string; repositoryId: string; snapshotId: string };
export type QuestionContext = {
  contractVersion: string; questionId: string; runId: string;
  scope: RunSummary['scope']; subjectNodeId: string; subjectNodeKind: string;
  questionKind: string; questionTemplateVersion: string; capabilityPolicyVersion: string;
  budgetProfileVersion: string; createdAtUtc: string; contextDigest: string;
};
export type QuestionAdmission = { investigation: RunSummary; questionContext: QuestionContext };
export type QuestionContextRead =
  | { status: 'verified'; context: QuestionContext }
  | { status: 'legacy-no-question-context'; context: null };

export type SourceView = {
  contractVersion: string; snapshotId: string; fingerprintSha256: string;
  filePath: string; fileSha256: string; totalLines: number; focusLine: number;
  startLine: number; endLine: number; lines: { number: number; text: string; truncated: boolean }[];
  status: string; caveat: string;
};

export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;
  constructor(status: number, message: string, body: unknown) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

/** Only loopback API addresses are accepted (rule 58: the API stays loopback-bound). */
export function normalizeBaseUrl(value: string): string {
  const url = new URL(value.trim());
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('The API address must start with http:// or https://');
  if (!['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname)) throw new Error('The API address must be a loopback address (127.0.0.1 or localhost).');
  if (url.pathname !== '/' || url.search || url.hash) throw new Error('Enter only the API origin, for example http://127.0.0.1:5127');
  return url.origin;
}

type Fetch = typeof fetch;

export class ApiClient {
  readonly baseUrl: string;
  readonly #fetch: Fetch;
  readonly #extraHeaders: Record<string, string>;
  #token: string | null = null;
  #expiresAtUtc: string | null = null;
  readonly #sessionLostListeners = new Set<() => void>();

  /** extraHeaders exist for non-browser tests (a browser sets Origin itself). */
  constructor(baseUrl: string, fetchImpl: Fetch = globalThis.fetch.bind(globalThis), extraHeaders: Record<string, string> = {}) {
    this.baseUrl = normalizeBaseUrl(baseUrl);
    this.#fetch = fetchImpl;
    this.#extraHeaders = extraHeaders;
  }

  get connected(): boolean { return this.#token !== null; }
  get expiresAtUtc(): string | null { return this.#expiresAtUtc; }
  /** For the SignalR accessTokenFactory only. */
  token(): string {
    if (!this.#token) throw new Error('Not connected.');
    return this.#token;
  }

  async pair(code: string): Promise<void> {
    const response = await this.#fetch(this.baseUrl + '/api/v1/auth/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CodeArcheologist-Request': '1', ...this.#extraHeaders },
      body: JSON.stringify({ code: code.trim() }),
    });
    const body = await readBody(response);
    if (!response.ok) throw new ApiError(response.status, response.status === 401
      ? 'The pairing code was not accepted. Copy it again from the API window.' : `Pairing failed (${response.status}).`, body);
    const token = (body as { accessToken?: unknown }).accessToken;
    if (typeof token !== 'string' || !/^[0-9a-f]{64}$/.test(token)) throw new ApiError(response.status, 'Unexpected token response.', body);
    this.#token = token;
    this.#expiresAtUtc = String((body as { expiresAtUtc?: unknown }).expiresAtUtc ?? '');
  }

  async revoke(): Promise<void> {
    if (!this.#token) return;
    try { await this.#send('POST', '/api/v1/auth/token/revoke', {}, false); } finally { this.forget(); }
  }

  /** Called once on loss of the authenticated session (401 or host-declared expiry). */
  onSessionLost(listener: () => void): () => void {
    this.#sessionLostListeners.add(listener);
    return () => { this.#sessionLostListeners.delete(listener); };
  }
  expireSession(): void { this.#sessionLost(); }
  #sessionLost(): void {
    if (!this.#token) return;
    this.forget();
    for (const listener of [...this.#sessionLostListeners]) {
      try { listener(); } catch { /* never hide the original authentication failure */ }
    }
  }
  forget(): void { this.#token = null; this.#expiresAtUtc = null; }

  listWorkspaces() { return this.#send<Page<Workspace>>('GET', '/api/v1/workspaces'); }
  createWorkspace(name: string) { return this.#send<Workspace>('POST', '/api/v1/workspaces', { name }); }
  listRepositories(workspaceId: string) {
    return this.#send<Page<Repository>>('GET', `/api/v1/workspaces/${id(workspaceId)}/repositories`);
  }
  registerRepository(workspaceId: string, localPath: string) {
    return this.#send<Repository>('POST', `/api/v1/workspaces/${id(workspaceId)}/repositories`, { localPath });
  }
  listSnapshots(workspaceId: string, repositoryId: string) {
    return this.#send<Page<Snapshot>>('GET', `/api/v1/workspaces/${id(workspaceId)}/repositories/${id(repositoryId)}/snapshots`);
  }
  /** Explicit, user-initiated read-only capture (rule 15). */
  captureSnapshot(workspaceId: string, repositoryId: string) {
    return this.#send<Snapshot>('POST', `/api/v1/workspaces/${id(workspaceId)}/repositories/${id(repositoryId)}/snapshots`, {});
  }
  /** Existing snapshot-scoped, verified graph API; response status labels are not semantic proof. */
  evidenceGraph(scope: Scope) { return this.#send<EvidenceGraph>('GET', scopePath(scope) + '/csharp-evidence-graph'); }
  /** Snapshot completeness is independent of the C# graph and agent inference. */
  getSnapshot(scope: Scope) { return this.#send<Snapshot>('GET', scopePath(scope)); }
  manifest(scope: Scope) { return this.#send<SnapshotManifest>('GET', scopePath(scope) + '/manifest'); }
  inventory(scope: Scope) { return this.#send<SourceInventory>('GET', scopePath(scope) + '/inventory'); }
  listEntryPoints(scope: Scope) { return this.#send<EntryPointList>('GET', scopePath(scope) + '/entry-points'); }
  listMethods(scope: Scope) { return this.#send<{ contractVersion: string; snapshotId: string; fingerprintSha256: string; count: number; truncated: boolean; items: Array<{ id: string; label: string; detail: string | null; anchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null }; inboundProvisionalCandidates: number }>; caveat: string }>('GET', scopePath(scope) + '/methods'); }
  impact(scope: Scope, methodId: string) {
    return this.#send<{ contractVersion: string; snapshotId: string; fingerprintSha256: string; method: { id: string; label: string; detail: string | null; anchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null }; inboundProvisionalCandidates: number }; directCandidates: Array<{ calleeEdgeId: string; evidenceStatus: string; invocationAnchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null }; callerLabel: string; callerKind: string; callerAnchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null } }>; candidateEdgesWithoutObservedContainment: number; snapshotUnresolvedCalleeCount: number; unknowns: string[]; caveat: string }>('GET', scopePath(scope) + '/impact?methodId=' + encodeURIComponent(methodId));
  }
  listRuns(scope: Scope) { return this.#send<Page<RunSummary>>('GET', scopePath(scope) + '/investigations'); }
  startInvestigation(scope: Scope, entryPointId: string) {
    return this.#send<RunSummary>('POST', scopePath(scope) + '/investigations', { entryPointId });
  }
  /** R3 PR2: explicit user action. The API verifies exact saved entry-point
   * identity; this client never infers a route from labels or relationships. */
  startQuestion(scope: Scope, subjectNodeId: string, idempotencyKey: string) {
    return this.#send<QuestionAdmission>('POST', scopePath(scope) + '/investigations/questions',
      { subjectNodeId, questionKind: 'explain-http-entry-point' }, true, idempotencyKey);
  }
  questionContext(scope: Scope, runId: string) {
    return this.#send<QuestionContextRead>('GET',
      scopePath(scope) + `/investigations/${id(runId)}/question-context`);
  }
  events(scope: Scope, runId: string) { return this.#send<LedgerEvent[]>('GET', scopePath(scope) + `/investigations/${id(runId)}/events`); }
  execution(scope: Scope, runId: string) { return this.#send<Execution>('GET', scopePath(scope) + `/investigations/${id(runId)}/execution`); }
  run<T>(scope: Scope, runId: string) { return this.#send<T>('GET', scopePath(scope) + `/investigations/${id(runId)}`); }
  /** MC4: bounded lines from the saved, hash-verified snapshot (never the live folder). */
  source(scope: Scope, path: string, sha256: string, line: number, context = 6) {
    const query = new URLSearchParams({ path, sha256, line: String(line), context: String(context) });
    return this.#send<SourceView>('GET', scopePath(scope) + '/source?' + query.toString());
  }
  continueInvestigation(scope: Scope, runId: string, expectedAttemptNumber: number, idempotencyKey: string) {
    if (!Number.isInteger(expectedAttemptNumber) || expectedAttemptNumber < 1)
      throw new Error('Expected attempt number must be positive.');
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(idempotencyKey))
      throw new Error('A stable UUID idempotency key is required for explicit continuation.');
    return this.#send<{ diagnostic: string; attempt: ExecutionAttempt | null }>(
      'POST', scopePath(scope) + `/investigations/${id(runId)}/continue`,
      { expectedAttemptNumber }, true, idempotencyKey);
  }
  cancel(scope: Scope, runId: string, expectedAttemptNumber: number) {
    return this.#send<unknown>('POST', scopePath(scope) + `/investigations/${id(runId)}/cancel`, { expectedAttemptNumber });
  }

  async #send<T>(method: 'GET' | 'POST', path: string, body?: unknown, parse = true, idempotencyKey?: string): Promise<T> {
    const headers: Record<string, string> = { Authorization: 'Bearer ' + this.token(), ...this.#extraHeaders };
    if (method === 'POST') {
      headers['Content-Type'] = 'application/json';
      headers['Idempotency-Key'] = idempotencyKey ?? crypto.randomUUID();
    }
    const response = await this.#fetch(this.baseUrl + path, {
      method, headers, body: method === 'POST' ? JSON.stringify(body ?? {}) : undefined,
    });
    const data = parse || !response.ok ? await readBody(response) : null;
    if (response.status === 401) this.#sessionLost();
    if (!response.ok) throw new ApiError(response.status, describeFailure(response.status, data), data);
    return data as T;
  }
}

/** IDs from the API are GUIDs; anything else is rejected before it reaches a URL path. */
function id(value: string): string {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) throw new Error('Invalid identifier.');
  return value;
}

function scopePath(scope: Scope): string {
  return `/api/v1/workspaces/${id(scope.workspaceId)}/repositories/${id(scope.repositoryId)}/snapshots/${id(scope.snapshotId)}`;
}

async function readBody(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) return null;
  try { return JSON.parse(text); } catch { return text; }
}

function describeFailure(status: number, body: unknown): string {
  const detail = typeof body === 'object' && body !== null
    ? String((body as { detail?: unknown; title?: unknown; error?: unknown }).detail
      ?? (body as { title?: unknown }).title ?? (body as { error?: unknown }).error ?? '')
    : typeof body === 'string' ? body : '';
  const prefix = status === 401 ? 'Session expired or not authorized; pair again.'
    : status === 403 ? 'Refused by the API (is this page’s address in client-origins.json?).'
    : status === 404 ? 'Not found in this scope.'
    : status === 409 ? 'Conflict.'
    : status === 413 ? 'Too large for the analyser\u2019s limits. Try registering a smaller subfolder (for example the web project folder).'
    : `Request failed (${status}).`;
  return detail ? `${prefix} ${detail}`.slice(0, 400) : prefix;
}
