import { useEffect, useState } from 'react';

type ApiStatus = 'checking' | 'connected' | 'unavailable';
type HealthResponse = {
  status: string;
  component: string;
  stage: string;
};

export function App() {
  const [status, setStatus] = useState<ApiStatus>('checking');

  useEffect(() => {
    const controller = new AbortController();
    async function checkApi() {
      try {
        const response = await fetch('/api/v1/health', {
          cache: 'no-store',
          signal: controller.signal,
        });
        if (!response.ok) throw new Error('API health request failed');
        const health: HealthResponse = await response.json();
        if (health.status !== 'ok' || health.component !== 'api') {
          throw new Error('Unexpected health response');
        }
        setStatus('connected');
      } catch {
        if (!controller.signal.aborted) setStatus('unavailable');
      }
    }
    void checkApi();
    return () => controller.abort();
  }, []);

  const message = {
    checking: 'Checking local API connection…',
    connected: 'Connected to the local API.',
    unavailable: 'Local API unavailable; start both developer processes.',
  }[status];

  return (
    <main className="app-shell">
      <div className="eyebrow">CODE ARCHEOLOGIST / FOUNDATION</div>
      <h1>Understand software through evidence.</h1>
      <p className="lead">
        A local-first investigative workbench for software architecture, functionality,
        dependencies, change impact, and institutional memory.
      </p>
      <section className="status-panel" aria-live="polite">
        <div className="status-top">
          <span className="status-label">LOCAL API</span>
          <span className={'status-dot ' + status} aria-hidden="true" />
        </div>
        <p>{message}</p>
        <small>This is a connectivity test only. No repository analysis or agents are running.</small>
      </section>
      <aside className="notice">
        <strong>Developer foundation</strong>
        <p>
          Authentication, ingestion, persistence, orchestration, and Mission Control
          will arrive in independently verified security-gated phases.
        </p>
      </aside>
    </main>
  );
}
