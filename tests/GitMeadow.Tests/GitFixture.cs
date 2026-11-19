using System.Diagnostics;
using System.Text;
using GitMeadow.Git;

namespace GitMeadow.Tests;

public sealed class GitFixture : IDisposable
{
    public const string Author = "Git Meadow Test";
    public const string Email = "test@example.invalid";

    private readonly string _scratch = Directory.CreateTempSubdirectory("git-meadow-test-").FullName;

    public GitFixture()
    {
        Root = Directory.CreateDirectory(Path.Combine(_scratch, "workspace")).FullName;
        Workspace = new GitWorkspace(Root);
    }

    public GitWorkspace Workspace { get; }

    public string Root { get; }

    public async Task<string> CreateAsync(string name = "repo")
    {
        var repo = await Workspace.CreateAsync(name);
        await ConfigureAsync(repo);
        return repo;
    }

    public async Task ConfigureAsync(string repo)
    {
        await GitAsync(repo, "config", "core.autocrlf", "false");
        await GitAsync(repo, "config", "commit.gpgsign", "false");
        await GitAsync(repo, "config", "merge.ff", "true");
        await GitAsync(repo, "config", "status.renames", "true");
        await GitAsync(repo, "config", "user.name", Author);
        await GitAsync(repo, "config", "user.email", Email);
    }

    public Task WriteAsync(string repo, string path, string contents)
    {
        var fullPath = Path.Combine(Root, repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return File.WriteAllTextAsync(fullPath, contents);
    }

    public async Task<string> CommitFileAsync(string repo, string path, string contents, string message)
    {
        await WriteAsync(repo, path, contents);
        await GitAsync(repo, "add", "--", path);
        await GitAsync(repo, "commit", "-m", message);
        return (await GitAsync(repo, "rev-parse", "HEAD")).Trim();
    }

    public async Task<string> GitAsync(string repo, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.Combine(Root, repo),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to launch Git.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Fixture Git timed out running {args[0]}.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture Git failed running {args[0]}: {await stderr}");
        }

        return await stdout;
    }

    public Task<string> WorkingFileAsync(string repo, string path) =>
        File.ReadAllTextAsync(Path.Combine(Root, repo, path));

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            // Git for Windows marks loose objects and packs read-only.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in Directory.EnumerateFiles(_scratch, "*", options))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(_scratch, recursive: true);
    }
}
