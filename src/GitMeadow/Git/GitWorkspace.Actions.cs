using System.Globalization;
using System.Text;

namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    public Task<string> ExecuteAsync(string repo, GitAction action, string? value = null, string? extra = null) =>
        WithRepositoryAsync(repo, repository => ExecuteCoreAsync(repository, action, value, extra));

    private async Task<string> ExecuteCoreAsync(Repository repo, GitAction action, string? value, string? extra)
    {
        return action switch
        {
            GitAction.Stage => await RunAsync(repo.Path, ["add", "-A", "--", .. await ChangePathsAsync(repo, value, extra)]),
            GitAction.StageAll => await RunAsync(repo.Path, ["add", "-A", "--", "."]),
            GitAction.Unstage => await UnstageAsync(repo, await ChangePathsAsync(repo, value, extra)),
            GitAction.UnstageAll => await UnstageAsync(repo, ["."]),
            GitAction.Discard => await DiscardAsync(repo, value),
            GitAction.Commit => await RunAsync(repo.Path, ["commit", "-F", "-"], Required(value, "Enter a commit message.")),
            GitAction.Amend => await AmendAsync(repo, value),
            GitAction.Switch => await SwitchAsync(repo, Ref(value)),
            GitAction.Branch => await CreateRefAsync(repo, "branch", Ref(value), extra),
            GitAction.DeleteBranch => await RunAsync(repo.Path, ["branch", "-d", "--", Ref(value)]),
            GitAction.Tag => await CreateRefAsync(repo, "tag", Ref(value), extra),
            GitAction.TagAnnotated => await RunAsync(repo.Path,
                ["tag", "-a", "-m", Required(extra, "Enter a tag message."), "--", Ref(value)]),
            GitAction.DeleteTag => await RunAsync(repo.Path, ["tag", "-d", "--", Ref(value)]),
            GitAction.Merge => await RunAsync(repo.Path, ["merge", "--no-edit", "--", await ResolveCommitAsync(repo, Ref(value))]),
            GitAction.Rebase => await RunAsync(repo.Path, ["rebase", await ResolveCommitAsync(repo, Ref(value))]),
            GitAction.RebaseOnto => await RunAsync(repo.Path,
                ["rebase", "--onto", await ResolveCommitAsync(repo, Ref(value)), await ResolveCommitAsync(repo, Ref(extra))]),
            GitAction.RebasePlan => await RunRebasePlanAsync(repo, value, extra),
            GitAction.CherryPick => await RunAsync(repo.Path, ["cherry-pick", await ResolveCommitAsync(repo, Ref(value))]),
            GitAction.Revert => await RunAsync(repo.Path, ["revert", "--no-edit", await ResolveCommitAsync(repo, Ref(value))]),
            GitAction.ResetSoft => await ResetAsync(repo, "--soft", value),
            GitAction.ResetMixed => await ResetAsync(repo, "--mixed", value),
            GitAction.ResetHard => await ResetAsync(repo, "--hard", value),
            GitAction.Fetch or GitAction.Pull or GitAction.Push => await TransferAsync(repo, action, value, extra),
            GitAction.AddRemote => await AddOrSetRemoteAsync(repo, Ref(value), extra, add: true),
            GitAction.SetRemote => await AddOrSetRemoteAsync(repo, Ref(value), extra, add: false),
            GitAction.RemoveRemote => await RunAsync(repo.Path, ["remote", "remove", "--", Ref(value)]),
            GitAction.Stash => await RunAsync(repo.Path,
                ["stash", "push", "--include-untracked", .. string.IsNullOrWhiteSpace(value) ? [] : new[] { "-m", value }],
                literalPaths: false),
            GitAction.ApplyStash => await RunAsync(repo.Path,
                ["stash", "apply", "--index", "--", await VerifyStashAsync(repo, value, extra, immutable: true)],
                literalPaths: false),
            GitAction.PopStash => await RunAsync(repo.Path,
                ["stash", "pop", "--index", "--", await VerifyStashAsync(repo, value, extra, immutable: false)],
                literalPaths: false),
            GitAction.DropStash => await RunAsync(repo.Path,
                ["stash", "drop", "--", await VerifyStashAsync(repo, value, extra, immutable: false)]),
            GitAction.Continue => await FinishAsync(repo, "--continue"),
            GitAction.Abort => await FinishAsync(repo, "--abort"),
            GitAction.Skip => await FinishAsync(repo, "--skip"),
            GitAction.SetIdentity => await SetIdentityAsync(repo, value, extra),
            GitAction.AddWorktree => await AddWorktreeAsync(repo, value, extra),
            GitAction.RemoveWorktree => await RemoveWorktreeAsync(repo, value),
            GitAction.AddSubmodule => await AddSubmoduleAsync(repo, value, extra),
            GitAction.UpdateSubmodules => await RunAsync(repo.Path, ["submodule", "update", "--init", "--recursive", "--checkout"]),
            GitAction.Prune => await RunAsync(repo.Path, ["worktree", "prune"]),
            GitAction.AddNote => await RunAsync(repo.Path,
                ["notes", "add", "-f", "-m", Required(extra, "Enter a note."), await ResolveCommitAsync(repo, Ref(value))]),
            GitAction.ApplyPatch => await ApplyPatchAsync(repo, value),
            GitAction.ResolveOurs => await ResolveConflictAsync(repo, value, 2),
            GitAction.ResolveTheirs => await ResolveConflictAsync(repo, value, 3),
            GitAction.WriteConflict => await WriteConflictAsync(repo, value, extra),
            GitAction.BisectStart => await StartBisectAsync(repo, value, extra),
            GitAction.BisectGood => await MarkBisectAsync(repo, "good", value),
            GitAction.BisectBad => await MarkBisectAsync(repo, "bad", value),
            GitAction.BisectReset => await RunAsync(repo.Path, ["bisect", "reset"]),
            GitAction.SetUpstream => await RunAsync(repo.Path, ["branch", $"--set-upstream-to={Ref(value)}"]),
            GitAction.SquashLast => await SquashAsync(repo, value, extra),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private static string Ref(string? value)
    {
        var reference = Required(value, "Choose a revision or reference.");
        if (reference.Length > 1024 || reference.StartsWith('-') || reference.Any(char.IsControl))
            throw new ArgumentException("Enter a valid Git reference.", nameof(value));
        return reference;
    }

    private static string StashRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "stash@{0}";
        if (value.StartsWith("stash@{", StringComparison.Ordinal) && value.EndsWith('}') &&
            uint.TryParse(value.AsSpan(7, value.Length - 8), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return value;
        throw new ArgumentException("Choose a stash from the repository's stash list.", nameof(value));
    }

    private static async Task<string> VerifyStashAsync(Repository repo, string? value, string? expectedHash, bool immutable)
    {
        var reference = StashRef(value);
        if (string.IsNullOrWhiteSpace(expectedHash))
            return reference;

        var actual = (await ReadStashesAsync(repo)).FirstOrDefault(stash => stash.Name == reference);
        if (actual is null || actual.Hash != expectedHash)
            throw new InvalidOperationException("The stash changed or moved. Refresh the stash list before retrying.");
        return immutable ? actual.Hash : actual.Name;
    }

    private static async Task<string> AmendAsync(Repository repo, string? message)
    {
        if (!await CanAmendAsync(repo, Operation(repo)))
            throw new InvalidOperationException("There is no commit available to amend. Continue or finish any operation in progress first.");
        return string.IsNullOrWhiteSpace(message)
            ? await RunAsync(repo.Path, ["commit", "--amend", "--no-edit"])
            : await RunAsync(repo.Path, ["commit", "--amend", "-F", "-"], message);
    }

    private static async Task<string[]> ChangePathsAsync(Repository repo, string? value, string? extra)
    {
        var path = ValidateFilePath(repo, value);
        if (extra is null)
        {
            var changes = await ReadChangesAsync(repo);
            extra = changes.FirstOrDefault(change => change.Path == path)?.OriginalPath;
        }
        return extra is null ? [path] : [path, ValidateFilePath(repo, extra)];
    }

    private static async Task<string> UnstageAsync(Repository repo, string[] paths)
    {
        return await HasHeadAsync(repo)
            ? await RunAsync(repo.Path, ["restore", "--staged", "--", .. paths])
            : await RunAsync(repo.Path, ["rm", "--cached", "-rf", "--ignore-unmatch", "--", .. paths]);
    }

    private static async Task<string> DiscardAsync(Repository repo, string? value)
    {
        var path = ValidateFilePath(repo, value);
        if (Directory.Exists(Path.Combine(repo.Path, path)) && !IsLink(Path.Combine(repo.Path, path)))
            throw new ArgumentException("Choose a single file to discard.", nameof(value));

        var untracked = await RunAsync(repo.Path, ["ls-files", "--others", "--exclude-standard", "-z", "--", path]);
        return untracked == path + "\0"
            ? await RunAsync(repo.Path, ["clean", "-f", "--", path])
            : await RunAsync(repo.Path, ["restore", "--worktree", "--", path]);
    }

    private static async Task<string> SwitchAsync(Repository repo, string reference)
    {
        if (reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            return await RunAsync(repo.Path, ["switch", "--no-guess", "--", reference[11..]]);
        if (reference.StartsWith("refs/remotes/", StringComparison.Ordinal))
            return await RunAsync(repo.Path, ["switch", "--track", "--", reference]);

        if (!reference.StartsWith("refs/", StringComparison.Ordinal))
        {
            if (await HasBranchAsync(repo, reference))
                return await RunAsync(repo.Path, ["switch", "--no-guess", "--", reference]);
            var remote = $"refs/remotes/{reference}";
            if ((await TryRunAsync(repo.Path, ["show-ref", "--quiet", "--verify", remote])).Success)
                return await RunAsync(repo.Path, ["switch", "--track", "--", remote]);
        }

        return await RunAsync(repo.Path, ["switch", "--detach", await ResolveCommitAsync(repo, reference)]);
    }

    private static async Task<bool> HasBranchAsync(Repository repo, string branch) =>
        (await TryRunAsync(repo.Path, ["show-ref", "--quiet", "--verify", $"refs/heads/{branch}"])).Success;

    private static async Task<string> CreateRefAsync(Repository repo, string command, string name, string? start)
    {
        if (string.IsNullOrWhiteSpace(start))
            return await RunAsync(repo.Path, [command, "--", name]);
        return await RunAsync(repo.Path, [command, "--", name, await ResolveCommitAsync(repo, Ref(start))]);
    }

    private static async Task<string> ResetAsync(Repository repo, string mode, string? value) =>
        await RunAsync(repo.Path, ["reset", mode, await ResolveCommitAsync(repo, Ref(value))]);

    private static Task<string> FinishAsync(Repository repo, string option)
    {
        var operation = Operation(repo);
        return operation switch
        {
            "merge" when option != "--skip" => RunAsync(repo.Path, [operation, option]),
            "rebase" or "cherry-pick" or "revert" => RunAsync(repo.Path, [operation, option]),
            "bisect" when option == "--abort" => RunAsync(repo.Path, ["bisect", "reset"]),
            _ => throw new InvalidOperationException("There is no operation to continue or abort.")
        };
    }

    private static async Task<string> SetIdentityAsync(Repository repo, string? name, string? email)
    {
        var author = Required(name, "Enter a Git author name.");
        var address = Required(email, "Enter a Git author email.");
        if (author.Any(char.IsControl) || address.Any(char.IsWhiteSpace) || !address.Contains('@') ||
            address.IndexOfAny(['<', '>']) >= 0)
            throw new ArgumentException("Enter a valid author name and email.");

        await RunAsync(repo.Path, ["config", "--local", "--", "user.name", author]);
        return await RunAsync(repo.Path, ["config", "--local", "--", "user.email", address]);
    }

    private async Task<string> AddWorktreeAsync(Repository repo, string? name, string? reference)
    {
        var target = GetWorkspacePath(Required(name, "Choose a path for the new worktree."));
        RequireNewDestination(target);
        reference = string.IsNullOrWhiteSpace(reference) ? "HEAD" : Ref(reference);
        if (await HasBranchAsync(repo, reference))
            return await RunAsync(repo.Path, ["worktree", "add", "--", target, reference]);
        return await RunAsync(repo.Path, ["worktree", "add", "--detach", "--", target, await ResolveCommitAsync(repo, reference)]);
    }

    private async Task<string> RemoveWorktreeAsync(Repository repo, string? name)
    {
        var target = await GetRepositoryAsync(Required(name, "Choose a worktree path."));
        if (!PathComparer.Equals(repo.CommonDirectory, target.CommonDirectory) ||
            PathComparer.Equals(target.Path, repo.Path))
            throw new ArgumentException("Choose another worktree belonging to this repository.", nameof(name));
        return await RunAsync(repo.Path, ["worktree", "remove", "--", target.Path]);
    }

    private static async Task<string> ApplyPatchAsync(Repository repo, string? value)
    {
        var patch = Required(value, "Paste a unified Git patch.");
        if (patch.Length > 4 * 1024 * 1024)
            throw new ArgumentException("Patches must be smaller than 4 MiB.", nameof(value));

        // Apply textual patches without relaxing Git's built-in path validation.
        return await RunAsync(repo.Path, ["apply", "--whitespace=nowarn", "-"], patch, literalPaths: false);
    }

    private static async Task<string> ResolveConflictAsync(Repository repo, string? value, int side)
    {
        var path = ValidateFilePath(repo, value);
        var entries = await UnmergedAsync(repo, path);
        if (!entries.Any(entry => entry.AsSpan(0, entry.IndexOf('\t')).EndsWith(side == 2 ? " 2" : " 3")))
            return await RunAsync(repo.Path, ["rm", "-f", "--", path]);
        await RunAsync(repo.Path, ["checkout", side == 2 ? "--ours" : "--theirs", "--", path]);
        return await RunAsync(repo.Path, ["add", "--", path]);
    }

    private static async Task<string> WriteConflictAsync(Repository repo, string? value, string? contents)
    {
        var path = ValidateFilePath(repo, value, allowFinalLink: false);
        var encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
        if (contents is null || contents.Contains('\0') || encoding.GetByteCount(contents) > GitProcess.OutputLimit)
            throw new ArgumentException("Enter a text resolution under 4 MiB.", nameof(contents));

        await ReadConflictTextAsync(repo, path);

        var target = Path.Combine(repo.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, contents, encoding);
        return await RunAsync(repo.Path, ["add", "--", path]);
    }

    private static async Task<string[]> UnmergedAsync(Repository repo, string path)
    {
        var entries = NullFields(await RunAsync(repo.Path, ["ls-files", "--unmerged", "-z", "--", path]))
            .Where(entry => entry.Length > 0).ToArray();
        if (entries.Length == 0 || entries.Any(entry => entry[(entry.IndexOf('\t') + 1)..] != path))
            throw new InvalidOperationException("This file no longer has an unresolved merge conflict.");
        return entries;
    }

    private static async Task<string> StartBisectAsync(Repository repo, string? bad, string? good)
    {
        if (string.IsNullOrWhiteSpace(bad) && !string.IsNullOrWhiteSpace(good))
            bad = "HEAD";
        var arguments = new List<string> { "bisect", "start" };
        if (!string.IsNullOrWhiteSpace(bad))
            arguments.Add(await ResolveCommitAsync(repo, Ref(bad)));
        if (!string.IsNullOrWhiteSpace(good))
            arguments.Add(await ResolveCommitAsync(repo, Ref(good)));
        arguments.Add("--");
        return await RunAsync(repo.Path, arguments);
    }

    private static async Task<string> MarkBisectAsync(Repository repo, string label, string? reference)
    {
        if (Operation(repo) != "bisect")
            throw new InvalidOperationException("Start a bisect before marking commits.");
        return string.IsNullOrWhiteSpace(reference)
            ? await RunAsync(repo.Path, ["bisect", label])
            : await RunAsync(repo.Path, ["bisect", label, await ResolveCommitAsync(repo, Ref(reference))]);
    }

    private static async Task<string> SquashAsync(Repository repo, string? value, string? extra)
    {
        var message = Required(value, "Enter a message for the squashed commit.");
        if (!int.TryParse(extra ?? "2", NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count is < 2 or > 100)
            throw new ArgumentException("Choose between 2 and 100 commits to squash.", nameof(extra));
        if (Operation(repo) is not null || (await ReadChangesAsync(repo)).Count > 0)
            throw new InvalidOperationException("Squashing requires a clean working tree and no operation in progress.");

        var history = Lines(await RunAsync(repo.Path, ["rev-list", "--first-parent", "--parents", $"-{count}", "HEAD", "--"]));
        var parents = history.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (parents.Length != count || parents.Any(fields => fields.Length > 2))
            throw new InvalidOperationException("Choose an available range of commits without merges.");

        var oldHead = parents[0][0];
        var tree = (await RunAsync(repo.Path, ["rev-parse", $"{oldHead}^{{tree}}"])).Trim();
        var create = new List<string> { "commit-tree", tree, "-F", "-" };
        if (parents[^1].Length == 2)
            create.AddRange(["-p", parents[^1][1]]);
        var newHead = (await RunAsync(repo.Path, create, message)).Trim();
        await RunAsync(repo.Path, ["update-ref", "-m", $"squash: {count} commits", "HEAD", newHead, oldHead]);
        return newHead;
    }
}
