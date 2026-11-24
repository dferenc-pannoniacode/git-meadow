namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    private static async Task<string> TrackLfsAsync(Repository repo, string? value)
    {
        var pattern = Required(value, "Enter a file pattern to track with Git LFS.");
        if (pattern.Length > 1024 || pattern.StartsWith('!') || pattern.StartsWith('"') || pattern.Any(char.IsControl))
            throw new ArgumentException("Enter a valid Git attributes pattern.", nameof(value));

        var attributes = ValidateFilePath(repo, ".gitattributes", allowFinalLink: false);
        if (Directory.Exists(Path.Combine(repo.Path, attributes)))
            throw new InvalidOperationException("The repository's .gitattributes path must be a file.");

        await EnsureLfsFiltersAsync(repo);
        var output = await RunLfsAsync(repo, ["lfs", "track", "--", pattern]);
        // Git LFS can report these errors with a successful exit code.
        if (Lines(output).Any(line => line is "Error reading '.gitattributes' file" or "Error opening '.gitattributes' file"))
            throw new InvalidOperationException("Git LFS could not edit .gitattributes. Check the checkout's permissions.");

        return output;
    }

    private static async Task EnsureLfsFiltersAsync(Repository repo)
    {
        if (!string.IsNullOrWhiteSpace(await ReadConfigValueAsync(repo, "filter.lfs.process")) ||
            !string.IsNullOrWhiteSpace(await ReadConfigValueAsync(repo, "filter.lfs.clean")) &&
            !string.IsNullOrWhiteSpace(await ReadConfigValueAsync(repo, "filter.lfs.smudge")))
            return;

        await RunLfsAsync(repo, ["lfs", "install", "--local", "--skip-repo"]);
    }

    private static async Task<string> RunLfsAsync(Repository repo, IReadOnlyList<string> arguments,
        bool localTransport = false)
    {
        var result = await TryRunAsync(repo.Path, arguments, localTransport: localTransport, literalPaths: false);
        if (!result.Success && (result.Error.Contains("expansion of alias 'lfs' failed; '' is not a git command", StringComparison.Ordinal) ||
                                result.Error.Contains("empty alias for lfs", StringComparison.Ordinal)))
            throw new InvalidOperationException("Git LFS is unavailable. Install Git LFS where Git Meadow runs.");

        return result.RequireSuccess();
    }
}
