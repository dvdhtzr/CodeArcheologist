import { useEffect, useState } from 'react';

type ApiStatus = 'checking' | 'connected' | 'unavailable';
type Session =
  | { status: 'checking' }
  | { status: 'locked' }
  | { status: 'unlocked'; csrfToken: string };

type HealthResponse = {
  status: string;
  component: string;
  stage: string;
};

async function readSession(signal?: AbortSignal): Promise<Session> {
  const response = await fetch('/api/v1/auth/session', {
    cache: 'no-store',
    credentials: 'same-origin',
    signal,
  });

  if (response.status === 401) return { status: 'locked' };
  if (!response.ok) throw new Error('Unable to read the local session');

  const data: { authenticated?: boolean; csrfToken?: string } = await response.json();
  if (!data.authenticated || typeof data.csrfToken !== 'string') {
    throw new Error('Unrecognized session response');
  }
  return { status: 'unlocked', csrfToken: data.csrfToken };
}

export function App() {
  const [apiStatus, setApiStatus] = useState<ApiStatus>('checking');
  const [session, setSession] = useState<Session>({ status: 'checking' });
  const [pairingCode, setPairingCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    const controller = new AbortController();

    async function checkApi() {
      try {
        const response = await fetch('/api/v1/health', {
          cache: 'no-store',
          signal: controller.signal,
        });
        if (!response.ok) throw new Error('Health check failed');
        const health: HealthResponse = await response.json();
        if (health.status !== 'ok' || health.component !== 'api') {
          throw new Error('Unexpected health response');
        }
        setApiStatus('connected');
      } catch {
        if (!controller.signal.aborted) setApiStatus('unavailable');
      }

      try {
        const currentSession = await readSession(controller.signal);
        if (!controller.signal.aborted) setSession(currentSession);
      } catch {
        if (!controller.signal.aborted) setSession({ status: 'locked' });
      }
    }

    void checkApi();
    return () => controller.abort();
  }, []);

  async function pair() {
    setBusy(true);
    setError('');
    try {
      const response = await fetch('/api/v1/auth/login', {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'X-CodeArcheologist-Request': '1',
        },
        body: JSON.stringify({ code: pairingCode.trim() }),
      });
      if (response.status === 401) {
        throw new Error('Pairing code rejected. Check the code displayed by the start script.');
      }
      if (!response.ok) throw new Error('Local pairing failed');
      const newSession = await readSession();
      if (newSession.status !== 'unlocked') throw new Error('Session was not established');
      setSession(newSession);
      setPairingCode('');
      setApiStatus('connected');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to pair');
    } finally {
      setBusy(false);
    }
  }

  async function signOut(csrfToken: string) {
    setBusy(true);
    setError('');
    try {
      const response = await fetch('/api/v1/auth/logout', {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'X-CodeArcheologist-Request': '1',
          'X-CodeArcheologist-Csrf': csrfToken,
        },
      });
      if (!response.ok) throw new Error('Sign-out request was rejected');
      setSession({ status: 'locked' });
    } catch {
      setError('Unable to end the local session. Refresh the page and retry.');
    } finally {
      setBusy(false);
    }
  }

  const message = {
    checking: 'Checking local API connection…',
    connected: 'Connected to the loopback API.',
    unavailable: 'Local API unavailable; start both developer processes.',
  }[apiStatus];

  return (
    <main className="app-shell">
      <div className="eyebrow">CODE ARCHEOLOGIST / FOUNDATION</div>
      <h1>Understand software through evidence.</h1>
      <p className="lead">
        A local-first investigative workbench for architecture, functionality,
        dependencies, change impact, and institutional memory.
      </p>
      <section className="status-panel" aria-live="polite">
        <div className="status-top">
          <span className="status-label">LOCAL API</span>
          <span className={'status-dot ' + apiStatus} aria-hidden="true" />
        </div>
        <p>{message}</p>
        <small>Connectivity only. No repositories or agents are running.</small>
      </section>

      <section className="auth-panel" aria-live="polite">
        <div className="eyebrow">LOCAL ACCESS</div>
        {session.status === 'checking' && <p>Checking browser session…</p>}
        {session.status === 'locked' && (
          <>
            <h2>Pair this browser</h2>
            <p>
              Run <code>scripts/start.ps1</code> (or <code>scripts/start.sh</code>)
              and enter the pairing code printed in the terminal. The code is
              generated for this application process and never sent to a cloud service.
            </p>
            <form onSubmit={(event) => { event.preventDefault(); void pair(); }}>
              <label htmlFor="pairing-code">Local pairing code</label>
              <div className="pairing-controls">
                <input
                  id="pairing-code"
                  type="password"
                  value={pairingCode}
                  autoComplete="off"
                  spellCheck={false}
                  maxLength={32}
                  placeholder="32-character code"
                  onChange={(event) => setPairingCode(event.target.value)}
                />
                <button type="submit" disabled={busy || pairingCode.trim().length !== 32}>
                  {busy ? 'Pairing…' : 'Connect'}
                </button>
              </div>
            </form>
          </>
        )}
        {session.status === 'unlocked' && (
          <>
            <h2>Browser paired</h2>
            <p>Authenticated local session active. This is not yet a multi-user login system.</p>
            <button type="button" disabled={busy} onClick={() => void signOut(session.csrfToken)}>
              End local session
            </button>
          </>
        )}
        {error && <p className="error" role="alert">{error}</p>}
      </section>

      <aside className="notice">
        <strong>Developer foundation</strong>
        <p>
          Workspace persistence, ingestion, analysis, MAF investigations, plugins, and
          Mission Control will arrive in independently verified, security-gated phases.
        </p>
      </aside>
    </main>
  );
}
