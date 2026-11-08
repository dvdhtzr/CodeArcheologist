using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeArcheologist.Api.Workspaces;

public sealed record SnapshotCaptureOutcome(SnapshotRecord Snapshot, bool Created);

/// <summary>
/// Explicit, read-only, bounded local capture. The API never runs Git,
/// MSBuild, project loaders, scripts, package managers or imported binaries.
/// </summary>
public sealed class SnapshotCaptureService(WorkspaceStore store)
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private static readonly JsonSerializerOptions ManifestJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<SnapshotCaptureOutcome> CaptureAsync(
        Guid workspaceId, Guid repositoryId, Guid requestKey, CancellationToken cancellationToken)
    {
        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            var source = store.GetRepository(workspaceId, repositoryId) ??
                throw new SnapshotCaptureException("The registered repository was not found.", 404);
            var requestDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "snapshot-request-v1\n" + workspaceId.ToString("D") +
                "\n" + repositoryId.ToString("D") +
                "\n" + SnapshotPolicy.Version))).ToLowerInvariant();

            var prior = store.ResolveSnapshotRequest(requestKey, requestDigest);
            if (prior.Conflict)
                throw new SnapshotCaptureException(
                    "This Idempotency-Key was used for another snapshot request.");
            if (prior.Existing is not null)
            {
                _ = ReadManifest(prior.Existing);
                return new SnapshotCaptureOutcome(prior.Existing, false);
            }

            var allowed = RepositoryPathPolicy.Validate(source.LocalPath, store.DatabasePath);
            if (!allowed.IsValid || allowed.Location?.PathKey !=
                (OperatingSystem.IsWindows()
                    ? source.LocalPath.ToUpperInvariant()
                    : source.LocalPath))
            {
                throw new SnapshotCaptureException(
                    "Registered source path is no longer safe or accessible.");
            }

            var sourceRoot = allowed.Location.LocalPath;
            var appDataRoot = Path.GetDirectoryName(store.DatabasePath)!;
            if (SnapshotPolicy.IsSameOrChildOf(appDataRoot, sourceRoot))
                throw new SnapshotCaptureException(
                    "The source contains Code Archeologist application data; refuse recursive capture.");

            // A source may legitimately change while being captured. Retry once
            // after an observed inventory/metadata mismatch, never store a partial result.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await CaptureAttemptAsync(source, sourceRoot,
                        requestKey, requestDigest, cancellationToken);
                }
                catch (SnapshotSourceChangedException) when (attempt == 0)
                {
                    // A fresh inventory and staging directory are used on retry.
                }
            }

            throw new SnapshotSourceChangedException(
                "The source changed during both capture attempts. Close active writers and retry.");
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public SnapshotManifest ReadManifest(SnapshotRecord record)
    {
        var manifestPath = Path.Combine(GetSnapshotRoot(), record.Id.ToString("D"), "manifest.json");
        if (!File.Exists(manifestPath))
            throw new SnapshotCaptureException("Snapshot manifest is missing from local storage.");

        var info = new FileInfo(manifestPath);
        if (info.Length > 4 * 1024 * 1024 ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SnapshotCaptureException("Snapshot manifest is invalid or linked.");
        }

        var bytes = File.ReadAllBytes(manifestPath);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, record.ManifestSha256, StringComparison.Ordinal))
            throw new SnapshotCaptureException("Snapshot manifest hash mismatch.");

        SnapshotManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SnapshotManifest>(bytes, ManifestJson);
        }
        catch (JsonException)
        {
            throw new SnapshotCaptureException("Saved snapshot manifest is not valid JSON.");
        }

        if (manifest is null ||
            manifest.SnapshotId != record.Id ||
            manifest.RepositoryId != record.RepositoryId ||
            manifest.WorkspaceId != record.WorkspaceId ||
            manifest.FingerprintSha256 != record.FingerprintSha256 ||
            manifest.PolicyVersion != record.PolicyVersion ||
            manifest.Files is null || manifest.Exclusions is null ||
            manifest.Files.Count != record.FileCount ||
            manifest.Exclusions.Count != record.ExclusionCount)
        {
            throw new SnapshotCaptureException("Snapshot manifest identity/count mismatch.");
        }

        var contentRoot = Path.Combine(GetSnapshotRoot(),
            record.Id.ToString("D"), "files");
        try
        {
            foreach (var file in manifest.Files)
            {
                var segments = file.Path.Split('/');
                if (segments.Any(segment => string.IsNullOrEmpty(segment) ||
                    segment is "." or ".." || segment.Any(char.IsControl)))
                {
                    throw new SnapshotCaptureException("Unsafe path in saved snapshot manifest.");
                }

                var location = Path.GetFullPath(Path.Combine(
                    contentRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!SnapshotPolicy.IsSameOrChildOf(location, contentRoot))
                    throw new SnapshotCaptureException("A saved artifact escaped snapshot storage.");

                SnapshotPolicy.VerifyDirectoryAncestry(
                    Path.GetDirectoryName(location)!, contentRoot);
                var info = new FileInfo(location);
                if (!info.Exists ||
                    (info.Attributes & (FileAttributes.ReparsePoint |
                        FileAttributes.Directory | FileAttributes.Device)) != 0 ||
                    info.LinkTarget is not null ||
                    info.Length != file.SizeBytes)
                {
                    throw new SnapshotCaptureException(
                        "A saved snapshot artifact was changed or replaced.");
                }

                using var stream = new FileStream(location, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream))
                    .ToLowerInvariant();
                if (!string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
                    throw new SnapshotCaptureException("Saved snapshot artifact hash mismatch.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SnapshotCaptureException(
                "Saved snapshot artifacts cannot be verified.");
        }

        return manifest;
    }

    private async Task<SnapshotCaptureOutcome> CaptureAttemptAsync(
        RepositorySourceRecord source, string sourceRoot,
        Guid requestKey, string requestDigest, CancellationToken cancellationToken)
    {
        var before = SnapshotPolicy.Inspect(sourceRoot);
        var id = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow;
        var root = GetSnapshotRoot();
        var stagingRoot = Path.Combine(root, ".staging");
        Directory.CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var movedToFinal = false;

        try
        {
            var copied = new List<SnapshotFile>(before.Files.Count);
            long totalBytes = 0;
            foreach (var entry in before.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeStorage = entry.RelativePath.Replace(
                    '/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(
                    Path.Combine(staging, "files", relativeStorage));
                if (!SnapshotPolicy.IsSameOrChildOf(destination,
                        Path.Combine(staging, "files")))
                    throw new SnapshotCaptureException("A source filename escaped staging storage.");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var digest = await CopyAndHashAsync(entry, sourceRoot, destination,
                    cancellationToken);
                copied.Add(new SnapshotFile(entry.RelativePath, entry.SizeBytes, digest));
                totalBytes += entry.SizeBytes;
            }

            var after = SnapshotPolicy.Inspect(sourceRoot);
            if (!SnapshotPolicy.Equivalent(before, after))
            {
                throw new SnapshotSourceChangedException(
                    "Source inventory changed during snapshot acquisition.");
            }

            var fingerprint = SnapshotPolicy.Fingerprint(copied, before.Exclusions);
            var existing = store.FindSnapshotByFingerprint(source.Id, fingerprint);
            if (existing is not null)
            {
                _ = ReadManifest(existing);
                store.SaveSnapshot(existing, requestKey, requestDigest, isNew: false);
                return new SnapshotCaptureOutcome(existing, false);
            }

            var manifest = new SnapshotManifest(
                FormatVersion: 1,
                PolicyVersion: SnapshotPolicy.Version,
                WorkspaceId: source.WorkspaceId,
                RepositoryId: source.Id,
                SnapshotId: id,
                SourceKind: source.SourceKind,
                SourceLocator: source.LocalPath,
                RevisionKind: "filesystem-content-manifest-not-git-commit",
                FingerprintSha256: fingerprint,
                CapturedAtUtc: capturedAt,
                Files: copied,
                Exclusions: before.Exclusions);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson);
            var manifestSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"),
                bytes, cancellationToken);

            // Staging and completed snapshots are on the same application volume.
            // The rename publishes a fully written directory before its SQLite
            // identity is committed; a crash in-between may leave an unreferenced
            // folder for later maintenance, never a partial trusted record.
            var finalPath = Path.Combine(root, id.ToString("D"));
            Directory.Move(staging, finalPath);
            movedToFinal = true;

            var record = new SnapshotRecord(
                id, source.WorkspaceId, source.Id, fingerprint, manifestSha,
                capturedAt, copied.Count, totalBytes, before.Exclusions.Count,
                SnapshotPolicy.Version);
            store.SaveSnapshot(record, requestKey, requestDigest, isNew: true);
            return new SnapshotCaptureOutcome(record, true);
        }
        finally
        {
            if (!movedToFinal && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task<string> CopyAndHashAsync(InventoryFile entry,
        string sourceRoot, string destination, CancellationToken cancellationToken)
    {
        SnapshotPolicy.VerifySourceFile(entry, sourceRoot);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(entry.FullPath, FileMode.Open,
            FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        if (input.Length != entry.SizeBytes)
            throw new SnapshotSourceChangedException("Source file size changed before reading.");

        await using (var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long copied = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                copied += count;
                if (copied > entry.SizeBytes || copied > SnapshotPolicy.MaxFileBytes)
                    throw new SnapshotSourceChangedException("A source file grew during capture.");

                hasher.AppendData(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            if (copied != entry.SizeBytes)
                throw new SnapshotSourceChangedException("Source file size changed during capture.");
            await output.FlushAsync(cancellationToken);
        }

        SnapshotPolicy.VerifySourceFile(entry, sourceRoot);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private string GetSnapshotRoot() =>
        Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "snapshots");
}
