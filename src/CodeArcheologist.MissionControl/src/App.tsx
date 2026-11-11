import { useEffect, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { AgentAvatar } from './AgentAvatar';
import {
  AGENTS, ROLES, SCENE_CAPTIONS, nextScene, sceneIndex, stageFor, visibleEvents,
} from './demo';
import type { DemoScene, RoleId, StageState } from './demo';
import { LiveConsole } from './live/LiveConsole.tsx';
import { Findings } from './live/Findings.tsx';
import { DeterministicExplorer } from './live/DeterministicExplorer.tsx';
import type { ExplorerSelection } from './live/DeterministicExplorer.tsx';
import type { LiveView } from './live/LiveConsole.tsx';
import { displayEvent, liveStages } from './live/ledger.ts';
import type { DisplayEvent, LiveStage } from './live/ledger.ts';

type Page = 'observatory' | 'explorer' | 'activity' | 'findings' | 'design';
type Mode = 'live' | 'demo';
const stateNames: Record<StageState, string> = {
  queued: 'Standing by', active: 'Working', accepted: 'Accepted', halted: 'Stopped',
};

/** Demo fixtures in the same display shape as live ledger events. */
function demoDisplay(scene: DemoScene): DisplayEvent[] {
  return visibleEvents(scene).map((event, index) => ({
    id: event.id, actor: event.actor, label: event.label, detail: event.detail, type: event.type,
    step: 'D' + String(index + 1).padStart(2, '0'),
  }));
}

function demoStages(scene: DemoScene): Record<RoleId, LiveStage> {
  const result = {} as Record<RoleId, LiveStage>;
  for (const role of ROLES) result[role] = { state: stageFor(scene, role), hostStatus: 'demo' };
  return result;
}
const accents: Record<RoleId, string> = {
  investigator: '#62d8ff', skeptic: '#ffc17e', reviewer: '#cbb2ff',
};

function Symbol({ name, size = 18 }: { name: 'orb' | 'layers' | 'pulse' | 'book' | 'arrow' | 'chevron' | 'play' | 'pause' | 'reset' | 'shield'; size?: number }) {
  const paths: Record<string, ReactNode> = {
    orb: <><circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="3"/><path d="M12 1v4 M12 19v4 M1 12h4 M19 12h4"/></>,
    layers: <><path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 12 9 5 9-5 M3 16l9 5 9-5"/></>,
    pulse: <><path d="M2 12h4l3-7 5 14 3-7h5"/></>,
    book: <><path d="M12 6a9 9 0 0 0-9-2v15a9 9 0 0 1 9 2M12 6a9 9 0 0 1 9-2v15a9 9 0 0 0-9 2M12 6v15"/></>,
    arrow: <><path d="M5 12h14m-6-6 6 6-6 6"/></>,
    chevron: <><path d="m8 5 7 7-7 7"/></>,
    play: <><path d="m8 5 11 7-11 7V5Z"/></>,
    pause: <><path d="M7 5v14M17 5v14"/></>,
    reset: <><path d="M3 11a9 9 0 1 1 2.8 6.5M3 4v7h7"/></>,
    shield: <><path d="M12 2 20 6v6c0 5-3.5 8-8 10-4.5-2-8-5-8-10V6l8-4Z"/><path d="m9 12 2 2 4-4"/></>,
  };
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none"
      stroke="currentColor" strokeWidth="1.65" strokeLinecap="round"
      strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>
  );
}

function AgentNode({ role, state, hostStatus, selected, onClick }: {
  role: RoleId; state: StageState; hostStatus: string; selected: boolean; onClick: () => void;
}) {
  const agent = AGENTS[role];
  return (
    <button type="button"
      className={`agent-node agent-node--${role}${selected ? ' agent-node--selected' : ''}`}
      data-state={state} style={{ '--agent-accent': accents[role] } as CSSProperties}
      aria-pressed={selected} onClick={onClick}
      aria-label={`${agent.name}; ${stateNames[state]}${hostStatus === 'demo' ? '' : ` (host status ${hostStatus})`}. Show agent details.`}>
      <span className="agent-avatar-wrap">
        <span className="agent-aura" aria-hidden="true" />
        <AgentAvatar role={role} />
        <span className="agent-index">{role === 'investigator' ? '01' : role === 'skeptic' ? '02' : '03'}</span>
      </span>
      <span className="agent-name">{agent.name}</span>
      <span className="agent-specialty">{agent.title}</span>
      <span className="agent-state"><span className="state-dot" aria-hidden="true"/>{stateNames[state]}{hostStatus !== 'demo' && <small className="host-status">{hostStatus}</small>}</span>
    </button>
  );
}

function CommunicationLines({ stages }: { stages: Record<RoleId, LiveStage> }) {
  const investigator = stages.investigator.state;
  const skeptic = stages.skeptic.state;
  const reviewer = stages.reviewer.state;
  function lineState(state: StageState) {
    return state === 'active' ? 'is-flowing' : state === 'accepted' ? 'is-accepted' : '';
  }
  return (
    <svg className="connection-map" viewBox="0 0 960 600" preserveAspectRatio="none"
      aria-hidden="true">
      <defs>
        <linearGradient id="mc-cyan" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#63dbff"/><stop offset="1" stopColor="#3b77ba"/>
        </linearGradient>
        <linearGradient id="mc-amber" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#f1b870"/><stop offset="1" stopColor="#a96f6c"/>
        </linearGradient>
        <linearGradient id="mc-violet" x1="0" y1="0" x2="0.5" y2="1">
          <stop offset="0" stopColor="#a88bfa"/><stop offset="1" stopColor="#9ed6fa"/>
        </linearGradient>
      </defs>
      <path d="M480 135 C480 195 270 177 270 230"
        className={`connection-line connection-line--cyan ${lineState(investigator)}`} />
      <path d="M480 135 C480 195 690 177 690 230"
        className={`connection-line connection-line--amber ${lineState(skeptic)}`} />
      <path d="M270 340 C270 400 480 366 480 432"
        className={`connection-line connection-line--violet ${reviewer === 'queued' ? '' : 'is-flowing'}`} />
      <path d="M690 340 C690 400 480 366 480 432"
        className={`connection-line connection-line--violet ${reviewer === 'queued' ? '' : 'is-flowing'}`} />
      <circle cx="480" cy="135" r="4" fill="#9fdef1" opacity=".75" />
      <circle cx="480" cy="432" r="4" fill="#c6b4ff" opacity={reviewer === 'queued' ? '.25' : '.9'} />
    </svg>
  );
}

function EventLog({ events, onAgent }: { events: DisplayEvent[]; onAgent: (role: RoleId) => void }) {
  if (events.length === 0) return <p className="live-help">No events recorded yet.</p>;
  return (
    <ol className="event-list">
      {[...events].reverse().map(event => (
        <li className="event-item" key={event.id}>
          <span className={'event-marker event-marker--' + event.actor} aria-hidden="true" />
          <div className="event-body">
            <div className="event-heading">
              {event.actor === 'host'
                ? <span className="event-title">{event.label}</span>
                : <button type="button" className="inline-link" onClick={() => onAgent(event.actor as RoleId)}>
                    {event.label}
                  </button>}
              <span className="event-step">{event.step}</span>
            </div>
            <p>{event.detail}</p>
            <span className="event-type">{event.type}</span>
          </div>
        </li>
      ))}
    </ol>
  );
}

function AgentStrip({ role, stage }: { role: RoleId; stage: LiveStage }) {
  const agent = AGENTS[role];
  return (
    <div className="agent-strip" style={{ '--agent-accent': accents[role] } as CSSProperties} aria-live="polite">
      <span className={'crew-indicator crew-indicator--' + role} aria-hidden="true" />
      <div>
        <strong>{agent.name}</strong> · {stateNames[stage.state]}{stage.hostStatus !== 'demo' && <> <code>{stage.hostStatus}</code></>}
        <p>{agent.description}</p>
      </div>
      <span className="agent-strip-note"><Symbol name="shield" size={14}/>{agent.principle}</span>
    </div>
  );
}

export function App() {
  const [page, setPage] = useState<Page>('observatory');
  const [scene, setScene] = useState<DemoScene>('ready');
  const [selected, setSelected] = useState<RoleId>('investigator');
  const [filter, setFilter] = useState<RoleId | 'all'>('all');
  const [playing, setPlaying] = useState(false);
  const [mode, setMode] = useState<Mode>('live');
  const [connected, setConnected] = useState(false);
  const [liveView, setLiveView] = useState<LiveView | null>(null);
  const [explorerSelection, setExplorerSelection] = useState<ExplorerSelection>(null);

  useEffect(() => {
    if (!playing) return;
    if (scene === 'complete') { setPlaying(false); return; }
    const timer = window.setTimeout(() => setScene(current => nextScene(current)), 2200);
    return () => window.clearTimeout(timer);
  }, [playing, scene]);

  function chooseAgent(role: RoleId) {
    setSelected(role);
    setFilter(current => current === role ? 'all' : role);
  }

  function reset() {
    setPlaying(false);
    setScene('ready');
  }

  function next() {
    setPlaying(false);
    setScene(current => nextScene(current));
  }

  const walkthroughStep = sceneIndex(scene);
  // Live only when a run feed is shown; otherwise the clearly labelled demo.
  const showingLive = mode === 'live' && liveView !== null;
  const stages = showingLive ? liveStages(liveView.snapshot.execution) : demoStages(scene);
  const events = showingLive ? liveView.snapshot.events.map(displayEvent) : demoDisplay(scene);
  const acceptedCount = ROLES.filter(role => stages[role].state === 'accepted').length;
  const liveEventCount = showingLive ? liveView.snapshot.events.length : 0;

  return (
    <div className="mc-shell">
      <aside className="sidebar" aria-label="Mission Control navigation">
        <div className="brand">
          <span className="brand-icon"><span className="brand-core">A</span></span>
          <div><strong>CODE ARCHEOLOGIST</strong><span>MISSION CONTROL</span></div>
        </div>
        <div className="sidebar-section">
          <span className="nav-caption">WORKSPACE</span>
          <nav aria-label="Primary">
            <button type="button" className={`nav-link ${page === 'observatory' ? 'active' : ''}`}
              aria-current={page === 'observatory' ? 'page' : undefined}
              onClick={() => setPage('observatory')}><Symbol name="orb"/> Agent Observatory <Symbol name="chevron" size={15}/></button>
            <button type="button" className={`nav-link ${page === 'explorer' ? 'active' : ''}`}
              aria-current={page === 'explorer' ? 'page' : undefined}
              onClick={() => setPage('explorer')}><Symbol name="layers"/> Architecture Explorer <Symbol name="chevron" size={15}/></button>
            <button type="button" className={`nav-link ${page === 'activity' ? 'active' : ''}`}
              aria-current={page === 'activity' ? 'page' : undefined}
              onClick={() => setPage('activity')}><Symbol name="pulse"/> Activity Timeline <Symbol name="chevron" size={15}/></button>
            <button type="button" className={`nav-link ${page === 'findings' ? 'active' : ''}`}
              aria-current={page === 'findings' ? 'page' : undefined}
              onClick={() => setPage('findings')}><Symbol name="shield"/> Findings <Symbol name="chevron" size={15}/></button>
            <button type="button" className={`nav-link ${page === 'design' ? 'active' : ''}`}
              aria-current={page === 'design' ? 'page' : undefined}
              onClick={() => setPage('design')}><Symbol name="book"/> Design Principles <Symbol name="chevron" size={15}/></button>
          </nav>
        </div>
        <div className="sidebar-divider" />
        <div className="sidebar-section">
          <span className="nav-caption">THE CREW</span>
          <div className="crew-list">
            {ROLES.map(role => (
              <button type="button" key={role} className={`crew-link ${selected === role ? 'chosen' : ''}`}
                onClick={() => { chooseAgent(role); setPage('observatory'); }}>
                <span className={'crew-indicator crew-indicator--' + role} />
                <span>{AGENTS[role].name.replace('The ', '')}</span>
                <span className="crew-state">{stateForShort(stages[role].state)}</span>
              </button>
            ))}
          </div>
        </div>
        <div className="sidebar-bottom">
          <div className="workspace-card">
            <span className="workspace-card-icon"><Symbol name="layers" size={19}/></span>
            <div><strong>{showingLive ? 'Live run' : 'Sample workspace'}</strong><small>{showingLive ? liveView.entryPoint : 'Visual walkthrough only'}</small></div>
          </div>
          <span className="sidebar-version">MC3 · {showingLive ? 'LIVE API DATA' : 'DEMO DATA'}</span>
        </div>
      </aside>

      <main className="main-content">
        <header className="topbar">
          <div className="breadcrumb"><span>CODE ARCHEOLOGIST</span><Symbol name="chevron" size={14}/><strong>{page === 'observatory' ? 'Agent Observatory' : page === 'explorer' ? 'Architecture Explorer' : page === 'activity' ? 'Activity Timeline' : page === 'findings' ? 'Findings' : 'Design Principles'}</strong></div>
          <div className="topbar-status">
            {page === 'observatory' && <span className="topbar-metrics" aria-label="Run status">
              <span>Accepted <strong>{acceptedCount}/3</strong></span>
              <span>Events <strong>{showingLive ? liveEventCount : events.length}</strong></span>
            </span>}
            {connected
            ? <><span className="online-dot" /> API connected <span className="topbar-separator"/> <span className={showingLive ? 'live-label' : 'preview-label'}>{showingLive ? 'LIVE' : mode === 'demo' ? 'DEMO MODE' : 'NO RUN SELECTED'}</span></>
            : <><span className="offline-dot" /> API not connected <span className="topbar-separator"/> <span className="preview-label">{mode === 'demo' ? 'DEMO MODE' : 'NOT LIVE'}</span></>}
          </div>
        </header>

        <div className={'page-content' + (page === 'observatory' ? ' page-content--fit' : '')}>
          {/* Always mounted (hidden on other pages) so the live connection and selections survive navigation. */}
          {(
            <div className="cockpit" hidden={page !== 'observatory'}>
              <section className="control-card" aria-labelledby="walkthrough-heading">
                <div className="panel-heading"><div><span className="eyebrow">{mode === 'live' ? 'LIVE API' : 'VISUAL WALKTHROUGH'}</span><h3 id="walkthrough-heading">{mode === 'live' ? 'Run an investigation' : 'Explore the sequence'}</h3></div>
                  <span className="demo-pill small">{showingLive ? 'LIVE' : 'NOT LIVE'}</span>
                </div>
                <div className="segmented" role="group" aria-label="Data source">
                  <button type="button" className={mode === 'live' ? 'is-selected' : ''} aria-pressed={mode === 'live'}
                    onClick={() => { setMode('live'); setPlaying(false); }}>Live API</button>
                  <button type="button" className={mode === 'demo' ? 'is-selected' : ''} aria-pressed={mode === 'demo'}
                    onClick={() => setMode('demo')}>Demo walkthrough</button>
                </div>
                <div className="control-body">
                  <div hidden={mode !== 'live'}><LiveConsole onView={setLiveView} onConnected={setConnected} onSelection={setExplorerSelection} onFindings={() => setPage('findings')} /></div>
                  {mode === 'demo' && <>
                    <div className="step-header"><span>PREVIEW PHASE</span><strong>{String(walkthroughStep).padStart(2, '0')} / 04</strong></div>
                    <div className="progress-track" role="progressbar" aria-label="Visual walkthrough step"
                      aria-valuemin={0} aria-valuemax={4} aria-valuenow={walkthroughStep}>
                      <span style={{ width: `${walkthroughStep * 25}%` }} />
                    </div>
                    <p className="scene-caption" aria-live="polite">{SCENE_CAPTIONS[scene]}</p>
                    <div className="control-actions">
                      <button type="button" className="primary-action"
                        onClick={() => { if (scene === 'complete') setScene('ready'); setPlaying(v => !v); }}>
                        <Symbol name={playing ? 'pause' : 'play'} size={17}/>
                        {playing ? 'Pause demo' : scene === 'complete' ? 'Replay walkthrough' : 'Play walkthrough'}
                      </button>
                      <button type="button" className="secondary-action" onClick={next}
                        disabled={scene === 'complete'}>Next stage <Symbol name="arrow" size={16}/></button>
                      <button type="button" className="icon-action" aria-label="Reset demonstration" title="Reset demonstration" onClick={reset}>
                        <Symbol name="reset" size={18}/>
                      </button>
                    </div>
                    <div className="control-disclaimer"><Symbol name="shield" size={17}/><span>This playback is an explicitly synthetic design preview. It does not execute models, open repositories or connect to the backend.</span></div>
                  </>}
                </div>
              </section>

              <section className="observatory-card" aria-labelledby="observatory-heading">
                <div className="panel-heading observatory-heading">
                  <div><span className="eyebrow">COLLABORATION MAP</span><h2 id="observatory-heading">Agent Observatory</h2></div>
                  <span className="demo-pill"><span className="mini-orbit"/> {showingLive ? 'LIVE HOST STATES' : 'ILLUSTRATIVE FLOW'}</span>
                </div>
                <div className="observatory-stage" aria-label="Click an agent to show its events">
                  <div className="stage-grain" aria-hidden="true" />
                  <CommunicationLines stages={stages} />
                  <div className="evidence-node">
                    <span className="evidence-glyph"><Symbol name="layers" size={20}/></span>
                    <div><strong>VERIFIED SCOPE</strong><small>{showingLive ? 'Saved, hash-verified snapshot' : 'Illustrative evidence source'}</small></div>
                  </div>
                  {ROLES.map(role => (
                    <AgentNode role={role} key={role} state={stages[role].state} hostStatus={stages[role].hostStatus}
                      selected={filter === role} onClick={() => chooseAgent(role)} />
                  ))}
                  <span className="graph-watermark">{showingLive ? 'HOST-OBSERVED STATES • LIVE API' : 'HOST-OBSERVED STATES • DEMO REPRESENTATION'}</span>
                </div>
                <AgentStrip role={filter === 'all' ? selected : filter} stage={stages[filter === 'all' ? selected : filter]} />
              </section>

              <section className="activity-card" aria-labelledby="recent-heading">
                <div className="panel-heading"><div><span className="eyebrow">{showingLive ? 'LIVE LEDGER' : 'PREVIEW'}</span><h3 id="recent-heading">Activity</h3></div>
                  <span className="heading-actions">
                    {showingLive && <button className="text-button" type="button" onClick={() => setPage('findings')}>Findings <Symbol name="arrow" size={15}/></button>}
                    <button className="text-button" type="button" onClick={() => setPage('activity')}>Full timeline <Symbol name="arrow" size={15}/></button>
                  </span>
                </div>
                <div className="filter-chips" role="group" aria-label="Show events for">
                  {(['all', ...ROLES] as const).map(option => (
                    <button type="button" key={option} aria-pressed={filter === option}
                      className={'chip' + (filter === option ? ' is-selected' : '') + (option === 'all' ? '' : ' chip--' + option)}
                      onClick={() => option === 'all' ? setFilter('all') : chooseAgent(option)}>
                      {option === 'all' ? 'All' : AGENTS[option].name.replace('The ', '')}
                    </button>
                  ))}
                </div>
                <div className="activity-scroll"><EventLog events={filter === 'all' ? events : events.filter(e => e.actor === filter)} onAgent={chooseAgent}/></div>
              </section>
            </div>
          )}

          {page === 'explorer' && <DeterministicExplorer selection={explorerSelection} />}
          {page === 'activity' && (
            <section className="secondary-page">
              <span className="eyebrow eyebrow--accent">PREVIEW EVENT JOURNAL</span>
              <h1>Every signal, in context.</h1>
              <p className="secondary-intro">{showingLive
                ? 'Committed host ledger events for the selected run, pushed over SignalR and re-read from REST on reconnect, so nothing is missed or shown twice.'
                : 'This is a demonstration of the activity timeline. Connect to the API under Live API to see committed ledger events.'}</p>
              <div className="secondary-panel">
                <div className="panel-heading"><h2>{showingLive ? 'Live ledger' : 'Illustrative sequence'}</h2><span className="demo-pill">{showingLive ? 'LIVE' : 'NO BACKEND EVENTS'}</span></div>
                <EventLog events={events} onAgent={role => { chooseAgent(role); setPage('observatory'); }}/>
                <button className="secondary-action" type="button" onClick={() => setPage('observatory')}>Back to Observatory <Symbol name="arrow" size={16}/></button>
              </div>
            </section>
          )}

          {page === 'findings' && (
            <section className="secondary-page">
              <span className="eyebrow eyebrow--accent">{showingLive ? 'HOST-RECORDED REPORT' : 'FINDINGS'}</span>
              <h1>What the agents <em>concluded.</em></h1>
              {showingLive
                ? <>
                    <p className="secondary-intro">{liveView.entryPoint}</p>
                    <div className="secondary-panel">
                      <Findings api={liveView.api} scope={liveView.scope} runId={liveView.runId}
                        runStatus={liveView.snapshot.execution?.runStatus} />
                    </div>
                  </>
                : <p className="secondary-intro">Connect under Live API on the Observatory page and start or choose a run. The demo walkthrough has no findings, because nothing in it is real.</p>}
              <button className="secondary-action" type="button" onClick={() => setPage('observatory')}>Back to Observatory <Symbol name="arrow" size={16}/></button>
            </section>
          )}

          {page === 'design' && (
            <section className="secondary-page">
              <span className="eyebrow eyebrow--accent">MISSION CONTROL / DESIGN CONTRACT</span>
              <h1>Expressive interface. <em>Grounded facts.</em></h1>
              <p className="secondary-intro">This visual client is intentionally separate from the existing bundled frontend and C6 execution code. It will consume only versioned, documented API contracts.</p>
              <div className="principle-grid">
                <article><Symbol name="shield" size={24}/><h2>Truth first</h2><p>Glowing agents mean host-observed activity, not secret reasoning or proven results. Explicit statuses always accompany animation.</p></article>
                <article><Symbol name="layers" size={24}/><h2>Independent roles</h2><p>The Investigator and Skeptic begin with separate evidence sessions. Their assessments only converge at the Reviewer.</p></article>
                <article><Symbol name="pulse" size={24}/><h2>Durable streaming</h2><p>Later SignalR notifications will describe committed ledger events. REST remains authoritative for replay and reconnect.</p></article>
                <article><Symbol name="orb" size={24}/><h2>Independent client</h2><p>Mission Control owns its visuals and can be replaced without requiring changes to the Code Archeologist API or worker.</p></article>
              </div>
              <button className="secondary-action" type="button" onClick={() => setPage('observatory')}>Return to Observatory <Symbol name="arrow" size={16}/></button>
            </section>
          )}
          {page !== 'observatory' && <footer className="main-footer"><span>CODE ARCHEOLOGIST / MISSION CONTROL</span><span>{showingLive ? 'MC3 · LIVE API · HOST RECORDS, NOT PROOF' : connected ? 'MC3 · API CONNECTED · DEMO OR NO RUN SELECTED' : 'MC3 · API DISCONNECTED · SAMPLE DATA ONLY'}</span></footer>}
        </div>
      </main>
    </div>
  );
}

function stateForShort(state: StageState): string {
  return state === 'accepted' ? '✓' : state === 'active' ? '●' : state === 'halted' ? '✕' : '–';
}
