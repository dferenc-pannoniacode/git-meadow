using System.Globalization;

namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    private static readonly string[] DiffOptions =
        ["--no-ext-diff", "--no-textconv", "--no-color", "--src-prefix=a/", "--dst-prefix=b/"];

    public Task<RepositoryState> ReadAsync(string repo, string? revision = null, int commitLimit = 300) =>
        WithRepositoryAsync(repo, async repository =>
        {
            var branch = RunAsync(repository.Path, ["branch", "--show-current"]);
            var commits = ReadCommitsAsync(repository, revision, commitLimit);
            var changes = ReadChangesAsync(repository);
            var refs = ReadRefsAsync(repository);
            var remotes = ReadRemotesAsync(repository);
            var stashes = ReadStashesAsync(repository);
            await Task.WhenAll(branch, commits, changes, refs, remotes, stashes);

            var head = (await branch).TrimEnd('\r', '\n');
            var operation = Operation(repository);
            return new RepositoryState(head.Length > 0 ? head : "(detached)",
                await commits, await changes, await refs, await remotes, await stashes, operation,
                await CanAmendAsync(repository, operation))
            {
                IsHeadDetached = head.Length == 0
            };
        });

    public Task<CommitDetails> CommitAsync(string repo, string hash) =>
        WithRepositoryAsync(repo, async repository =>
        {
            var commit = await ResolveCommitAsync(repository, hash);
            var header = RunAsync(repository.Path,
                ["show", "-s", "--no-notes", "--format=%H%x00%an%x00%ae%x00%aI%x00%B", commit, "--"]);
            var files = RunAsync(repository.Path,
                ["show", "--format=", "--diff-merges=first-parent", "--name-only", "-z", "--no-renames",
                    .. DiffOptions, commit, "--"]);
            var diff = ReadCommitPreviewAsync(repository, commit);
            await Task.WhenAll(header, files, diff);

            var fields = NullFields(await header);
            var preview = await diff;
            return new CommitDetails(fields[0], fields[1], fields[2], ParseDate(fields[3]),
                fields[4].TrimEnd('\n', '\r'), NullFields(await files).Where(path => path.Length > 0).ToArray(),
                preview.Diff, preview.Omitted);
        });

    public Task<string> InspectAsync(string repo, GitView view, string? reference = null, string? path = null) =>
        WithRepositoryAsync(repo, repository => view switch
        {
            GitView.Diff => ReadDiffAsync(repository, path, staged: false),
            GitView.StagedDiff => ReadDiffAsync(repository, path, staged: true),
            GitView.File => ReadFileAsync(repository, reference, path),
            GitView.FileHistory => ReadFileHistoryAsync(repository, reference, path),
            GitView.Blame => ReadBlameAsync(repository, reference, path),
            GitView.Tree => ReadTreeAsync(repository, reference),
            GitView.Reflog => RunAsync(repository.Path, ["reflog", "show", "--all", "-n", "100", "--date=iso-strict"]),
            GitView.Worktrees => RunAsync(repository.Path, ["worktree", "list", "--porcelain"]),
            GitView.Submodules => RunAsync(repository.Path, ["submodule", "status", "--recursive"]),
            GitView.Config => ReadIdentityAsync(repository),
            GitView.Patch => ReadPatchAsync(repository, reference),
            GitView.StashPatch => ReadStashPatchAsync(repository, reference),
            GitView.Compare => ReadComparisonAsync(repository, reference, path),
            GitView.Notes => ReadNoteAsync(repository, reference),
            GitView.RebasePlan => PreviewRebasePlanAsync(repository, reference),
            GitView.Conflicts when path is not null => ReadConflictTextAsync(repository, path),
            GitView.Conflicts => RunAsync(repository.Path, ["diff", "--name-only", "--diff-filter=U", .. DiffOptions, "--"]),
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        });

    private static async Task<IReadOnlyList<GitCommit>> ReadCommitsAsync(Repository repository, string? revision, int commitLimit)
    {
        // Without a commit-graph index, --topo-order can walk the entire history before honoring -n.
        var arguments = new List<string>
        {
            "log", "-n", Math.Clamp(commitLimit, 1, 5000).ToString(CultureInfo.InvariantCulture),
            "-z", "--no-notes", "--format=%H%x00%P%x00%an%x00%aI%x00%s"
        };
        if (!string.IsNullOrWhiteSpace(revision))
            arguments.Add(await ResolveCommitAsync(repository, revision));
        else
        {
            arguments.AddRange(["--branches", "--remotes", "--tags"]);
            if (await HasHeadAsync(repository))
                arguments.Add("HEAD");
        }
        arguments.Add("--");

        var fields = NullFields(await RunAsync(repository.Path, arguments));
        List<GitCommit> commits = [];
        for (var index = 0; index + 4 < fields.Length; index += 5)
        {
            commits.Add(new GitCommit(fields[index],
                fields[index + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                fields[index + 2], ParseDate(fields[index + 3]), fields[index + 4]));
        }
        return OrderLoadedCommits(commits);
    }

    private static IReadOnlyList<GitCommit> OrderLoadedCommits(List<GitCommit> commits)
    {
        var positions = new Dictionary<string, int>(commits.Count, StringComparer.Ordinal);
        for (var index = 0; index < commits.Count; index++)
            positions.Add(commits[index].Hash, index);

        // A ref can point directly to a parent whose timestamp is newer than its child's.
        // Keep Git's order wherever possible while placing visible children before their parents.
        var remainingChildren = new int[commits.Count];
        var needsSorting = false;
        for (var index = 0; index < commits.Count; index++)
        {
            foreach (var parent in commits[index].Parents)
            {
                if (!positions.TryGetValue(parent, out var parentIndex))
                    continue;

                remainingChildren[parentIndex]++;
                needsSorting |= parentIndex <= index;
            }
        }

        if (!needsSorting)
            return commits;

        var ready = new PriorityQueue<int, int>();
        for (var index = 0; index < commits.Count; index++)
        {
            if (remainingChildren[index] == 0)
                ready.Enqueue(index, index);
        }

        var ordered = new List<GitCommit>(commits.Count);
        while (ready.TryDequeue(out var index, out _))
        {
            var commit = commits[index];
            ordered.Add(commit);
            foreach (var parent in commit.Parents)
            {
                if (positions.TryGetValue(parent, out var parentIndex) && --remainingChildren[parentIndex] == 0)
                    ready.Enqueue(parentIndex, parentIndex);
            }
        }

        if (ordered.Count != commits.Count)
            throw new InvalidOperationException("Git returned a cycle in the loaded commit history.");

        return ordered;
    }

    private static async Task<IReadOnlyList<GitChange>> ReadChangesAsync(Repository repository)
    {
        var fields = NullFields(await RunAsync(repository.Path,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"]));
        List<GitChange> changes = [];
        for (var index = 0; index < fields.Length && fields[index].Length > 0; index++)
        {
            var line = fields[index];
            var from = line[0] is 'R' or 'C' || line[1] is 'R' or 'C' ? fields[++index] : null;
            changes.Add(new GitChange(line[3..], from, line[0], line[1]));
        }
        return changes;
    }

    private static async Task<IReadOnlyList<GitRef>> ReadRefsAsync(Repository repository)
    {
        var output = await RunAsync(repository.Path,
            ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(*objectname)",
                "refs/heads", "refs/remotes", "refs/tags"]);
        List<GitRef> refs = [];
        foreach (var line in Lines(output))
        {
            var fields = NullFields(line);
            var (name, kind) = fields[0] switch
            {
                var branch when branch.StartsWith("refs/heads/", StringComparison.Ordinal) => (branch[11..], "branch"),
                var remote when remote.StartsWith("refs/remotes/", StringComparison.Ordinal) => (remote[13..], "remote"),
                var tag when tag.StartsWith("refs/tags/", StringComparison.Ordinal) => (tag[10..], "tag"),
                _ => throw new InvalidOperationException("Git returned an unexpected reference namespace.")
            };
            refs.Add(new GitRef(name, fields[2].Length > 0 ? fields[2] : fields[1], kind)
            {
                RefObjectId = fields[1]
            });
        }
        return refs;
    }

    private static async Task<IReadOnlyList<GitRemote>> ReadRemotesAsync(Repository repository)
    {
        var output = await RunAsync(repository.Path, ["remote", "-v"]);
        var remotes = new Dictionary<string, GitRemote>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var separator = line.IndexOf('\t');
            if (separator < 0)
                continue;
            var name = line[..separator];
            var current = remotes.GetValueOrDefault(name, new GitRemote(name, "", ""));
            var description = line[(separator + 1)..];
            if (description.EndsWith(" (fetch)", StringComparison.Ordinal) ||
                description.EndsWith(']') && description.Contains(" (fetch) [", StringComparison.Ordinal))
            {
                // The verbose listing omits secondary fetch URLs and can annotate the primary URL.
                var urls = await ReadRemoteUrlsAsync(repository, name, push: false);
                current = current with
                {
                    FetchUrl = RedactUrl(urls.FirstOrDefault() ?? ""),
                    HasEmbeddedHttpCredentials = current.HasEmbeddedHttpCredentials || urls.Any(HasEmbeddedHttpCredentials)
                };
            }
            else if (description.EndsWith(" (push)", StringComparison.Ordinal))
            {
                var url = description[..^7];
                current = current with
                {
                    PushUrl = current.PushUrl.Length == 0 ? RedactUrl(url) : current.PushUrl,
                    HasEmbeddedHttpCredentials = current.HasEmbeddedHttpCredentials || HasEmbeddedHttpCredentials(url)
                };
            }
            remotes[name] = current;
        }
        return remotes.Values.ToArray();
    }

    internal static string RedactUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Host.Length > 0 &&
            (uri.Scheme != "ssh" || Uri.UnescapeDataString(uri.UserInfo).Contains(':')))
            return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString();
        return url;
    }

    private static async Task<IReadOnlyList<GitStash>> ReadStashesAsync(Repository repository)
    {
        var output = await RunAsync(repository.Path, ["stash", "list", "--format=%gd%x00%H%x00%gs"]);
        return Lines(output).Select(NullFields).Select(fields => new GitStash(fields[0], fields[2], fields[1])).ToArray();
    }

    private static async Task<string> ReadDiffAsync(Repository repository, string? path, bool staged)
    {
        var selected = path is null ? Array.Empty<string>() : [ValidateFilePath(repository, path)];
        var patch = await RunAsync(repository.Path,
            ["diff", .. staged ? new[] { "--cached" } : [], .. DiffOptions, "--", .. selected]);
        if (staged || path is null || patch.Length > 0)
            return patch;

        var untracked = await RunAsync(repository.Path,
            ["ls-files", "--others", "--exclude-standard", "-z", "--", .. selected]);
        if (untracked != selected[0] + "\0")
            return "";

        var noIndexPath = selected[0] == "-" ? "./-" : selected[0];
        var result = await TryRunAsync(repository.Path,
            ["diff", "--no-index", .. DiffOptions, "--", "/dev/null", noIndexPath]);
        if (result.ExitCode != 1)
            return result.RequireSuccess();
        if (noIndexPath != "./-")
            return result.Output;

        // The disambiguating prefix keeps Git from reading stdin, but git apply rejects it in a path.
        var hunkStart = result.Output.IndexOf("\n@@ ", StringComparison.Ordinal);
        var headerEnd = hunkStart < 0 ? result.Output.Length : hunkStart;
        return result.Output[..headerEnd].Replace("a/./-", "a/-", StringComparison.Ordinal)
            .Replace("b/./-", "b/-", StringComparison.Ordinal) + result.Output[headerEnd..];
    }

    private static async Task<string> ReadFileAsync(Repository repository, string? reference, string? path)
    {
        var selected = ValidateFilePath(repository, path);
        var commit = await ResolveCommitAsync(repository, reference ?? "HEAD");
        var blob = await RunAsync(repository.Path, ["cat-file", "blob", $"{commit}:{selected}"]);
        return DisplayFile(blob);
    }

    private static string DisplayFile(string content) => content.Contains('\0') ? "[Binary file]" : content;

    private static async Task<string> ReadFileHistoryAsync(Repository repository, string? reference, string? path)
    {
        var selected = ValidateFilePath(repository, path);
        if (reference is null && !await HasHeadAsync(repository))
            return "";
        var commit = await ResolveCommitAsync(repository, reference ?? "HEAD");
        return await RunAsync(repository.Path,
            ["log", "--follow", "--date=short", "--format=%h  %ad  %an  %s", "-n", "150", commit, "--", selected]);
    }

    private static async Task<string> ReadBlameAsync(Repository repository, string? reference, string? path)
    {
        var selected = ValidateFilePath(repository, path);
        var commit = await ResolveCommitAsync(repository, reference ?? "HEAD");
        return await RunAsync(repository.Path, ["blame", "--date=short", commit, "--", selected]);
    }

    private static async Task<string> ReadTreeAsync(Repository repository, string? reference)
    {
        if (reference is null && !await HasHeadAsync(repository))
            return "";
        var commit = await ResolveCommitAsync(repository, reference ?? "HEAD");
        return await RunAsync(repository.Path, ["ls-tree", "-rz", "--name-only", commit, "--"]);
    }

    private static async Task<string> ReadIdentityAsync(Repository repository)
    {
        var result = await TryRunAsync(repository.Path, ["config", "--get-regexp", "^user\\.(name|email)$"]);
        return result is { ExitCode: 1, Error.Length: 0 } ? "" : result.RequireSuccess();
    }

    private static async Task<string> ReadPatchAsync(Repository repository, string? reference)
    {
        var commit = await ResolveCommitAsync(repository, reference ?? "HEAD");
        return await ReadCommitPatchAsync(repository, commit, includeBinary: true);
    }

    private static async Task<string> ReadStashPatchAsync(Repository repository, string? reference)
    {
        var commit = await ResolveCommitAsync(repository, Ref(reference));
        return await RunAsync(repository.Path,
            ["stash", "show", "--patch", "--include-untracked", "--binary", .. DiffOptions, "--", commit]);
    }

    private static async Task<string> ReadComparisonAsync(Repository repository, string? from, string? to)
    {
        var source = await ResolveCommitAsync(repository, Ref(from));
        var target = await ResolveCommitAsync(repository, Ref(to));
        return await RunAsync(repository.Path, ["diff", .. DiffOptions, source, target, "--"]);
    }

    private static async Task<string> ReadNoteAsync(Repository repository, string? reference)
    {
        var commit = await ResolveCommitAsync(repository, reference ?? "HEAD");
        var result = await TryRunAsync(repository.Path, ["notes", "show", commit]);
        return result.ExitCode == 1 && result.Error.StartsWith("error: no note found", StringComparison.Ordinal)
            ? ""
            : result.RequireSuccess();
    }

    private static async Task<(string Diff, bool Omitted)> ReadCommitPreviewAsync(Repository repository, string commit)
    {
        try
        {
            return (await ReadCommitPatchAsync(repository, commit, includeBinary: false), false);
        }
        catch (GitOutputLimitException)
        {
            return ("", true);
        }
    }

    private static Task<string> ReadCommitPatchAsync(Repository repository, string commit, bool includeBinary) =>
        RunAsync(repository.Path,
            ["show", "--format=", "--diff-merges=first-parent", .. includeBinary ? new[] { "--binary" } : [],
                .. DiffOptions, commit, "--"]);

    private static async Task<string> ResolveCommitAsync(Repository repository, string reference) =>
        (await RunAsync(repository.Path,
            ["rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}"])).TrimEnd('\r', '\n');

    private static async Task<bool> HasHeadAsync(Repository repository) =>
        (await TryRunAsync(repository.Path, ["rev-parse", "--quiet", "--verify", "HEAD"])).Success;

    private static async Task<bool> CanAmendAsync(Repository repository, string? operation)
    {
        if (operation is null)
            return await HasHeadAsync(repository);
        if (operation != "rebase")
            return false;

        var marker = Path.Combine(repository.GitDirectory, "rebase-merge", "amend");
        if (!File.Exists(marker) || IsLink(marker))
            return false;
        var expectedHead = await ReadMetadataPointerAsync(marker);
        return expectedHead == await ResolveCommitAsync(repository, "HEAD");
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static string? Operation(Repository repository)
    {
        if (Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-merge")) ||
            Directory.Exists(Path.Combine(repository.GitDirectory, "rebase-apply")))
            return "rebase";
        if (File.Exists(Path.Combine(repository.GitDirectory, "MERGE_HEAD")))
            return "merge";
        if (File.Exists(Path.Combine(repository.GitDirectory, "CHERRY_PICK_HEAD")))
            return "cherry-pick";
        if (File.Exists(Path.Combine(repository.GitDirectory, "REVERT_HEAD")))
            return "revert";
        if (File.Exists(Path.Combine(repository.GitDirectory, "BISECT_START")))
            return "bisect";
        return null;
    }
}
