using System.Text;
using GitMeadow.Components.Workbench;
using GitMeadow.Git;
using GitMeadow.Workbench;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GitMeadow.Components.Pages;

public partial class Home
{
    private const int CommitPage = 300;
    private const int MaximumCommits = 5000;
    private enum RepositoryForm { Open, Create, Clone }
    private sealed class AddRepositoryMenu(ElementReference anchor)
    {
        public ElementReference Anchor { get; } = anchor;
    }
    private sealed record CreateReferenceMenu(ElementReference Anchor, IReadOnlyList<Operation> Actions);
    private sealed record CommitMenu(string Hash, MouseEventArgs Pointer, IReadOnlyList<Operation> Actions);
    private sealed record RepositoryMenu(string Repository, MouseEventArgs Pointer);
    private sealed record ReferenceMenu(GitRef Reference, MouseEventArgs Pointer, IReadOnlyList<Operation> Actions);
    private sealed class OriginTagCheck(string repository)
    {
        public string Repository { get; } = repository;
        public bool Started { get; set; }
    }

    private IReadOnlyList<string> _repositories = [];
    private string? _repo;
    private string? _revision;
    private int _commitLimit = CommitPage;
    private RepositoryState? _state;
    private OriginTagCheck? _originTagCheck;
    private IReadOnlyDictionary<string, string>? _originTags;
    private CommitDetails? _details;
    private ViewTab _tab;
    private bool _busy = true;
    private bool _terminalOpen;
    private RepositorySelectionStorage? _repositorySelection;
    private bool _gitSettingsOpen;
    private string? _error;
    private string? _status;
    private string? _dialogError;
    private ElementReference _createRefButton;
    private AddRepositoryMenu? _addRepositoryMenu;
    private CreateReferenceMenu? _createReferenceMenu;
    private CommitMenu? _commitMenu;
    private RepositoryMenu? _repositoryMenu;
    private ReferenceMenu? _referenceMenu;
    private Operation? _operation;
    private string? _operationRepo;
    private string? _operationCommit;
    private RepositoryForm? _repoForm;
    private GitWorkspace.RepositoryFolder? _repositoryFolder;
    private ChangeSelection? _change;
    private string? _workingDiff;
    private string? _workingError;
    private IReadOnlyList<PatchHunk> _hunks = [];
    private string _fileRevision = "HEAD";
    private string? _file;
    private string? _fileOutput;
    private GitView _fileView = GitView.File;
    private IReadOnlyList<string> _files = [];
    private FileQuery? _outputQuery;
    private string? _output;
    private string? _compareFrom;

    private string RepositoryTitle => _repo is { } repo ? RepositoryDisplayName(repo) : "";
    private string RepositoryDisplayName(string repo) =>
        repo == "." ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Workspace.Root)) : repo;
    private string? RevisionTitle => _state?.Refs.FirstOrDefault(reference => reference.Qualified() == _revision)?.Name ?? _revision;
    private bool ModalOpen => _gitSettingsOpen || _repoForm is not null || _operation is not null || _outputQuery is not null || _compareFrom is not null;
    private string PatchDownload => $"data:text/x-diff;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(_output ?? ""))}";
    private bool OutputIsPatch => _outputQuery?.View is GitView.Patch or GitView.StashPatch or GitView.Compare;
    private string OutputTitle => _outputQuery?.View switch
    {
        GitView.Config => "Commit identity",
        GitView.Patch => "Commit patch",
        GitView.StashPatch => "Stash changes",
        GitView.Compare => "Revision comparison",
        _ => _outputQuery?.View.ToString() ?? ""
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            await CheckOriginTagsAsync();
            return;
        }

        var selection = new RepositorySelectionStorage(JS, Logger, Workspace.Root);
        _repositorySelection = selection;
        _busy = false;
        await WorkAsync(async () =>
        {
            _repositories = await Workspace.ListAsync();
            if (await selection.ReadAsync() is { } savedRepository)
            {
                try
                {
                    await ReadRepositoryAsync(savedRepository);
                    return;
                }
                catch (Exception ex) when (IsWorkspaceError(ex))
                {
                    Logger.LogDebug(ex, "The saved repository could not be reopened.");
                }
            }

            if (_repositories.Count > 0)
                await ReadRepositoryAsync(_repositories[0]);
        });
        StateHasChanged();
    }

    private void UpdateRepositoryState(RepositoryState state, bool refreshOriginTags = false)
    {
        var previousOrigin = GetTagStatusOrigin(_state);
        var origin = GetTagStatusOrigin(state);
        _state = state;
        if (refreshOriginTags || origin != previousOrigin)
        {
            _originTags = null;
            _originTagCheck = _repo is { } repo && origin is not null ? new OriginTagCheck(repo) : null;
        }
    }

    private static GitRemote? GetTagStatusOrigin(RepositoryState? state) =>
        state is not null && state.Refs.Any(reference => reference.Kind == "tag")
            ? state.Remotes.FirstOrDefault(remote => remote.Name == "origin")
            : null;

    private async Task CheckOriginTagsAsync()
    {
        if (_originTagCheck is not { Started: false } check)
            return;

        check.Started = true;
        IReadOnlyDictionary<string, string>? tags = null;
        try
        {
            await CommandHistory.CaptureAsync(async () => { tags = await Workspace.ReadOriginTagsAsync(check.Repository); });
        }
        catch (Exception ex) when (IsWorkspaceError(ex))
        {
            Logger.LogDebug("Could not check origin tags. Error type {ErrorType}.", ex.GetType().Name);
        }

        if (ReferenceEquals(_originTagCheck, check))
        {
            (_originTagCheck, _originTags) = (null, tags);
            StateHasChanged();
        }
    }

    private Task RefreshAsync() => WorkAsync(async () =>
    {
        _repositories = await Workspace.ListAsync();
        if (_repo is { } repo)
        {
            IncludeRepository(repo);
            await ReloadAsync(refreshOriginTags: true);
        }
    });

    private Task OpenRepositoryAsync(string repo) => WorkAsync(async () =>
    {
        CloseDialog();
        await SelectRepositoryAsync(repo);
    });

    private async Task SelectRepositoryAsync(string path)
    {
        var repo = await ReadRepositoryAsync(path);
        if (_repositorySelection is not null)
            await _repositorySelection.WriteAsync(repo);
    }

    private async Task<string> ReadRepositoryAsync(string path)
    {
        var repo = await Workspace.OpenAsync(path);
        var state = await Workspace.ReadAsync(repo);
        var details = state.Commits.FirstOrDefault() is { } first
            ? await Workspace.CommitAsync(repo, first.Hash)
            : null;
        (_repo, _details) = (repo, details);
        UpdateRepositoryState(state, refreshOriginTags: true);
        _revision = null;
        _commitLimit = CommitPage;
        _tab = ViewTab.History;
        _change = null;
        _file = null;
        _files = [];
        IncludeRepository(repo);
        return repo;
    }

    private void IncludeRepository(string repo)
    {
        if (!_repositories.Contains(repo, StringComparer.Ordinal))
            _repositories = _repositories.Append(repo).ToArray();
    }

    private Task BrowseAsync(string? revision) => WorkAsync(async () =>
    {
        if (_repo is null)
            return;

        var state = await Workspace.ReadAsync(_repo, revision);
        var details = state.Commits.FirstOrDefault() is { } first
            ? await Workspace.CommitAsync(_repo, first.Hash)
            : null;
        (_revision, _details, _tab) = (revision, details, ViewTab.History);
        UpdateRepositoryState(state);
        _commitLimit = CommitPage;
    });

    private Task LoadMoreAsync() => WorkAsync(async () =>
    {
        if (_repo is null)
            return;

        var next = Math.Min(_commitLimit + CommitPage, MaximumCommits);
        UpdateRepositoryState(await Workspace.ReadAsync(_repo, _revision, next));
        _commitLimit = next;
    });

    private Task SelectCommitAsync(string hash) => WorkAsync(async () =>
    {
        if (_repo is not null)
            _details = await Workspace.CommitAsync(_repo, hash);
    });

    private string? Active(ViewTab tab) => tab == _tab ? "page" : null;

    private Task SwitchTabAsync(ViewTab tab) => WorkAsync(async () =>
    {
        _tab = tab;
        if (tab == ViewTab.Files)
            await ReadFileAsync(new(GitView.Tree, _details?.Hash ?? "HEAD"));
        else if (tab == ViewTab.Changes && _change is null)
            await RefreshChangeAsync();
    });

    private Task SelectChangeAsync(ChangeSelection choice) => WorkAsync(() => ReadChangeAsync(choice));
    private Task LoadFileAsync(FileQuery query) => WorkAsync(async () =>
    {
        CloseDialog();
        _tab = ViewTab.Files;
        await ReadFileAsync(query);
    });

    private async Task ReadFileAsync(FileQuery query)
    {
        if (_repo is null || _state is null)
            return;

        var reference = query.Reference ?? _fileRevision;
        var path = query.Path;
        if (query.View == GitView.Tree || reference != _fileRevision)
        {
            var tree = _state.Commits.Count == 0 && reference == "HEAD"
                ? ""
                : await Workspace.InspectAsync(_repo, GitView.Tree, reference);
            _files = tree.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            _fileRevision = reference;
            _file = null;
            _fileOutput = null;
        }

        if (query.View != GitView.Tree && path is not null)
        {
            _fileOutput = await Workspace.InspectAsync(_repo, query.View, reference, path);
            (_file, _fileView) = (path, query.View);
        }
    }

    private async Task ReadChangeAsync(ChangeSelection choice)
    {
        if (_repo is null)
            return;

        var path = choice.Change.Path;
        var conflict = WorkingTree.IsConflict(choice.Change);
        var view = conflict ? GitView.Conflicts : choice.Staged ? GitView.StagedDiff : GitView.Diff;
        _change = choice;
        _workingDiff = null;
        _workingError = null;
        _hunks = [];
        try
        {
            _workingDiff = await Workspace.InspectAsync(_repo, view, path: path);
            if (!conflict)
                _hunks = await Workspace.HunksAsync(_repo, path, choice.Staged);
        }
        catch (Exception ex) when (IsWorkspaceError(ex))
        {
            _workingError = ex.Message;
        }
    }

    private async Task RefreshChangeAsync()
    {
        if (_state is null)
            return;

        var current = _change;
        if (current is not null)
        {
            var same = _state.Changes.FirstOrDefault(change => change.Path == current.Change.Path);
            if (same is not null)
            {
                if (WorkingTree.IsConflict(same) || WorkingTree.IsUnstaged(same) && !current.Staged || WorkingTree.IsStaged(same) && current.Staged)
                {
                    await ReadChangeAsync(new(same, current.Staged));
                    return;
                }
            }
        }

        var next = _state.Changes.FirstOrDefault(WorkingTree.IsConflict) ?? _state.Changes.FirstOrDefault(WorkingTree.IsUnstaged);
        if (next is not null)
            await ReadChangeAsync(new(next, false));
        else if (_state.Changes.FirstOrDefault(WorkingTree.IsStaged) is { } staged)
            await ReadChangeAsync(new(staged, true));
        else
            (_change, _workingDiff, _workingError, _hunks) = (null, null, null, []);
    }

    private async Task ReloadAsync(bool refreshOriginTags = false)
    {
        if (_repo is null)
            return;

        RepositoryState state;
        try
        {
            state = await Workspace.ReadAsync(_repo, _revision, _commitLimit);
        }
        catch (InvalidOperationException) when (_revision is not null)
        {
            _revision = null;
            state = await Workspace.ReadAsync(_repo, commitLimit: _commitLimit);
        }

        UpdateRepositoryState(state, refreshOriginTags);
        var hash = state.Commits.FirstOrDefault(commit => commit.Hash == _details?.Hash)?.Hash
            ?? state.Commits.FirstOrDefault()?.Hash;
        _details = hash is null ? null : await Workspace.CommitAsync(_repo, hash);

        if (_tab == ViewTab.Changes)
            await RefreshChangeAsync();
    }

    private async Task<bool> TryReloadAsync(bool refreshOriginTags = false)
    {
        try
        {
            await ReloadAsync(refreshOriginTags);
            return true;
        }
        catch (Exception ex) when (IsWorkspaceError(ex))
        {
            _error = $"Repository refresh failed. {ex.Message}";
            return false;
        }
    }

    private Task ApplyHunkAsync(HunkSelection hunk) => WorkAsync(async () =>
    {
        if (_repo is null)
            return;

        try
        {
            if (hunk.Lines is { } lines)
            {
                await Workspace.ApplyLinesAsync(_repo, hunk.Path, hunk.Staged, hunk.Id, lines);
                _status = hunk.Staged ? "Selected lines unstaged." : "Selected lines staged.";
            }
            else
            {
                await Workspace.ApplyHunkAsync(_repo, hunk.Path, hunk.Staged, hunk.Id);
                _status = hunk.Staged ? "Hunk unstaged." : "Hunk staged.";
            }
        }
        finally
        {
            await TryReloadAsync();
        }
    });

    private Task ShowOutputAsync(FileQuery query) => WorkAsync(() => ReadOutputAsync(query));

    private async Task ReadOutputAsync(FileQuery query)
    {
        if (_repo is null)
            return;

        var output = await Workspace.InspectAsync(_repo, query.View, query.Reference, query.Path);
        CloseDialog();
        (_outputQuery, _output) = (query, output);
    }

    private void ShowCompare(string reference)
    {
        CloseDialog();
        _compareFrom = reference;
    }

    private Task SubmitCompareAsync((string? Value, string? Extra) input) =>
        WorkAsync(() => ReadOutputAsync(new(GitView.Compare, _compareFrom, input.Value?.Trim())), dialog: true);

    private Task ShowRebasePlanAsync(string reference) => WorkAsync(async () =>
    {
        if (_repo is null)
            return;

        var plan = await Workspace.InspectAsync(_repo, GitView.RebasePlan, reference);
        ShowCommitOperation(new("Start interactive rebase", GitAction.RebasePlan, ExtraLabel: "Commit plan",
            Value: reference, Extra: plan, Destructive: true, ExtraOptional: false, ExtraMultiline: true), reference);
    });

    private void ShowPull() => ShowOperation(new("Pull", GitAction.Pull, "Remote (optional)", "Branch (optional)", ValueOptional: true));
    private void ShowPush() => ShowOperation(new("Push", GitAction.Push));
    private void ShowRepositoryMenu(ElementReference anchor)
    {
        if (_busy || ModalOpen)
            return;

        CloseContextMenus();
        _addRepositoryMenu = new(anchor);
    }

    private Task UseAddRepositoryMenuAsync(AddRepositoryMenu menu, RepositoryForm form) =>
        ReferenceEquals(_addRepositoryMenu, menu) ? ShowRepositoryBrowserAsync(form) : Task.CompletedTask;

    private void ShowCreateReferenceMenu(ElementReference anchor)
    {
        if (_busy || ModalOpen || _repo is null)
            return;

        CloseContextMenus();
        _createReferenceMenu = new(anchor,
        [
            .. NewRefActions(null),
            new("Create annotated tag at HEAD", GitAction.TagAnnotated, "Tag name", "Tag message", ExtraOptional: false)
        ]);
    }

    private void UseCreateReferenceMenu(CreateReferenceMenu menu, Operation operation)
    {
        if (ReferenceEquals(_createReferenceMenu, menu) && !_busy && !ModalOpen)
            ShowOperation(operation);
    }

    private static Operation[] NewRefActions(string? hash) =>
    [
        new("Create branch", GitAction.Branch, "Branch name", "Starting revision (optional)", Extra: hash),
        new("Create tag", GitAction.Tag, "Tag name", "Target revision (optional)", Extra: hash)
    ];

    private void ShowRefActions((string Repository, GitRef Reference, MouseEventArgs Pointer) request)
    {
        if (_busy || ModalOpen || request.Repository != _repo)
            return;

        var (_, reference, pointer) = request;
        var name = reference.Name;
        var qualified = reference.Qualified();
        List<Operation> actions =
        [
            new("Check out", GitAction.Switch, Value: qualified),
            new("Merge into current branch", GitAction.Merge, Value: qualified),
            new("Rebase current branch here", GitAction.Rebase, Value: qualified, Destructive: true)
        ];
        if (reference.Kind == "branch")
            actions.Add(new("Delete branch", GitAction.DeleteBranch, Value: name, Destructive: true));
        if (reference.Kind == "tag")
            actions.Add(new("Delete tag", GitAction.DeleteTag, Value: name, Destructive: true));
        if (reference.Kind == "remote")
            actions.Add(new("Set as upstream", GitAction.SetUpstream, Value: name));
        CloseContextMenus();
        _referenceMenu = new(reference, pointer, actions);
    }

    private void ShowReferenceOperation(ReferenceMenu menu, Operation operation)
    {
        if (ReferenceEquals(_referenceMenu, menu) && !_busy && !ModalOpen)
            ShowOperation(operation);
    }

    private void ShowRepositoryActions((string Repository, MouseEventArgs Pointer) request)
    {
        if (_busy || ModalOpen)
            return;

        CloseContextMenus();
        _repositoryMenu = new(request.Repository, request.Pointer);
    }

    private Task PromptRepositoryOperationAsync(RepositoryMenu menu, Operation operation) =>
        UseRepositoryMenuAsync(menu, () =>
        {
            ShowOperation(operation);
            return Task.CompletedTask;
        });

    private async Task UseRepositoryMenuAsync(RepositoryMenu menu, Func<Task> action)
    {
        if (!ReferenceEquals(_repositoryMenu, menu) || _busy || ModalOpen)
            return;

        _repositoryMenu = null;
        if (_repo != menu.Repository)
            await OpenRepositoryAsync(menu.Repository);

        if (_repo == menu.Repository)
            await action();
    }

    private void ShowCommitActions((string Hash, MouseEventArgs Pointer) request)
    {
        if (_busy || ModalOpen)
            return;

        var (hash, pointer) = request;
        CloseContextMenus();
        _commitMenu = new(hash, pointer,
        [
            .. NewRefActions(hash),
            new("Check out commit", GitAction.Switch, Value: hash),
            new("Cherry-pick", GitAction.CherryPick, Value: hash),
            new("Revert", GitAction.Revert, Value: hash),
            new("Merge", GitAction.Merge, Value: hash),
            new("Rebase here", GitAction.Rebase, Value: hash, Destructive: true),
            new("Rebase onto this commit", GitAction.RebaseOnto, ExtraLabel: "Old base", Value: hash, ExtraOptional: false, Destructive: true),
            new("Reset softly", GitAction.ResetSoft, Value: hash, Destructive: true),
            new("Reset index", GitAction.ResetMixed, Value: hash, Destructive: true),
            new("Hard reset", GitAction.ResetHard, Value: hash, Destructive: true),
            new("Add note", GitAction.AddNote, ExtraLabel: "Note", Value: hash, ExtraOptional: false, ExtraMultiline: true)
        ]);
    }

    private void ShowOperation(Operation operation)
    {
        CloseDialog();
        _operation = operation;
        _operationRepo = _repo;
    }

    private void ShowCommitOperation(Operation operation, string hash)
    {
        ShowOperation(operation);
        _operationCommit = hash;
    }

    private string? OperationDescription(Operation operation)
    {
        var target = _operationCommit is { } hash ? $"Target commit {hash[..Math.Min(12, hash.Length)]}." : null;
        return operation.Action switch
        {
            GitAction.RebasePlan => $"{target} Reorder the rows or choose pick, reword, edit, squash, fixup, or drop. Keep each commit on a row.".TrimStart(),
            GitAction.TrackLfs => "Enter a pattern such as *.zip. Stage .gitattributes with the matching files, and restage any matching files already in the index.",
            GitAction.PullLfs => "Download LFS contents for the current checkout and fill in files that still contain pointers.",
            _ => target
        };
    }

    private Task ShowRepositoryBrowserAsync(RepositoryForm form)
    {
        if (_busy || ModalOpen)
            return Task.CompletedTask;

        CloseDialog();
        _repoForm = form;
        return BrowseRepositoryFoldersAsync(form, ".");
    }

    private Task BrowseRepositoryFoldersAsync(RepositoryForm form, string path) => WorkAsync(() =>
    {
        if (_repoForm == form)
            _repositoryFolder = Workspace.BrowseRepositories(path);
        return Task.CompletedTask;
    }, dialog: true);

    private Task CreateRepositoryFolderAsync(RepositoryForm form, (string Path, string Name) input) => WorkAsync(async () =>
    {
        if (_repoForm != form || form == RepositoryForm.Open || _repositoryFolder?.Path != input.Path)
            return;

        var folder = await Workspace.CreateFolderAsync(input.Path, input.Name);
        _repositoryFolder = Workspace.BrowseRepositories(folder);
    }, dialog: true);

    private Task SubmitRepositoryAsync(RepositoryForm form, (string Path, string Remote) input) => WorkAsync(async () =>
    {
        if (_repoForm != form || _repositoryFolder is not { } folder || folder.Path != input.Path)
            return;
        if ((form == RepositoryForm.Open && !folder.HasGitMarker) || DestinationWarning(form, folder) is not null)
            return;

        string repo;
        try
        {
            repo = form switch
            {
                RepositoryForm.Create => await Workspace.CreateInFolderAsync(folder.Path),
                RepositoryForm.Clone => await Workspace.CloneInFolderAsync(input.Remote.Trim(), folder.Path),
                _ => folder.Path
            };
        }
        catch (GitWorkspace.RepositoryDestinationConflictException)
        {
            _repositoryFolder = Workspace.BrowseRepositories(folder.Path);
            throw;
        }

        _repositories = await Workspace.ListAsync();
        await SelectRepositoryAsync(repo);
        CloseDialog();
    }, dialog: true);

    private static string FormTitle(RepositoryForm form) => form switch
    {
        RepositoryForm.Open => "Open repository",
        RepositoryForm.Clone => "Clone repository",
        _ => "New repository"
    };

    private static string? DestinationWarning(RepositoryForm form, GitWorkspace.RepositoryFolder? folder) => (form, folder) switch
    {
        (RepositoryForm.Create or RepositoryForm.Clone, { ContainsRepository: true }) => GitWorkspace.ExistingRepositoryMessage,
        (RepositoryForm.Clone, { IsEmpty: false }) => GitWorkspace.CloneFolderNotEmptyMessage,
        _ => null
    };

    private void ShowGitSettings()
    {
        CloseDialog();
        _gitSettingsOpen = true;
    }

    private void CloseDialog()
    {
        (_repoForm, _operation, _outputQuery, _output, _dialogError) = (null, null, null, null, null);
        _repositoryFolder = null;
        _gitSettingsOpen = false;
        _operationRepo = null;
        _operationCommit = null;
        _compareFrom = null;
        CloseContextMenus();
    }

    private void CloseContextMenus()
    {
        _addRepositoryMenu = null;
        _createReferenceMenu = null;
        _commitMenu = null;
        _repositoryMenu = null;
        _referenceMenu = null;
    }

    private Task CreateSampleAsync() => WorkAsync(async () =>
    {
        var repo = await Sample.CreateAsync();
        _repositories = await Workspace.ListAsync();
        await SelectRepositoryAsync(repo);
    });

    private Task SubmitOperationAsync((string? Value, string? Extra) input) => _operation is { } operation
        ? ExecuteAsync(operation with { Value = input.Value, Extra = input.Extra }, dialog: true)
        : Task.CompletedTask;

    private Task RunAsync(Operation operation) => ExecuteAsync(operation, dialog: false);

    private Task ExecuteAsync(Operation operation, bool dialog) => WorkAsync(async () =>
    {
        if (_repo is null)
            return;

        if (dialog && _operationRepo != _repo)
            throw new InvalidOperationException("The selected repository changed. Open the operation again.");

        var refreshOriginTags = operation.Action is GitAction.Fetch or GitAction.Pull or GitAction.Push
            or GitAction.Tag or GitAction.TagAnnotated or GitAction.DeleteTag
            or GitAction.AddRemote or GitAction.SetRemote or GitAction.RemoveRemote;
        string result;
        try
        {
            result = await Workspace.ExecuteAsync(_repo, operation.Action, operation.Value, operation.Extra);
        }
        catch (Exception ex) when (IsWorkspaceError(ex))
        {
            if (await TryReloadAsync(refreshOriginTags) && _state?.Operation is not null && _state.Changes.Any(WorkingTree.IsConflict))
            {
                CloseDialog();
                _tab = ViewTab.Changes;
                await RefreshChangeAsync();
            }
            throw;
        }

        CloseDialog();
        _status = string.IsNullOrWhiteSpace(result) ? $"{operation.Title} completed." : result.Trim();
        if (operation.Action == GitAction.TrackLfs)
            _tab = ViewTab.Changes;
        if (await TryReloadAsync(refreshOriginTags) && operation.Action == GitAction.RebasePlan && _state?.Operation == "rebase")
        {
            _tab = ViewTab.Changes;
            await RefreshChangeAsync();
        }
        if (operation.Action is GitAction.AddWorktree or GitAction.RemoveWorktree)
            _repositories = await Workspace.ListAsync();
    }, dialog);

    private async Task WorkAsync(Func<Task> work, bool dialog = false)
    {
        if (_busy)
            return;

        _busy = true;
        CloseContextMenus();
        (_error, _status, _dialogError) = (null, null, null);
        try
        {
            await CommandHistory.CaptureAsync(work);
        }
        catch (Exception ex) when (IsWorkspaceError(ex))
        {
            if (dialog && (_repoForm is not null || _operation is not null || _compareFrom is not null))
                _dialogError = ex.Message;
            else
                _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private static bool IsWorkspaceError(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception;
}
