using GitMeadow.Git;
using GitMeadow.Workbench;
using Xunit;

namespace GitMeadow.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly GitFixture _git = new();
    private GitWorkspace Workspace => _git.Workspace;

    [Fact]
    public async Task Merge_history_exposes_two_parents_refs_and_revision_filter()
    {
        var repo = await _git.CreateAsync();
        var root = await _git.CommitFileAsync(repo, "root.txt", "root\n", "Root");
        await Workspace.ExecuteAsync(repo, GitAction.Branch, "feature", root);
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "feature");
        var topic = await _git.CommitFileAsync(repo, "feature.txt", "feature\n", "Feature tip");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "main");
        var previousMain = await _git.CommitFileAsync(repo, "main.txt", "main\n", "Main tip");
        await Workspace.ExecuteAsync(repo, GitAction.Merge, "feature");
        var merge = (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim();

        var state = await Workspace.ReadAsync(repo);
        Assert.Equal("main", state.Branch);
        Assert.Equal(4, state.Commits.Count);
        Assert.Equal([previousMain, topic], state.Commits.Single(commit => commit.Hash == merge).Parents);
        Assert.Contains(state.Refs, reference => reference is { Name: "main", Kind: "branch" } && reference.Hash == merge);
        Assert.Contains(state.Refs, reference => reference is { Name: "feature", Kind: "branch" } && reference.Hash == topic);
        Assert.Equal([topic, root], (await Workspace.ReadAsync(repo, "feature")).Commits.Select(commit => commit.Hash));

        var detail = await Workspace.CommitAsync(repo, merge);
        Assert.Contains("feature.txt", detail.Files);
        Assert.Contains("+feature", detail.Diff);
    }

    [Fact]
    public async Task Expanding_history_limit_retains_order_and_adds_older_revisions()
    {
        var repo = await _git.CreateAsync();
        var first = await _git.CommitFileAsync(repo, "one.txt", "one\n", "First");
        var second = await _git.CommitFileAsync(repo, "two.txt", "two\n", "Second");
        var third = await _git.CommitFileAsync(repo, "three.txt", "three\n", "Third");

        var preview = await Workspace.ReadAsync(repo, "main", commitLimit: 2);
        var expanded = await Workspace.ReadAsync(repo, "main", commitLimit: 3);

        Assert.Equal([third, second], preview.Commits.Select(commit => commit.Hash));
        Assert.Equal([first], preview.Commits[1].Parents);
        Assert.Equal([third, second, first], expanded.Commits.Select(commit => commit.Hash));
    }

    [Fact]
    public async Task History_uses_a_recent_window_and_displays_loaded_children_before_parents()
    {
        var repo = await _git.CreateAsync();
        const string history = """
            commit refs/heads/main
            mark :1
            committer Test <test@example.invalid> 1600000300 +0000
            data <<END
            Shared root
            END

            reset refs/tags/root
            from :1

            commit refs/heads/main
            committer Test <test@example.invalid> 1600000100 +0000
            data <<END
            Backdated main tip
            END

            commit refs/heads/topic
            committer Test <test@example.invalid> 1600000200 +0000
            data <<END
            Topic base
            END
            from :1

            commit refs/heads/topic
            committer Test <test@example.invalid> 1600000400 +0000
            data <<END
            Topic tip
            END

            done
            """;
        (await GitProcess.RunAsync(Path.Combine(_git.Root, repo), ["fast-import", "--quiet"],
            history.ReplaceLineEndings("\n") + "\n")).RequireSuccess();

        var preview = await Workspace.ReadAsync(repo, commitLimit: 2);
        var expanded = await Workspace.ReadAsync(repo, commitLimit: 4);

        Assert.Equal(["Topic tip", "Shared root"], preview.Commits.Select(commit => commit.Subject));
        Assert.Equal(["Topic tip", "Topic base", "Backdated main tip", "Shared root"],
            expanded.Commits.Select(commit => commit.Subject));
        Assert.Equal([preview.Commits[1].Hash], expanded.Commits[2].Parents);

        var tip = preview.Commits[0].Hash;
        var previewGraph = RevisionGraph.Build(preview.Commits);
        Assert.Empty(previewGraph.Rows[tip].Lines);
        Assert.Empty(previewGraph.Rows[preview.Commits[1].Hash].Lines);

        var expandedGraph = RevisionGraph.Build(expanded.Commits);
        var topicBase = expandedGraph.Rows[expanded.Commits[1].Hash];
        Assert.True(topicBase.Incoming);
        Assert.Equal(topicBase.Lane, Assert.Single(expandedGraph.Rows[tip].Lines).To);
    }

    [Fact]
    public async Task File_inspection_at_old_revision_ignores_newer_work_tree_content()
    {
        const string path = "guide/č.txt";
        var repo = await _git.CreateAsync();
        var first = await _git.CommitFileAsync(repo, path, "introduced line\n", "Introduce guide");
        var second = await _git.CommitFileAsync(repo, path, "introduced line\nsecond line\n", "Extend guide");
        await _git.CommitFileAsync(repo, "elsewhere.txt", "unrelated\n", "Unrelated change");
        await _git.WriteAsync(repo, path, "work in progress\n");

        Assert.Equal("introduced line\n", await Workspace.InspectAsync(repo, GitView.File, first, path));
        var history = await Workspace.InspectAsync(repo, GitView.FileHistory, "HEAD", path);
        Assert.Contains("Introduce guide", history);
        Assert.Contains("Extend guide", history);
        Assert.DoesNotContain("Unrelated change", history);
        var blame = await Workspace.InspectAsync(repo, GitView.Blame, second, path);
        Assert.Contains("introduced line", blame);
        Assert.Contains("second line", blame);
        Assert.DoesNotContain("work in progress", blame);
        var tree = await Workspace.InspectAsync(repo, GitView.Tree, second);
        Assert.DoesNotContain("elsewhere.txt", tree);
        Assert.Contains("guide/", tree);
        var comparison = await Workspace.InspectAsync(repo, GitView.Compare, first, second);
        Assert.Contains("+second line", comparison);
        Assert.DoesNotContain("work in progress", comparison);
        Assert.DoesNotContain("elsewhere.txt", comparison);
    }

    [Fact]
    public async Task Oversized_commit_preserves_details_when_diff_is_omitted()
    {
        var repo = await _git.CreateAsync();
        var contents = string.Concat(Enumerable.Repeat(new string('x', 60) + "\n", 80_000));
        var hash = await _git.CommitFileAsync(repo, "large.txt", contents, "Large text change");

        var details = await Workspace.CommitAsync(repo, hash);

        Assert.Equal(hash, details.Hash);
        Assert.Equal("Large text change", details.Message);
        Assert.Equal("large.txt", Assert.Single(details.Files));
        Assert.True(details.DiffOmitted);
        Assert.Empty(details.Diff);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Workspace.InspectAsync(repo, GitView.Patch, hash));
    }

    [Fact]
    public async Task Detached_tip_remains_visible_after_switch_by_commit()
    {
        var repo = await _git.CreateAsync();
        var first = await _git.CommitFileAsync(repo, "one.txt", "one\n", "First revision");
        await _git.CommitFileAsync(repo, "two.txt", "two\n", "Second revision");

        await Workspace.ExecuteAsync(repo, GitAction.Switch, first);
        Assert.Equal("", await _git.GitAsync(repo, "branch", "--show-current"));
        var detached = await _git.CommitFileAsync(repo, "three.txt", "three\n", "Detached revision");
        var state = await Workspace.ReadAsync(repo);

        Assert.NotEqual("main", state.Branch);
        Assert.Equal("Detached revision", Assert.Single(state.Commits, commit => commit.Hash == detached).Subject);
        Assert.Equal([first], state.Commits.Single(commit => commit.Hash == detached).Parents);
    }

    [Fact]
    public async Task Amend_tag_and_revert_update_refs_and_are_visible_in_history()
    {
        var repo = await _git.CreateAsync();
        var baseCommit = await _git.CommitFileAsync(repo, "base.txt", "base\n", "Base");
        var original = await _git.CommitFileAsync(repo, "second.txt", "before amend\n", "Original message");
        await _git.WriteAsync(repo, "second.txt", "after amend\n");
        await Workspace.ExecuteAsync(repo, GitAction.Stage, "second.txt");
        await Workspace.ExecuteAsync(repo, GitAction.Amend, "Amended revision");

        var amended = Assert.Single((await Workspace.ReadAsync(repo)).Commits, commit => commit.Subject == "Amended revision");
        Assert.NotEqual(original, amended.Hash);
        Assert.Equal([baseCommit], amended.Parents);
        Assert.Equal("after amend\n", await Workspace.InspectAsync(repo, GitView.File, amended.Hash, "second.txt"));

        await Workspace.ExecuteAsync(repo, GitAction.Tag, "v1.0.0", amended.Hash);
        Assert.Contains((await Workspace.ReadAsync(repo)).Refs,
            reference => reference is { Name: "v1.0.0", Kind: "tag" } && reference.Hash == amended.Hash);
        await Workspace.ExecuteAsync(repo, GitAction.Revert, amended.Hash);
        var reverted = await Workspace.ReadAsync(repo);
        Assert.Equal([amended.Hash], Assert.Single(reverted.Commits, commit => commit.Subject.StartsWith("Revert", StringComparison.Ordinal)).Parents);
        Assert.False(File.Exists(Path.Combine(_git.Root, repo, "second.txt")));
        Assert.Empty(reverted.Changes);

        await Workspace.ExecuteAsync(repo, GitAction.DeleteTag, "v1.0.0");
        Assert.DoesNotContain((await Workspace.ReadAsync(repo)).Refs, reference => reference.Name == "v1.0.0");
    }

    [Fact]
    public async Task Cherry_pick_applies_selected_tree_change_to_current_branch()
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, "base.txt", "base\n", "Base");
        await Workspace.ExecuteAsync(repo, GitAction.Branch, "topic");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "topic");
        var topic = await _git.CommitFileAsync(repo, "new.txt", "picked contents\n", "Chosen revision");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "main");
        var previousMain = await _git.CommitFileAsync(repo, "main.txt", "main retained\n", "Main tip");

        await Workspace.ExecuteAsync(repo, GitAction.CherryPick, topic);

        Assert.Equal("main", (await Workspace.ReadAsync(repo)).Branch);
        Assert.Equal(previousMain, (await _git.GitAsync(repo, "rev-parse", "HEAD^")).Trim());
        Assert.Equal("picked contents\n", await _git.WorkingFileAsync(repo, "new.txt"));
        Assert.Equal("main retained\n", await _git.WorkingFileAsync(repo, "main.txt"));
    }

    [Fact]
    public async Task Squash_replaces_selected_revisions_while_preserving_tree_and_base_parent()
    {
        var repo = await _git.CreateAsync();
        var @base = await _git.CommitFileAsync(repo, "base.txt", "retained\n", "Base");
        var first = await _git.CommitFileAsync(repo, "one.txt", "first\n", "Old first");
        var second = await _git.CommitFileAsync(repo, "two.txt", "second\n", "Old second");
        var originalTree = (await _git.GitAsync(repo, "rev-parse", "HEAD^{tree}")).Trim();

        await Workspace.ExecuteAsync(repo, GitAction.SquashLast, "Combined work", "2");

        var state = await Workspace.ReadAsync(repo, "main");
        Assert.Equal(2, state.Commits.Count);
        var tip = Assert.Single(state.Commits, commit => commit.Subject == "Combined work");
        Assert.Equal([@base], tip.Parents);
        Assert.DoesNotContain(state.Commits, commit => commit.Hash == first || commit.Hash == second);
        Assert.Equal(originalTree, (await _git.GitAsync(repo, "rev-parse", "HEAD^{tree}")).Trim());
        Assert.Empty(state.Changes);
    }

    [Fact]
    public async Task Squash_rejects_dirty_work_tree_and_preserves_current_tip()
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, "one.txt", "first\n", "First");
        var tip = await _git.CommitFileAsync(repo, "two.txt", "second\n", "Second");
        await _git.WriteAsync(repo, "two.txt", "uncommitted edit\n");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.ExecuteAsync(repo, GitAction.SquashLast, "Should not commit", "2"));

        Assert.Equal(tip, (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim());
        Assert.Equal("uncommitted edit\n", await _git.WorkingFileAsync(repo, "two.txt"));
    }

    public void Dispose() => _git.Dispose();
}
