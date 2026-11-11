/** Display-coordinate continuity only. Source graph IDs and edges are unchanged.
 * Align a newly computed Dagre layout to the previously visible selected center,
 * falling back to the first shared node. No coordinate is persisted as evidence. */
export type AtlasPosition = { x: number; y: number };
export function anchorAtlasLayout<T extends { id: string; position: AtlasPosition }>(
  next: readonly T[], previous: ReadonlyMap<string, AtlasPosition>, preferredId = '',
): T[] {
  const pivot = (preferredId && next.find(n => n.id === preferredId && previous.has(n.id))) ||
    next.find(n => previous.has(n.id));
  if (!pivot) return [...next];
  const last = previous.get(pivot.id)!;
  const dx = last.x - pivot.position.x;
  const dy = last.y - pivot.position.y;
  if (dx === 0 && dy === 0) return [...next];
  return next.map(node => ({ ...node, position: {
    x: node.position.x + dx, y: node.position.y + dy,
  } } as T));
}
