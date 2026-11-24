using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GitMeadow.Git;
using Xunit;

namespace GitMeadow.Tests;

public sealed class GitLfsTests : IDisposable
{
    private const string Asset = "asset.bin";
    private const string Payload = "Git LFS payload\n";
    private const string PointerHeader = "version https://git-lfs.github.com/spec/v1\n";
    private const string Remote = "origin.git";

    private readonly GitFixture _git = new();
    private GitWorkspace Workspace => _git.Workspace;

    [RequiresGitLfs]
    public async Task Track_leaves_attributes_unstaged_and_matching_file_stages_as_an_lfs_pointer()
    {
        var source = await _git.CreateAsync();
        await _git.WriteAsync(source, Asset, Payload);

        await Workspace.ExecuteAsync(source, GitAction.TrackLfs, "*.bin");

        Assert.Equal("?? .gitattributes", (await _git.GitAsync(source, "status", "--porcelain=v1", "--", ".gitattributes")).Trim());
        Assert.Equal("asset.bin: filter: lfs", (await _git.GitAsync(source, "check-attr", "filter", "--", Asset)).Trim());

        await Workspace.ExecuteAsync(source, GitAction.StageAll);

        var bytes = Encoding.UTF8.GetBytes(Payload);
        var oid = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal($"{PointerHeader}oid sha256:{oid}\nsize {bytes.Length}\n", await _git.GitAsync(source, "show", $":{Asset}"));
        Assert.Equal(Payload, await _git.WorkingFileAsync(source, Asset));
    }

    [RequiresGitLfs]
    public async Task Push_uploads_a_selected_tag_and_pull_hydrates_its_checkout_without_moving_head()
    {
        const string release = "refs/tags/release";
        var (source, remotePath, baseTip) = await CreateSourceAndRemoteAsync();
        var assetTip = await CommitAssetAsync(source);
        await _git.GitAsync(source, "tag", "-a", "-m", "LFS release", "release");
        await Workspace.ExecuteAsync(source, GitAction.Switch, baseTip);
        var hook = Path.Combine(_git.Root, source, ".git", "test-hooks", "pre-push");
        await _git.WriteAsync(source, ".git/test-hooks/pre-push", "#!/bin/sh\necho repository-hook-ran >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await _git.GitAsync(source, "config", "--local", "core.hooksPath", Path.GetDirectoryName(hook)!);
        var directPush = await Assert.ThrowsAsync<InvalidOperationException>(() => _git.GitAsync(source, "push", "origin", release));
        Assert.Contains("repository-hook-ran", directPush.Message);

        await Workspace.ExecuteAsync(source, GitAction.Push, "origin", release);

        Assert.Equal(assetTip, (await _git.GitAsync(Remote, "rev-parse", $"{release}^{{commit}}")).Trim());
        Assert.Equal(baseTip, (await _git.GitAsync(Remote, "rev-parse", "refs/heads/main")).Trim());
        var clone = await Workspace.CloneAsync(remotePath, "clone");
        await _git.GitAsync(clone, "checkout", "--detach", release);
        Assert.StartsWith(PointerHeader, await _git.WorkingFileAsync(clone, Asset));

        await Workspace.ExecuteAsync(clone, GitAction.PullLfs);

        Assert.Equal(Payload, await _git.WorkingFileAsync(clone, Asset));
        Assert.Equal(assetTip, (await _git.GitAsync(clone, "rev-parse", "HEAD")).Trim());
    }

    [RequiresGitLfs]
    public async Task Push_sends_the_lfs_object_and_branch_to_each_configured_push_url()
    {
        const string secondRemote = "second.git";
        const string branch = "refs/heads/main";
        var (source, remotePath, _) = await CreateSourceAndRemoteAsync();
        var secondPath = Directory.CreateDirectory(Path.Combine(_git.Root, secondRemote)).FullName;
        await _git.GitAsync(secondRemote, "init", "--bare", "-b", "main");
        await _git.GitAsync(source, "remote", "set-url", "--add", "--push", "origin", remotePath);
        await _git.GitAsync(source, "remote", "set-url", "--add", "--push", "origin", secondPath);
        var assetTip = await CommitAssetAsync(source);

        await Workspace.ExecuteAsync(source, GitAction.Push, "origin", branch);

        foreach (var (remote, path, cloneName) in new[] { (Remote, remotePath, "first-clone"), (secondRemote, secondPath, "second-clone") })
        {
            Assert.Equal(assetTip, (await _git.GitAsync(remote, "rev-parse", branch)).Trim());
            var clone = await Workspace.CloneAsync(path, cloneName);
            await Workspace.ExecuteAsync(clone, GitAction.PullLfs, "origin");
            Assert.Equal(Payload, await _git.WorkingFileAsync(clone, Asset));
        }
    }

    [RequiresGitLfs]
    public async Task Push_with_a_missing_lfs_object_leaves_the_remote_branch_at_its_previous_tip()
    {
        var (source, _, baseTip) = await CreateSourceAndRemoteAsync();
        var assetTip = await CommitAssetAsync(source);
        var emptyStorage = Path.Combine(_git.Root, source, ".git", "empty-lfs");
        await _git.GitAsync(source, "config", "--local", "lfs.storage", emptyStorage);
        await _git.GitAsync(source, "config", "--local", "lfs.allowincompletepush", "true");

        var failure = await Assert.ThrowsAsync<GitCommandException>(() =>
            Workspace.ExecuteAsync(source, GitAction.Push, "origin", "refs/heads/main"));

        Assert.Contains("missing", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(baseTip, (await _git.GitAsync(Remote, "rev-parse", "refs/heads/main")).Trim());
        Assert.Equal(assetTip, (await _git.GitAsync(source, "rev-parse", "HEAD")).Trim());
    }

    private async Task<(string Source, string RemotePath, string BaseTip)> CreateSourceAndRemoteAsync()
    {
        var source = await _git.CreateAsync("source");
        var baseTip = await _git.CommitFileAsync(source, "base.txt", "base\n", "Base");
        var remotePath = Directory.CreateDirectory(Path.Combine(_git.Root, Remote)).FullName;
        await _git.GitAsync(Remote, "init", "--bare", "-b", "main");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "origin", remotePath);
        await Workspace.ExecuteAsync(source, GitAction.Push, "origin", "refs/heads/main");
        return (source, remotePath, baseTip);
    }

    private async Task<string> CommitAssetAsync(string source)
    {
        await Workspace.ExecuteAsync(source, GitAction.TrackLfs, "*.bin");
        await _git.WriteAsync(source, Asset, Payload);
        await Workspace.ExecuteAsync(source, GitAction.StageAll);
        await Workspace.ExecuteAsync(source, GitAction.Commit, "Add asset");
        return (await _git.GitAsync(source, "rev-parse", "HEAD")).Trim();
    }

    public void Dispose() => _git.Dispose();
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequiresGitLfsAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(ProbeGitLfs);

    public RequiresGitLfsAttribute() => Skip = SkipReason.Value;

    private static string? ProbeGitLfs()
    {
        var startInfo = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("lfs");
        startInfo.ArgumentList.Add("version");
        startInfo.Environment["LC_ALL"] = "C";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to launch Git to check for Git LFS.");
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Git did not finish checking for Git LFS.");
        }

        if (process.ExitCode == 0)
            return null;

        var error = process.StandardError.ReadToEnd();
        return error.Contains("'lfs' is not a git command", StringComparison.Ordinal)
            ? "The local LFS integration tests require Git LFS."
            : throw new InvalidOperationException($"Checking for Git LFS failed. {error.Trim()}");
    }
}
