import test from 'node:test';
import assert from 'node:assert/strict';
import { verifySnapshotCoverage } from '../src/live/snapshotCoverage.ts';

// A manifest is verified by the API, but the UI must additionally refuse a
// foreign/mismatched inventory instead of accidentally mixing snapshots.
const scope = { workspaceId: '11111111-1111-4111-8111-111111111111',
  repositoryId: '22222222-2222-4222-8222-222222222222',
  snapshotId: '33333333-3333-4333-8333-333333333333' };
const hash = 'ab'.repeat(32);
const file = { path: 'Sample.cs', sizeBytes: 20, sha256: 'cd'.repeat(32) };
const exclusion = { path: 'node_modules', reason: 'ignored-directory' };
const snapshot = { id: scope.snapshotId, workspaceId: scope.workspaceId,
  repositoryId: scope.repositoryId, fingerprintSha256: hash, manifestSha256: 'ef'.repeat(32),
  capturedAtUtc: '2026-11-11T12:00:00Z', fileCount: 1, totalBytes: 20,
  exclusionCount: 1, policyVersion: 'local-tree-v1' };
const manifest = { workspaceId: scope.workspaceId, repositoryId: scope.repositoryId,
  snapshotId: scope.snapshotId, fingerprintSha256: hash, formatVersion: 1,
  policyVersion: 'local-tree-v1', sourceKind: 'folder', sourceLocator: 'temp',
  revisionKind: 'local-tree', capturedAtUtc: snapshot.capturedAtUtc,
  files: [file], exclusions: [exclusion] };
const inventory = { workspaceId: scope.workspaceId, repositoryId: scope.repositoryId,
  snapshotId: scope.snapshotId, fingerprintSha256: hash, analyzerVersion: 'manifest-inventory-v1',
  detectionMethod: 'path-and-extension-only',
  includedFileCount: 1, includedBytes: 20, excludedEntryCount: 1,
  languages: [{ name: 'C#', fileCount: 1, totalBytes: 20 }],
  roles: [{ name: 'source-code', fileCount: 1, totalBytes: 20 }],
  files: [{ ...file, language: 'C#', role: 'source-code' }], exclusions: [exclusion],
  limitations: ['Classification by filename only.'] };

test('snapshot coverage accepts one matching verified scope, manifest and inventory', () => {
  const result = verifySnapshotCoverage(scope, snapshot, manifest, inventory);
  assert.equal(result.inventory.files[0].path, 'Sample.cs');
  assert.equal(result.snapshot.exclusionCount, 1);
});
test('snapshot coverage fails closed for foreign identities, hashes, policies and contradictory counts', () => {
  const bad = [
    () => verifySnapshotCoverage({ ...scope, snapshotId: 'other' }, snapshot, manifest, inventory),
    () => verifySnapshotCoverage(scope, snapshot, manifest, { ...inventory, fingerprintSha256: 'x' }),
    () => verifySnapshotCoverage(scope, snapshot, { ...manifest, policyVersion: 'different' }, inventory),
    () => verifySnapshotCoverage(scope, snapshot, manifest, { ...inventory, analyzerVersion: 'unsupported' }),
    () => verifySnapshotCoverage(scope, snapshot, manifest, { ...inventory, includedFileCount: 2 }),
    () => verifySnapshotCoverage(scope, snapshot, manifest, { ...inventory, includedBytes: 21 }),
    () => verifySnapshotCoverage(scope, snapshot, manifest, { ...inventory, files: [{ ...inventory.files[0], sha256: 'bad' }] }),
    () => verifySnapshotCoverage(scope, snapshot, manifest, { ...inventory, exclusions: [{ ...exclusion, reason: 'not-recorded' }] }),
    () => verifySnapshotCoverage(scope, snapshot, manifest, { ...inventory, roles: [] }),
    () => verifySnapshotCoverage(scope, snapshot, { ...manifest, files: [file, file] }, inventory),
  ];
  for (const reject of bad) assert.throws(reject);
});
