using GitMeadow.Git;

namespace GitMeadow.Workbench;

public sealed record Operation(
    string Title, GitAction Action, string? ValueLabel = null, string? ExtraLabel = null,
    string? Value = null, string? Extra = null, bool Destructive = false,
    bool ValueOptional = false, bool ExtraOptional = true, bool Multiline = false,
    bool ExtraMultiline = false);

public enum ViewTab { History, Changes, Files, Repository }

public sealed record FileQuery(GitView View, string? Reference = null, string? Path = null);
public sealed record ChangeSelection(GitChange Change, bool Staged);
public sealed record HunkSelection(string Path, bool Staged, string Id, IReadOnlyList<int>? Lines = null);

public static class ReferenceNames
{
    public static string Qualified(this GitRef reference) => reference.Kind switch
    {
        "branch" => $"refs/heads/{reference.Name}",
        "remote" => $"refs/remotes/{reference.Name}",
        "tag" => $"refs/tags/{reference.Name}",
        _ => reference.Name
    };
}
