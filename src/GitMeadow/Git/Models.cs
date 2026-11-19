namespace GitMeadow.Git;

public sealed record RepositoryState(
    string Branch,
    IReadOnlyList<GitCommit> Commits,
    IReadOnlyList<GitChange> Changes,
    IReadOnlyList<GitRef> Refs,
    IReadOnlyList<GitRemote> Remotes,
    IReadOnlyList<GitStash> Stashes,
    string? Operation,
    bool CanAmend = true)
{
    internal bool IsHeadDetached { get; init; }
}

public sealed record GitCommit(string Hash, IReadOnlyList<string> Parents, string Author, DateTimeOffset Date, string Subject);
public sealed record GitChange(string Path, string? OriginalPath, char Index, char WorkTree);
public sealed record GitRef(string Name, string Hash, string Kind);
public sealed record GitRemote(string Name, string FetchUrl, string PushUrl);
public sealed record GitStash(string Name, string Subject, string Hash = "");
public sealed record CommitDetails(
    string Hash,
    string Author,
    string Email,
    DateTimeOffset Date,
    string Message,
    IReadOnlyList<string> Files,
    string Diff,
    bool DiffOmitted = false);

public sealed record PatchHunk(string Id, string Text, string Header = "")
{
    public IReadOnlyList<PatchLine> Lines { get; init; } = [];
}

public sealed record PatchLine(int Id, char Kind, string Text, int? OldNumber, int? NewNumber);

public enum GitView
{
    Diff, StagedDiff, File, FileHistory, Blame, Tree, Reflog, Worktrees, Submodules, Config, Patch, Conflicts, Notes,
    RebasePlan, StashPatch, Compare
}

public enum GitAction
{
    Stage, StageAll, Unstage, UnstageAll, Discard, Commit, Amend,
    Switch, Branch, DeleteBranch, Tag, DeleteTag, Merge, Rebase,
    CherryPick, Revert, ResetSoft, ResetMixed, ResetHard,
    Fetch, Pull, Push, AddRemote, RemoveRemote, SetRemote,
    Stash, ApplyStash, PopStash, DropStash, Continue, Abort,
    SetIdentity, AddWorktree, RemoveWorktree, UpdateSubmodules, Prune,
    AddNote, ApplyPatch, ResolveOurs, ResolveTheirs, BisectStart, BisectGood, BisectBad, BisectReset, WriteConflict,
    RebaseOnto, SquashLast, SetUpstream, TagAnnotated, RebasePlan, Skip, AddSubmodule
}

public sealed class GitCommandException(string message, int exitCode) : InvalidOperationException(message)
{
    public int ExitCode { get; } = exitCode;
}
