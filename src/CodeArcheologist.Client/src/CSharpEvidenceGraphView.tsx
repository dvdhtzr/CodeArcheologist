import { useEffect, useMemo, useState } from 'react';

type GraphNode = {
  id: string;
  kind: string;
  label: string;
  filePath: string | null;
  fileSha256: string | null;
  line: number | null;
  column: number | null;
  detail: string | null;
};

type ProjectEvidence = {
  projectPath: string;
  projectSha256: string;
  inclusionBasis: string;
  unresolvedRuleCount: number;
};

type GraphEdge = {
  id: string;
  fromId: string;
  toId: string;
  kind: string;
  status: string;
  projectContextStatus: string | null;
  evidencePath: string;
  evidenceSha256: string;
  line: number | null;
  column: number | null;
  explanation: string;
  sourceProjectCandidates: ProjectEvidence[] | null;
  targetProjectCandidates: ProjectEvidence[] | null;
  sharedProjectCandidates: ProjectEvidence[] | null;
};

type EvidenceGraphReport = {
  snapshotId: string;
  fingerprintSha256: string;
  analyzerVersion: string;
  linkContextAnalyzerVersion: string;
  projectAnalyzerVersion: string;
  syntaxAnalyzerVersion: string;
  syntaxDeclarationCount: number;
  invocationAnalyzerVersion: string;
  invocationCount: number;
  methodContainedInvocationCount: number;
  calleeAnalyzerVersion: string;
  provisionalCalleeCount: number;
  unresolvedCalleeCount: number;
  calleeSharedCandidateCount: number;
  calleeDisjointCandidateCount: number;
  calleeUnknownProjectCount: number;
  httpAttributeAnalyzerVersion: string;
  httpAttributeCandidateCount: number;
  nonliteralHttpAttributeRouteCount: number;
  minimalApiAnalyzerVersion: string;
  minimalApiCandidateCount: number;
  nonliteralMinimalApiRouteCount: number;
  nodeCount: number;
  edgeCount: number;
  nodes: GraphNode[];
  edges: GraphEdge[];
  limitations: string[];
};

type AdjacentEvidence = { edge: GraphEdge; other: GraphNode; outgoing: boolean };

function Short({ value, length = 24 }: { value: string; length?: number }) {
  return <>{value.length > length ? value.slice(0, length - 1) + '…' : value}</>;
}

function ProjectCandidateList({ label, candidates }: {
  label: string;
  candidates: ProjectEvidence[] | null;
}) {
  return (
    <div className="workspace-note">
      <strong>{label}:</strong>{' '}
      {!candidates?.length
        ? 'No static project candidates'
        : candidates.map(candidate => (
          <div key={candidate.projectPath}>
            <code>{candidate.projectPath}</code>
            {' '}· project SHA-256 {candidate.projectSha256.slice(0, 16)}…
            {' '}· {candidate.inclusionBasis}
            {' '}· {candidate.unresolvedRuleCount} unresolved rules
          </div>
        ))}
    </div>
  );
}

function EvidenceAddress({ path, hash, line, column }: {
  path: string; hash: string; line: number | null; column: number | null;
}) {
  return (
    <code>
      {path}{line === null ? '' : ':' + line}{column === null ? '' : ':' + column}
      {' '}· SHA-256 {hash.slice(0, 16)}…
    </code>
  );
}

export function CSharpEvidenceGraphView({ endpoint }: { endpoint: string }) {
  const [report, setReport] = useState<EvidenceGraphReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [selectedId, setSelectedId] = useState('');
  const [search, setSearch] = useState('');
  const [kind, setKind] = useState('all');
  const [calleeContextFilter, setCalleeContextFilter] = useState('all');
  const [impactSearch, setImpactSearch] = useState('');
  const [impactMethodId, setImpactMethodId] = useState('');

  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      setLoading(true);
      setError('');
      setReport(null);
      setSelectedId('');
      try {
        const response = await fetch(endpoint, {
          credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        });
        if (!response.ok) {
          const body: { error?: string } = await response.json().catch(() => ({}));
          throw new Error(body.error ?? 'Evidence graph failed (HTTP ' + response.status + ')');
        }
        const data: EvidenceGraphReport = await response.json();
        if (!controller.signal.aborted) {
          setReport(data);
          // Start with a candidate-backed target when possible, so users
          // immediately see meaningful evidence rather than an empty example.
          const firstCallee = data.edges.find(edge => edge.kind === 'candidate-callee');
          const firstMethod = data.nodes.find(node =>
            node.kind === 'syntax-declaration' &&
            node.detail?.startsWith('method syntax declaration'));
          setImpactMethodId(firstCallee?.toId ?? firstMethod?.id ?? '');
          setImpactSearch('');
          setSelectedId(data.nodes.find(node => node.kind === 'project-file')?.id ??
            data.nodes.find(node => node.kind === 'csharp-file')?.id ??
            data.nodes[0]?.id ?? '');
        }
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : 'Evidence graph unavailable.');
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => controller.abort();
  }, [endpoint]);

  const nodesById = useMemo(() => new Map(
    (report?.nodes ?? []).map(node => [node.id, node])), [report]);

  // Callee edges were previously discoverable only after a user happened
  // to choose an invocation with a candidate target. Expose them directly.
  const calleeEdges = useMemo(
    () => (report?.edges ?? []).filter(edge =>
      edge.kind === 'candidate-callee' &&
      nodesById.has(edge.fromId) && nodesById.has(edge.toId)),
    [report, nodesById]
  );
  const httpAttributeRows = useMemo(() =>
    (report?.nodes ?? [])
      .filter(node => node.kind === 'http-endpoint-attribute')
      .map(node => {
        const link = report?.edges.find(edge =>
          edge.kind === 'annotates-method' && edge.fromId === node.id &&
          edge.status === 'candidate');
        const method = link ? nodesById.get(link.toId) : undefined;
        return { node, link, method };
      }), [report, nodesById]);

  const minimalApiRows = useMemo(() =>
    (report?.nodes ?? [])
      .filter(node => node.kind === 'http-minimal-mapping')
      .map(node => {
        const link = report?.edges.find(edge =>
          edge.kind === 'candidate-http-mapping' &&
          edge.fromId === node.id && edge.status === 'candidate');
        const invocation = link ? nodesById.get(link.toId) : undefined;
        return { node, link, invocation };
      }), [report, nodesById]);

  const visibleCalleeEdges = useMemo(
    () => calleeEdges.filter(edge =>
      calleeContextFilter === 'all' ||
      edge.projectContextStatus === calleeContextFilter),
    [calleeEdges, calleeContextFilter]
  );

  // A one-hop *reverse* projection of already supplied graph evidence.
  // Never infer a caller by a matching method name, and never recursively
  // traverse candidate-callee links into a verified impact path.
  const methodTargetIds = useMemo(
    () => new Set(calleeEdges.map(edge => edge.toId)), [calleeEdges]
  );
  const impactMethods = useMemo(() =>
    (report?.nodes ?? []).filter(node =>
      (node.kind === 'syntax-declaration' &&
        node.detail?.startsWith('method syntax declaration')) ||
      methodTargetIds.has(node.id)
    ).sort((a, b) => {
      const candidateOrder =
        Number(methodTargetIds.has(b.id)) - Number(methodTargetIds.has(a.id));
      return candidateOrder !== 0 ? candidateOrder :
        a.label < b.label ? -1 : a.label > b.label ? 1 :
        a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
    }), [report, methodTargetIds]);
  const impactMethod = impactMethods.find(node => node.id === impactMethodId);
  const impactMethodMatches = impactMethods.filter(node =>
    (node.label + ' ' + (node.filePath ?? ''))
      .toLocaleLowerCase().includes(impactSearch.trim().toLocaleLowerCase()));
  const inboundCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const edge of calleeEdges)
      counts.set(edge.toId, (counts.get(edge.toId) ?? 0) + 1);
    return counts;
  }, [calleeEdges]);
  const sourceOwnerByInvocation = useMemo(() =>
    new Map((report?.edges ?? [])
      .filter(edge => edge.kind === 'contains-invocation' &&
        edge.status === 'observed-syntax')
      .map(edge => [edge.toId, edge] as const)), [report]);
  const directImpactCandidates = useMemo(() => calleeEdges
    .filter(edge => edge.toId === impactMethodId &&
      edge.status === 'provisional-local')
    .flatMap(edge => {
      const invocation = nodesById.get(edge.fromId);
      const observedContainment = sourceOwnerByInvocation.get(edge.fromId);
      const sourceOwner = observedContainment
        ? nodesById.get(observedContainment.fromId) : undefined;
      if (!invocation || !observedContainment || !sourceOwner) return [];
      return [{ edge, invocation, sourceOwner, observedContainment }];
    }), [calleeEdges, impactMethodId, nodesById, sourceOwnerByInvocation]);
  const directlyObservedCallerMethods = new Set(directImpactCandidates
    .filter(item => item.sourceOwner.kind === 'syntax-declaration' &&
      /^(method|constructor) syntax declaration/.test(item.sourceOwner.detail ?? ''))
    .map(item => item.sourceOwner.id)).size;
  const fileScopedImpactSites = directImpactCandidates.filter(
    item => item.sourceOwner.kind === 'csharp-file').length;

  const selectedNode = nodesById.get(selectedId);
  const allNeighbors = useMemo((): AdjacentEvidence[] => {
    if (!report || !selectedId) return [];
    return report.edges.flatMap(edge => {
      if (edge.fromId !== selectedId && edge.toId !== selectedId) return [];
      const outgoing = edge.fromId === selectedId;
      const other = nodesById.get(outgoing ? edge.toId : edge.fromId);
      return other ? [{ edge, other, outgoing }] : [];
    });
  }, [report, selectedId, nodesById]);

  const visibleNodes = useMemo(() => (report?.nodes ?? []).filter(node =>
    (kind === 'all' || node.kind === kind) &&
    (search.trim() === '' ||
      (node.label + ' ' + (node.filePath ?? '') + ' ' + node.kind)
        .toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()))),
  [report, kind, search]);

  const diagramNeighbors = Array.from(
    new Map(allNeighbors.map(item => [item.other.id, item])).values()
  ).slice(0, 8);
  const center = { x: 300, y: 170 };
  const positions = diagramNeighbors.map((item, index) => {
    const angle = (index / Math.max(1, diagramNeighbors.length)) * Math.PI * 2 - Math.PI / 2;
    return {
      ...item,
      x: center.x + Math.cos(angle) * 195,
      y: center.y + Math.sin(angle) * 108,
    };
  });

  return (
    <section className="snapshot-manifest syntax-view evidence-graph" aria-label="C# evidence graph explorer">
      <h4>C# evidence graph explorer</h4>
      {loading && <p role="status">Constructing the saved-snapshot evidence graph…</p>}
      {error && <p role="alert" className="error">{error}</p>}
      {report && (
        <>
          <p>{report.nodeCount} evidence nodes · {report.edgeCount} typed edges ·
            {' '}{report.syntaxDeclarationCount} syntax declarations ·
            {' '}{report.invocationCount} invocation sites
            {' '}({report.methodContainedInvocationCount} in direct method/constructor bodies) ·
            {' '}{report.provisionalCalleeCount} provisional callee candidates ·
            {' '}{report.unresolvedCalleeCount} unresolved callees ·
            {' '}{report.httpAttributeCandidateCount} HTTP attribute candidates ·
            {' '}{report.minimalApiCandidateCount} Minimal API mapping candidates
          </p>
          <p className="workspace-note">
            Callee project context: {report.calleeSharedCandidateCount} shared candidates ·
            {' '}{report.calleeDisjointCandidateCount} disjoint candidates ·
            {' '}{report.calleeUnknownProjectCount} unknown memberships.
            All candidate callees remain provisional-local.
          </p>
          <p className="workspace-note">
            {report.analyzerVersion}. Every project membership is a candidate;
            every type reference is provisional or unresolved. Parsed declaration nodes
            are observed source syntax, not proven compiler symbols. Invocation sites
            may have a synthetic source-only candidate target, but none are project-verified
            calls to named methods. Shared project candidates do not verify compilation,
            and disjoint candidates do not prove the call is impossible.
            This is not a verified dependency graph.
          </p>
          <section className="graph-http-entrypoints" aria-label="Candidate HTTP entry-point attributes">
            <h4>Find HTTP attribute entry-point candidates</h4>
            <p className="workspace-note">
              These are source-observed conventional HTTP verb attribute names,
              not verified ASP.NET symbols or registered HTTP endpoints.
              Route strings are shown separately, not composed into effective URLs.
            </p>
            {httpAttributeRows.length === 0 ? (
              <p role="status">
                No conventional method-level HTTP verb attributes were identified
                in this saved snapshot. Minimal API mapping spellings are inventoried
                separately below; convention routes and external endpoints remain
                unexamined. This does not prove that the application has no HTTP endpoints.
              </p>
            ) : (
              <>
                <p>
                  {report.httpAttributeCandidateCount} candidate annotations ·
                  {' '}{report.nonliteralHttpAttributeRouteCount} with nonliteral or
                  unsupported HTTP attribute route arguments.
                </p>
                <ul className="syntax-declaration-list">
                  {httpAttributeRows.slice(0, 30).map(({ node, link, method }) => (
                    <li key={node.id}>
                      <strong>{node.label}</strong>
                      {node.filePath && node.fileSha256 && (
                        <EvidenceAddress
                          path={node.filePath}
                          hash={node.fileSha256}
                          line={node.line}
                          column={node.column}
                        />
                      )}
                      <span>
                        {link?.kind ?? 'Source-only candidate'} ·
                        {' '}<strong>{link?.status ?? 'unresolved'}</strong>
                        {method ? ' · method: ' + method.label : ' · method unavailable'}
                      </span>
                      <p>{node.detail}</p>
                      <button
                        type="button"
                        onClick={() => {
                          setSelectedId(node.id);
                          setKind('http-endpoint-attribute');
                          setSearch('');
                        }}
                      >
                        Inspect annotation and method in graph
                      </button>
                      {method && (
                        <a href="#direct-impact" onClick={() => {
                          setImpactMethodId(method.id);
                          setImpactSearch('');
                        }}>
                          Review direct impact candidates for {method.label}
                        </a>
                      )}
                    </li>
                  ))}
                </ul>
                {httpAttributeRows.length > 30 && (
                  <p className="workspace-note">
                    Showing 30 of {httpAttributeRows.length} annotations.
                    All candidates and their evidence are available in the
                    authenticated local graph API.
                  </p>
                )}
              </>
            )}
          </section>
          <section className="graph-http-entrypoints" aria-label="Candidate Minimal API mapping invocations">
            <h4>Find Minimal API mapping candidates</h4>
            <p className="workspace-note">
              This inventory recognizes written MapGet, MapPost, MapPut, MapPatch
              and MapDelete member calls. Such names can also belong to unrelated
              methods; these are candidates, never verified route registrations.
              Displayed route literals have not been composed into effective URLs.
            </p>
            {minimalApiRows.length === 0 ? (
              <p role="status">
                No supported Minimal API mapping spellings were identified in
                this saved snapshot. This does not prove the absence of HTTP
                endpoints or route registrations; other mapping styles are
                outside the inspector's current scope.
              </p>
            ) : (
              <>
                <p>
                  {report.minimalApiCandidateCount} mapping syntax candidates ·
                  {' '}{report.nonliteralMinimalApiRouteCount} with missing,
                  dynamic or unsupported route arguments.
                </p>
                <ul className="syntax-declaration-list">
                  {minimalApiRows.slice(0, 40).map(({ node, link, invocation }) => (
                    <li key={node.id}>
                      <strong>{node.label}</strong>
                      {node.filePath && node.fileSha256 && (
                        <EvidenceAddress
                          path={node.filePath}
                          hash={node.fileSha256}
                          line={node.line}
                          column={node.column}
                        />
                      )}
                      <span>
                        {link?.kind ?? 'Source-only candidate'} ·
                        {' '}<strong>{link?.status ?? 'unresolved'}</strong>
                        {' '}· {invocation
                          ? 'observed invocation: ' + invocation.label
                          : 'invocation evidence unavailable'}
                      </span>
                      <p>{node.detail}</p>
                      <button
                        type="button"
                        onClick={() => {
                          setSelectedId(node.id);
                          setKind('http-minimal-mapping');
                          setSearch('');
                        }}
                      >
                        Inspect mapping and source invocation in graph
                      </button>
                    </li>
                  ))}
                </ul>
                {minimalApiRows.length > 40 && (
                  <p className="workspace-note">
                    Showing the first 40 of {minimalApiRows.length} candidates.
                    Full evidence is available in the authenticated local graph API.
                  </p>
                )}
              </>
            )}
          </section>
          <section className="graph-callee-finder" aria-label="Provisional callee project evidence">
            <h4>Find callee project evidence</h4>
            <p className="workspace-note">
              Select a candidate to see its source and target methods and the static
              project files behind the classification. These are synthetic source-only
              hypotheses, never verified compiled calls.
            </p>
            {calleeEdges.length === 0 ? (
              <p role="status">
                No provisional callee links were identified in this saved snapshot.
                There are no callee project classifications to inspect here.
                This can happen when none of the written calls bind uniquely to
                captured C# method declarations; it does not mean there are no
                calls or dependencies in the repository.
              </p>
            ) : (
              <>
                <label>
                  Filter callee project context
                  <select
                    value={calleeContextFilter}
                    onChange={event => setCalleeContextFilter(event.target.value)}
                  >
                    <option value="all">All candidate callee links ({calleeEdges.length})</option>
                    <option value="shared-project-candidate">
                      Shared project candidates ({report.calleeSharedCandidateCount})
                    </option>
                    <option value="disjoint-project-candidates">
                      Disjoint project candidates ({report.calleeDisjointCandidateCount})
                    </option>
                    <option value="unknown-project-candidate">
                      Unknown project candidates ({report.calleeUnknownProjectCount})
                    </option>
                  </select>
                </label>
                {visibleCalleeEdges.length === 0 ? (
                  <p role="status">
                    No callee links in this snapshot have the selected project-context
                    classification. Choose another classification to inspect available evidence.
                  </p>
                ) : (
                  <ul className="syntax-declaration-list">
                    {visibleCalleeEdges.slice(0, 25).map(edge => {
                      const source = nodesById.get(edge.fromId)!;
                      const target = nodesById.get(edge.toId)!;
                      return (
                        <li key={edge.id}>
                          <button
                            type="button"
                            onClick={() => {
                              setSelectedId(edge.fromId);
                              setKind('invocation-site');
                              setSearch('');
                            }}
                          >
                            Inspect in graph: {source.label} → {target.label}
                          </button>
                          <a
                            href="#direct-impact"
                            onClick={() => {
                              setImpactMethodId(edge.toId);
                              setImpactSearch('');
                            }}
                          >
                            Review direct impact candidates for {target.label}
                          </a>
                          <span>
                            {edge.kind} · <strong>{edge.status}</strong>
                            {' '}· {edge.projectContextStatus}
                          </span>
                          <EvidenceAddress
                            path={edge.evidencePath}
                            hash={edge.evidenceSha256}
                            line={edge.line}
                            column={edge.column}
                          />
                          <details>
                            <summary>Show source, target and shared project evidence</summary>
                            <p>{edge.explanation}</p>
                            <ProjectCandidateList
                              label="Source project candidates"
                              candidates={edge.sourceProjectCandidates}
                            />
                            <ProjectCandidateList
                              label="Target project candidates"
                              candidates={edge.targetProjectCandidates}
                            />
                            <ProjectCandidateList
                              label="Shared project candidates"
                              candidates={edge.sharedProjectCandidates}
                            />
                          </details>
                        </li>
                      );
                    })}
                  </ul>
                )}
                {visibleCalleeEdges.length > 25 && (
                  <p className="workspace-note">
                    Showing the first 25 matching candidate links. Select a narrower
                    project-context classification or inspect more links in the graph.
                  </p>
                )}
              </>
            )}
          </section>
          <section id="direct-impact" className="graph-impact-explorer" aria-label="Direct change-impact candidates">
            <h4>Explore direct change-impact candidates</h4>
            <p className="workspace-note">
              Choose a captured C# method and inspect written invocation sites
              that Roslyn provisionally linked to it. This is a one-hop
              review checklist, not a prediction of a broken method, verified
              call graph, or transitive impact analysis.
            </p>
            {impactMethods.length === 0 ? (
              <p role="status">
                No supported method declarations were found in this saved
                snapshot. Direct change-impact exploration is unavailable
                for this snapshot, not evidence that changes are safe.
              </p>
            ) : (
              <>
                <label>
                  Find a method to examine
                  <input
                    type="search"
                    value={impactSearch}
                    onChange={event => setImpactSearch(event.target.value)}
                    placeholder="Method name or captured source path"
                  />
                </label>
                <div className="graph-impact-method-picker" aria-label="Choose an impact method">
                  {impactMethodMatches.length === 0 ? (
                    <p>No methods match this search. Try a different name or path.</p>
                  ) : (
                    <ul>
                      {impactMethodMatches.slice(0, 25).map(method => (
                        <li key={method.id}>
                          <button
                            type="button"
                            aria-pressed={impactMethodId === method.id}
                            className={impactMethodId === method.id ? 'graph-selected' : ''}
                            onClick={() => setImpactMethodId(method.id)}
                          >
                            <span>{method.label}</span>
                            <small>
                              {method.filePath}:{method.line ?? '?'} ·
                              {' '}{inboundCounts.get(method.id) ?? 0} candidate invocation sites
                            </small>
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                  {impactMethodMatches.length > 25 && (
                    <p className="workspace-note">
                      Showing 25 of {impactMethodMatches.length} matching methods.
                      Refine your search to find a particular declaration or overload.
                    </p>
                  )}
                </div>
                {impactMethod && (
                  <div className="graph-impact-results">
                    <h4>Potential direct callers of {impactMethod.label}</h4>
                    {impactMethod.filePath && impactMethod.fileSha256 && (
                      <EvidenceAddress
                        path={impactMethod.filePath}
                        hash={impactMethod.fileSha256}
                        line={impactMethod.line}
                        column={impactMethod.column}
                      />
                    )}
                    <p>
                      {directImpactCandidates.length} provisional invocation sites ·
                      {' '}{directlyObservedCallerMethods} distinct directly enclosing
                      methods/constructors · {fileScopedImpactSites} file-scoped sites
                      without an identified caller.
                    </p>
                    {directImpactCandidates.length === 0 ? (
                      <p role="status">
                        No inbound provisional callee candidates were found for
                        this method in this saved snapshot. This does NOT establish
                        that it has no callers or that changing it is safe.
                      </p>
                    ) : (
                      <ul className="syntax-declaration-list">
                        {directImpactCandidates.slice(0, 40).map(item => (
                          <li key={item.edge.id}>
                            <strong>
                              {item.sourceOwner.kind === 'csharp-file'
                                ? 'File-scoped invocation; enclosing caller not identified'
                                : 'Observed containing declaration: ' + item.sourceOwner.label}
                            </strong>
                            {item.sourceOwner.filePath && item.sourceOwner.fileSha256 && (
                              <EvidenceAddress
                                path={item.sourceOwner.filePath}
                                hash={item.sourceOwner.fileSha256}
                                line={item.sourceOwner.line}
                                column={item.sourceOwner.column}
                              />
                            )}
                            <span>
                              Written invocation: {item.invocation.label} ·
                              observed source containment (<strong>observed-syntax</strong>)
                            </span>
                            <EvidenceAddress
                              path={item.edge.evidencePath}
                              hash={item.edge.evidenceSha256}
                              line={item.edge.line}
                              column={item.edge.column}
                            />
                            <span>
                              Callee hypothesis: <strong>{item.edge.status}</strong>
                              {' '}· {item.edge.projectContextStatus}
                            </span>
                            <button
                              type="button"
                              onClick={() => {
                                setSelectedId(item.invocation.id);
                                setKind('invocation-site');
                                setSearch('');
                              }}
                            >
                              Inspect invocation and candidate in graph
                            </button>
                            <details>
                              <summary>See candidate reasoning and project evidence</summary>
                              <p>{item.edge.explanation}</p>
                              <ProjectCandidateList
                                label="Source project candidates"
                                candidates={item.edge.sourceProjectCandidates}
                              />
                              <ProjectCandidateList
                                label="Target project candidates"
                                candidates={item.edge.targetProjectCandidates}
                              />
                              <ProjectCandidateList
                                label="Shared project candidates"
                                candidates={item.edge.sharedProjectCandidates}
                              />
                            </details>
                          </li>
                        ))}
                      </ul>
                    )}
                    {directImpactCandidates.length > 40 && (
                      <p className="workspace-note">
                        Showing the first 40 of {directImpactCandidates.length}
                        provisional call-site candidates. The full list is
                        available from the authenticated local evidence graph API.
                      </p>
                    )}
                    <p className="workspace-note">
                      The snapshot also contains {report.unresolvedCalleeCount}
                      unresolved invocation sites that cannot be assigned to this
                      or any other target by the current binder. They are not
                      counted above. No transitive callers, external consumers,
                      test effects or actual breakages are inferred.
                    </p>
                  </div>
                )}
              </>
            )}
          </section>
          <div className="graph-controls">
            <label>
              Find graph nodes
              <input
                value={search}
                onChange={event => setSearch(event.target.value)}
                placeholder="Search project, file, or declaration"
              />
            </label>
            <label>
              Node kind
              <select value={kind} onChange={event => setKind(event.target.value)}>
                <option value="all">All node kinds</option>
                <option value="project-file">Project files</option>
                <option value="csharp-file">C# source files</option>
                <option value="target-declaration">Referenced declarations</option>
                <option value="syntax-declaration">Parsed declarations (unreferenced)</option>
                <option value="invocation-site">Invocation sites (candidate or unresolved)</option>
                <option value="http-endpoint-attribute">HTTP attribute candidates (not registered endpoints)</option>
                <option value="http-minimal-mapping">Minimal API mapping candidates (not registered endpoints)</option>
                <option value="unresolved-use">Unresolved occurrences</option>
              </select>
            </label>
          </div>
          <div className="graph-explorer-layout">
            <div className="graph-node-picker" aria-label="Select a graph node">
              <strong>Nodes ({visibleNodes.length})</strong>
              {visibleNodes.length === 0 && <p>No matching nodes.</p>}
              {visibleNodes.slice(0, 150).map(node => (
                <button
                  type="button"
                  key={node.id}
                  className={node.id === selectedId ? 'graph-selected' : ''}
                  aria-pressed={node.id === selectedId}
                  onClick={() => setSelectedId(node.id)}
                >
                  <span>{node.label}</span>
                  <small>{node.kind}</small>
                </button>
              ))}
              {visibleNodes.length > 150 && (
                <p className="workspace-note">
                  Showing 150 matching nodes; narrow your search to find others.
                </p>
              )}
            </div>
            <div className="graph-detail">
              {!selectedNode && <p>Select a graph node to inspect its relationships.</p>}
              {selectedNode && (
                <>
                  <h4>{selectedNode.label}</h4>
                  <p className="workspace-note">
                    {selectedNode.kind} · {selectedNode.detail ?? 'Captured evidence'}
                  </p>
                  {selectedNode.filePath && selectedNode.fileSha256 && (
                    <EvidenceAddress
                      path={selectedNode.filePath}
                      hash={selectedNode.fileSha256}
                      line={selectedNode.line}
                      column={selectedNode.column}
                    />
                  )}
                  {allNeighbors.length > 0 && (
                    <svg
                      className="graph-neighborhood"
                      viewBox="0 0 600 340"
                      role="img"
                      aria-label="Diagram of selected evidence node and up to eight connected neighbors"
                    >
                      {positions.map(item => (
                        <line
                          key={item.edge.id}
                          x1={item.outgoing ? center.x : item.x}
                          y1={item.outgoing ? center.y : item.y}
                          x2={item.outgoing ? item.x : center.x}
                          y2={item.outgoing ? item.y : center.y}
                          stroke={item.edge.status === 'observed-location' ? '#7aa29c' : '#c5a36c'}
                          strokeWidth="2"
                          strokeDasharray={item.edge.status === 'observed-location' ? undefined : '5 5'}
                        />
                      ))}
                      {positions.map(item => (
                        <g key={item.other.id}>
                          <circle cx={item.x} cy={item.y} r="23" fill="#324656" stroke="#9fb3c1" strokeWidth="1.5" />
                          <text x={item.x} y={item.y + 5} textAnchor="middle" fontSize="12" fill="#fff">
                            {item.other.kind === 'project-file' ? 'P' :
                              item.other.kind === 'csharp-file' ? 'F' :
                              item.other.kind === 'target-declaration' ? 'T' :
                              item.other.kind === 'syntax-declaration' ? 'D' :
                              item.other.kind === 'invocation-site' ? 'I' : '?'}
                          </text>
                          <text x={item.x} y={item.y + (item.y < center.y ? -32 : 39)}
                            textAnchor="middle" fontSize="10" fill="currentColor">
                            {item.other.label.length > 19
                              ? item.other.label.slice(0, 18) + '…'
                              : item.other.label}
                          </text>
                        </g>
                      ))}
                      <circle cx={center.x} cy={center.y} r="32" fill="#2e6575" stroke="#a0c3c9" strokeWidth="2" />
                      <text x={center.x} y={center.y + 5} textAnchor="middle" fontSize="15" fill="#fff">
                        {selectedNode.kind === 'project-file' ? 'P' :
                          selectedNode.kind === 'csharp-file' ? 'F' :
                          selectedNode.kind === 'target-declaration' ? 'T' :
                          selectedNode.kind === 'syntax-declaration' ? 'D' :
                          selectedNode.kind === 'invocation-site' ? 'I' : '?'}
                      </text>
                    </svg>
                  )}
                  {allNeighbors.length === 0 && (
                    <p>No observed relationships connect to this node in the current snapshot.</p>
                  )}
                  <h4>Connected evidence ({allNeighbors.length})</h4>
                  <ul className="syntax-declaration-list">
                    {allNeighbors.slice(0, 100).map(item => (
                      <li key={item.edge.id}>
                        <button type="button" onClick={() => setSelectedId(item.other.id)}>
                          {item.outgoing ? '→ ' : '← '}
                          <Short value={item.other.label} length={55} />
                        </button>
                        <span>
                          {item.edge.kind} · <strong>{item.edge.status}</strong>
                          {item.edge.projectContextStatus
                            ? ' · ' + item.edge.projectContextStatus : ''}
                        </span>
                        <EvidenceAddress
                          path={item.edge.evidencePath}
                          hash={item.edge.evidenceSha256}
                          line={item.edge.line}
                          column={item.edge.column}
                        />
                        <span>{item.edge.explanation}</span>
                        {item.edge.kind === 'candidate-callee' && (
                          <div className="graph-project-evidence">
                            <ProjectCandidateList
                              label="Source project candidates"
                              candidates={item.edge.sourceProjectCandidates}
                            />
                            <ProjectCandidateList
                              label="Target project candidates"
                              candidates={item.edge.targetProjectCandidates}
                            />
                            <ProjectCandidateList
                              label="Shared project candidates"
                              candidates={item.edge.sharedProjectCandidates}
                            />
                          </div>
                        )}
                      </li>
                    ))}
                  </ul>
                  {allNeighbors.length > 100 && (
                    <p className="workspace-note">
                      Showing the first 100 direct edges; full graph is available from the local API.
                    </p>
                  )}
                </>
              )}
            </div>
          </div>
          <details>
            <summary>Graph scope and limitations</summary>
            <ul>{report.limitations.map(item => <li key={item}>{item}</li>)}</ul>
          </details>
        </>
      )}
    </section>
  );
}
