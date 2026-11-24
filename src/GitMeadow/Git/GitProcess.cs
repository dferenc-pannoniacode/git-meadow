using System.Diagnostics;
using System.Text;

namespace GitMeadow.Git;

internal static class GitProcess
{
    internal const int OutputLimit = 4 * 1024 * 1024;
    private const string LfsPrePushHook = """
        #!/bin/sh
        if ! command -v git-lfs >/dev/null 2>&1; then
            exit 0
        fi

        configured_lfs_url() {
            git config --includes --get -- "remote.$1.lfspushurl" ||
            git config --includes --get -- "remote.$1.lfsurl" ||
            if [ -f .lfsconfig ]; then
                git config --includes --file .lfsconfig --get -- "remote.$1.lfsurl"
            elif git cat-file -e :.lfsconfig 2>/dev/null; then
                git config --includes --blob :.lfsconfig --get -- "remote.$1.lfsurl"
            else
                git config --includes --blob HEAD:.lfsconfig --get -- "remote.$1.lfsurl" 2>/dev/null
            fi
        }

        # A shared tracking ref can advance after the first URL and hide objects needed by the next URL.
        lfs_remote=$1
        push_urls=$(git remote get-url --push --all -- "$1") || exit $?
        case "$push_urls" in
            *'
        '*)
                hook_dir=${0%/*}
                lfs_remote=${hook_dir##*/}
                ;;
        esac

        export GIT_CONFIG_COUNT=2
        export GIT_CONFIG_KEY_0=lfs.allowincompletepush GIT_CONFIG_VALUE_0=false
        export GIT_CONFIG_KEY_1="remote.$lfs_remote.pushurl" GIT_CONFIG_VALUE_1="$2"
        if [ "$lfs_remote" != "$1" ] && lfs_url=$(configured_lfs_url "$1"); then
            export GIT_CONFIG_COUNT=3
            export GIT_CONFIG_KEY_2="remote.$lfs_remote.lfspushurl" GIT_CONFIG_VALUE_2="$lfs_url"
        fi
        exec git-lfs pre-push "$lfs_remote" "$2"
        """;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly string[] RepositoryOverrides =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_NAMESPACE",
        "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG",
        "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_EXTERNAL_DIFF", "GIT_TEMPLATE_DIR",
        "GIT_SSH", "GIT_GLOB_PATHSPECS", "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS"
    ];

    public static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    public static Task<Result> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        string? input = null,
        bool localTransport = false,
        bool literalPaths = true,
        string? sequencePlanPath = null,
        bool strictOutput = false,
        string? settingsHome = null) =>
        RunCoreAsync(directory, arguments, input, localTransport, literalPaths, sequencePlanPath, strictOutput,
            settingsHome, hooksPath: null);

    public static async Task<Result> RunPushAsync(string directory, IReadOnlyList<string> arguments, bool localTransport)
    {
        // Git supplies the negotiated ref updates to this app-owned hook. Repository hooks stay disabled.
        var hooksDirectory = Directory.CreateTempSubdirectory("git-meadow-lfs-");
        var hookPath = Path.Combine(hooksDirectory.FullName, "pre-push");
        try
        {
            await File.WriteAllTextAsync(hookPath, LfsPrePushHook.ReplaceLineEndings("\n") + "\n", Utf8);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            return await RunCoreAsync(directory, arguments, input: null, localTransport: localTransport, literalPaths: true,
                sequencePlanPath: null, strictOutput: false, settingsHome: null, hooksPath: hooksDirectory.FullName);
        }
        finally
        {
            try
            {
                File.Delete(hookPath);
                hooksDirectory.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not remove the temporary Git LFS hook: {0}", exception.Message);
            }
        }
    }

    private static async Task<Result> RunCoreAsync(string directory, IReadOnlyList<string> arguments, string? input,
        bool localTransport, bool literalPaths, string? sequencePlanPath, bool strictOutput, string? settingsHome,
        string? hooksPath)
    {
        var start = CreateStartInfo("git", directory);
        foreach (var key in RepositoryOverrides)
            start.Environment.Remove(key);

        var liveHome = GitSettings.TryResolveHome();
        if (settingsHome is not null && !string.Equals(settingsHome, liveHome, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            start.Environment["HOME"] = settingsHome;
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(settingsHome, ".config");
            start.Environment.Remove("GIT_CONFIG_GLOBAL");
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_ASKPASS"] = "";
        start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        if (sequencePlanPath is not null)
        {
            start.Environment["GIT_MEADOW_SEQUENCE_PLAN"] = OperatingSystem.IsWindows()
                ? sequencePlanPath.Replace('\\', '/')
                : sequencePlanPath;
            // Git supplies the todo filename as the next argument. The file path and plan never become shell syntax.
            start.Environment["GIT_SEQUENCE_EDITOR"] = "cp -- \"$GIT_MEADOW_SEQUENCE_PLAN\"";
        }
        start.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        start.Environment["GIT_ALLOW_PROTOCOL"] = localTransport ? "http:https:ssh:file" : "http:https:ssh";
        start.Environment["GIT_LITERAL_PATHSPECS"] = literalPaths ? "1" : "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        if (arguments is ["lfs", "track", ..])
            start.Environment["GIT_LFS_TRACK_NO_INSTALL_HOOKS"] = "1";

        var hooksLocation = hooksPath ?? NullDevice;
        if (OperatingSystem.IsWindows())
            hooksLocation = hooksLocation.Replace('\\', '/');
        foreach (var argument in new[]
                 {
                     "--no-pager", "-c", "color.ui=false", "-c", "core.quotePath=false",
                     "-c", "i18n.logOutputEncoding=UTF-8", "-c", $"safe.directory={directory.Replace('\\', '/')}",
                     "-c", $"core.hooksPath={hooksLocation}", "-c", "core.fsmonitor=false",
                     "-c", "commit.gpgSign=false", "-c", "tag.gpgSign=false", "-c", "gc.auto=0",
                     "-c", "maintenance.auto=false", "-c", "diff.external=",
                     "-c", "fetch.recurseSubmodules=false", "-c", "push.recurseSubmodules=no",
                     "-c", "submodule.recurse=false"
                 })
            start.ArgumentList.Add(argument);

        // Prevent a repository alias from running if the Git LFS executable is absent.
        if (arguments is ["lfs", ..])
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("alias.lfs=");
        }

        var home = settingsHome ?? liveHome;
        if (home is not null && GitSettings.ExistingCredentialConfigPath(home) is { } credentialConfig)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"include.path={credentialConfig}");
        }

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        var history = GitCommandHistory.Current;
        var entry = history?.Start(directory, arguments);
        Result result;
        try
        {
            var lfsTransfer = hooksPath is not null || arguments is ["lfs", "pull", ..];
            var timeout = TimeSpan.FromMinutes(lfsTransfer ? 30 : 3);
            var minutes = lfsTransfer ? "30" : "three";
            result = await RunProcessAsync(start, input, strictOutput, timeout,
                $"Git did not finish within {minutes} minutes. Refresh the repository before retrying.");
        }
        catch (Exception exception)
        {
            if (entry is { } failed)
                history!.Complete(failed, null, exception.Message);
            throw;
        }

        if (entry is { } completed)
            history!.Complete(completed, result);
        return result;
    }

    public static Task<Result> RunSshKeygenAsync(string directory, IReadOnlyList<string> arguments, string? input = null)
    {
        var start = CreateStartInfo("ssh-keygen", directory);
        start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        return RunProcessAsync(start, input, strictOutput: false, TimeSpan.FromSeconds(15), "SSH key setup timed out. Try again.");
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string directory) => new()
    {
        FileName = executable,
        WorkingDirectory = directory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        StandardOutputEncoding = Utf8,
        StandardErrorEncoding = Utf8,
        StandardInputEncoding = Utf8,
        CreateNoWindow = true
    };

    private static async Task<Result> RunProcessAsync(ProcessStartInfo start, string? input, bool strictOutput,
        TimeSpan timeLimit, string timeoutMessage)
    {
        using var timeout = new CancellationTokenSource(timeLimit);
        var cancellationToken = timeout.Token;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start.");
        using var stdout = process.StandardOutput;
        using var stderr = process.StandardError;
        try
        {
            var output = ReadUtf8Async(stdout.BaseStream, strictOutput, cancellationToken);
            var errors = ReadUtf8Async(stderr.BaseStream, strict: false, cancellationToken);
            var write = WriteAsync(process.StandardInput, input, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(write, output, errors);
            return new Result(process.ExitCode, await output, await errors);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw new TimeoutException(timeoutMessage);
        }
    }

    private static async Task WriteAsync(StreamWriter writer, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
                await writer.WriteAsync(input.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // Git can close stdin when it rejects the command. Its exit status and stderr describe the failure.
        }
        finally
        {
            writer.BaseStream.Close();
        }
    }

    internal static async Task<string> ReadUtf8Async(Stream stream, bool strict, CancellationToken cancellationToken = default)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        var exceeded = false;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var available = Math.Min(read, OutputLimit - (int)result.Length);
            exceeded |= read > available;
            if (available > 0)
                result.Write(buffer, 0, available);
        }

        if (exceeded)
            throw new GitOutputLimitException();
        return (strict ? StrictUtf8 : Utf8).GetString(result.GetBuffer(), 0, (int)result.Length);
    }

    internal readonly record struct Result(int ExitCode, string Output, string Error)
    {
        public bool Success => ExitCode == 0;

        public string RequireSuccess()
        {
            if (Success)
                return Output;
            var message = string.Join(Environment.NewLine, new[] { Error.Trim(), Output.Trim() }
                .Where(part => part.Length > 0));
            throw new GitCommandException(message.Length == 0 ? $"Git exited with code {ExitCode}." : message, ExitCode);
        }
    }
}

internal sealed class GitOutputLimitException()
    : InvalidOperationException("Git output exceeded the 4 MiB display limit. Narrow the selection and retry.");
