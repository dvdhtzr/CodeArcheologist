/**
 * MC1 presentation fixtures only. Nothing in this file is a model result, API
 * event, persisted checkpoint, semantic proof, or indication of live progress.
 * MC3 will add a separate typed adapter for real API contracts.
 */
export type RoleId = 'investigator' | 'skeptic' | 'reviewer';
export type StageState = 'queued' | 'active' | 'accepted' | 'halted';
export type DemoScene = 'ready' | 'investigating' | 'challenging' | 'reviewing' | 'complete';
export type InspectorTab = 'profile' | 'activity';

export type AgentProfile = {
  id: RoleId;
  name: string;
  title: string;
  assignment: string;
  description: string;
  principle: string;
  signature: string;
};

export const ROLES: readonly RoleId[] = ['investigator', 'skeptic', 'reviewer'];
export const SCENES: readonly DemoScene[] = [
  'ready', 'investigating', 'challenging', 'reviewing', 'complete',
];

export const AGENTS: Record<RoleId, AgentProfile> = {
  investigator: {
    id: 'investigator',
    name: 'The Investigator',
    title: 'Architecture & evidence',
    assignment: 'Inspect source-backed hypotheses',
    description: 'Searches a bounded, verified snapshot for evidence and proposes explanations with explicit uncertainty.',
    principle: 'An evidence reference is not proof that a narrative claim is true.',
    signature: 'CYAN / OBSERVATION',
  },
  skeptic: {
    id: 'skeptic',
    name: 'The Skeptic',
    title: 'Independent challenge',
    assignment: 'Identify material weaknesses',
    description: 'Independently inspects the same permitted evidence without receiving the Investigator’s conclusions.',
    principle: 'Independence comes before reconciliation. There is no Investigator-to-Skeptic handoff.',
    signature: 'AMBER / CHALLENGE',
  },
  reviewer: {
    id: 'reviewer',
    name: 'The Reviewer',
    title: 'Reconciliation & decisions',
    assignment: 'Reconcile validated assessments',
    description: 'Receives separately accepted assessments and accounts for material concerns; the host validates its output.',
    principle: 'Completing a workflow does not establish semantic or runtime truth.',
    signature: 'VIOLET / SYNTHESIS',
  },
};

export const SCENE_STATES: Record<DemoScene, Record<RoleId, StageState>> = {
  ready:         { investigator: 'queued',   skeptic: 'queued',   reviewer: 'queued' },
  investigating: { investigator: 'active',   skeptic: 'queued',   reviewer: 'queued' },
  challenging:   { investigator: 'accepted', skeptic: 'active',   reviewer: 'queued' },
  reviewing:     { investigator: 'accepted', skeptic: 'accepted', reviewer: 'active' },
  complete:      { investigator: 'accepted', skeptic: 'accepted', reviewer: 'accepted' },
};

export const SCENE_CAPTIONS: Record<DemoScene, string> = {
  ready: 'A source scope is selected. No agent is running in this preview.',
  investigating: 'The Investigator examines the saved evidence scope.',
  challenging: 'The Skeptic performs a separate, independent assessment.',
  reviewing: 'The Reviewer reconciles two host-accepted assessments.',
  complete: 'The demonstration workflow is finished; its findings are not proof.',
};

export type DemoEvent = {
  id: string;
  scene: DemoScene;
  type: string;
  actor: RoleId | 'host';
  label: string;
  detail: string;
};

export const DEMO_EVENTS: readonly DemoEvent[] = [
  { id: 'demo-01', scene: 'ready', type: 'scenario-loaded', actor: 'host',
    label: 'Preview scenario loaded', detail: 'Illustrative source scope. No real repository was read.' },
  { id: 'demo-02', scene: 'investigating', type: 'role-started', actor: 'investigator',
    label: 'Investigator begins', detail: 'Demonstration of a host-observed role-started event.' },
  { id: 'demo-03', scene: 'challenging', type: 'checkpoint-accepted', actor: 'investigator',
    label: 'Investigator checkpoint accepted', detail: 'Acceptance refers to a validated output contract, not semantic proof.' },
  { id: 'demo-04', scene: 'challenging', type: 'role-started', actor: 'skeptic',
    label: 'Skeptic begins independently', detail: 'No information moves from the Investigator to the Skeptic.' },
  { id: 'demo-05', scene: 'reviewing', type: 'checkpoint-accepted', actor: 'skeptic',
    label: 'Skeptic checkpoint accepted', detail: 'Two independently accepted assessments can now be reconciled.' },
  { id: 'demo-06', scene: 'reviewing', type: 'role-started', actor: 'reviewer',
    label: 'Reviewer begins reconciliation', detail: 'The Reviewer receives the validated assessment outputs.' },
  { id: 'demo-07', scene: 'complete', type: 'checkpoint-accepted', actor: 'reviewer',
    label: 'Reviewer checkpoint accepted', detail: 'The host accepts the reconciliation contract.' },
  { id: 'demo-08', scene: 'complete', type: 'attempt-completed', actor: 'host',
    label: 'Demonstration complete', detail: 'Workflow complete does not mean source behavior is proven.' },
];

export function stageFor(scene: DemoScene, role: RoleId): StageState {
  return SCENE_STATES[scene][role];
}

export function sceneIndex(scene: DemoScene): number {
  return SCENES.indexOf(scene);
}

export function visibleEvents(scene: DemoScene): DemoEvent[] {
  const ceiling = sceneIndex(scene);
  return DEMO_EVENTS.filter(event => sceneIndex(event.scene) <= ceiling);
}

export function nextScene(scene: DemoScene): DemoScene {
  return SCENES[Math.min(sceneIndex(scene) + 1, SCENES.length - 1)];
}
