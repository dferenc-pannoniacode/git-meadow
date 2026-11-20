using System.Text.RegularExpressions;

namespace GitMeadow.Git;

internal sealed partial class GitCommandHistory
{
    internal const int Capacity = 200;
    private const int StreamLimit = 16 * 1024;
    private const int CommandLimit = 4096;
    private static readonly AsyncLocal<GitCommandHistory?> Active = new();
    private readonly Lock _gate = new();
    private readonly SortedDictionary<long, Entry> _entries = [];
    private long _nextId;

    internal static GitCommandHistory? Current => Active.Value;

    internal event Action? Changed;

    // The workspace keeps its shared repository locks. Each asynchronous UI operation supplies its own history.
    internal async Task CaptureAsync(Func<Task> operation)
    {
        var previous = Active.Value;
        Active.Value = this;
        try
        {
            await operation();
        }
        finally
        {
            Active.Value = previous;
        }
    }

    internal IReadOnlyList<Entry> Snapshot()
    {
        lock (_gate)
            return _entries.Values.Reverse().ToArray();
    }

    internal void Clear()
    {
        lock (_gate)
            _entries.Clear();
        Changed?.Invoke();
    }

    internal long Start(string directory, IReadOnlyList<string> arguments)
    {
        var configKey = CredentialConfigKeyIndex(arguments);
        var hideResponse = configKey >= 0 || arguments.Count > 0 &&
            (arguments[0] == "credential" || arguments[0].StartsWith("credential-", StringComparison.Ordinal));
        var hasRemoteUrls = arguments.Count > 0 &&
            arguments[0] is "remote" or "clone" or "fetch" or "pull" or "push" or "submodule";
        var command = "git " + string.Join(' ', arguments.Select((argument, index) =>
            configKey >= 0 && index > configKey ? "[hidden]" : QuoteArgument(RedactUrls(argument, hasRemoteUrls))));

        long id;
        lock (_gate)
        {
            id = ++_nextId;
            _entries.Add(id, new Entry(id, Limit(command, CommandLimit), directory, hideResponse, hasRemoteUrls));
            if (_entries.Count > Capacity)
                _entries.Remove(_entries.First().Key);
        }

        Changed?.Invoke();
        return id;
    }

    internal void Complete(long id, GitProcess.Result? result, string? failure = null)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry))
                return;
        }

        var completed = entry with
        {
            ExitCode = result?.ExitCode,
            Output = entry.OutputHidden ? "" : DisplayOutput(result?.Output, entry.HasRemoteUrls),
            Error = entry.OutputHidden ? "" : DisplayOutput(result?.Error, entry.HasRemoteUrls),
            Failure = failure is null ? null : entry.OutputHidden
                ? "Git could not complete the command."
                : DisplayOutput(failure, entry.HasRemoteUrls)
        };

        lock (_gate)
        {
            if (!_entries.ContainsKey(id))
                return;
            _entries[id] = completed;
        }

        Changed?.Invoke();
    }

    private static string DisplayOutput(string? output, bool hasRemoteUrls) => output is null ? "" :
        Limit(RedactUrls(output, hasRemoteUrls).Replace("\0", "\\0", StringComparison.Ordinal), StreamLimit);

    private static int CredentialConfigKeyIndex(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0] != "config")
            return -1;

        // Git Settings passes the config key immediately after the option separator.
        for (var index = 2; index < arguments.Count; index++)
        {
            if (arguments[index - 1] == "--")
                return arguments[index].StartsWith("credential.", StringComparison.OrdinalIgnoreCase) ? index : -1;
        }

        return -1;
    }

    private static string RedactUrls(string text, bool hasRemoteUrls) => hasRemoteUrls
        ? RemoteUrl().Replace(text, static match => GitWorkspace.RedactUrl(match.Value))
        : UrlCredentials().Replace(text, "$1[hidden]@");

    private static string Limit(string text, int limit)
    {
        if (text.Length <= limit)
            return text;

        var end = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..end] + "\n[truncated]";
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character =>
                char.IsAsciiLetterOrDigit(character) || "-_./:=@+%,".Contains(character)))
            return argument;

        return "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "'";
    }

    [GeneratedRegex(@"((?:https?|ssh)://)[^/?#\s\0]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s\0]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RemoteUrl();

    internal sealed record Entry(long Id, string Command, string Directory, bool OutputHidden, bool HasRemoteUrls,
        int? ExitCode = null, string Output = "", string Error = "", string? Failure = null)
    {
        internal bool Running => ExitCode is null && Failure is null;
    }
}
