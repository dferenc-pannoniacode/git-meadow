using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace GitMeadow.Git;

internal sealed class GitSettings(string home)
{
    private const string CredentialConfigName = ".git-meadow-credential-config";
    private readonly string _home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string SshDirectory => Path.Combine(_home, ".ssh");
    private string SshKeyPath => Path.Combine(SshDirectory, "id_ed25519");
    private string CredentialPath => Path.Combine(_home, ".git-meadow-credentials");

    internal static string ResolveHome() => TryResolveHome()
        ?? throw new InvalidOperationException("The Git user's home directory could not be found.");

    internal static string? TryResolveHome()
    {
        var configured = Environment.GetEnvironmentVariable("HOME");
        var directory = string.IsNullOrEmpty(configured)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : configured;
        return Path.IsPathFullyQualified(directory) ? Path.TrimEndingDirectorySeparator(directory) : null;
    }

    internal static string? ExistingCredentialConfigPath(string home)
    {
        var path = Path.Combine(home, CredentialConfigName);
        return File.Exists(path) ? path : null;
    }

    public async Task<GitIdentity> GetIdentityAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var name = await ReadGlobalValueAsync("user.name");
            var email = await ReadGlobalValueAsync("user.email");
            return new GitIdentity(name, email);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveIdentityAsync(string name, string email)
    {
        name = ValidateField(name, 256, "Enter a valid Git name.").Trim();
        email = ValidateField(email, 320, "Enter a valid Git email address.").Trim();
        if (!new EmailAddressAttribute().IsValid(email) || email.Any(char.IsWhiteSpace) || email.IndexOfAny(['<', '>']) >= 0)
            throw new ArgumentException("Enter a valid Git email address.");

        await _gate.WaitAsync();
        try
        {
            EnsureSuccess(await RunGitAsync(["config", "--global", "--replace-all", "--", "user.name", name]),
                "Git could not save the default name. Check that the Git configuration is writable.");
            EnsureSuccess(await RunGitAsync(["config", "--global", "--replace-all", "--", "user.email", email]),
                "Git could not save the default email. Check that the Git configuration is writable.");
            if (await ReadGlobalValueAsync("user.name") != name || await ReadGlobalValueAsync("user.email") != email)
                throw new InvalidOperationException("An included Git configuration overrides this identity. Update it before saving here.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetSshPublicKeyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await ReadSshPublicKeyAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GenerateSshKeyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var existing = await ReadSshPublicKeyAsync();
            if (existing is not null)
                return existing;

            var key = SshKeyPath;
            var publicKey = key + ".pub";
            if (File.Exists(key) || File.Exists(publicKey))
                throw new InvalidOperationException("The Ed25519 key files already exist. Restore the matching public key to show it here.");

            CreatePrivateDirectory(SshDirectory);
            var temporaryDirectory = Path.Combine(SshDirectory, $"git-meadow-{Guid.NewGuid():N}");
            CreatePrivateDirectory(temporaryDirectory);
            var temporaryKey = Path.Combine(temporaryDirectory, "id_ed25519");
            try
            {
                EnsureSuccess(await RunSshKeygenAsync(["-q", "-t", "ed25519", "-N", "", "-C", "Git Meadow", "-f", temporaryKey]),
                    "OpenSSH could not generate an Ed25519 key.");
                var generated = await ReadEd25519PublicKeyAsync(temporaryKey + ".pub")
                    ?? throw new InvalidOperationException("OpenSSH did not produce a valid Ed25519 public key.");

                File.Move(temporaryKey + ".pub", publicKey);
                try
                {
                    File.Move(temporaryKey, key);
                }
                catch
                {
                    File.Delete(publicKey);
                    throw;
                }
                return generated;
            }
            finally
            {
                File.Delete(temporaryKey);
                File.Delete(temporaryKey + ".pub");
                Directory.Delete(temporaryDirectory);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSshHostKeyAsync(string entry)
    {
        const string message = "Enter one verified known_hosts line with a host, key type, and public key.";
        entry = entry.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(entry) || entry.Length > 16 * 1024 || entry.Any(c => char.IsControl(c) && c != '\t'))
            throw new ArgumentException(message);

        var fields = SplitSshLine(entry);
        if (fields.Length < 3 || !IsLiteralSshHost(fields[0]))
            throw new ArgumentException(message);

        var hostKey = string.Join(' ', fields.Take(3));
        if (!(await RunSshKeygenAsync(["-l", "-f", "-"], hostKey + "\n")).Success)
            throw new ArgumentException(message);

        await _gate.WaitAsync();
        try
        {
            CreatePrivateDirectory(SshDirectory);
            var path = Path.Combine(SshDirectory, "known_hosts");
            if (File.Exists(path))
            {
                await foreach (var line in File.ReadLinesAsync(path))
                {
                    var existing = SplitSshLine(line);
                    if (existing.Length >= 3 && existing.Take(3).SequenceEqual(fields.Take(3), StringComparer.Ordinal))
                        return;
                }
            }

            // A manually created file may lack a final newline.
            await File.AppendAllTextAsync(path, $"\n{hostKey}\n");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveHttpsCredentialAsync(string origin, string username, string token)
    {
        var server = ParseHttpsOrigin(origin);
        username = ValidateField(username, 256, "Enter a valid HTTPS username.").Trim();
        token = ValidateField(token, 4096, "Enter a valid HTTPS access token.");

        await _gate.WaitAsync();
        try
        {
            foreach (var host in server.Hosts)
                await RunCredentialStoreAsync("store", $"protocol=https\nhost={host}\nusername={username}\npassword={token}\n\n");
            await ConfigureCredentialHelperAsync(server.Origin, username);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveHttpsCredentialAsync(string origin, string username)
    {
        var server = ParseHttpsOrigin(origin);
        username = ValidateField(username, 256, "Enter a valid HTTPS username.").Trim();

        await _gate.WaitAsync();
        try
        {
            foreach (var host in server.Hosts)
                await RunCredentialStoreAsync("erase", $"protocol=https\nhost={host}\nusername={username}\n\n");
            var config = ExistingCredentialConfigPath(_home);
            if (config is null)
                return;

            var section = $"credential.{server.Origin}";
            var helpers = await RunGitAsync(["config", "--file", config, "--get-all", "--", $"{section}.helper"]);
            if (helpers.ExitCode == 1 && helpers.Error.Length == 0)
                return;
            EnsureSuccess(helpers, "Git could not read its saved HTTPS authentication settings.");

            const string usernamePrefix = "username=";
            foreach (var host in server.Hosts)
            {
                var remaining = await RunCredentialStoreAsync("get", $"protocol=https\nhost={host}\n\n");
                var end = remaining.Output.IndexOf('\n');
                if (remaining.Output.StartsWith(usernamePrefix, StringComparison.Ordinal) && end > usernamePrefix.Length)
                {
                    await ConfigureCredentialHelperAsync(server.Origin, remaining.Output[usernamePrefix.Length..end].TrimEnd('\r'));
                    return;
                }
            }

            EnsureSuccess(await RunGitAsync(["config", "--file", config, "--remove-section", "--", section]),
                "Git could not remove HTTPS authentication for this origin.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ReadGlobalValueAsync(string key)
    {
        var result = await RunGitAsync(["config", "--global", "--includes", "--get", "--", key]);
        if (result.ExitCode == 1 && result.Error.Length == 0)
            return "";

        EnsureSuccess(result, "Git could not read the global configuration.");
        return result.Output.TrimEnd('\r', '\n');
    }

    private async Task ConfigureCredentialHelperAsync(string origin, string username)
    {
        const string message = "Git could not configure HTTPS authentication. Check that the Git home is writable and try saving again.";
        var config = Path.Combine(_home, CredentialConfigName);
        var section = $"credential.{origin}";
        var key = $"{section}.helper";
        var path = OperatingSystem.IsWindows() ? CredentialPath.Replace('\\', '/') : CredentialPath;
        var quotedPath = $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";
        // Keep explicitly saved tokens until the user replaces or removes them in Git settings.
        var helper = $"!f() {{ if test \"$1\" = get; then git credential-store --file {quotedPath} get; fi; }}; f";
        // Publish the empty helper and store as a pair so a concurrent Git operation sees complete settings.
        var temporaryConfig = $"{config}.{Guid.NewGuid():N}";
        try
        {
            if (File.Exists(config))
            {
                File.Copy(config, temporaryConfig);
                var existing = await RunGitAsync(["config", "--file", temporaryConfig, "--get-all", "--", key]);
                if (existing.Success)
                    EnsureSuccess(await RunGitAsync(["config", "--file", temporaryConfig, "--remove-section", "--", section]), message);
                else if (existing.ExitCode != 1 || existing.Error.Length != 0)
                    EnsureSuccess(existing, message);
            }

            EnsureSuccess(await RunGitAsync(["config", "--file", temporaryConfig, "--add", "--", key, ""]), message);
            EnsureSuccess(await RunGitAsync(["config", "--file", temporaryConfig, "--add", "--", key, helper]), message);
            EnsureSuccess(await RunGitAsync(["config", "--file", temporaryConfig, "--add", "--", $"{section}.useHttpPath", "false"]), message);
            EnsureSuccess(await RunGitAsync(["config", "--file", temporaryConfig, "--add", "--", $"{section}.username", username]), message);
            File.Move(temporaryConfig, config, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryConfig);
        }
    }

    private async Task<GitProcess.Result> RunCredentialStoreAsync(string operation, string input)
    {
        var result = await RunGitAsync(["credential-store", "--file", CredentialPath, operation], input);
        // A helper's diagnostics may contain credentials. Report the failure without its raw output.
        EnsureSuccess(result, "Git could not access the HTTPS credential store. Check that the Git home is writable.");
        return result;
    }

    private Task<GitProcess.Result> RunGitAsync(IReadOnlyList<string> arguments, string? input = null) =>
        GitProcess.RunAsync(_home, arguments, input, settingsHome: _home);

    private async Task<GitProcess.Result> RunSshKeygenAsync(IReadOnlyList<string> arguments, string? input = null)
    {
        try
        {
            return await GitProcess.RunSshKeygenAsync(_home, arguments, input);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new InvalidOperationException("Install the OpenSSH client tools to set up SSH in Git Meadow.", exception);
        }
    }

    private async Task<string?> ReadSshPublicKeyAsync() => File.Exists(SshKeyPath)
        ? await ReadEd25519PublicKeyAsync(SshKeyPath + ".pub")
        : null;

    private static async Task<string?> ReadEd25519PublicKeyAsync(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 4096)
            return null;

        var fields = SplitSshLine((await File.ReadAllTextAsync(path)).TrimEnd('\r', '\n'));
        if (fields.Length < 2 || fields[0] != "ssh-ed25519")
            return null;

        var key = new byte[51];
        return Convert.TryFromBase64String(fields[1], key, out var bytes) && bytes == key.Length &&
               key.AsSpan().StartsWith("\0\0\0\vssh-ed25519\0\0\0 "u8)
            ? $"{fields[0]} {fields[1]}"
            : null;
    }

    private static string[] SplitSshLine(string line) => line.Split([' ', '\t'], 4, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsLiteralSshHost(string host)
    {
        if (host.StartsWith('['))
        {
            var end = host.IndexOf("]:", StringComparison.Ordinal);
            if (end < 2 || !ushort.TryParse(host.AsSpan(end + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)
                return false;
            host = host[1..end];
        }

        return host.Length <= 253 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':') &&
               Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static (string Origin, string[] Hosts) ParseHttpsOrigin(string value)
    {
        const string message = "Enter an HTTPS origin such as https://github.com, with an optional port.";
        var origin = ValidateField(value, 512, message).Trim();
        if (!origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            origin[8..].TrimEnd('/').IndexOfAny(['/', '\\', '?', '#', '@', '%', '*']) >= 0 ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) || uri.Port == 0)
            throw new ArgumentException(message);

        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        if (!uri.IsDefaultPort)
            host += $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";

        // Git normalizes the config URL but its credential store matches the raw host and port.
        return ($"https://{host}", uri.IsDefaultPort ? [host, $"{host}:443"] : [host]);
    }

    private static string ValidateField(string value, int limit, string message) =>
        string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl)
            ? throw new ArgumentException(message)
            : value;

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void EnsureSuccess(GitProcess.Result result, string message)
    {
        if (!result.Success)
            throw new GitCommandException(message, result.ExitCode);
    }
}

internal sealed record GitIdentity(string Name, string Email);
