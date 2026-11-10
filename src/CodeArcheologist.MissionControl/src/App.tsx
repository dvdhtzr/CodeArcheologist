import { useEffect, useState } from 'react';
import { AgentAvatar } from './AgentAvatar';
import {
  AGENTS, ROLES, SCENES, SCENE_CAPTIONS, DemoScene, InspectorTab,
  RoleId, StageState, nextScene, sceneIndex, stageFor, visibleEvents,
} from './demo';

type Page = 'observatory' | 'activity' | 'design';
const stateNames: Record<StageState, string> = {
  queued: 'Standing by', active: 'Working', accepted: 'Accepted',
};
const accents: Record<RoleId, string> = {
  investigator: '#62d8ff', skeptic: '#ffc17e', reviewer: '#cbb2ff',
};

function Symbol({ name, size = 18 }: { name: 'orb' | 'layers' | 'pulse' | 'book' | 'arrow' | 'chevron' | 'play' | 'pause' | 'reset' | 'shield'; size?: number }) {
  const paths: Record<string, React.ReactNode> = {
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

function AgentNode({ role, state, selected, onClick }: {
  role: RoleId; state: StageState; selected: boolean; onClick: () => void;
}) {
  const agent = AGENTS[role];
  return (
    <button type="button"
      className={`agent-node agent-node--${role}${selected ? ' agent-node--selected' : ''}`}
      data-state={state} style={{ '--agent-accent': accents[role] } as React.CSSProperties}
      aria-pressed={selected} onClick={onClick}
      aria-label={`${agent.name}; ${stateNames[state]}. Show agent details.`}>
      <span className="agent-avatar-wrap">
        <span className="agent-aura" aria-hidden="true" />
        <AgentAvatar role={role} />
        <span className="agent-index">{role === 'investigator' ? '01' : role === 'skeptic' ? '02' : '03'}</span>
      </span>
      <span className="agent-name">{agent.name}</span>
      <span className="agent-specialty">{agent.title}</span>
      <span className="agent-state"><span className="state-dot" aria-hidden="true"/>{stateNames[state]}</span>
    </button>
  );
}

function CommunicationLines({ scene }: { scene: DemoScene }) {
  const investigator = stageFor(scene, 'investigator');
  const skeptic = stageFor(scene, 'skeptic');
  const reviewer = stageFor(scene, 'reviewer');
  function lineState(state: StageState) {
    return state === 'active' ? 'is-flowing' : state === 'accepted' ? 'is-accepted' : '';
  }
  return (
    <svg className="connection-map" viewBox="0 0 960 600" preserveAspectRatio="xMidYMid meet"
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

function EventLog({ scene, onAgent }: { scene: DemoScene; onAgent: (role: RoleId) => void }) {
  return (
    <ol className="event-list">
      {[...visibleEvents(scene)].reverse().map((event, index) => (
        <li className="event-item" key={event.id}>
          <span className={'event-marker event-marker--' + event.actor} aria-hidden="true" />
          <div className="event-body">
            <div className="event-heading">
              {event.actor === 'host'
                ? <span className="event-title">{event.label}</span>
                : <button type="button" className="inline-link" onClick={() => onAgent(event.actor as RoleId)}>
                    {event.label}
                  </button>}
              <span className="event-step">D{String(visibleEvents(scene).length - index).padStart(2, '0')}</span>
            </div>
            <p>{event.detail}</p>
            <span className="event-type">{event.type}</span>
          </div>
        </li>
      ))}
    </ol>
  );
}

function AgentInspector({ scene, selected, tab, onTab }: {
  scene: DemoScene; selected: RoleId; tab: InspectorTab; onTab: (tab: InspectorTab) => void;
}) {
  const profile = AGENTS[selected];
  const state = stageFor(scene, selected);
  const agentEvents = visibleEvents(scene).filter(e => e.actor === selected);
  return (
    <section className="inspector" aria-labelledby="inspector-title"
      style={{ '--agent-accent': accents[selected] } as React.CSSProperties}>
      <div className="panel-heading">
        <div><span className="eyebrow">AGENT SPOTLIGHT</span><h3 id="inspector-title">Character dossier</h3></div>
        <span className="panel-icon"><Symbol name="orb" size={17}/></span>
      </div>
      <div className="inspector-profile">
        <div className="inspector-portrait"><AgentAvatar role={selected}/></div>
        <div>
          <span className="profile-signature">{profile.signature}</span>
          <h4>{profile.name}</h4>
          <p>{profile.title}</p>
          <span className={'status-chip status-chip--' + state}>{stateNames[state]}</span>
        </div>
      </div>
      <div className="segmented" role="group" aria-label="Agent dossier section">
        <button type="button" className={tab === 'profile' ? 'is-selected' : ''}
          aria-pressed={tab === 'profile'} onClick={() => onTab('profile')}>Profile</button>
        <button type="button" className={tab === 'activity' ? 'is-selected' : ''}
          aria-pressed={tab === 'activity'} onClick={() => onTab('activity')}>Activity</button>
      </div>
      {tab === 'profile' ? (
        <div className="inspector-copy">
          <span className="detail-label">ASSIGNMENT</span>
          <p className="detail-strong">{profile.assignment}</p>
          <span className="detail-label">OPERATING ROLE</span>
          <p>{profile.description}</p>
          <div className="integrity-note"><Symbol name="shield" size={18}/><span>{profile.principle}</span></div>
        </div>
      ) : (
        <div className="inspector-copy">
          <span className="detail-label">DEMONSTRATION ACTIVITY</span>
          {agentEvents.length === 0
            ? <p>No stage events yet in this visual walkthrough.</p>
            : <ul className="agent-activity">{agentEvents.map(event => (
                <li key={event.id}><strong>{event.label}</strong><span>{event.type}</span></li>
              ))}</ul>}
          <div className="integrity-note"><Symbol name="shield" size={18}/><span>These are visual fixtures, not actual model calls or persisted host events.</span></div>
        </div>
      )}
      <div className="inspector-footer"><span className="tiny-led"/> COMPONENT / AGENT CARD V1</div>
    </section>
  );
}

export function App() {
  const [page, setPage] = useState<Page>('observatory');
  const [scene, setScene] = useState<DemoScene>('ready');
  const [selected, setSelected] = useState<RoleId>('investigator');
  const [tab, setTab] = useState<InspectorTab>('profile');
  const [playing, setPlaying] = useState(false);

  useEffect(() => {
    if (!playing) return;
    if (scene === 'complete') { setPlaying(false); return; }
    const timer = window.setTimeout(() => setScene(current => nextScene(current)), 2200);
    return () => window.clearTimeout(timer);
  }, [playing, scene]);

  function chooseAgent(role: RoleId) {
    setSelected(role);
    setTab('profile');
  }

  function reset() {
    setPlaying(false);
    setScene('ready');
  }

  function next() {
    setPlaying(false);
    setScene(current => nextScene(current));
  }

  const stages = sceneIndex(scene);
  const acceptedCount = ROLES.filter(role => stageFor(scene, role) === 'accepted').length;

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
            <button type="button" className={`nav-link ${page === 'activity' ? 'active' : ''}`}
              aria-current={page === 'activity' ? 'page' : undefined}
              onClick={() => setPage('activity')}><Symbol name="pulse"/> Activity Timeline <Symbol name="chevron" size={15}/></button>
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
                <span className="crew-state">{stateForShort(stageFor(scene, role))}</span>
              </button>
            ))}
          </div>
        </div>
        <div className="sidebar-bottom">
          <div className="workspace-card">
            <span className="workspace-card-icon"><Symbol name="layers" size={19}/></span>
            <div><strong>Sample workspace</strong><small>Visual walkthrough only</small></div>
          </div>
          <span className="sidebar-version">MC1 PREVIEW · CLIENT ONLY</span>
        </div>
      </aside>

      <main className="main-content">
        <header className="topbar">
          <div className="breadcrumb"><span>CODE ARCHEOLOGIST</span><Symbol name="chevron" size={14}/><strong>{page === 'observatory' ? 'Agent Observatory' : page === 'activity' ? 'Activity Timeline' : 'Design Principles'}</strong></div>
          <div className="topbar-status"><span className="offline-dot" /> API not connected <span className="topbar-separator"/> <span className="preview-label">DEMO MODE</span></div>
        </header>

        <div className="page-content">
          {page === 'observatory' && (
            <>
              <div className="hero">
                <div><span className="eyebrow eyebrow--accent">THE INTELLIGENCE WORKSPACE / 001</span>
                  <h1>Where intelligence <em>takes shape.</em></h1>
                  <p>Watch a disciplined team of agents examine evidence, challenge conclusions, and reconcile findings.</p>
                </div>
                <span className="hero-seal"><Symbol name="orb" size={34}/></span>
              </div>

              <div className="stat-row" aria-label="Demo status">
                <div className="stat-card"><span className="stat-label">CREW MEMBERS</span><strong>03 <small>agents</small></strong><span className="stat-foot">Distinct, evidence-bounded roles</span></div>
                <div className="stat-card"><span className="stat-label">ACCEPTED STAGES</span><strong>{String(acceptedCount).padStart(2, '0')} <small>/ 03</small></strong><span className="stat-foot">Demonstration state only</span></div>
                <div className="stat-card"><span className="stat-label">LIVE API EVENTS</span><strong>00 <small>connected</small></strong><span className="stat-foot">Integration is scheduled for MC3</span></div>
              </div>

              <div className="mission-grid">
                <section className="observatory-card" aria-labelledby="observatory-heading">
                  <div className="panel-heading observatory-heading">
                    <div><span className="eyebrow">COLLABORATION MAP</span><h2 id="observatory-heading">Agent Observatory</h2></div>
                    <span className="demo-pill"><span className="mini-orbit"/> ILLUSTRATIVE FLOW</span>
                  </div>
                  <div className="observatory-explainer"><Symbol name="shield" size={15}/><span>Independent Investigator and Skeptic paths converge only at the Reviewer.</span></div>
                  <div className="observatory-stage" aria-label="Click an agent to inspect its simulated stage">
                    <div className="stage-grain" aria-hidden="true" />
                    <CommunicationLines scene={scene} />
                    <div className="evidence-node">
                      <span className="evidence-glyph"><Symbol name="layers" size={22}/></span>
                      <div><strong>VERIFIED SCOPE</strong><small>Illustrative evidence source</small></div>
                    </div>
                    {ROLES.map(role => (
                      <AgentNode role={role} key={role} state={stageFor(scene, role)}
                        selected={selected === role} onClick={() => chooseAgent(role)} />
                    ))}
                    <span className="graph-watermark">HOST-OBSERVED STATES • DEMO REPRESENTATION</span>
                  </div>
                  <div className="observatory-bottom">
                    <div className="legend"><span><i className="legend-line cyan" /> Evidence access</span><span><i className="legend-line amber" /> Independent access</span><span><i className="legend-line violet" /> Reconciliation</span></div>
                  </div>
                </section>

                <AgentInspector scene={scene} selected={selected} tab={tab} onTab={setTab}/>
              </div>

              <div className="lower-grid">
                <section className="activity-card" aria-labelledby="recent-heading">
                  <div className="panel-heading"><div><span className="eyebrow">EVENT JOURNAL / PREVIEW</span><h3 id="recent-heading">Activity signal</h3></div>
                    <button className="text-button" type="button" onClick={() => setPage('activity')}>View all <Symbol name="arrow" size={15}/></button>
                  </div>
                  <div className="activity-scroll"><EventLog scene={scene} onAgent={chooseAgent}/></div>
                </section>
                <section className="control-card" aria-labelledby="walkthrough-heading">
                  <div className="panel-heading"><div><span className="eyebrow">VISUAL WALKTHROUGH</span><h3 id="walkthrough-heading">Explore the sequence</h3></div>
                    <span className="demo-pill small">NOT LIVE</span>
                  </div>
                  <div className="step-header"><span>PREVIEW PHASE</span><strong>{String(stages).padStart(2, '0')} / 04</strong></div>
                  <div className="progress-track" role="progressbar" aria-label="Visual walkthrough step"
                    aria-valuemin={0} aria-valuemax={4} aria-valuenow={stages}>
                    <span style={{ width: `${stages * 25}%` }} />
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
                </section>
              </div>
            </>
          )}

          {page === 'activity' && (
            <section className="secondary-page">
              <span className="eyebrow eyebrow--accent">PREVIEW EVENT JOURNAL</span>
              <h1>Every signal, in context.</h1>
              <p className="secondary-intro">This is a demonstration of the activity timeline. MC3 will subscribe to committed, versioned SignalR events and recover from REST on reconnect.</p>
              <div className="secondary-panel">
                <div className="panel-heading"><h2>Illustrative sequence</h2><span className="demo-pill">NO BACKEND EVENTS</span></div>
                <EventLog scene={scene} onAgent={role => { chooseAgent(role); setPage('observatory'); }}/>
                <button className="secondary-action" type="button" onClick={() => setPage('observatory')}>Back to Observatory <Symbol name="arrow" size={16}/></button>
              </div>
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
          <footer className="main-footer"><span>CODE ARCHEOLOGIST / MISSION CONTROL</span><span>MC1 FOUNDATION · API DISCONNECTED · SAMPLE DATA ONLY</span></footer>
        </div>
      </main>
    </div>
  );
}

function stateForShort(state: StageState): string {
  return state === 'accepted' ? '✓' : state === 'active' ? '●' : '–';
}
