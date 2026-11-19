using GitMeadow.Git;
using Xunit;

namespace GitMeadow.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly GitFixture _git = new();
    private GitWorkspace Workspace => _git.Workspace;

    [Fact]
    public async Task Catalog_discovers_created_and_existing_working_repositories()
    {
        Assert.Empty(await Workspace.ListAsync());
        var created = await _git.CreateAsync("space folder");
        Directory.CreateDirectory(Path.Combine(_git.Root, "existing"));
        await _git.GitAsync("existing", "init", "-b", "main");
        Directory.CreateDirectory(Path.Combine(_git.Root, "ordinary-folder"));

        var listed = await Workspace.ListAsync();
        Assert.Equal(["existing", "space folder"], listed.Order(StringComparer.Ordinal));
        Assert.Equal("space folder", created);
        Assert.Equal("existing", await Workspace.OpenAsync("existing"));
        var failure = await Record.ExceptionAsync(() => Workspace.OpenAsync("ordinary-folder"));
        Assert.IsAssignableFrom<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task Workspace_rejects_existing_repository_outside_workspace()
    {
        const string outside = "../other";
        Directory.CreateDirectory(Path.Combine(_git.Root, outside));
        await _git.GitAsync(outside, "init", "-b", "main");
        var head = await File.ReadAllTextAsync(Path.Combine(_git.Root, outside, ".git", "HEAD"));

        var failure = await Record.ExceptionAsync(() => Workspace.OpenAsync(outside));
        Assert.True(failure is ArgumentException or InvalidOperationException, failure?.Message);

        var mutation = await Record.ExceptionAsync(() =>
            Workspace.ExecuteAsync(outside, GitAction.SetIdentity, "Denied identity", "denied@example.invalid"));
        Assert.True(mutation is ArgumentException or InvalidOperationException, mutation?.Message);
        Assert.Equal(head, await File.ReadAllTextAsync(Path.Combine(_git.Root, outside, ".git", "HEAD")));
        Assert.DoesNotContain("denied@example.invalid", await File.ReadAllTextAsync(Path.Combine(_git.Root, outside, ".git", "config")));
    }

    [Fact]
    public async Task Linked_worktree_is_accepted_when_git_metadata_stays_in_workspace()
    {
        var main = await _git.CreateAsync();
        var initial = await _git.CommitFileAsync(main, "main.txt", "shared base\n", "Base");
        var worktree = Path.Combine(_git.Root, "linked");
        await Workspace.ExecuteAsync(main, GitAction.Branch, "topic");
        await Workspace.ExecuteAsync(main, GitAction.AddWorktree, "linked", "topic");

        Assert.Equal("linked", await Workspace.OpenAsync("linked"));
        Assert.Equal("topic", (await Workspace.ReadAsync("linked")).Branch);
        Assert.Contains("branch refs/heads/topic", await Workspace.InspectAsync(main, GitView.Worktrees));
        await _git.WriteAsync("linked", "topic.txt", "worktree change\n");
        await Workspace.ExecuteAsync("linked", GitAction.Stage, "topic.txt");
        await Workspace.ExecuteAsync("linked", GitAction.Commit, "Linked commit");

        Assert.Equal(initial, (await _git.GitAsync(main, "rev-parse", "HEAD")).Trim());
        Assert.Equal("Linked commit", (await _git.GitAsync(main, "log", "-1", "--format=%s", "topic")).Trim());
        Assert.Equal("worktree change\n", await _git.WorkingFileAsync("linked", "topic.txt"));
        await Workspace.ExecuteAsync(main, GitAction.RemoveWorktree, "linked");
        Assert.False(Directory.Exists(worktree));
        Assert.DoesNotContain("branch refs/heads/topic", await Workspace.InspectAsync(main, GitView.Worktrees));
    }

    [Fact]
    public async Task Linked_worktree_with_external_metadata_is_rejected()
    {
        const string outside = "../other";
        Directory.CreateDirectory(Path.Combine(_git.Root, outside));
        await _git.GitAsync(outside, "init", "-b", "main");
        await _git.ConfigureAsync(outside);
        await _git.CommitFileAsync(outside, "base.txt", "base\n", "Base");
        await _git.GitAsync(outside, "worktree", "add", "-b", "topic", Path.Combine(_git.Root, "external-worktree"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Workspace.OpenAsync("external-worktree"));
        Assert.Equal("topic", (await _git.GitAsync("external-worktree", "branch", "--show-current")).Trim());
    }

    [Fact]
    public async Task Bare_remote_supports_clone_fetch_pull_and_rejects_non_fast_forward_push()
    {
        const string remoteName = "origin";
        const string bare = "origin.git";
        var source = await _git.CreateAsync("source");
        var first = await _git.CommitFileAsync(source, "base.txt", "base\n", "Initial revision");
        var remotePath = Directory.CreateDirectory(Path.Combine(_git.Root, bare)).FullName;
        await _git.GitAsync(bare, "init", "--bare", "-b", "main");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, remoteName, remotePath);
        await Workspace.ExecuteAsync(source, GitAction.Push, remoteName, "main");
        Assert.Contains((await Workspace.ReadAsync(source)).Remotes, remote => remote.Name == remoteName);

        var clone = await Workspace.CloneAsync(remotePath, "clone");
        await _git.ConfigureAsync(clone);
        Assert.Equal(first, Assert.Single((await Workspace.ReadAsync(clone)).Commits).Hash);
        Assert.Equal("base\n", (await _git.WorkingFileAsync(clone, "base.txt")).ReplaceLineEndings("\n"));

        var advanced = await _git.CommitFileAsync(source, "upstream.txt", "from upstream\n", "Advance origin");
        await Workspace.ExecuteAsync(source, GitAction.Push, remoteName, "main");
        await Workspace.ExecuteAsync(clone, GitAction.Fetch, remoteName);
        var fetched = await Workspace.ReadAsync(clone);
        Assert.Contains(fetched.Refs, reference => reference is { Name: "origin/main", Kind: "remote" } && reference.Hash == advanced);
        Assert.Equal(first, (await _git.GitAsync(clone, "rev-parse", "HEAD")).Trim());

        await Workspace.ExecuteAsync(clone, GitAction.Pull, remoteName, "main");
        Assert.Equal(advanced, (await _git.GitAsync(clone, "rev-parse", "HEAD")).Trim());
        Assert.Equal("from upstream\n", await _git.WorkingFileAsync(clone, "upstream.txt"));

        var serverTip = await _git.CommitFileAsync(source, "server.txt", "server only\n", "Server diverges");
        await Workspace.ExecuteAsync(source, GitAction.Push, remoteName, "main");
        var localTip = await _git.CommitFileAsync(clone, "local.txt", "local only\n", "Client diverges");

        await Assert.ThrowsAsync<GitCommandException>(() => Workspace.ExecuteAsync(clone, GitAction.Push, remoteName, "main"));
        Assert.Equal(serverTip, (await _git.GitAsync(bare, "rev-parse", "refs/heads/main")).Trim());
        Assert.Equal(localTip, (await _git.GitAsync(clone, "rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task Pushing_to_configured_fork_does_not_replace_pull_upstream()
    {
        var upstream = Directory.CreateDirectory(Path.Combine(_git.Root, "upstream.git")).FullName;
        var fork = Directory.CreateDirectory(Path.Combine(_git.Root, "fork.git")).FullName;
        await _git.GitAsync("upstream.git", "init", "--bare", "-b", "main");
        await _git.GitAsync("fork.git", "init", "--bare", "-b", "main");
        var source = await _git.CreateAsync("source");
        var original = await _git.CommitFileAsync(source, "base.txt", "base\n", "Base");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "origin", upstream);
        await Workspace.ExecuteAsync(source, GitAction.Push, "origin", "main");
        var clone = await Workspace.CloneAsync(upstream, "clone");
        await _git.ConfigureAsync(clone);
        await Workspace.ExecuteAsync(clone, GitAction.AddRemote, "fork", fork);
        await _git.GitAsync(clone, "config", "remote.pushDefault", "fork");
        var updated = await _git.CommitFileAsync(clone, "fork.txt", "fork update\n", "Work to publish");

        await Workspace.ExecuteAsync(clone, GitAction.Push);

        Assert.Equal(updated, (await _git.GitAsync("fork.git", "rev-parse", "refs/heads/main")).Trim());
        Assert.Equal(original, (await _git.GitAsync("upstream.git", "rev-parse", "refs/heads/main")).Trim());
        Assert.Equal("origin/main", (await _git.GitAsync(clone, "rev-parse", "--abbrev-ref", "@{upstream}")).Trim());
    }

    [Fact]
    public async Task Adding_submodule_stages_gitlink_and_preserves_submodule_revision()
    {
        const string submodulePath = "vendor/library";
        var library = await _git.CreateAsync("library");
        var libraryTip = await _git.CommitFileAsync(library, "item.txt", "library data\n", "Library revision");
        var libraryRemote = Directory.CreateDirectory(Path.Combine(_git.Root, "library.git")).FullName;
        await _git.GitAsync("library.git", "init", "--bare", "-b", "main");
        await Workspace.ExecuteAsync(library, GitAction.AddRemote, "origin", libraryRemote);
        await Workspace.ExecuteAsync(library, GitAction.Push, "origin", "main");
        var parent = await _git.CreateAsync("parent");
        await _git.CommitFileAsync(parent, "main.txt", "parent data\n", "Parent revision");

        await Workspace.ExecuteAsync(parent, GitAction.AddSubmodule, libraryRemote, submodulePath);

        var state = await Workspace.ReadAsync(parent);
        Assert.Contains(state.Changes, change => change is { Path: ".gitmodules", Index: 'A' });
        Assert.Contains(state.Changes, change => change is { Path: submodulePath, Index: 'A' });
        Assert.StartsWith($"160000 {libraryTip} ", await _git.GitAsync(parent, "ls-files", "-s", "--", submodulePath));
        var status = await Workspace.InspectAsync(parent, GitView.Submodules);
        Assert.Contains(libraryTip, status);
        Assert.Contains(submodulePath, status);
        Assert.Equal("library data\n",
            (await _git.WorkingFileAsync(parent, submodulePath + "/item.txt")).ReplaceLineEndings("\n"));
    }

    public void Dispose() => _git.Dispose();
}
