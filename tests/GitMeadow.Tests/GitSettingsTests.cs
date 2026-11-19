using GitMeadow.Git;
using Xunit;

namespace GitMeadow.Tests;

public sealed class GitSettingsTests : IDisposable
{
    private const string Host = "git.example.invalid:8443";
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINdamAGCsQq31Uv+08lkBzoO4XLz2qYjJa8CGmj3B1Ea";
    private readonly GitFixture _git = new();
    private readonly string _home;

    public GitSettingsTests()
    {
        _home = Directory.CreateDirectory(Path.Combine(_git.Root, "Git Meadow's home")).FullName;
    }

    [Fact]
    public async Task Default_identity_persists_and_repository_identity_takes_precedence()
    {
        await new GitSettings(_home).SaveIdentityAsync("Élodie Meadow", "default@example.invalid");

        var identity = await new GitSettings(_home).GetIdentityAsync();
        Assert.Equal("Élodie Meadow", identity.Name);
        Assert.Equal("default@example.invalid", identity.Email);

        var repository = Directory.CreateDirectory(Path.Combine(_git.Root, "repo")).FullName;
        (await GitProcess.RunAsync(repository, ["init", "--template="], settingsHome: _home)).RequireSuccess();
        var effectiveName = await GitProcess.RunAsync(repository, ["config", "--get", "user.name"], settingsHome: _home);
        Assert.Equal("Élodie Meadow", effectiveName.RequireSuccess().Trim());

        (await GitProcess.RunAsync(repository, ["config", "--local", "user.name", "Repository author"], settingsHome: _home)).RequireSuccess();
        effectiveName = await GitProcess.RunAsync(repository, ["config", "--get", "user.name"], settingsHome: _home);
        Assert.Equal("Repository author", effectiveName.RequireSuccess().Trim());
        Assert.Equal("Élodie Meadow", (await new GitSettings(_home).GetIdentityAsync()).Name);
    }

    [Fact]
    public async Task Https_token_overrides_the_manual_helper_only_for_its_origin_until_removed()
    {
        const string manualToken = "manual-fixture-token";
        const string firstToken = "ui-fixture-token:encoded@value";
        const string rotatedToken = "rotated-fixture-token";
        const string manualHelper = "!f() { printf 'username=meadow\\npassword=manual-fixture-token\\n'; }; f";
        var origin = $"https://{Host}";

        (await GitProcess.RunAsync(_home, ["config", "--global", "--add", "credential.helper", ""], settingsHome: _home)).RequireSuccess();
        (await GitProcess.RunAsync(_home, ["config", "--global", "--add", "credential.helper", manualHelper], settingsHome: _home)).RequireSuccess();
        (await GitProcess.RunAsync(_home, ["config", "--global", "credential.useHttpPath", "true"], settingsHome: _home)).RequireSuccess();
        Assert.Equal(manualToken, await ReadPasswordAsync("https", Host));

        await new GitSettings(_home).SaveHttpsCredentialAsync("https://GIT.example.invalid:8443/", "meadow", firstToken);
        Assert.Equal(firstToken, await ReadPasswordAsync("https", Host));
        Assert.Equal(manualToken, await ReadPasswordAsync("http", Host));
        Assert.Equal(manualToken, await ReadPasswordAsync("https", "other.example.invalid:8443"));
        Assert.Equal(manualToken, await ReadPasswordAsync("https", "git.example.invalid"));

        var reopened = new GitSettings(_home);
        await reopened.SaveHttpsCredentialAsync(origin, "meadow", rotatedToken);
        Assert.Equal(rotatedToken, await ReadPasswordAsync("https", Host));

        const string rejection = $"protocol=https\nhost={Host}\nusername=meadow\npassword={rotatedToken}\n\n";
        (await GitProcess.RunAsync(_home, ["credential", "reject"], rejection, settingsHome: _home)).RequireSuccess();
        Assert.Equal(rotatedToken, await ReadPasswordAsync("https", Host));

        const string defaultHost = "git.example.invalid";
        const string defaultToken = "default-port-fixture-token";
        await reopened.SaveHttpsCredentialAsync($"https://{defaultHost}:443", "secondary", defaultToken);
        Assert.Equal(defaultToken, await ReadPasswordAsync("https", defaultHost));
        Assert.Equal(defaultToken, await ReadPasswordAsync("https", $"{defaultHost}:443"));
        Assert.Equal(rotatedToken, await ReadPasswordAsync("https", Host));
        await reopened.RemoveHttpsCredentialAsync($"https://{defaultHost}", "secondary");
        Assert.Equal(manualToken, await ReadPasswordAsync("https", defaultHost));
        Assert.Equal(manualToken, await ReadPasswordAsync("https", $"{defaultHost}:443"));

        await reopened.RemoveHttpsCredentialAsync(origin, "meadow");
        Assert.Equal(manualToken, await ReadPasswordAsync("https", Host));
    }

    [Fact]
    public async Task Https_setup_rejects_unsafe_origins_and_credential_fields_before_persisting()
    {
        var settings = new GitSettings(_home);
        var origin = $"https://{Host}";

        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SaveHttpsCredentialAsync($"http://{Host}", "meadow", "fixture-token"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SaveHttpsCredentialAsync($"{origin}/team/repo", "meadow", "fixture-token"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SaveHttpsCredentialAsync("https://user@git.example.invalid", "meadow", "fixture-token"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SaveHttpsCredentialAsync(origin, "meadow\0user", "fixture-token"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SaveHttpsCredentialAsync(origin, "meadow", "fixture-token\nprotocol=http"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_home));
    }

    [Fact]
    public async Task Ssh_setup_rejects_wildcards_markers_and_multiple_known_hosts_entries()
    {
        var settings = new GitSettings(_home);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SaveSshHostKeyAsync($"*.example.invalid {HostKey}"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SaveSshHostKeyAsync($"@cert-authority git.example.invalid {HostKey}"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            settings.SaveSshHostKeyAsync($"git.example.invalid {HostKey}\nother.example.invalid {HostKey}"));

        Assert.False(File.Exists(Path.Combine(_home, ".ssh", "known_hosts")));
    }

    private async Task<string> ReadPasswordAsync(string protocol, string host)
    {
        var input = $"protocol={protocol}\nhost={host}\npath=team/repo.git\n\n";
        var result = await GitProcess.RunAsync(_home, ["credential", "fill"], input, settingsHome: _home);
        var passwordLine = Assert.Single(result.RequireSuccess().ReplaceLineEndings("\n").Split('\n'),
            static line => line.StartsWith("password=", StringComparison.Ordinal));
        return passwordLine["password=".Length..];
    }

    public void Dispose() => _git.Dispose();
}
