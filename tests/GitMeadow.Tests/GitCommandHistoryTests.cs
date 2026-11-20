using GitMeadow.Git;
using Xunit;

namespace GitMeadow.Tests;

public sealed class GitCommandHistoryTests
{
    [Fact]
    public async Task CapturesGitOutputAndExitCodesAcrossParallelCommands()
    {
        using var fixture = new GitFixture();
        var repo = await fixture.CreateAsync();
        var directory = Path.Combine(fixture.Root, repo);
        await fixture.WriteAsync(repo, "review note.txt", "Untracked file");
        var history = new GitCommandHistory();

        await history.CaptureAsync(() => Task.WhenAll(
            GitProcess.RunAsync(directory, ["status", "--porcelain", "-z", "--", "review note.txt"]),
            GitProcess.RunAsync(directory, ["rev-parse", "--verify", "refs/heads/missing"])));

        Assert.Collection(history.Snapshot(),
            failed =>
            {
                Assert.Equal("git rev-parse --verify refs/heads/missing", failed.Command);
                Assert.True(failed.ExitCode is > 0);
                Assert.Contains("fatal:", failed.Error);
                Assert.Equal("", failed.Output);
            },
            status =>
            {
                Assert.Equal("git status --porcelain -z -- 'review note.txt'", status.Command);
                Assert.Equal(directory, status.Directory);
                Assert.Equal(0, status.ExitCode);
                Assert.Equal("?? review note.txt\\0", status.Output);
                Assert.Equal("", status.Error);
            });
    }

    [Fact]
    public async Task KeepsOverlappingSessionsAndUntrackedCommandsSeparate()
    {
        using var fixture = new GitFixture();
        var first = new GitCommandHistory();
        var second = new GitCommandHistory();

        await Task.WhenAll(
            first.CaptureAsync(async () => await fixture.Workspace.CreateAsync("first")),
            second.CaptureAsync(async () => await fixture.Workspace.CreateAsync("second")));
        await fixture.Workspace.CreateAsync("outside");

        Assert.Contains($"{Path.DirectorySeparatorChar}first", Assert.Single(first.Snapshot()).Command);
        Assert.Contains($"{Path.DirectorySeparatorChar}second", Assert.Single(second.Snapshot()).Command);
    }

    [Fact]
    public async Task HidesCredentialResponsesAndUrlUserInfoInHistory()
    {
        using var fixture = new GitFixture();
        var repo = await fixture.CreateAsync();
        var directory = Path.Combine(fixture.Root, repo);
        var history = new GitCommandHistory();
        const string secret = "test-credential-value";
        const string url = "https://test-user:test-credential-value@example.invalid/project.git?token=test-credential-value#test-credential-value";
        const string safeUrl = "https://example.invalid/project.git";

        await history.CaptureAsync(async () =>
        {
            (await GitProcess.RunAsync(directory, ["config", "--local", "--add", "--", "credential.helper", secret])).RequireSuccess();
            var helper = await GitProcess.RunAsync(directory, ["config", "--local", "--get", "--", "credential.helper"]);
            Assert.Equal(secret, helper.RequireSuccess().Trim());
            (await GitProcess.RunAsync(directory, ["remote", "add", "origin", url])).RequireSuccess();
            var lookup = await GitProcess.RunAsync(directory, ["remote", "get-url", "origin"]);
            Assert.Equal(url, lookup.RequireSuccess().Trim());
            var listing = await GitProcess.RunAsync(directory, ["remote", "-v"]);
            Assert.Contains(url, listing.RequireSuccess());
        });

        var entries = history.Snapshot();
        Assert.Equal(5, entries.Count);
        Assert.Contains($"origin\t{safeUrl} (fetch)", entries[0].Output);
        Assert.Equal(safeUrl, entries[1].Output.Trim());
        Assert.EndsWith(safeUrl, entries[2].Command);
        Assert.True(entries[3].OutputHidden);
        Assert.Empty(entries[3].Output);
        Assert.EndsWith("credential.helper [hidden]", entries[4].Command);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain(secret, entry.Command);
            Assert.DoesNotContain(secret, entry.Output);
            Assert.DoesNotContain(secret, entry.Error);
        });
    }

    [Fact]
    public void PreservesAtSignsOutsideUrlAuthorityInRegularOutput()
    {
        var history = new GitCommandHistory();
        var command = history.Start("workspace", ["show", "HEAD"]);
        const string publicUrl = "https://example.invalid?contact=mail@example.org#person@example.net";
        history.Complete(command, new GitProcess.Result(0,
            $"{publicUrl}\nhttps://user:password@example.invalid/project.git", ""));

        Assert.Equal($"{publicUrl}\nhttps://[hidden]@example.invalid/project.git", Assert.Single(history.Snapshot()).Output);
    }

    [Fact]
    public void BoundsHistoryAndDiscardsCompletionsAfterClear()
    {
        var history = new GitCommandHistory();
        var oldest = history.Start("workspace", ["status"]);
        for (var index = 0; index < GitCommandHistory.Capacity; index++)
            history.Start("workspace", ["log"]);

        history.Complete(oldest, new GitProcess.Result(0, "Evicted response", ""));
        var entries = history.Snapshot();
        Assert.Equal(GitCommandHistory.Capacity, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.Id == oldest);
        Assert.True(entries[0].Running);

        history.Complete(entries[0].Id, new GitProcess.Result(0, new string('x', 20_000), ""));
        var response = history.Snapshot()[0];
        Assert.Equal(0, response.ExitCode);
        Assert.EndsWith("\n[truncated]", response.Output);

        history.Clear();
        history.Complete(entries[1].Id, new GitProcess.Result(0, "Cleared response", ""));
        Assert.Empty(history.Snapshot());
    }
}
