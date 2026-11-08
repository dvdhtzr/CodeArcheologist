namespace CodeArcheologist.Api.Workspaces;

public sealed record RepositoryLocation(string LocalPath, string PathKey, string SourceKind);

public sealed record RepositoryPathResult(RepositoryLocation? Location, string? Error)
{
    public bool IsValid => Location is not null;
}

/// <summary>
/// Validates an explicitly supplied local directory for *registration only*.
/// No source file is opened, parsed, copied, built or executed here.
/// Every future snapshot acquisition MUST revalidate the path and enforce its
/// own symlink, traversal, size and file-change protections.
/// </summary>
public static class RepositoryPathPolicy
{
    public static RepositoryPathResult Validate(string? input, string databasePath)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048 ||
            input.Any(char.IsControl))
        {
            return Invalid("Enter a local directory path (up to 2048 characters).");
        }

        var raw = input.Trim();
        try
        {
            // Do not silently reinterpret relative paths, traverse '..',
            // resolve network shares, or accept Windows device paths.
            if (!Path.IsPathFullyQualified(raw) ||
                raw.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part is "." or ".."))
            {
                return Invalid("Enter an absolute directory path without relative segments.");
            }

            if (OperatingSystem.IsWindows() &&
                (raw.StartsWith(@"\\", StringComparison.Ordinal) ||
                 raw.StartsWith("//", StringComparison.Ordinal)))
            {
                return Invalid("Network shares and device paths are not supported.");
            }

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
            if (string.Equals(full, root, comparison))
            {
                return Invalid("Select a project folder, not a filesystem root.");
            }

            var dataDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetDirectoryName(databasePath)!));
            if (string.Equals(full, dataDirectory, comparison) ||
                full.StartsWith(dataDirectory + Path.DirectorySeparatorChar, comparison))
            {
                return Invalid("The application data directory cannot be registered as source.");
            }

            if (!Directory.Exists(full))
            {
                return Invalid("The selected local directory does not exist or is not accessible.");
            }

            // Explicitly reject aliases/junctions/symlinks at any ancestor:
            // a later phase must not accidentally treat them as trusted scope.
            for (DirectoryInfo? directory = new(full);
                directory is not null;
                directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    directory.LinkTarget is not null)
                {
                    return Invalid("Symbolic links and junctions are not supported for repository roots.");
                }
            }

            var gitMarker = Path.Combine(full, ".git");
            var sourceKind = Directory.Exists(gitMarker) || File.Exists(gitMarker)
                ? "git"
                : "folder";
            var pathKey = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
            return new RepositoryPathResult(
                new RepositoryLocation(full, pathKey, sourceKind), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return Invalid("The local directory path could not be validated.");
        }
    }

    private static RepositoryPathResult Invalid(string error) => new(null, error);
}
