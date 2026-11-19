using GitMeadow.Git;
using Xunit;

namespace GitMeadow.Tests;

public sealed class WorkingTreeTests : IDisposable
{
    private readonly GitFixture _git = new();
    private GitWorkspace Workspace => _git.Workspace;

    [Fact]
    public async Task Unborn_repository_can_unstage_then_create_first_commit()
    {
        var repo = await _git.CreateAsync();
        var initial = await Workspace.ReadAsync(repo);
        Assert.Equal("main", initial.Branch);
        Assert.Empty(initial.Commits);
        Assert.Empty(initial.Changes);

        await _git.WriteAsync(repo, "readme.txt", "first content\n");
        var untracked = Assert.Single((await Workspace.ReadAsync(repo)).Changes);
        Assert.Equal(("readme.txt", '?', '?'), (untracked.Path, untracked.Index, untracked.WorkTree));
        Assert.Contains("+first content", await Workspace.InspectAsync(repo, GitView.Diff, path: "readme.txt"));

        await Workspace.ExecuteAsync(repo, GitAction.Stage, "readme.txt");
        Assert.Contains("+first content", await Workspace.InspectAsync(repo, GitView.StagedDiff, path: "readme.txt"));
        await Workspace.ExecuteAsync(repo, GitAction.Unstage, "readme.txt");
        Assert.Equal('?', Assert.Single((await Workspace.ReadAsync(repo)).Changes).Index);
        Assert.Equal("first content\n", await _git.WorkingFileAsync(repo, "readme.txt"));

        await Workspace.ExecuteAsync(repo, GitAction.StageAll);
        await Workspace.ExecuteAsync(repo, GitAction.SetIdentity, "First Author", "first@example.invalid");
        await Workspace.ExecuteAsync(repo, GitAction.Commit, "Initial revision\n\nA body line.");
        var committed = await Workspace.ReadAsync(repo);
        var commit = Assert.Single(committed.Commits);
        Assert.Equal("Initial revision", commit.Subject);
        Assert.Equal("First Author", commit.Author);
        Assert.Empty(commit.Parents);
        Assert.Empty(committed.Changes);
        var detail = await Workspace.CommitAsync(repo, commit.Hash);
        Assert.Equal("first@example.invalid", detail.Email);
        Assert.Contains("A body line.", detail.Message);
        Assert.Equal("readme.txt", Assert.Single(detail.Files));
        Assert.Contains("+first content", detail.Diff);
    }

    [Fact]
    public async Task Partial_staging_keeps_independent_index_and_work_tree_diffs()
    {
        const string file = "separated.txt";
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, file, SeparateChanges("old first", "old second"), "Base file");
        await _git.WriteAsync(repo, file, SeparateChanges("new first", "new second"));

        var hunks = await Workspace.HunksAsync(repo, file, staged: false);
        Assert.Equal(2, hunks.Count);
        var first = Assert.Single(hunks, hunk => hunk.Text.Contains("+new first", StringComparison.Ordinal));
        await Workspace.ApplyHunkAsync(repo, file, staged: false, first.Id);

        var change = Assert.Single((await Workspace.ReadAsync(repo)).Changes);
        Assert.Equal(('M', 'M'), (change.Index, change.WorkTree));
        var stagedDiff = await Workspace.InspectAsync(repo, GitView.StagedDiff, path: file);
        var unstagedDiff = await Workspace.InspectAsync(repo, GitView.Diff, path: file);
        Assert.Contains("+new first", stagedDiff);
        Assert.DoesNotContain("+new second", stagedDiff);
        Assert.Contains("+new second", unstagedDiff);
        Assert.DoesNotContain("+new first", unstagedDiff);

        var staged = Assert.Single(await Workspace.HunksAsync(repo, file, staged: true));
        await Workspace.ApplyHunkAsync(repo, file, staged: true, staged.Id);
        Assert.Empty(await Workspace.HunksAsync(repo, file, staged: true));
        Assert.Equal(2, (await Workspace.HunksAsync(repo, file, staged: false)).Count);
        Assert.Equal(SeparateChanges("new first", "new second"), await _git.WorkingFileAsync(repo, file));
    }

    [Fact]
    public async Task Stale_hunk_is_rejected_without_staging_or_overwriting_edits()
    {
        const string file = "notes.txt";
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, file, "before\n", "Base file");
        await _git.WriteAsync(repo, file, "first edit\n");
        var hunk = Assert.Single(await Workspace.HunksAsync(repo, file, staged: false));
        await _git.WriteAsync(repo, file, "later edit\n");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.ApplyHunkAsync(repo, file, staged: false, hunk.Id));

        Assert.Equal("", await _git.GitAsync(repo, "diff", "--cached"));
        Assert.Equal("later edit\n", await _git.WorkingFileAsync(repo, file));
    }

    [Fact]
    public async Task Unicode_and_glob_like_paths_are_staged_literally()
    {
        const string target = "[é] notes.txt";
        const string neighbor = "é notes.txt";
        const string optionLike = "--delete.txt";
        var repo = await _git.CreateAsync();
        await _git.WriteAsync(repo, target, "target\n");
        await _git.WriteAsync(repo, neighbor, "neighbor\n");
        await _git.WriteAsync(repo, optionLike, "literal option\n");

        await Workspace.ExecuteAsync(repo, GitAction.Stage, target);
        await Workspace.ExecuteAsync(repo, GitAction.Stage, optionLike);
        var changes = (await Workspace.ReadAsync(repo)).Changes;
        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, change => change is { Path: target, Index: 'A' });
        Assert.Contains(changes, change => change is { Path: optionLike, Index: 'A' });
        Assert.Contains(changes, change => change is { Path: neighbor, Index: '?' });
        Assert.Contains("+target", await Workspace.InspectAsync(repo, GitView.StagedDiff, path: target));

        await Workspace.ExecuteAsync(repo, GitAction.UnstageAll);
        Assert.All((await Workspace.ReadAsync(repo)).Changes, change => Assert.Equal('?', change.Index));
        Assert.Equal("neighbor\n", await _git.WorkingFileAsync(repo, neighbor));
    }

    [Fact]
    public async Task Rename_status_preserves_both_paths_through_unstage()
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, "before.txt", "identical contents\n", "Before rename");
        await _git.GitAsync(repo, "mv", "before.txt", "after ä.txt");

        var rename = Assert.Single((await Workspace.ReadAsync(repo)).Changes);
        Assert.Equal(("after ä.txt", "before.txt", 'R'), (rename.Path, rename.OriginalPath, rename.Index));
        await Workspace.ExecuteAsync(repo, GitAction.Unstage, rename.Path, rename.OriginalPath);

        var changes = (await Workspace.ReadAsync(repo)).Changes;
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, change => change is { Path: "before.txt", WorkTree: 'D' });
        Assert.Contains(changes, change => change is { Path: "after ä.txt", Index: '?' });
        Assert.Equal("identical contents\n", await _git.WorkingFileAsync(repo, "after ä.txt"));
    }

    [Fact]
    public async Task Discard_restores_work_tree_from_index_and_keeps_staged_edit()
    {
        const string path = "tracked.txt";
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, path, "committed\n", "Base file");
        await _git.WriteAsync(repo, path, "staged\n");
        await Workspace.ExecuteAsync(repo, GitAction.Stage, path);
        await _git.WriteAsync(repo, path, "unstaged\n");

        await Workspace.ExecuteAsync(repo, GitAction.Discard, path);

        Assert.Equal("staged\n", await _git.WorkingFileAsync(repo, path));
        Assert.Equal("", await _git.GitAsync(repo, "diff", "--", path));
        Assert.Contains("+staged", await _git.GitAsync(repo, "diff", "--cached", "--", path));
    }

    private static string SeparateChanges(string first, string second) =>
        first + "\n" + string.Concat(Enumerable.Range(1, 24).Select(number => $"context {number}\n")) + second + "\n";

    public void Dispose() => _git.Dispose();
}
