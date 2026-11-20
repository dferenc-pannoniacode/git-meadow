using System.Text.RegularExpressions;

namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    [GeneratedRegex(@"^(?:[A-Za-z0-9._-]+@)?[A-Za-z0-9._-]+:[^\\\s].*$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpAddress();

    private async Task<string> AddOrSetRemoteAsync(Repository repo, string name, string? url, bool add)
    {
        var remote = ValidateRemote(repo.Path, Required(url, "Enter a remote URL."));
        return await RunAsync(repo.Path, ["remote", add ? "add" : "set-url", "--", name, remote.Url]);
    }

    private async Task<string> AddSubmoduleAsync(Repository repo, string? address, string? path)
    {
        var target = ValidateFilePath(repo, path, allowFinalLink: false);
        RequireNewDestination(Path.Combine(repo.Path, target));
        var remote = ValidateRemote(repo.Path, Required(address, "Enter a submodule URL."));
        return await RunAsync(repo.Path, ["submodule", "add", "--", remote.Url, target],
            localTransport: remote.IsLocal);
    }

    private async Task<string> TransferAsync(Repository repo, GitAction action, string? remote, string? reference)
    {
        var branch = (await RunAsync(repo.Path, ["branch", "--show-current"])).TrimEnd('\r', '\n');
        var remotes = Lines(await RunAsync(repo.Path, ["remote"]));
        if (action == GitAction.Fetch && string.IsNullOrWhiteSpace(remote) && string.IsNullOrWhiteSpace(reference))
        {
            if (remotes.Length == 0)
                throw new InvalidOperationException("Add a remote before fetching.");
            var transport = await VerifyRemoteUrlsAsync(repo, remotes, push: false);
            return await RunAsync(repo.Path, ["fetch", "--all", "--prune", "--no-recurse-submodules"],
                localTransport: transport);
        }

        var tracked = branch.Length > 0 ? await ReadConfigValueAsync(repo, $"branch.{branch}.remote") : null;
        if (string.IsNullOrWhiteSpace(remote) && action == GitAction.Push)
        {
            remote = branch.Length > 0 ? await ReadConfigValueAsync(repo, $"branch.{branch}.pushRemote") : null;
            remote ??= await ReadConfigValueAsync(repo, "remote.pushDefault");
        }
        if (string.IsNullOrWhiteSpace(remote))
        {
            var automatic = remotes.Length == 1 ? remotes[0] : remotes.FirstOrDefault(name => name == "origin");
            remote = tracked ?? automatic;
        }
        if (remote is null || !remotes.Contains(remote, StringComparer.Ordinal))
            throw new InvalidOperationException("Choose a configured remote.");

        var allowLocal = await VerifyRemoteUrlsAsync(repo, [remote], action == GitAction.Push);
        var arguments = action switch
        {
            GitAction.Fetch => new List<string> { "fetch", "--prune", "--no-recurse-submodules", "--", remote },
            GitAction.Pull => new List<string> { "pull", "--ff-only", "--no-recurse-submodules", "--", remote },
            GitAction.Push => await PushArgumentsAsync(repo, remote, tracked, branch, reference),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (action != GitAction.Push && !string.IsNullOrWhiteSpace(reference))
            arguments.Add(Ref(reference));

        return await RunAsync(repo.Path, arguments, localTransport: allowLocal);
    }

    private static async Task<List<string>> PushArgumentsAsync(
        Repository repo, string remote, string? tracked, string currentBranch, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            if (currentBranch.Length == 0)
                throw new InvalidOperationException("Choose a local branch or tag to push.");
            var merge = tracked == remote ? await ReadConfigValueAsync(repo, $"branch.{currentBranch}.merge") : null;
            var source = $"refs/heads/{currentBranch}";
            var target = !string.IsNullOrWhiteSpace(merge) && merge.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? merge
                : source;
            await RunAsync(repo.Path, ["check-ref-format", target]);
            return await PushBranchArgumentsAsync(repo, remote, currentBranch, source, target);
        }

        var name = Ref(reference);
        const string branchPrefix = "refs/heads/";
        const string tagPrefix = "refs/tags/";
        if (!name.StartsWith(tagPrefix, StringComparison.Ordinal))
        {
            var branch = name.StartsWith(branchPrefix, StringComparison.Ordinal) ? name[branchPrefix.Length..] : name;
            if (await HasBranchAsync(repo, branch))
            {
                var branchRef = branchPrefix + branch;
                return await PushBranchArgumentsAsync(repo, remote, branch, branchRef, branchRef);
            }
        }

        if (!name.StartsWith(branchPrefix, StringComparison.Ordinal))
        {
            var tag = name.StartsWith(tagPrefix, StringComparison.Ordinal) ? name : tagPrefix + name;
            if ((await TryRunAsync(repo.Path, ["show-ref", "--quiet", "--verify", tag])).Success)
                return ["push", "--", remote, $"{tag}:{tag}"];
        }
        throw new InvalidOperationException("Choose an existing local branch or tag to push.");
    }

    private static async Task<List<string>> PushBranchArgumentsAsync(
        Repository repo, string remote, string branch, string source, string target)
    {
        var upstreamRemote = await ReadConfigValueAsync(repo, $"branch.{branch}.remote");
        var upstreamTarget = await ReadConfigValueAsync(repo, $"branch.{branch}.merge");
        var hasUpstream = !string.IsNullOrWhiteSpace(upstreamRemote) && !string.IsNullOrWhiteSpace(upstreamTarget);
        return ["push", .. hasUpstream ? Array.Empty<string>() : ["--set-upstream"], "--", remote, $"{source}:{target}"];
    }

    private async Task<bool> VerifyRemoteUrlsAsync(Repository repo, string[] remotes, bool push)
    {
        var isLocal = false;
        var direction = push ? "push" : "fetch";
        foreach (var remote in remotes)
        {
            foreach (var url in await ReadRemoteUrlsAsync(repo, remote, push))
                isLocal |= ValidateRemote(repo.Path, url, $"The {direction} URL for remote '{remote}'").IsLocal;
        }
        return isLocal;
    }

    private static async Task<string[]> ReadRemoteUrlsAsync(Repository repo, string remote, bool push) =>
        Lines(await RunAsync(repo.Path, ["remote", "get-url", "--all", .. push ? new[] { "--push" } : [], "--", remote]));

    private static async Task<string?> ReadConfigValueAsync(Repository repo, string key)
    {
        var value = await TryRunAsync(repo.Path, ["config", "--get", "--", key]);
        return value.ExitCode == 1 && value.Error.Length == 0 ? null : value.RequireSuccess().TrimEnd('\r', '\n');
    }

    private static bool HasEmbeddedHttpCredentials(Uri uri) =>
        uri.Scheme is "http" or "https" && uri.UserInfo.Length > 0;

    private static bool HasEmbeddedHttpCredentials(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && HasEmbeddedHttpCredentials(uri);

    private (string Url, bool IsLocal) ValidateRemote(string directory, string address, string urlDescription = "The remote URL")
    {
        if (address.Length > 2048 || address.Trim() != address || address.StartsWith('-') ||
            address.Any(char.IsControl))
            throw new ArgumentException("Enter a supported Git remote URL or a repository path inside the workspace.");

        if (Path.IsPathFullyQualified(address))
            return LocalRemote(directory, address);
        if (address.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                throw new ArgumentException("Enter a valid Git remote URL.");
            if (uri.Scheme == "file" && !uri.IsUnc && uri.UserInfo.Length == 0)
                return LocalRemote(directory, uri.LocalPath);
            if (HasEmbeddedHttpCredentials(uri))
                throw new ArgumentException($"{urlDescription} contains embedded HTTP(S) credentials. " +
                    "Remove them from the remote URL or its Git URL rewrite. For authentication, use an HTTPS remote and Git settings → HTTPS.");
            if (uri.Scheme is "http" or "https" or "ssh" && uri.Host.Length > 0 &&
                uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
                (uri.UserInfo.Length == 0 ||
                    uri.Scheme == "ssh" && !Uri.UnescapeDataString(uri.UserInfo).Contains(':')))
                return (address, false);
            throw new ArgumentException("Use an HTTP(S) or SSH URL without embedded credentials, or a local repository path.");
        }
        if (ScpAddress().IsMatch(address) && !address.Contains("::", StringComparison.Ordinal))
            return (address, false);
        return LocalRemote(directory, address);
    }

    private (string Url, bool IsLocal) LocalRemote(string directory, string address)
    {
        var target = NormalizePath(Path.GetFullPath(address, directory));
        if (OperatingSystem.IsWindows() && target[Path.GetPathRoot(target)!.Length..].Contains(':'))
            throw new ArgumentException("Choose a local directory path without an alternate data stream.", nameof(address));
        ValidateNoLinks(Root, target);
        if (!Directory.Exists(target))
            throw new ArgumentException("The local remote directory does not exist inside the workspace.");
        return (OperatingSystem.IsWindows() ? target.Replace('\\', '/') : target, true);
    }
}
