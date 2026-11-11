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
export type Anchor = { filePath: string; fileSha256: string; line: number; column: number };
export type EntryPoint = { id: string; kind: string; evidenceStatus: string; label: string; detail: string; anchor: Anchor };
export type EntryPointList = {
  contractVersion: string; snapshotId: string; fingerprintSha256: string; count: number; truncated: boolean; items: EntryPoint[]; caveat: string;
};
export type RunSummary = {
  runId: string; status: string; diagnostic: string; startedAtUtc: string; finishedAtUtc: string | null;
  scope: { workspaceId: string; repositoryId: string; snapshotId: string; entryPointId: string };
};
export type LedgerEvent = {
  eventId: string; attemptId: string; runId: string; sequenceNumber: number; eventType: string; diagnostic: string;
  fromStatus: string | null; toStatus: string | null; occurredAtUtc: string; stage: string | null;
  ownershipEpoch: number | null; checkpointId: string | null;
};
export type ExecutionStage = { role: string; status: string; checkpoint: { checkpointId: string; contentSha256: string } | null };
export type ExecutionAttempt = { attemptId: string; number: number; status: string; terminalCause: string | null; diagnostic: string };
export type Execution = {
  projectionVersion: string; runId: string; runStatus: string; runDiagnostic: string; legacy: boolean;
  currentAttempt: ExecutionAttempt | null; attempts: ExecutionAttempt[]; stages: ExecutionStage[];
  cancellable: boolean; note: string;
};
export type Scope = { workspaceId: string; repositoryId: string; snapshotId: string };
export type SourceView = {
  contractVersion: string; filePath: string; fileSha256: string; totalLines: number; focusLine: number;
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
  listEntryPoints(scope: Scope) { return this.#send<EntryPointList>('GET', scopePath(scope) + '/entry-points'); }
  listMethods(scope: Scope) { return this.#send<{ contractVersion: string; snapshotId: string; fingerprintSha256: string; count: number; truncated: boolean; items: Array<{ id: string; label: string; detail: string | null; anchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null }; inboundProvisionalCandidates: number }>; caveat: string }>('GET', scopePath(scope) + '/methods'); }
  impact(scope: Scope, methodId: string) {
    return this.#send<{ contractVersion: string; snapshotId: string; fingerprintSha256: string; method: { id: string; label: string; detail: string | null; anchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null }; inboundProvisionalCandidates: number }; directCandidates: Array<{ calleeEdgeId: string; evidenceStatus: string; invocationAnchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null }; callerLabel: string; callerKind: string; callerAnchor: { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null } }>; candidateEdgesWithoutObservedContainment: number; snapshotUnresolvedCalleeCount: number; unknowns: string[]; caveat: string }>('GET', scopePath(scope) + '/impact?methodId=' + encodeURIComponent(methodId));
  }
  listRuns(scope: Scope) { return this.#send<Page<RunSummary>>('GET', scopePath(scope) + '/investigations'); }
  startInvestigation(scope: Scope, entryPointId: string) {
    return this.#send<RunSummary>('POST', scopePath(scope) + '/investigations', { entryPointId });
  }
  events(scope: Scope, runId: string) { return this.#send<LedgerEvent[]>('GET', scopePath(scope) + `/investigations/${id(runId)}/events`); }
  execution(scope: Scope, runId: string) { return this.#send<Execution>('GET', scopePath(scope) + `/investigations/${id(runId)}/execution`); }
  run<T>(scope: Scope, runId: string) { return this.#send<T>('GET', scopePath(scope) + `/investigations/${id(runId)}`); }
  /** MC4: bounded lines from the saved, hash-verified snapshot (never the live folder). */
  source(scope: Scope, path: string, sha256: string, line: number, context = 6) {
    const query = new URLSearchParams({ path, sha256, line: String(line), context: String(context) });
    return this.#send<SourceView>('GET', scopePath(scope) + '/source?' + query.toString());
  }
  cancel(scope: Scope, runId: string, expectedAttemptNumber: number) {
    return this.#send<unknown>('POST', scopePath(scope) + `/investigations/${id(runId)}/cancel`, { expectedAttemptNumber });
  }

  async #send<T>(method: 'GET' | 'POST', path: string, body?: unknown, parse = true): Promise<T> {
    const headers: Record<string, string> = { Authorization: 'Bearer ' + this.token(), ...this.#extraHeaders };
    if (method === 'POST') {
      headers['Content-Type'] = 'application/json';
      headers['Idempotency-Key'] = crypto.randomUUID();
    }
    const response = await this.#fetch(this.baseUrl + path, {
      method, headers, body: method === 'POST' ? JSON.stringify(body ?? {}) : undefined,
    });
    const data = parse || !response.ok ? await readBody(response) : null;
    if (response.status === 401) this.forget();
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
