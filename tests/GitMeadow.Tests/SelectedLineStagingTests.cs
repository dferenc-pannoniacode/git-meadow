using GitMeadow.Git;
using Xunit;

namespace GitMeadow.Tests;

public sealed class SelectedLineStagingTests : IDisposable
{
    private const string FileName = "changes.txt";
    private readonly GitFixture _git = new();
    private GitWorkspace Workspace => _git.Workspace;

    [Fact]
    public async Task Replacement_addition_and_deletion_can_be_staged_and_unstaged_separately()
    {
        const string baseline = "header\nold\nfooter\n";
        const string replacement = "header\nnew\nfooter\n";
        var repo = await CreateChangeAsync(baseline, replacement);
        var hunk = await SingleHunkAsync(repo, staged: false);
        var addition = Select(hunk, '+', "new");
        var deletion = Select(hunk, '-', "old");
        Assert.Equal((null, (int?)2), (addition.OldNumber, addition.NewNumber));
        Assert.Equal(((int?)2, null), (deletion.OldNumber, deletion.NewNumber));
        Assert.NotEqual(addition.Id, deletion.Id);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, [addition.Id]);

        Assert.Equal("header\nold\nnew\nfooter\n", await ReadIndexAsync(repo));
        var mixed = Assert.Single((await Workspace.ReadAsync(repo)).Changes);
        Assert.Equal(('M', 'M'), (mixed.Index, mixed.WorkTree));
        var remainder = await SingleHunkAsync(repo, staged: false);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, remainder.Id, [Select(remainder, '-', "old").Id]);
        Assert.Equal(replacement, await ReadIndexAsync(repo));

        var staged = await SingleHunkAsync(repo, staged: true);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: true, staged.Id, [Select(staged, '-', "old").Id]);
        Assert.Equal("header\nold\nnew\nfooter\n", await ReadIndexAsync(repo));
        staged = await SingleHunkAsync(repo, staged: true);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: true, staged.Id, [Select(staged, '+', "new").Id]);
        Assert.Equal(baseline, await ReadIndexAsync(repo));
        Assert.Equal(replacement, await _git.WorkingFileAsync(repo, FileName));
        Assert.Empty(await Workspace.HunksAsync(repo, FileName, staged: true));
    }

    [Fact]
    public async Task Mixed_selected_lines_leave_preexisting_staged_and_unselected_work_tree_edits()
    {
        const string baseline = "head\nold first\ncontext\nold second\nseparator\ntail\n";
        const string alreadyStaged = "staged head\nold first\ncontext\nold second\nseparator\ntail\n";
        const string working = "staged head\nnew first\ncontext\nnew second\nseparator\nworking tail\n";
        var repo = await CreateChangeAsync(baseline, alreadyStaged);
        await Workspace.ExecuteAsync(repo, GitAction.Stage, FileName);
        await _git.WriteAsync(repo, FileName, working);
        var hunk = await SingleHunkAsync(repo, staged: false);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id,
            [Select(hunk, '-', "old first").Id, Select(hunk, '+', "new second").Id]);

        Assert.Equal("staged head\ncontext\nold second\nnew second\nseparator\ntail\n", await ReadIndexAsync(repo));
        var unstaged = await Workspace.InspectAsync(repo, GitView.Diff, path: FileName);
        Assert.Contains("+new first", unstaged);
        Assert.Contains("+working tail", unstaged);
        Assert.DoesNotContain("+staged head", unstaged);
        Assert.Equal(working, await _git.WorkingFileAsync(repo, FileName));
    }

    [Fact]
    public async Task Selecting_one_of_identical_deleted_lines_uses_its_own_position()
    {
        var repo = await CreateChangeAsync("repeat\none\nrepeat\ntwo\n", "one\ntwo\n");
        var hunk = await SingleHunkAsync(repo, staged: false);
        var deletions = hunk.Lines.Where(line => line is { Kind: '-', Text: "repeat" }).ToArray();
        Assert.Equal(new int?[] { 1, 3 }, deletions.Select(line => line.OldNumber));
        Assert.NotEqual(deletions[0].Id, deletions[1].Id);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, [deletions[1].Id]);

        Assert.Equal("repeat\none\ntwo\n", await ReadIndexAsync(repo));
        Assert.Equal("one\ntwo\n", await _git.WorkingFileAsync(repo, FileName));
    }

    [Fact]
    public async Task Selecting_later_hunk_ignores_offsets_from_earlier_unselected_hunk()
    {
        var middle = string.Concat(Enumerable.Range(1, 24).Select(number => $"context {number}\n"));
        var baseline = "start\n" + middle + "old tail\n";
        var working = "new above\nanother above\nstart\n" + middle + "new tail\n";
        var repo = await CreateChangeAsync(baseline, working);
        var hunks = await Workspace.HunksAsync(repo, FileName, staged: false);
        Assert.Equal(2, hunks.Count);
        var later = Assert.Single(hunks, hunk => hunk.Lines.Any(line => line is { Kind: '+', Text: "new tail" }));

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, later.Id,
            [Select(later, '-', "old tail").Id, Select(later, '+', "new tail").Id]);

        Assert.Equal("start\n" + middle + "new tail\n", await ReadIndexAsync(repo));
        Assert.Equal(working, await _git.WorkingFileAsync(repo, FileName));
    }

    [Fact]
    public async Task Unborn_new_file_can_be_partially_staged_then_returned_to_untracked()
    {
        const string contents = "alpha\nbeta\nomega\n";
        var repo = await _git.CreateAsync();
        await _git.WriteAsync(repo, FileName, contents);
        var untracked = await SingleHunkAsync(repo, staged: false);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, untracked.Id, [Select(untracked, '+', "beta").Id]);

        Assert.Equal("beta\n", await ReadIndexAsync(repo));
        var partlyStaged = Assert.Single((await Workspace.ReadAsync(repo)).Changes);
        Assert.Equal(('A', 'M'), (partlyStaged.Index, partlyStaged.WorkTree));
        await Workspace.ExecuteAsync(repo, GitAction.Stage, FileName);
        var staged = await SingleHunkAsync(repo, staged: true);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: true, staged.Id, [Select(staged, '+', "beta").Id]);
        Assert.Equal("alpha\nomega\n", await ReadIndexAsync(repo));

        staged = await SingleHunkAsync(repo, staged: true);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: true, staged.Id, SelectedChanges(staged));

        Assert.Equal("", await _git.GitAsync(repo, "ls-files", "--", FileName));
        Assert.Equal('?', Assert.Single((await Workspace.ReadAsync(repo)).Changes).Index);
        Assert.Equal(contents, await _git.WorkingFileAsync(repo, FileName));
    }

    [Fact]
    public async Task Untracked_single_dash_is_diffed_and_staged_as_a_literal_path()
    {
        const string path = "-";
        const string contents = "leave unstaged\nselect this line\n";
        var repo = await _git.CreateAsync();
        await _git.WriteAsync(repo, path, contents);
        var hunk = Assert.Single(await Workspace.HunksAsync(repo, path, staged: false));

        await Workspace.ApplyLinesAsync(repo, path, staged: false, hunk.Id, [Select(hunk, '+', "select this line").Id]);

        Assert.Equal("select this line\n", await _git.GitAsync(repo, "show", ":-"));
        Assert.Equal(contents, await _git.WorkingFileAsync(repo, path));
        Assert.Contains("+leave unstaged", await Workspace.InspectAsync(repo, GitView.Diff, path: path));
    }

    [Fact]
    public async Task Deleted_work_tree_file_can_be_partly_staged_and_restored_to_index_only()
    {
        const string contents = "alpha\nbeta\nomega\n";
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, FileName, contents, "Base");
        var absolute = Path.Combine(_git.Root, repo, FileName);
        File.Delete(absolute);
        var deletion = await SingleHunkAsync(repo, staged: false);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, deletion.Id, [Select(deletion, '-', "beta").Id]);

        Assert.Equal("alpha\nomega\n", await ReadIndexAsync(repo));
        Assert.False(File.Exists(absolute));
        deletion = await SingleHunkAsync(repo, staged: false);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, deletion.Id, SelectedChanges(deletion));
        Assert.Equal("", await _git.GitAsync(repo, "ls-files", "--", FileName));
        var stagedDeletion = await SingleHunkAsync(repo, staged: true);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: true, stagedDeletion.Id,
            [Select(stagedDeletion, '-', "beta").Id]);

        Assert.Equal("beta\n", await ReadIndexAsync(repo));
        Assert.False(File.Exists(absolute));
        var change = Assert.Single((await Workspace.ReadAsync(repo)).Changes);
        Assert.Equal(('M', 'D'), (change.Index, change.WorkTree));
    }

    [Fact]
    public async Task Staging_all_removed_lines_of_a_replacement_keeps_tracked_empty_file()
    {
        const string replacement = "entirely different\n";
        var repo = await CreateChangeAsync("removed first\nremoved second\n", replacement);
        var hunk = await SingleHunkAsync(repo, staged: false);
        var removed = hunk.Lines.Where(line => line.Kind == '-').Select(line => line.Id).ToArray();

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, removed);

        Assert.Equal("", await ReadIndexAsync(repo));
        Assert.Equal(FileName, (await _git.GitAsync(repo, "ls-files", "--", FileName)).TrimEnd('\r', '\n'));
        Assert.Equal(replacement, await _git.WorkingFileAsync(repo, FileName));
        var change = Assert.Single((await Workspace.ReadAsync(repo)).Changes);
        Assert.Equal(('M', 'M'), (change.Index, change.WorkTree));
    }

    [Fact]
    public async Task Invalid_line_selection_is_rejected_without_mutating_index()
    {
        const string baseline = "context\nold\n";
        const string replacement = "context\nnew\n";
        var repo = await CreateChangeAsync(baseline, replacement);
        var hunk = await SingleHunkAsync(repo, staged: false);
        var context = Select(hunk, ' ', "context");
        var addition = Select(hunk, '+', "new");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, []));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, [context.Id]));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, [int.MaxValue]));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, [addition.Id, addition.Id]));

        Assert.Equal(baseline, await ReadIndexAsync(repo));
        Assert.Equal(replacement, await _git.WorkingFileAsync(repo, FileName));
    }

    [Fact]
    public async Task Stale_line_selection_is_rejected_after_work_tree_changes()
    {
        const string baseline = "before\n";
        var repo = await CreateChangeAsync(baseline, "stale edit\n");
        var stale = await SingleHunkAsync(repo, staged: false);
        await _git.WriteAsync(repo, FileName, "newer edit\n");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace.ApplyLinesAsync(repo, FileName, staged: false, stale.Id, [Select(stale, '+', "stale edit").Id]));

        Assert.Equal(baseline, await ReadIndexAsync(repo));
        Assert.Equal("newer edit\n", await _git.WorkingFileAsync(repo, FileName));
    }

    [Fact]
    public async Task Selected_lines_preserve_missing_terminal_newline_in_index_and_work_tree()
    {
        const string baseline = "header\nfirst\nmiddle\nlast";
        const string replacement = "header\nsecond\nmiddle\nfinal";
        var repo = await CreateChangeAsync(baseline, replacement);
        var hunk = await SingleHunkAsync(repo, staged: false);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id,
            [Select(hunk, '-', "first").Id, Select(hunk, '+', "second").Id]);

        Assert.Equal("header\nsecond\nmiddle\nlast", await ReadIndexAsync(repo));
        hunk = await SingleHunkAsync(repo, staged: false);
        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, SelectedChanges(hunk));
        Assert.Equal(replacement, await ReadIndexAsync(repo));
        Assert.Equal(replacement, await _git.WorkingFileAsync(repo, FileName));
        Assert.Empty(await Workspace.HunksAsync(repo, FileName, staged: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staging_adjacent_addition_terminates_kept_old_eof_line_only_in_index(bool useCrLf)
    {
        var lineEnding = useCrLf ? "\r\n" : "\n";
        var replacement = $"header{lineEnding}replacement";
        var repo = await CreateChangeAsync($"header{lineEnding}unterminated", replacement);
        var hunk = await SingleHunkAsync(repo, staged: false);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, [Select(hunk, '+', "replacement").Id]);

        Assert.Equal($"header{lineEnding}unterminated{lineEnding}replacement", await ReadIndexAsync(repo));
        Assert.Equal(replacement, await _git.WorkingFileAsync(repo, FileName));
    }

    [Fact]
    public async Task Partial_staging_preserves_crlf_and_unicode_content()
    {
        const string baseline = "über\r\nstare\r\nsol\r\n";
        const string replacement = "über\r\nnovo ž\r\nsol\r\n";
        var repo = await CreateChangeAsync(baseline, replacement);
        var hunk = await SingleHunkAsync(repo, staged: false);

        await Workspace.ApplyLinesAsync(repo, FileName, staged: false, hunk.Id, [Select(hunk, '+', "novo ž").Id]);

        Assert.Equal("über\r\nstare\r\nnovo ž\r\nsol\r\n", await ReadIndexAsync(repo));
        Assert.Equal(replacement, await _git.WorkingFileAsync(repo, FileName));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xff)]
    public async Task Diff_forced_to_text_with_uneditable_bytes_only_allows_whole_file_staging(int rawByte)
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, ".gitattributes", "*.txt diff\n", "Force text diff");
        await _git.CommitFileAsync(repo, FileName, "before\n", "Base");
        var absolute = Path.Combine(_git.Root, repo, FileName);
        byte[] original = [.. "new "u8, (byte)rawByte, (byte)'\n'];
        await File.WriteAllBytesAsync(absolute, original);
        var rawHash = (await _git.GitAsync(repo, "hash-object", "--no-filters", "--", FileName)).Trim();

        var diff = await Workspace.InspectAsync(repo, GitView.Diff, path: FileName);
        Assert.Contains("@@ ", diff);
        Assert.Contains(rawByte == 0 ? "\0" : "\uFFFD", diff);
        Assert.Empty(await Workspace.HunksAsync(repo, FileName, staged: false));

        await Workspace.ExecuteAsync(repo, GitAction.Stage, FileName);

        Assert.Equal(rawHash, (await _git.GitAsync(repo, "rev-parse", $":{FileName}")).Trim());
        Assert.Equal(original, await File.ReadAllBytesAsync(absolute));
        Assert.Empty(await Workspace.HunksAsync(repo, FileName, staged: true));
    }

    private async Task<string> CreateChangeAsync(string baseline, string replacement)
    {
        var repo = await _git.CreateAsync();
        await _git.CommitFileAsync(repo, FileName, baseline, "Base");
        await _git.WriteAsync(repo, FileName, replacement);
        return repo;
    }

    private async Task<PatchHunk> SingleHunkAsync(string repo, bool staged) =>
        Assert.Single(await Workspace.HunksAsync(repo, FileName, staged));

    private Task<string> ReadIndexAsync(string repo) => _git.GitAsync(repo, "show", $":{FileName}");

    private static PatchLine Select(PatchHunk hunk, char kind, string text) =>
        Assert.Single(hunk.Lines, line => line.Kind == kind && line.Text.TrimEnd('\r') == text);

    private static int[] SelectedChanges(PatchHunk hunk) =>
        hunk.Lines.Where(line => line.Kind is '+' or '-').Select(line => line.Id).ToArray();

    public void Dispose() => _git.Dispose();
}
