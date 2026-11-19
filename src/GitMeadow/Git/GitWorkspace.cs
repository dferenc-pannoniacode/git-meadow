namespace GitMeadow.Git;

public sealed partial class GitWorkspace(string root)
{
    internal const string ExistingRepositoryMessage = "This folder already contains a Git repository or its metadata. Choose another folder.";
    internal const string CloneFolderNotEmptyMessage = "Choose an empty folder to clone into.";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly SemaphoreSlim[] _repositoryGates = Enumerable.Range(0, 32)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public Task<IReadOnlyList<string>> ListAsync()
    {
        ValidateNoLinks(Root, Root);
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

    internal RepositoryFolder BrowseRepositories(string relativePath)
    {
        var path = GetRepositoryFolderPath(relativePath);
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        var folders = Directory.EnumerateDirectories(path, "*", options)
            .Select(directory => Path.GetFileName(directory))
            .Where(name => !HasMetadataComponent(name))
            .Order(PathComparer)
            .ToArray();

        return new RepositoryFolder(Path.GetRelativePath(Root, path), HasGitMarker(path), folders,
            ContainsGitMetadata(path), IsEmptyDirectory(path));
    }

    public async Task<string> OpenAsync(string path)
    {
        var repository = await GetRepositoryAsync(path);
        return Path.GetRelativePath(Root, repository.Path);
    }

    public Task<string> CreateAsync(string name) =>
        UseDestinationAsync(() => GetNewRepositoryDestination(name), InitializeRepositoryAsync);

    public Task<string> CloneAsync(string url, string name) =>
        UseDestinationAsync(() => GetNewRepositoryDestination(name), target => CloneRepositoryAsync(url, target));

    internal Task<string> CreateInFolderAsync(string path) =>
        UseDestinationAsync(() => GetAvailableRepositoryFolder(path, requireEmpty: false), InitializeRepositoryAsync);

    internal Task<string> CloneInFolderAsync(string url, string path) =>
        UseDestinationAsync(() => GetAvailableRepositoryFolder(path, requireEmpty: true),
            target => CloneRepositoryAsync(Required(url, "Enter a remote URL."), target));

    internal Task<string> CreateFolderAsync(string parentPath, string name) =>
        UseDestinationAsync(() => GetNewSubfolderDestination(parentPath, name), target =>
        {
            Directory.CreateDirectory(target);
            return Task.CompletedTask;
        });

    private async Task<string> UseDestinationAsync(Func<string> resolveTarget, Func<string, Task> change)
    {
        await _catalogGate.WaitAsync();
        try
        {
            var target = resolveTarget();
            await change(target);
            return Path.GetRelativePath(Root, target);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private Task InitializeRepositoryAsync(string target) =>
        RunAsync(Root, ["init", "--initial-branch=main", "--template=", "--", target]);

    private Task CloneRepositoryAsync(string url, string target)
    {
        var remote = ValidateRemote(Root, url);
        return RunAsync(Root,
            ["clone", "--no-local", "--no-recurse-submodules", "--template=", "--", remote.Url, target],
            localTransport: remote.IsLocal);
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

    private string GetNewRepositoryDestination(string name)
    {
        if (!IsFolderName(name) || name.StartsWith('.'))
            throw new ArgumentException("Enter a folder name without slashes or leading dots.", nameof(name));

        var target = GetWorkspacePath(name);
        Directory.CreateDirectory(Root);
        RequireNewDestination(target);
        return target;
    }

    private string GetNewSubfolderDestination(string parentPath, string name)
    {
        if (!IsFolderName(name) || OperatingSystem.IsWindows() && name.EndsWith('.'))
            throw new ArgumentException("Enter a valid folder name without slashes.", nameof(name));

        var parent = GetRepositoryFolderPath(parentPath);
        var target = NormalizePath(Path.Combine(parent, name));
        ValidateNoLinks(Root, target);
        RequireNewDestination(target);
        return target;
    }

    private static bool IsFolderName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim() == name && name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') &&
        !HasMetadataComponent(name);

    private string GetAvailableRepositoryFolder(string relativePath, bool requireEmpty)
    {
        var path = GetRepositoryFolderPath(relativePath);
        if (ContainsGitMetadata(path))
            throw new RepositoryDestinationConflictException(ExistingRepositoryMessage);
        if (requireEmpty && !IsEmptyDirectory(path))
            throw new RepositoryDestinationConflictException(CloneFolderNotEmptyMessage);
        return path;
    }

    private string GetRepositoryFolderPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Components(relativePath).Contains(".."))
            throw new ArgumentException("Choose a folder inside the workspace.", nameof(relativePath));

        var path = GetWorkspacePath(relativePath);
        if (!Directory.Exists(path))
            throw new InvalidOperationException("This workspace folder is no longer available.");
        return path;
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

        if (IsLink(root))
            throw new InvalidOperationException("The selected path traverses a symbolic link or junction.");

        var current = root;
        foreach (var segment in Components(relative).Where(segment => segment != "."))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
                throw new InvalidOperationException("The selected path traverses a symbolic link or junction.");
        }
    }

    private static bool IsLink(string path) =>
        ReadPathAttributes(path) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0;

    private static FileAttributes? ReadPathAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool HasGitMarker(string path)
    {
        var marker = Path.Combine(path, ".git");
        return (Directory.Exists(marker) || File.Exists(marker)) && !IsLink(marker);
    }

    private static bool ContainsGitMetadata(string path) =>
        ReadPathAttributes(Path.Combine(path, ".git")) is not null ||
        ReadPathAttributes(Path.Combine(path, "HEAD")) is { } head && (head & FileAttributes.Directory) == 0 &&
        HasDirectoryEntry(path, "objects") && (HasDirectoryEntry(path, "refs") || HasDirectoryEntry(path, "reftable"));

    private static bool HasDirectoryEntry(string path, string name) =>
        ReadPathAttributes(Path.Combine(path, name)) is { } attributes &&
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0;

    private static bool IsEmptyDirectory(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

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
    internal sealed record RepositoryFolder(string Path, bool HasGitMarker, IReadOnlyList<string> Folders,
        bool ContainsRepository, bool IsEmpty);
    internal sealed class RepositoryDestinationConflictException(string message) : InvalidOperationException(message);
}
