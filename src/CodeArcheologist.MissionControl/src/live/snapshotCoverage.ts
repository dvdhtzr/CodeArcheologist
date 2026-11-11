import type { Scope, Snapshot, SnapshotManifest, SourceInventory } from './api.ts';

export type SnapshotCoverage = { snapshot: Snapshot; manifest: SnapshotManifest; inventory: SourceInventory };

export function verifySnapshotCoverage(
  scope: Scope, snapshot: Snapshot, manifest: SnapshotManifest, inventory: SourceInventory,
): SnapshotCoverage {
  const matches = (data: { workspaceId: string; repositoryId: string; snapshotId?: string; id?: string; fingerprintSha256: string }) =>
    data.workspaceId === scope.workspaceId && data.repositoryId === scope.repositoryId &&
    (data.snapshotId ?? data.id) === scope.snapshotId &&
    data.fingerprintSha256 === snapshot.fingerprintSha256;
  if (!matches(snapshot) || !matches(manifest) || !matches(inventory))
    throw new Error('Snapshot coverage belongs to another source scope or fingerprint.');
  if (manifest.policyVersion !== snapshot.policyVersion ||
      inventory.analyzerVersion !== 'manifest-inventory-v1' ||
      inventory.detectionMethod !== 'path-and-extension-only')
    throw new Error('Snapshot coverage policy or filename classifier is incompatible.');
  if (!Array.isArray(manifest.files) || !Array.isArray(manifest.exclusions) ||
      !Array.isArray(inventory.files) || !Array.isArray(inventory.exclusions) ||
      !Array.isArray(inventory.languages) || !Array.isArray(inventory.roles) ||
      !Array.isArray(inventory.limitations))
    throw new Error('Incomplete snapshot coverage response.');
  const files = new Map(manifest.files.map(f => [f.path, f]));
  const classified = new Map(inventory.files.map(f => [f.path, f]));
  const totalBytes = manifest.files.reduce((sum, f) => sum + f.sizeBytes, 0);
  if (files.size !== manifest.files.length || classified.size !== inventory.files.length ||
      manifest.files.length !== snapshot.fileCount || inventory.files.length !== snapshot.fileCount ||
      manifest.exclusions.length !== snapshot.exclusionCount ||
      inventory.exclusions.length !== snapshot.exclusionCount ||
      inventory.includedFileCount !== snapshot.fileCount ||
      inventory.excludedEntryCount !== snapshot.exclusionCount ||
      inventory.includedBytes !== snapshot.totalBytes || totalBytes !== snapshot.totalBytes)
    throw new Error('Snapshot coverage file, exclusion or byte counts disagree.');
  if (inventory.files.some(f => {
    const saved = files.get(f.path);
    return !saved || f.sha256 !== saved.sha256 || f.sizeBytes !== saved.sizeBytes;
  })) throw new Error('Snapshot inventory does not agree with verified saved files.');
  const exclusions = new Map(manifest.exclusions.map(x => [x.path + '\u0000' + x.reason, x]));
  const inventoryExclusions = new Set(inventory.exclusions.map(e => e.path + '\u0000' + e.reason));
  if (exclusions.size !== manifest.exclusions.length ||
      inventoryExclusions.size !== inventory.exclusions.length ||
      inventory.exclusions.some(e => !exclusions.has(e.path + '\u0000' + e.reason)))
    throw new Error('Snapshot exclusions disagree.');
  if (inventory.languages.reduce((n, x) => n + x.fileCount, 0) !== snapshot.fileCount ||
      inventory.roles.reduce((n, x) => n + x.fileCount, 0) !== snapshot.fileCount)
    throw new Error('Snapshot inventory classifications do not cover the saved file count.');
  return { snapshot, manifest, inventory };
}
