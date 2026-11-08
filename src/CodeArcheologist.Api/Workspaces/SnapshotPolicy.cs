using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CodeArcheologist.Api.Workspaces;

public sealed record SnapshotFile(string Path, long SizeBytes, string Sha256);
public sealed record SnapshotExclusion(string Path, string Reason);

public sealed record SnapshotManifest(
    int FormatVersion,
    string PolicyVersion,
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string SourceKind,
    string SourceLocator,
    string RevisionKind,
    string FingerprintSha256,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<SnapshotFile> Files,
    IReadOnlyList<SnapshotExclusion> Exclusions);

public class SnapshotCaptureException(string message, int statusCode = 409)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class SnapshotSourceChangedException(string message)
    : SnapshotCaptureException(message);

internal sealed record InventoryFile(
    string FullPath,
    string RelativePath,
    long SizeBytes,
    long LastWriteTicks);

internal sealed record SnapshotInventory(
    IReadOnlyList<InventoryFile> Files,
    IReadOnlyList<SnapshotExclusion> Exclusions);

/// <summary>
/// Bounded and explicitly non-executing local-folder acquisition policy.
/// This is a controlled-user-account snapshot baseline, not an OS sandbox:
/// directory entries may still change between validation and opening.
/// </summary>
internal static class SnapshotPolicy
{
    public const string Version = "local-tree-v1";
    public const int MaxFiles = 5000;
    public const long MaxFileBytes = 8L * 1024 * 1024;
    public const long MaxTotalBytes = 128L * 1024 * 1024;
    public const int MaxDepth = 24;
    private const int MaxEntries = 25000;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build",
        "target", "vendor", "coverage", ".vs", ".idea", ".vscode", ".next",
        "__pycache__", ".venv", "venv", ".tox", ".cache", ".gradle",
        ".terraform", ".aws", ".ssh", ".azure", ".nuget", ".config", "secrets", ".env"
    };

    private static readonly HashSet<string> IgnoredFilenames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".git", ".svn", ".hg", ".npmrc", ".pypirc", ".netrc", "credentials.json", "secrets.json",
        "appsettings.local.json", "id_rsa", "id_ed25519", "known_hosts",
        "thumbs.db", ".ds_store"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".pdf", ".zip",
        ".7z", ".rar", ".tar", ".gz", ".exe", ".dll", ".pdb", ".so", ".dylib",
        ".class", ".jar", ".o", ".a", ".mp3", ".mp4", ".wav", ".woff", ".woff2",
        ".ttf", ".otf", ".sqlite", ".sqlite3", ".db", ".pfx", ".p12", ".pem",
        ".key", ".p8", ".snk", ".log", ".bak", ".bin", ".dat", ".pyc"
    };

    public static SnapshotInventory Inspect(string root)
    {
        var files = new List<InventoryFile>();
        var exclusions = new List<SnapshotExclusion>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            VerifyDirectoryAncestry(directory, root);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++visited > MaxEntries)
                    throw Limit("Too many filesystem entries. Reduce the registered scope.");

                var full = Path.GetFullPath(entry);
                if (!IsSameOrChildOf(full, root))
                    throw new SnapshotCaptureException("A source path escaped its registered root.");

                var relative = RelativePath(root, full);
                if (relative.Split('/').Any(part =>
                    string.IsNullOrEmpty(part) || part == "." || part == ".." ||
                    part.Any(char.IsControl)))
                    throw new SnapshotCaptureException("A source path contains unsafe characters.");

                var name = Path.GetFileName(full);
                // A literal backslash is a valid filename character on Unix,
                // but would be ambiguous with our portable '/' manifest separator.
                if (name.Contains('\\'))
                    throw new SnapshotCaptureException(
                        "A source filename contains an ambiguous path separator.");

                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    exclusions.Add(new SnapshotExclusion(relative, "symbolic-link-or-junction"));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IgnoredDirectories.Contains(name))
                    {
                        exclusions.Add(new SnapshotExclusion(relative, "ignored-directory"));
                        continue;
                    }

                    if (depth + 1 > MaxDepth)
                        throw Limit("Repository directory depth exceeds the snapshot limit.");

                    pending.Push((full, depth + 1));
                    continue;
                }

                if ((attributes & FileAttributes.Device) != 0)
                {
                    exclusions.Add(new SnapshotExclusion(relative, "non-regular-file"));
                    continue;
                }

                if (IgnoredFilenames.Contains(name) ||
                    name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("appsettings.local.", StringComparison.OrdinalIgnoreCase) ||
                    IgnoredExtensions.Contains(Path.GetExtension(name)))
                {
                    exclusions.Add(new SnapshotExclusion(relative, "excluded-sensitive-or-binary-file"));
                    continue;
                }

                var info = new FileInfo(full);
                if (info.LinkTarget is not null)
                {
                    exclusions.Add(new SnapshotExclusion(relative, "symbolic-link-or-junction"));
                    continue;
                }

                if (info.Length > MaxFileBytes)
                    throw Limit($"A source file exceeds the {MaxFileBytes} byte per-file limit.");

                totalBytes = checked(totalBytes + info.Length);
                if (totalBytes > MaxTotalBytes)
                    throw Limit("Included source bytes exceed the snapshot size limit.");

                files.Add(new InventoryFile(full, relative, info.Length, info.LastWriteTimeUtc.Ticks));
                if (files.Count > MaxFiles)
                    throw Limit("The snapshot file-count limit was exceeded.");
            }
        }

        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        exclusions.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new SnapshotInventory(files, exclusions);
    }

    public static void VerifyDirectoryAncestry(string directory, string root)
    {
        var current = Path.GetFullPath(directory);
        while (true)
        {
            if (!IsSameOrChildOf(current, root))
                throw new SnapshotCaptureException("An entry left its registered repository root.");

            var info = new DirectoryInfo(current);
            if (!info.Exists ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.LinkTarget is not null)
            {
                throw new SnapshotSourceChangedException(
                    "A repository directory changed or became a symbolic link during capture.");
            }

            if (string.Equals(current, root, PathComparison)) break;
            current = Path.GetDirectoryName(current) ??
                throw new SnapshotCaptureException("Invalid directory ancestry.");
        }
    }

    public static void VerifySourceFile(InventoryFile file, string root)
    {
        VerifyDirectoryAncestry(Path.GetDirectoryName(file.FullPath)!, root);
        var attributes = File.GetAttributes(file.FullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint |
            FileAttributes.Device)) != 0)
        {
            throw new SnapshotSourceChangedException(
                "A source file was replaced by a directory, link or device.");
        }

        var info = new FileInfo(file.FullPath);
        if (info.LinkTarget is not null ||
            info.Length != file.SizeBytes ||
            info.LastWriteTimeUtc.Ticks != file.LastWriteTicks)
        {
            throw new SnapshotSourceChangedException(
                "A source file changed during snapshot acquisition.");
        }
    }

    public static bool Equivalent(SnapshotInventory first, SnapshotInventory second) =>
        first.Files.SequenceEqual(second.Files) &&
        first.Exclusions.SequenceEqual(second.Exclusions);

    public static string Fingerprint(
        IReadOnlyList<SnapshotFile> files, IReadOnlyList<SnapshotExclusion> exclusions)
    {
        // Length-delimited records avoid ambiguous path/content concatenation.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendRecord(hash, "policy", Version);
        foreach (var file in files)
            AppendRecord(hash, "file", file.Path, file.SizeBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture), file.Sha256);
        foreach (var excluded in exclusions)
            AppendRecord(hash, "excluded", excluded.Path, excluded.Reason);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string RelativePath(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    public static bool IsSameOrChildOf(string path, string directory)
    {
        var comparison = PathComparison;
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(normalizedPath, normalizedDirectory, comparison) ||
            normalizedPath.StartsWith(
                normalizedDirectory + Path.DirectorySeparatorChar, comparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static SnapshotCaptureException Limit(string message) => new(message, 413);

    private static void AppendRecord(IncrementalHash hash, params string[] fields)
    {
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}
