/**
 * MC3 pure projections of host records into what Mission Control shows.
 * Agent states come ONLY from the API's execution projection
 * (gate-c6b-execution-projection-v1); timeline entries come ONLY from
 * committed ledger events. Nothing here is inferred from model text, timers
 * or guesses, and every displayed state keeps the exact host status code.
 */
import type { Execution, LedgerEvent } from './api.ts';
import type { RoleId, StageState } from '../demo.ts';

export const LIVE_ROLES: readonly RoleId[] = ['investigator', 'skeptic', 'reviewer'];

export type LiveStage = { state: StageState; hostStatus: string };

/** Maps each host stage status onto a visual state, keeping the original code. */
export function stageState(hostStatus: string): StageState {
  switch (hostStatus) {
    case 'running': return 'active';
    case 'accepted':
    case 'accepted-earlier':
    case 'reused-checkpoint': return 'accepted';
    case 'rejected':
    case 'timed-out':
    case 'cancelled':
    case 'failed':
    case 'abandoned': return 'halted';
    default: return 'queued'; // not-started, legacy-unavailable, unknown future codes
  }
}

export function liveStages(execution: Execution | null): Record<RoleId, LiveStage> {
  const result = {} as Record<RoleId, LiveStage>;
  for (const role of LIVE_ROLES) {
    const stage = execution?.stages.find(s => s.role === role);
    const hostStatus = stage?.status ?? 'not-started';
    result[role] = { state: stageState(hostStatus), hostStatus };
  }
  return result;
}

/**
 * De-duplicates by eventId (delivery is at-least-once) and orders by
 * attempt, then sequence number. Attempts are ordered by their first event.
 */
export function mergeEvents(existing: readonly LedgerEvent[], incoming: readonly LedgerEvent[]): LedgerEvent[] {
  const byId = new Map<string, LedgerEvent>();
  for (const event of [...existing, ...incoming]) if (!byId.has(event.eventId)) byId.set(event.eventId, event);
  const events = [...byId.values()];
  const attemptStart = new Map<string, string>();
  for (const event of events) {
    const first = attemptStart.get(event.attemptId);
    if (first === undefined || event.occurredAtUtc < first) attemptStart.set(event.attemptId, event.occurredAtUtc);
  }
  return events.sort((a, b) => {
    if (a.attemptId !== b.attemptId) {
      const order = (attemptStart.get(a.attemptId) ?? '').localeCompare(attemptStart.get(b.attemptId) ?? '');
      return order !== 0 ? order : a.attemptId.localeCompare(b.attemptId);
    }
    return a.sequenceNumber - b.sequenceNumber;
  });
}

/** True when some attempt's sequence numbers are not exactly 1..n: resync from REST. */
export function hasGap(events: readonly LedgerEvent[]): boolean {
  const perAttempt = new Map<string, number[]>();
  for (const event of events) {
    const list = perAttempt.get(event.attemptId) ?? [];
    list.push(event.sequenceNumber);
    perAttempt.set(event.attemptId, list);
  }
  for (const list of perAttempt.values()) {
    const sorted = [...new Set(list)].sort((a, b) => a - b);
    if (sorted.some((value, index) => value !== index + 1)) return true;
  }
  return false;
}

export type DisplayEvent = { id: string; actor: RoleId | 'host'; label: string; detail: string; type: string; step: string };

const LABELS: Record<string, string> = {
  'attempt-admitted': 'Investigation admitted',
  'attempt-started': 'Host started the attempt',
  'attempt-completed': 'Attempt completed',
  'attempt-incomplete': 'Attempt ended incomplete',
  'attempt-interrupted': 'Attempt interrupted',
  'attempt-cancelled': 'Attempt cancelled',
  'attempt-rejected': 'Attempt rejected',
  'attempt-failed': 'Attempt failed',
  'role-started': 'started',
  'role-assessment-accepted': 'assessment accepted by the host',
  'role-assessment-rejected': 'assessment rejected by the host',
  'role-timed-out': 'timed out',
  'role-cancelled': 'cancelled',
  'role-failed': 'failed',
  'checkpoint-accepted': 'checkpoint recorded',
  'cancellation-requested': 'Cancellation requested',
};

const ROLE_NAMES: Record<RoleId, string> = { investigator: 'Investigator', skeptic: 'Skeptic', reviewer: 'Reviewer' };

export function displayEvent(event: LedgerEvent): DisplayEvent {
  const actor = (LIVE_ROLES as readonly string[]).includes(event.stage ?? '') ? event.stage as RoleId : 'host';
  const known = LABELS[event.eventType];
  const label = actor === 'host'
    ? known ?? humanize(event.eventType)
    : `${ROLE_NAMES[actor]} ${known ?? humanize(event.eventType).toLowerCase()}`;
  const transition = event.fromStatus || event.toStatus ? ` (${event.fromStatus ?? '–'} → ${event.toStatus ?? '–'})` : '';
  return {
    id: event.eventId,
    actor,
    label,
    detail: `Host diagnostic: ${event.diagnostic}${transition}`,
    type: event.eventType,
    step: '#' + String(event.sequenceNumber).padStart(2, '0'),
  };
}

function humanize(code: string): string {
  const text = code.replace(/-/g, ' ');
  return text.charAt(0).toUpperCase() + text.slice(1);
}

export function isTerminal(execution: Execution | null): boolean {
  const status = execution?.currentAttempt?.status;
  return status !== undefined && !['queued', 'running'].includes(status);
}
