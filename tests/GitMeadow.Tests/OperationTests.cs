using GitMeadow.Git;
using Xunit;

namespace GitMeadow.Tests;

public sealed class OperationTests : IDisposable
{
    private const string ConflictFile = "conflict.txt";
    private readonly GitFixture _git = new();
    private GitWorkspace Workspace => _git.Workspace;

    [Fact]
    public async Task Merge_conflict_can_be_edited_staged_and_completed()
    {
        var repo = await StartConflictingMergeAsync();
        var conflicted = await Workspace.ReadAsync(repo);
        Assert.Equal("merge", conflicted.Operation);
        Assert.False(conflicted.CanAmend);
        Assert.Contains(conflicted.Changes, change => change is { Path: ConflictFile, Index: 'U', WorkTree: 'U' });
        Assert.Contains("<<<<<<<", await _git.WorkingFileAsync(repo, ConflictFile));

        await Workspace.ExecuteAsync(repo, GitAction.WriteConflict, ConflictFile, "resolved combination\n");
        var resolved = await Workspace.ReadAsync(repo);
        Assert.Equal("merge", resolved.Operation);
        Assert.Equal('M', Assert.Single(resolved.Changes).Index);
        await Workspace.ExecuteAsync(repo, GitAction.Continue);

        var completed = await Workspace.ReadAsync(repo);
        Assert.Null(completed.Operation);
        Assert.True(completed.CanAmend);
        Assert.Empty(completed.Changes);
        Assert.Equal(2, completed.Commits[0].Parents.Count);
        Assert.Equal("resolved combination\n", await Workspace.InspectAsync(repo, GitView.File, "HEAD", ConflictFile));
    }

    [Fact]
    public async Task Abort_merge_restores_pre_merge_tree_without_committing()
    {
        var repo = await StartConflictingMergeAsync();
        var originalHead = await _git.GitAsync(repo, "rev-parse", "HEAD");
        Assert.Contains("<<<<<<<", await _git.WorkingFileAsync(repo, ConflictFile));

        await Workspace.ExecuteAsync(repo, GitAction.Abort);

        var aborted = await Workspace.ReadAsync(repo);
        Assert.Null(aborted.Operation);
        Assert.Empty(aborted.Changes);
        Assert.Equal(originalHead, await _git.GitAsync(repo, "rev-parse", "HEAD"));
        Assert.Equal("main's edit\n", await _git.WorkingFileAsync(repo, ConflictFile));
    }

    [Fact]
    public async Task Stash_pop_recovers_changes_and_removes_the_stash()
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, "tracked.txt", "clean base\n", "Base");
        await _git.WriteAsync(repo, "tracked.txt", "stash this\n");
        await _git.WriteAsync(repo, "new.txt", "untracked stash content\n");

        await Workspace.ExecuteAsync(repo, GitAction.Stash, "Save while switching");

        var saved = await Workspace.ReadAsync(repo);
        var stash = Assert.Single(saved.Stashes);
        Assert.Contains("Save while switching", stash.Subject);
        Assert.Empty(saved.Changes);
        Assert.Equal("clean base\n", await _git.WorkingFileAsync(repo, "tracked.txt"));
        Assert.False(File.Exists(Path.Combine(_git.Root, repo, "new.txt")));
        var patch = await Workspace.InspectAsync(repo, GitView.StashPatch, stash.Hash);
        Assert.Contains("+stash this", patch);
        Assert.Contains("+untracked stash content", patch);

        await Workspace.ExecuteAsync(repo, GitAction.PopStash, stash.Name);

        var popped = await Workspace.ReadAsync(repo);
        Assert.Empty(popped.Stashes);
        Assert.Equal(2, popped.Changes.Count);
        Assert.Contains(popped.Changes, change => change is { Path: "tracked.txt", WorkTree: 'M' });
        Assert.Contains(popped.Changes, change => change is { Path: "new.txt", WorkTree: '?' });
        Assert.Equal("stash this\n", await _git.WorkingFileAsync(repo, "tracked.txt"));
        Assert.Equal("untracked stash content\n", await _git.WorkingFileAsync(repo, "new.txt"));
    }

    [Fact]
    public async Task Stale_stash_slot_cannot_pop_or_drop_a_different_stash()
    {
        const string path = "tracked.txt";
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, path, "base\n", "Base");
        await _git.WriteAsync(repo, path, "older edit\n");
        await Workspace.ExecuteAsync(repo, GitAction.Stash, "Older");
        var older = Assert.Single((await Workspace.ReadAsync(repo)).Stashes);
        await _git.WriteAsync(repo, path, "newer edit\n");
        await Workspace.ExecuteAsync(repo, GitAction.Stash, "Newer");

        var olderPatch = await Workspace.InspectAsync(repo, GitView.StashPatch, older.Hash);
        Assert.Contains("+older edit", olderPatch);
        Assert.DoesNotContain("+newer edit", olderPatch);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.ExecuteAsync(repo, GitAction.PopStash, older.Name, older.Hash));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.ExecuteAsync(repo, GitAction.DropStash, older.Name, older.Hash));

        var state = await Workspace.ReadAsync(repo);
        Assert.Equal(2, state.Stashes.Count);
        Assert.Contains("Newer", state.Stashes[0].Subject);
        Assert.Equal(older.Hash, state.Stashes[1].Hash);
        Assert.Equal("base\n", await _git.WorkingFileAsync(repo, path));
        Assert.Empty(state.Changes);
    }

    [Fact]
    public async Task Rebase_moves_topic_commit_onto_current_main_tip()
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, "base.txt", "base\n", "Base");
        await Workspace.ExecuteAsync(repo, GitAction.Branch, "topic");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "topic");
        var originalTopic = await _git.CommitFileAsync(repo, "topic.txt", "topic contents\n", "Topic change");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "main");
        var main = await _git.CommitFileAsync(repo, "main.txt", "main contents\n", "Main advance");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "topic");

        await Workspace.ExecuteAsync(repo, GitAction.Rebase, "main");

        var rebased = await Workspace.ReadAsync(repo);
        var tip = Assert.Single(rebased.Commits, commit => commit.Subject == "Topic change");
        Assert.Equal("topic", rebased.Branch);
        Assert.NotEqual(originalTopic, tip.Hash);
        Assert.Equal([main], tip.Parents);
        Assert.Equal("topic contents\n", await _git.WorkingFileAsync(repo, "topic.txt"));
        Assert.Equal("main contents\n", await _git.WorkingFileAsync(repo, "main.txt"));
        Assert.Null(rebased.Operation);
    }

    [Fact]
    public async Task Conflicted_rebase_does_not_allow_amending_the_base_commit()
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, ConflictFile, "shared base\n", "Base");
        await Workspace.ExecuteAsync(repo, GitAction.Branch, "topic");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "topic");
        var topic = await _git.CommitFileAsync(repo, ConflictFile, "topic edit\n", "Topic change");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "main");
        var main = await _git.CommitFileAsync(repo, ConflictFile, "main edit\n", "Main change");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "topic");
        await Assert.ThrowsAsync<GitCommandException>(() => Workspace.ExecuteAsync(repo, GitAction.Rebase, "main"));

        var state = await Workspace.ReadAsync(repo);
        Assert.Equal("rebase", state.Operation);
        Assert.False(state.CanAmend);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.ExecuteAsync(repo, GitAction.Amend, "Must not rewrite the base"));
        Assert.Equal(main, (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim());
        await Workspace.ExecuteAsync(repo, GitAction.Abort);
        Assert.Equal("topic", (await Workspace.ReadAsync(repo)).Branch);
        Assert.Equal(topic, (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task Interactive_rebase_reorders_and_rewords_independent_commits()
    {
        var repo = await _git.CreateAsync();
        var baseCommit = await _git.CommitFileAsync(repo, "base.txt", "base\n", "Base");
        var first = await _git.CommitFileAsync(repo, "one.txt", "first change\n", "First change");
        var second = await _git.CommitFileAsync(repo, "two.txt", "second change\n", "Second change");
        var preview = await Workspace.InspectAsync(repo, GitView.RebasePlan, baseCommit);
        Assert.Equal([$"pick {first} First change", $"pick {second} Second change"], preview.Split('\n'));

        await Workspace.ExecuteAsync(repo, GitAction.RebasePlan, baseCommit, $"reword {second}\npick {first}");
        var paused = await Workspace.ReadAsync(repo);
        Assert.Equal("rebase", paused.Operation);
        Assert.True(paused.CanAmend);
        Assert.Equal("second change\n", await _git.WorkingFileAsync(repo, "two.txt"));
        Assert.False(File.Exists(Path.Combine(_git.Root, repo, "one.txt")));
        await Workspace.ExecuteAsync(repo, GitAction.Amend, "Reworded second change");
        await Workspace.ExecuteAsync(repo, GitAction.Continue);

        var completed = await Workspace.ReadAsync(repo, "main");
        Assert.Null(completed.Operation);
        Assert.Equal("main", completed.Branch);
        Assert.Empty(completed.Changes);
        Assert.Equal(["First change", "Reworded second change", "Base"], completed.Commits.Select(commit => commit.Subject));
        Assert.Equal("first change\n", await _git.WorkingFileAsync(repo, "one.txt"));
        Assert.Equal("second change\n", await _git.WorkingFileAsync(repo, "two.txt"));
    }

    [Fact]
    public async Task Interactive_rebase_rejects_omitted_revision_without_modifying_history()
    {
        var repo = await _git.CreateAsync();
        var baseCommit = await _git.CommitFileAsync(repo, "base.txt", "base\n", "Base");
        var first = await _git.CommitFileAsync(repo, "one.txt", "one\n", "First change");
        var second = await _git.CommitFileAsync(repo, "two.txt", "two\n", "Second change");

        await Assert.ThrowsAsync<ArgumentException>(
            () => Workspace.ExecuteAsync(repo, GitAction.RebasePlan, baseCommit, $"pick {first}"));

        var state = await Workspace.ReadAsync(repo);
        Assert.Null(state.Operation);
        Assert.Empty(state.Changes);
        Assert.Equal([second, first, baseCommit], state.Commits.Select(commit => commit.Hash));
    }

    [Fact]
    public async Task Binary_conflict_rejects_text_edit_but_allows_selecting_a_side()
    {
        const string file = "conflict.bin";
        const string mainBytes = "main\0contents";
        const string topicBytes = "topic\0contents";
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, file, "base\0contents", "Base");
        await Workspace.ExecuteAsync(repo, GitAction.Branch, "topic");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "topic");
        await _git.CommitFileAsync(repo, file, topicBytes, "Topic binary");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "main");
        await _git.CommitFileAsync(repo, file, mainBytes, "Main binary");
        await Assert.ThrowsAsync<GitCommandException>(() => Workspace.ExecuteAsync(repo, GitAction.Merge, "topic"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.InspectAsync(repo, GitView.Conflicts, path: file));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.ExecuteAsync(repo, GitAction.WriteConflict, file, "would lose binary data"));
        Assert.Equal(mainBytes, await _git.WorkingFileAsync(repo, file));
        Assert.Contains((await Workspace.ReadAsync(repo)).Changes, change => change is { Path: file, Index: 'U' });

        await Workspace.ExecuteAsync(repo, GitAction.ResolveTheirs, file);
        await Workspace.ExecuteAsync(repo, GitAction.Continue);
        Assert.Equal(topicBytes, await _git.WorkingFileAsync(repo, file));
        Assert.Empty((await Workspace.ReadAsync(repo)).Changes);
    }

    [Fact]
    public async Task Soft_and_mixed_reset_preserve_contents_and_differ_on_staging()
    {
        var repo = await _git.CreateAsync();
        var baseCommit = await _git.CommitFileAsync(repo, "tracked.txt", "before\n", "Base");
        await _git.CommitFileAsync(repo, "tracked.txt", "after\n", "Change");

        await Workspace.ExecuteAsync(repo, GitAction.ResetSoft, baseCommit);
        Assert.Equal('M', Assert.Single((await Workspace.ReadAsync(repo)).Changes).Index);

        await Workspace.ExecuteAsync(repo, GitAction.ResetMixed, baseCommit);

        Assert.Empty(await Workspace.HunksAsync(repo, "tracked.txt", staged: true));
        Assert.Equal('M', Assert.Single((await Workspace.ReadAsync(repo)).Changes).WorkTree);
        Assert.Equal("after\n", await _git.WorkingFileAsync(repo, "tracked.txt"));
        Assert.Contains("reset", await Workspace.InspectAsync(repo, GitView.Reflog));
    }

    [Fact]
    public async Task Commit_patch_applies_to_matching_tree_without_committing()
    {
        const string file = "tracked.txt";
        var repo = await _git.CreateAsync();
        var before = await _git.CommitFileAsync(repo, file, "first version\n", "Base");
        var after = await _git.CommitFileAsync(repo, file, "second version\n", "Change");
        var patch = await Workspace.InspectAsync(repo, GitView.Patch, after);
        Assert.Contains("-first version", patch);
        Assert.Contains("+second version", patch);
        await Workspace.ExecuteAsync(repo, GitAction.ResetHard, before);

        await Workspace.ExecuteAsync(repo, GitAction.ApplyPatch, patch);

        Assert.Equal("second version\n", await _git.WorkingFileAsync(repo, file));
        Assert.Equal(before, (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim());
        Assert.Equal(file, Assert.Single((await Workspace.ReadAsync(repo)).Changes).Path);
    }

    [Fact]
    public async Task Notes_attach_to_selected_commit_without_rewriting_history()
    {
        var repo = await _git.CreateAsync();
        var first = await _git.CommitFileAsync(repo, "one.txt", "first\n", "Base");
        var second = await _git.CommitFileAsync(repo, "two.txt", "second\n", "Change");

        await Workspace.ExecuteAsync(repo, GitAction.AddNote, first, "Review complete\nValidated both outputs.");

        var notes = await Workspace.InspectAsync(repo, GitView.Notes, first);
        Assert.Contains("Review complete\nValidated both outputs.", notes);
        Assert.Equal([second, first], (await Workspace.ReadAsync(repo, "main")).Commits.Select(commit => commit.Hash));
        Assert.Equal(second, (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task Bisect_finds_first_bad_revision_and_reset_restores_branch()
    {
        var repo = await _git.CreateAsync();
        var good = await _git.CommitFileAsync(repo, "behavior.txt", "working\n", "Last good revision");
        var firstBad = await _git.CommitFileAsync(repo, "behavior.txt", "regression\n", "Introduce regression");
        var tip = await _git.CommitFileAsync(repo, "later.txt", "later change\n", "Still broken");

        await Workspace.ExecuteAsync(repo, GitAction.BisectStart, tip, good);

        Assert.Equal("bisect", (await Workspace.ReadAsync(repo)).Operation);
        Assert.Equal(firstBad, (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim());
        var result = await Workspace.ExecuteAsync(repo, GitAction.BisectBad);
        Assert.Contains(firstBad, result);

        await Workspace.ExecuteAsync(repo, GitAction.BisectReset);
        var resumed = await Workspace.ReadAsync(repo);
        Assert.Equal("main", resumed.Branch);
        Assert.Null(resumed.Operation);
        Assert.Equal(tip, (await _git.GitAsync(repo, "rev-parse", "HEAD")).Trim());
    }

    private async Task<string> StartConflictingMergeAsync()
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, ConflictFile, "common base\n", "Base");
        await Workspace.ExecuteAsync(repo, GitAction.Branch, "topic");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "topic");
        await _git.CommitFileAsync(repo, ConflictFile, "topic's edit\n", "Topic edit");
        await Workspace.ExecuteAsync(repo, GitAction.Switch, "main");
        await _git.CommitFileAsync(repo, ConflictFile, "main's edit\n", "Main edit");

        await Assert.ThrowsAsync<GitCommandException>(() => Workspace.ExecuteAsync(repo, GitAction.Merge, "topic"));
        return repo;
    }

    public void Dispose() => _git.Dispose();
}
