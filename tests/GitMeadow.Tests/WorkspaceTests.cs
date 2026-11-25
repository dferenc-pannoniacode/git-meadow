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
    public async Task Repository_browser_lists_only_immediate_folders_and_opens_a_nested_checkout()
    {
        var nested = Path.Combine("Team projects", "Nested checkout");
        await _git.WriteAsync(nested, "docs/readme.txt", "Workspace fixture");
        await _git.GitAsync(nested, "init", "-b", "main");
        await _git.WriteAsync(".", "workspace.txt", "Workspace fixture");
        Directory.CreateDirectory(Path.Combine(_git.Root, "Archive"));

        var root = Workspace.BrowseRepositories(".");
        Assert.Equal(".", root.Path);
        Assert.False(root.HasGitMarker);
        Assert.Equal(["Archive", "Team projects"], root.Folders);

        var parent = Workspace.BrowseRepositories("Team projects");
        Assert.False(parent.HasGitMarker);
        Assert.Equal("Nested checkout", Assert.Single(parent.Folders));

        var checkout = Workspace.BrowseRepositories(Path.Combine(parent.Path, parent.Folders[0]));
        Assert.True(checkout.HasGitMarker);
        Assert.Equal("docs", Assert.Single(checkout.Folders));
        Assert.Equal(nested, await Workspace.OpenAsync(checkout.Path));

        var docs = Workspace.BrowseRepositories(Path.Combine(checkout.Path, "docs"));
        Assert.False(docs.HasGitMarker);
        Assert.Empty(docs.Folders);
    }

    [Fact]
    public async Task Repository_browser_can_open_workspace_root_but_cannot_browse_metadata_or_outside()
    {
        await _git.GitAsync(".", "init", "-b", "main");
        var sibling = Directory.CreateDirectory(Path.Combine(_git.Root, "..", "workspace-other")).FullName;

        var root = Workspace.BrowseRepositories(".");
        Assert.True(root.HasGitMarker);
        Assert.Empty(root.Folders);
        Assert.Equal(".", await Workspace.OpenAsync(root.Path));

        Assert.Throws<ArgumentException>(() => Workspace.BrowseRepositories(".git"));
        Assert.Throws<ArgumentException>(() => Workspace.BrowseRepositories(sibling));
        Assert.Throws<ArgumentException>(() => Workspace.BrowseRepositories(Path.GetRelativePath(_git.Root, sibling)));
        Assert.Throws<InvalidOperationException>(() => Workspace.BrowseRepositories("missing"));
    }

    [Fact]
    public async Task New_folder_can_be_selected_and_initialized_without_changing_existing_files()
    {
        const string parent = "Team projects";
        Directory.CreateDirectory(Path.Combine(_git.Root, parent));
        var created = await Workspace.CreateFolderAsync(parent, "New checkout");

        Assert.Equal(Path.Combine(parent, "New checkout"), created);
        Assert.Equal("New checkout", Assert.Single(Workspace.BrowseRepositories(parent).Folders));
        Assert.True(Workspace.BrowseRepositories(created).IsEmpty);
        await _git.WriteAsync(created, "notes.txt", "Keep these notes");
        var selected = Workspace.BrowseRepositories(created);
        Assert.False(selected.IsEmpty);
        Assert.False(selected.ContainsRepository);

        Assert.Equal(created, await Workspace.CreateInFolderAsync(selected.Path));
        Assert.Equal(created, await Workspace.OpenAsync(created));
        Assert.Equal("main", (await Workspace.ReadAsync(created)).Branch);
        Assert.True(Workspace.BrowseRepositories(created).ContainsRepository);
        Assert.Equal("Keep these notes", await _git.WorkingFileAsync(created, "notes.txt"));
        Assert.False(Workspace.BrowseRepositories(parent).ContainsRepository);
    }

    [Fact]
    public async Task Clone_uses_the_existing_empty_nested_folder()
    {
        var source = await _git.CreateAsync("source");
        var commit = await _git.CommitFileAsync(source, "base.txt", "base\n", "Initial revision");
        var destination = Path.Combine("Team projects", "Cloned checkout");
        Directory.CreateDirectory(Path.Combine(_git.Root, destination));

        var selected = Workspace.BrowseRepositories(destination);
        Assert.True(selected.IsEmpty);
        Assert.False(selected.ContainsRepository);

        Assert.Equal(destination, await Workspace.CloneInFolderAsync(source, selected.Path));
        Assert.Equal(commit, Assert.Single((await Workspace.ReadAsync(destination)).Commits).Hash);
        Assert.Equal("base\n", (await _git.WorkingFileAsync(destination, "base.txt")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Clone_requires_an_empty_folder_and_preserves_existing_files()
    {
        const string destination = "Existing documents";
        await _git.WriteAsync(destination, ".notes", "Original notes");
        var selected = Workspace.BrowseRepositories(destination);
        Assert.Empty(selected.Folders);
        Assert.False(selected.IsEmpty);
        Assert.False(selected.ContainsRepository);

        var refused = await Assert.ThrowsAsync<GitWorkspace.RepositoryDestinationConflictException>(() =>
            Workspace.CloneInFolderAsync("unused", selected.Path));

        Assert.Equal(GitWorkspace.CloneFolderNotEmptyMessage, refused.Message);
        Assert.Equal("Original notes", await _git.WorkingFileAsync(destination, ".notes"));
        Assert.False(Workspace.BrowseRepositories(destination).ContainsRepository);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_working_or_bare_repository_blocks_init_and_clone(bool bare)
    {
        const string destination = "Existing checkout";
        Directory.CreateDirectory(Path.Combine(_git.Root, destination));
        var selectedBeforeInit = Workspace.BrowseRepositories(destination);
        Assert.False(selectedBeforeInit.ContainsRepository);
        if (bare)
            await _git.GitAsync(destination, "init", "--bare", "-b", "main");
        else
            await _git.GitAsync(destination, "init", "-b", "main");
        var headPath = bare ? "HEAD" : ".git/HEAD";

        var selectedAfterInit = Workspace.BrowseRepositories(destination);
        Assert.True(selectedAfterInit.ContainsRepository);
        Assert.Equal(!bare, selectedAfterInit.HasGitMarker);
        var init = await Assert.ThrowsAsync<GitWorkspace.RepositoryDestinationConflictException>(() =>
            Workspace.CreateInFolderAsync(selectedBeforeInit.Path));
        var clone = await Assert.ThrowsAsync<GitWorkspace.RepositoryDestinationConflictException>(() =>
            Workspace.CloneInFolderAsync("unused", selectedBeforeInit.Path));

        Assert.Equal(GitWorkspace.ExistingRepositoryMessage, init.Message);
        Assert.Equal(GitWorkspace.ExistingRepositoryMessage, clone.Message);
        Assert.Equal("ref: refs/heads/main\n", (await _git.WorkingFileAsync(destination, headPath)).ReplaceLineEndings("\n"));
        Assert.Equal(!bare, Directory.Exists(Path.Combine(_git.Root, destination, ".git")));
    }

    [Fact]
    public async Task Existing_git_metadata_is_protected_before_initializing_an_existing_folder()
    {
        const string destination = "Checkout metadata";
        const string pointer = "gitdir: ../missing-metadata\n";
        await _git.WriteAsync(destination, ".git", pointer);

        Assert.True(Workspace.BrowseRepositories(destination).ContainsRepository);
        var refused = await Assert.ThrowsAsync<GitWorkspace.RepositoryDestinationConflictException>(() =>
            Workspace.CreateInFolderAsync(destination));

        Assert.Equal(GitWorkspace.ExistingRepositoryMessage, refused.Message);
        Assert.Equal(pointer, await _git.WorkingFileAsync(destination, ".git"));
    }

    [Fact]
    public async Task Folder_destination_commands_reject_parent_traversal_metadata_and_collisions()
    {
        const string outside = "../other";
        Directory.CreateDirectory(Path.Combine(_git.Root, outside));
        Directory.CreateDirectory(Path.Combine(_git.Root, "existing"));
        await _git.WriteAsync(".", "existing.txt", "Original file");

        await Assert.ThrowsAsync<ArgumentException>(() => Workspace.CreateFolderAsync(outside, "new"));
        await Assert.ThrowsAsync<ArgumentException>(() => Workspace.CreateInFolderAsync(outside));
        await Assert.ThrowsAsync<ArgumentException>(() => Workspace.CloneInFolderAsync("unused", outside));
        await Assert.ThrowsAsync<ArgumentException>(() => Workspace.CreateFolderAsync(".", "../new"));
        await Assert.ThrowsAsync<ArgumentException>(() => Workspace.CreateFolderAsync(".", ".git"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workspace.CreateFolderAsync(".", "existing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workspace.CreateFolderAsync(".", "existing.txt"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_git.Root, outside)));
        Assert.Equal("Original file", await _git.WorkingFileAsync(".", "existing.txt"));
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
    public async Task Remote_with_an_embedded_username_is_flagged_until_its_url_is_replaced()
    {
        const string username = "fixture-organization";
        const string cleanUrl = "https://git.example.invalid/organization/project/_git/repo";
        var repo = await _git.CreateAsync();
        await _git.GitAsync(repo, "remote", "add", "origin",
            $"https://{username}@git.example.invalid/organization/project/_git/repo");

        var remote = Assert.Single((await Workspace.ReadAsync(repo)).Remotes);
        Assert.Equal("origin", remote.Name);
        Assert.True(remote.HasEmbeddedHttpCredentials);
        Assert.Equal(cleanUrl, remote.FetchUrl);
        Assert.Equal(cleanUrl, remote.PushUrl);

        foreach (var action in new[] { GitAction.Fetch, GitAction.Pull })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => Workspace.ExecuteAsync(repo, action));
            Assert.Contains("fetch URL for remote 'origin'", error.Message);
            Assert.Contains("embedded HTTP(S) credentials", error.Message);
            Assert.Contains("Git settings → HTTPS", error.Message);
            Assert.DoesNotContain(username, error.Message);
        }

        await Workspace.ExecuteAsync(repo, GitAction.SetRemote, "origin", cleanUrl);

        var updated = Assert.Single((await Workspace.ReadAsync(repo)).Remotes);
        Assert.False(updated.HasEmbeddedHttpCredentials);
        Assert.Equal(cleanUrl, updated.FetchUrl);
        Assert.Equal(cleanUrl, (await _git.GitAsync(repo, "remote", "get-url", "origin")).Trim());
    }

    [Fact]
    public async Task Secondary_fetch_url_with_credentials_is_flagged_when_primary_urls_are_clean()
    {
        const string fetchUrl = "https://git.example.invalid/team/repo";
        const string pushUrl = "https://publish.example.invalid/team/repo";
        var repo = await _git.CreateAsync();
        await Workspace.ExecuteAsync(repo, GitAction.AddRemote, "origin", fetchUrl);
        await _git.GitAsync(repo, "config", "--add", "remote.origin.url",
            "https://fixture-user@mirror.example.invalid/team/repo");
        await _git.GitAsync(repo, "remote", "set-url", "--push", "origin", pushUrl);

        var remote = Assert.Single((await Workspace.ReadAsync(repo)).Remotes);
        Assert.True(remote.HasEmbeddedHttpCredentials);
        Assert.Equal(fetchUrl, remote.FetchUrl);
        Assert.Equal(pushUrl, remote.PushUrl);
    }

    [Fact]
    public async Task Push_url_rewrite_with_credentials_is_flagged_without_exposing_them()
    {
        const string cleanUrl = "https://git.example.invalid/team/repo";
        const string username = "fixture-user";
        const string token = "fixture-token";
        var repo = await _git.CreateAsync();
        await Workspace.ExecuteAsync(repo, GitAction.AddRemote, "origin", cleanUrl);
        await _git.GitAsync(repo, "config", $"url.https://{username}:{token}@git.example.invalid/.pushInsteadOf",
            "https://git.example.invalid/");

        var remote = Assert.Single((await Workspace.ReadAsync(repo)).Remotes);
        Assert.True(remote.HasEmbeddedHttpCredentials);
        Assert.Equal(cleanUrl, remote.FetchUrl);
        Assert.Equal(cleanUrl, remote.PushUrl);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Workspace.ExecuteAsync(repo, GitAction.Push, "origin"));
        Assert.Contains("push URL for remote 'origin'", error.Message);
        Assert.Contains("Git URL rewrite", error.Message);
        Assert.DoesNotContain(username, error.Message);
        Assert.DoesNotContain(token, error.Message);
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
    public async Task Push_distinguishes_the_current_branch_from_a_same_named_tag()
    {
        var remote = Directory.CreateDirectory(Path.Combine(_git.Root, "remote.git")).FullName;
        await _git.GitAsync("remote.git", "init", "--bare", "-b", "published");
        var source = await _git.CreateAsync("source");
        await _git.CommitFileAsync(source, "base.txt", "base\n", "Base");
        await _git.GitAsync(source, "tag", "-a", "main", "-m", "Release");
        var tag = (await _git.GitAsync(source, "rev-parse", "refs/tags/main")).Trim();
        var tip = await _git.CommitFileAsync(source, "next.txt", "next\n", "Next");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "origin", remote);
        await _git.GitAsync(source, "config", "branch.main.remote", "origin");
        await _git.GitAsync(source, "config", "branch.main.merge", "refs/heads/published");

        Assert.Equal("main", (await Workspace.ReadAsync(source)).Branch);

        await Workspace.ExecuteAsync(source, GitAction.Push);

        Assert.Equal($"refs/heads/published {tip}", (await _git.GitAsync(
            "remote.git", "for-each-ref", "--format=%(refname) %(objectname)")).Trim());

        await Workspace.ExecuteAsync(source, GitAction.Push, "origin", "refs/tags/main");

        var refs = await _git.GitAsync("remote.git", "for-each-ref", "--format=%(refname) %(objectname)");
        Assert.Equal([$"refs/heads/published {tip}", $"refs/tags/main {tag}"],
            refs.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Tag_status_follows_origin_and_compares_annotated_tag_objects()
    {
        var remote = Directory.CreateDirectory(Path.Combine(_git.Root, "origin.git")).FullName;
        await _git.GitAsync("origin.git", "init", "--bare", "-b", "main");
        var source = await _git.CreateAsync("source");
        var commit = await _git.CommitFileAsync(source, "base.txt", "base\n", "Base");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "origin", remote);
        await _git.GitAsync(source, "tag", "lightweight");
        await _git.GitAsync(source, "tag", "-a", "release/v1", "-m", "Original annotation");

        var localTags = (await Workspace.ReadAsync(source)).Refs.Where(reference => reference.Kind == "tag")
            .ToDictionary(reference => reference.Name);
        var beforePush = await Workspace.ReadOriginTagsAsync(source);
        Assert.Equal(TagPushStatus.LocalOnly, localTags["lightweight"].GetTagPushStatus(beforePush));
        Assert.Equal(TagPushStatus.LocalOnly, localTags["release/v1"].GetTagPushStatus(beforePush));

        await Workspace.ExecuteAsync(source, GitAction.Push, "origin", "refs/tags/lightweight");
        await Workspace.ExecuteAsync(source, GitAction.Push, "origin", "refs/tags/release/v1");

        var afterPush = await Workspace.ReadOriginTagsAsync(source);
        Assert.Equal(TagPushStatus.OnOrigin, localTags["lightweight"].GetTagPushStatus(afterPush));
        Assert.Equal(TagPushStatus.OnOrigin, localTags["release/v1"].GetTagPushStatus(afterPush));

        await _git.GitAsync(source, "tag", "-f", "-a", "release/v1", "-m", "New annotation", commit);
        await _git.GitAsync("origin.git", "tag", "-d", "lightweight");

        var currentTag = Assert.Single((await Workspace.ReadAsync(source)).Refs, reference => reference.Name == "release/v1");
        var afterChanges = await Workspace.ReadOriginTagsAsync(source);
        Assert.Equal(commit, currentTag.Hash);
        Assert.Equal(TagPushStatus.Different, currentTag.GetTagPushStatus(afterChanges));
        Assert.Equal(TagPushStatus.LocalOnly, localTags["lightweight"].GetTagPushStatus(afterChanges));
    }

    [Fact]
    public async Task Unreachable_origin_does_not_produce_an_empty_tag_listing()
    {
        var remote = Directory.CreateDirectory(Path.Combine(_git.Root, "not-a-repository")).FullName;
        var source = await _git.CreateAsync("source");
        await _git.CommitFileAsync(source, "base.txt", "base\n", "Base");
        await _git.GitAsync(source, "tag", "release");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "origin", remote);

        var tag = Assert.Single((await Workspace.ReadAsync(source)).Refs, reference => reference.Kind == "tag");
        await Assert.ThrowsAsync<GitCommandException>(() => Workspace.ReadOriginTagsAsync(source));
        Assert.Equal(TagPushStatus.Unknown, tag.GetTagPushStatus(null));
    }

    [Fact]
    public async Task Detached_tag_push_with_blank_remote_uses_the_configured_push_default()
    {
        var origin = Directory.CreateDirectory(Path.Combine(_git.Root, "origin.git")).FullName;
        var fork = Directory.CreateDirectory(Path.Combine(_git.Root, "fork.git")).FullName;
        await _git.GitAsync("origin.git", "init", "--bare", "-b", "main");
        await _git.GitAsync("fork.git", "init", "--bare", "-b", "main");
        var source = await _git.CreateAsync("source");
        var tip = await _git.CommitFileAsync(source, "base.txt", "base\n", "Base");
        await _git.GitAsync(source, "tag", "release");
        await _git.GitAsync(source, "switch", "--detach");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "origin", origin);
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "fork", fork);
        await _git.GitAsync(source, "config", "remote.pushDefault", "fork");

        await Workspace.ExecuteAsync(source, GitAction.Push, "", "refs/tags/release");

        Assert.Equal(tip, (await _git.GitAsync("fork.git", "rev-parse", "refs/tags/release")).Trim());
        Assert.Empty(await _git.GitAsync("origin.git", "for-each-ref"));
    }

    [Fact]
    public async Task Pushing_a_missing_qualified_branch_does_not_publish_a_tag()
    {
        var remote = Directory.CreateDirectory(Path.Combine(_git.Root, "remote.git")).FullName;
        await _git.GitAsync("remote.git", "init", "--bare", "-b", "main");
        var source = await _git.CreateAsync("source");
        await _git.CommitFileAsync(source, "base.txt", "base\n", "Base");
        await _git.GitAsync(source, "tag", "refs/heads/release");
        await Workspace.ExecuteAsync(source, GitAction.AddRemote, "origin", remote);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workspace.ExecuteAsync(source, GitAction.Push, "origin", "refs/heads/release"));
        Assert.Empty(await _git.GitAsync("remote.git", "for-each-ref"));
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
