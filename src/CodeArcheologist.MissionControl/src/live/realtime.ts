/**
 * MC3 live run feed: push-only SignalR hub (code-archeologist-realtime-v1)
 * plus REST as the authority. On subscribe, on every reconnect and whenever a
 * sequence gap is seen, the full ledger and execution projection are re-read
 * over REST, so the view is gap-free and never shows events twice (rule 59).
 * Commands never go over SignalR; this module only subscribes.
 */
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';
import type { ApiClient, Execution, LedgerEvent, Scope } from './api.ts';
import { hasGap, mergeEvents } from './ledger.ts';

export const REALTIME_CONTRACT = 'code-archeologist-realtime-v1';
const TERMINAL = /^attempt-(completed|incomplete|interrupted|cancelled|rejected|failed)$/;

export type LiveSnapshot = {
  events: LedgerEvent[];
  execution: Execution | null;
  connection: 'connecting' | 'live' | 'reconnecting' | 'disconnected';
  error: string | null;
};

type Listener = (state: LiveSnapshot) => void;

export class LiveRun {
  readonly #api: ApiClient;
  readonly #scope: Scope;
  readonly #runId: string;
  readonly #listener: Listener;
  readonly #headers: Record<string, string>;
  #hub: HubConnection | null = null;
  #state: LiveSnapshot = { events: [], execution: null, connection: 'connecting', error: null };
  #closed = false;
  #resyncing: Promise<void> | null = null;

  constructor(api: ApiClient, scope: Scope, runId: string, listener: Listener, headers: Record<string, string> = {}) {
    this.#api = api;
    this.#scope = scope;
    this.#runId = runId;
    this.#listener = listener;
    this.#headers = headers;
  }

  get state(): LiveSnapshot { return this.#state; }

  async start(): Promise<void> {
    const hub = new HubConnectionBuilder()
      .withUrl(this.#api.baseUrl + '/hubs/v1/investigations', {
        accessTokenFactory: () => this.#api.token(),
        headers: this.#headers,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();
    this.#hub = hub;
    hub.on('eventAppended', (message: { contractVersion: string; event: LedgerEvent }) => {
      if (message?.contractVersion !== REALTIME_CONTRACT || message.event?.runId !== this.#runId) return;
      const events = mergeEvents(this.#state.events, [message.event]);
      this.#update({ events });
      // A gap, or a terminal attempt event, triggers an authoritative REST re-read
      // (the latter so a slower earlier REST answer can never leave a stale state).
      if (hasGap(events) || TERMINAL.test(message.event.eventType)) void this.resync();
    });
    hub.on('executionUpdated', (message: { contractVersion: string; execution: Execution }) => {
      if (message?.contractVersion !== REALTIME_CONTRACT || message.execution?.runId !== this.#runId) return;
      this.#update({ execution: message.execution });
    });
    hub.onreconnecting(() => this.#update({ connection: 'reconnecting' }));
    hub.onreconnected(async () => {
      try {
        await this.#subscribe();
        await this.resync();
        this.#update({ connection: 'live', error: null });
      } catch (error) { this.#update({ error: message(error) }); }
    });
    hub.onclose(error => { if (!this.#closed) this.#update({ connection: 'disconnected', error: error ? message(error) : null }); });

    await this.resync(); // REST first: the screen is correct even before the hub connects
    try {
      await hub.start();
      await this.#subscribe();
      await this.resync();
      this.#update({ connection: 'live', error: null });
    } catch (error) {
      this.#update({ connection: 'disconnected', error: 'Live updates unavailable: ' + message(error) });
    }
  }

  /** Re-reads the authoritative ledger and execution projection over REST. */
  resync(): Promise<void> {
    if (this.#resyncing) return this.#resyncing;
    this.#resyncing = (async () => {
      try {
        const [events, execution] = await Promise.all([
          this.#api.events(this.#scope, this.#runId),
          this.#api.execution(this.#scope, this.#runId),
        ]);
        this.#update({ events: mergeEvents(this.#state.events, events), execution });
      } catch (error) {
        this.#update({ error: message(error) });
      } finally {
        this.#resyncing = null;
      }
    })();
    return this.#resyncing;
  }

  async stop(): Promise<void> {
    this.#closed = true;
    const hub = this.#hub;
    this.#hub = null;
    if (!hub) return;
    try {
      if (hub.state === HubConnectionState.Connected) await hub.invoke('Unsubscribe', this.#runId);
    } catch { /* closing anyway */ }
    await hub.stop();
  }

  async #subscribe(): Promise<void> {
    const result = await this.#hub!.invoke('Subscribe', { ...this.#scope, runId: this.#runId }) as { contractVersion?: string };
    if (result?.contractVersion !== REALTIME_CONTRACT) throw new Error('Unexpected realtime contract version.');
  }

  #update(patch: Partial<LiveSnapshot>): void {
    if (this.#closed) return;
    this.#state = { ...this.#state, ...patch };
    this.#listener(this.#state);
  }
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
