namespace GitMeadow.Git;

public sealed partial class GitWorkspace(string root)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly SemaphoreSlim[] _repositoryGates = Enumerable.Range(0, 32)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public Task<IReadOnlyList<string>> ListAsync()
    {
        Directory.CreateDirectory(Root);
        List<string> repositories = [];
        if (HasGitMarker(Root))
            repositories.Add(".");

        foreach (var directory in Directory.EnumerateDirectories(Root).Order(PathComparer))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith('.') && !IsLink(directory) && HasGitMarker(directory))
                repositories.Add(name);
        }

        return Task.FromResult<IReadOnlyList<string>>(repositories);
    }

    public async Task<string> OpenAsync(string path)
    {
        var repository = await GetRepositoryAsync(path);
        return Path.GetRelativePath(Root, repository.Path);
    }

    public async Task<string> CreateAsync(string name)
    {
        var target = Destination(name);
        await _catalogGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Root);
            RequireNewDestination(target);
            await RunAsync(Root, ["init", "--initial-branch=main", "--template=", "--", target]);
            return Path.GetRelativePath(Root, target);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    public async Task<string> CloneAsync(string url, string name)
    {
        var target = Destination(name);
        await _catalogGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Root);
            RequireNewDestination(target);
            var remote = ValidateRemote(Root, url);
            await RunAsync(Root,
                ["clone", "--no-local", "--no-recurse-submodules", "--template=", "--", remote.Url, target],
                localTransport: remote.IsLocal);
            return Path.GetRelativePath(Root, target);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task<T> WithRepositoryAsync<T>(string name, Func<Repository, Task<T>> action)
    {
        var repository = await GetRepositoryAsync(name);
        var stripe = (uint)PathComparer.GetHashCode(repository.CommonDirectory) % (uint)_repositoryGates.Length;
        var gate = _repositoryGates[stripe];
        await gate.WaitAsync();
        try
        {
            return await action(repository);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Repository> GetRepositoryAsync(string name)
    {
        var path = GetWorkspacePath(name);
        if (!Directory.Exists(path) || !HasGitMarker(path))
            throw new InvalidOperationException("This folder is not a Git working tree.");

        var marker = Path.Combine(path, ".git");
        ValidateNoLinks(Root, marker);
        await ValidateMetadataPointerAsync(marker);
        var info = Lines(await RunAsync(path,
            ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir"]));
        if (info.Length != 3 || !PathComparer.Equals(path, NormalizePath(info[0])))
            throw new InvalidOperationException("Choose the root folder of a Git working tree.");

        var gitDirectory = NormalizePath(info[1]);
        var commonDirectory = NormalizePath(info[2]);
        ValidateNoLinks(Root, gitDirectory);
        ValidateNoLinks(Root, commonDirectory);
        return new Repository(path, gitDirectory, commonDirectory);
    }

    private async Task ValidateMetadataPointerAsync(string marker)
    {
        var directory = marker;
        if (File.Exists(marker))
        {
            var contents = await ReadMetadataPointerAsync(marker);
            const string prefix = "gitdir: ";
            if (!contents.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("The worktree has an invalid Git metadata pointer.");
            directory = Path.GetFullPath(contents[prefix.Length..], Path.GetDirectoryName(marker)!);
            ValidateNoLinks(Root, directory);
        }

        var commonDirectoryFile = Path.Combine(directory, "commondir");
        ValidateNoLinks(Root, commonDirectoryFile);
        if (File.Exists(commonDirectoryFile))
        {
            var commonDirectory = await ReadMetadataPointerAsync(commonDirectoryFile);
            ValidateNoLinks(Root, Path.GetFullPath(commonDirectory, directory));
        }
    }

    private static async Task<string> ReadMetadataPointerAsync(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024)
            throw new InvalidOperationException("The worktree has an invalid Git metadata pointer.");
        var contents = (await File.ReadAllTextAsync(path)).TrimEnd('\r', '\n');
        if (contents.Length == 0 || contents.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new InvalidOperationException("The worktree has an invalid Git metadata pointer.");
        return contents;
    }

    private string Destination(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim() != name ||
            name is "." or ".." || name.StartsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Enter a folder name without slashes or leading dots.", nameof(name));
        return GetWorkspacePath(name);
    }

    private string GetWorkspacePath(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if (Path.IsPathRooted(relative) || HasMetadataComponent(relative) ||
            OperatingSystem.IsWindows() && relative.Contains(':'))
            throw new ArgumentException("Choose a path relative to the workspace.", nameof(relative));

        var fullName = NormalizePath(Path.GetFullPath(relative, Root));
        ValidateNoLinks(Root, fullName);
        return fullName;
    }

    private static void RequireNewDestination(string target)
    {
        if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidOperationException("That destination already exists.");
    }

    private static string ValidateFilePath(Repository repository, string? path, bool allowFinalLink = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path) || HasMetadataComponent(path) ||
            OperatingSystem.IsWindows() && path.Contains(':') ||
            Components(path).Any(segment => segment is "." or ".."))
            throw new ArgumentException("Choose a file path inside the repository.", nameof(path));

        var absolute = Path.GetFullPath(path, repository.Path);
        ValidateNoLinks(repository.Path, allowFinalLink ? Path.GetDirectoryName(absolute)! : absolute);
        if (PathComparer.Equals(absolute, repository.Path))
            throw new ArgumentException("Choose a file path inside the repository.", nameof(path));

        return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
    }

    private static string[] Components(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static bool HasMetadataComponent(string path) => Components(path)
        .Any(segment => segment.TrimEnd(' ', '.').Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static void ValidateNoLinks(string root, string fullName)
    {
        var relative = Path.GetRelativePath(root, fullName);
        if (Path.IsPathRooted(relative) || Components(relative).FirstOrDefault() == "..")
            throw new InvalidOperationException("The path is outside the configured workspace.");

        var current = root;
        foreach (var segment in Components(relative).Where(segment => segment != "."))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
                throw new InvalidOperationException("The selected path traverses a symbolic link or junction.");
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool HasGitMarker(string path)
    {
        var marker = Path.Combine(path, ".git");
        return Directory.Exists(marker) || File.Exists(marker);
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static Task<GitProcess.Result> TryRunAsync(
        string directory, IReadOnlyList<string> arguments, string? input = null,
        bool localTransport = false, bool literalPaths = true) =>
        GitProcess.RunAsync(directory, arguments, input, localTransport, literalPaths);

    private static async Task<string> RunAsync(
        string directory, IReadOnlyList<string> arguments, string? input = null,
        bool localTransport = false, bool literalPaths = true) =>
        (await TryRunAsync(directory, arguments, input, localTransport, literalPaths)).RequireSuccess();

    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

    private static string[] NullFields(string output) => output.Split('\0');

    private static string Required(string? value, string prompt)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(prompt, nameof(value));
        return value;
    }

    private sealed record Repository(string Path, string GitDirectory, string CommonDirectory);
}
