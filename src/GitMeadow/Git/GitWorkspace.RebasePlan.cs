using System.Text;

namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    private const int RebasePlanLimit = 150;

    private static async Task<string> PreviewRebasePlanAsync(Repository repo, string? reference)
    {
        var onto = await ResolveCommitAsync(repo, Ref(reference));
        var candidates = await ReadRebaseCandidatesAsync(repo, onto);
        return string.Join('\n', candidates.Select(commit => $"pick {commit.Hash} {commit.Subject}"));
    }

    private static async Task<string> RunRebasePlanAsync(Repository repo, string? reference, string? plan)
    {
        if (Operation(repo) is not null)
            throw new InvalidOperationException("Finish the operation already in progress before rebasing.");
        var onto = await ResolveCommitAsync(repo, Ref(reference));
        var candidates = await ReadRebaseCandidatesAsync(repo, onto);
        var instructions = ValidateRebasePlan(candidates, Required(plan, "Enter the commit plan."));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, instructions, new UTF8Encoding(false));
            var result = await GitProcess.RunAsync(repo.Path,
                ["rebase", "--interactive", "--no-autosquash", "--no-autostash", "--no-rebase-merges",
                    "--no-update-refs", "--reapply-cherry-picks", "--empty=keep", "--onto", onto, onto],
                sequencePlanPath: path);
            return result.RequireSuccess();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<IReadOnlyList<RebaseCandidate>> ReadRebaseCandidatesAsync(Repository repo, string onto)
    {
        var commits = await RunAsync(repo.Path,
            ["log", "-z", "--topo-order", "--reverse", $"-n{RebasePlanLimit + 1}",
                "--no-notes", "--format=%H%x00%P%x00%s", $"{onto}..HEAD", "--"]);
        var fields = NullFields(commits);
        if (fields.Length == 1)
            throw new InvalidOperationException("There are no commits to rebase onto the selected base.");
        if (fields.Length > RebasePlanLimit * 3 + 1)
            throw new InvalidOperationException($"Choose a range of {RebasePlanLimit} commits or fewer.");

        List<RebaseCandidate> candidates = [];
        for (var field = 0; field + 2 < fields.Length; field += 3)
        {
            if (fields[field + 1].Contains(' '))
                throw new InvalidOperationException("An interactive plan requires a linear range without merge commits.");
            candidates.Add(new RebaseCandidate(fields[field], fields[field + 2]));
        }
        return candidates;
    }

    private static string ValidateRebasePlan(IReadOnlyList<RebaseCandidate> candidates, string plan)
    {
        if (plan.Length > 1024 * 1024)
            throw new ArgumentException("The rebase plan is longer than 1 MiB.", nameof(plan));

        var available = candidates.Select(commit => commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder();
        var hasKeptCommit = false;
        foreach (var entry in Lines(plan))
        {
            var line = entry.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var fields = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !available.Remove(fields[1]))
                throw new ArgumentException("Include each commit from the generated plan exactly once by full hash.", nameof(plan));
            var action = fields[0] switch
            {
                // The regular commit editor is noninteractive. Pause at either action so the browser can amend.
                "reword" => "edit",
                "pick" or "edit" or "squash" or "fixup" or "drop" => fields[0],
                _ => throw new ArgumentException("Use pick, reword, edit, squash, fixup, or drop in each plan row.", nameof(plan))
            };
            if (action is "squash" or "fixup" && !hasKeptCommit)
                throw new ArgumentException("A squash or fixup needs a previous commit to combine with.", nameof(plan));
            hasKeptCommit |= action != "drop";
            result.Append(action).Append(' ').Append(fields[1].ToLowerInvariant()).Append('\n');
        }

        if (available.Count > 0)
            throw new ArgumentException("Include all commits from the generated plan. Use drop to remove a commit.", nameof(plan));
        return result.ToString();
    }

    private sealed record RebaseCandidate(string Hash, string Subject);
}
