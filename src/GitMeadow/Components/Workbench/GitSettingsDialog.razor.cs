using Microsoft.AspNetCore.Components;

namespace GitMeadow.Components.Workbench;

public partial class GitSettingsDialog
{
    [Parameter] public EventCallback Cancel { get; set; }

    private enum Section { Identity, Ssh, Https }

    private Section _section;
    private bool _busy;
    private string? _error;
    private string? _status;
    private string _name = "";
    private string _email = "";
    private string? _publicKey;
    private string _hostKey = "";
    private string _origin = "";
    private string _username = "";
    private string _token = "";
    private int _tokenField;

    protected override Task OnInitializedAsync() => LoadIdentityAsync();

    private string Pressed(Section section) => _section == section ? "true" : "false";

    private Task SelectAsync(Section section)
    {
        if (_busy || _section == section)
            return Task.CompletedTask;

        _section = section;
        (_error, _status, _token) = (null, null, "");
        return section == Section.Ssh
            ? ExecuteAsync(async () => _publicKey = await Settings.GetSshPublicKeyAsync())
            : Task.CompletedTask;
    }

    private Task LoadIdentityAsync() => ExecuteAsync(async () =>
    {
        var identity = await Settings.GetIdentityAsync();
        (_name, _email) = (identity.Name, identity.Email);
    });

    private Task SaveIdentityAsync() => ExecuteAsync(() => Settings.SaveIdentityAsync(_name, _email), "Default commit identity saved.");

    private Task GenerateSshKeyAsync() => ExecuteAsync(async () => _publicKey = await Settings.GenerateSshKeyAsync(), "SSH key is ready.");

    private Task SaveSshHostKeyAsync() => ExecuteAsync(async () =>
    {
        await Settings.SaveSshHostKeyAsync(_hostKey);
        _hostKey = "";
    }, "SSH host key saved.");

    private void ReadToken(ChangeEventArgs args) => _token = args.Value?.ToString() ?? "";

    private Task SaveHttpsCredentialAsync() => ExecuteAsync(async () =>
    {
        var token = _token;
        ClearToken();
        await Settings.SaveHttpsCredentialAsync(_origin, _username, token);
    }, "HTTPS token saved.");

    private Task RemoveHttpsCredentialAsync() => ExecuteAsync(async () =>
    {
        ClearToken();
        await Settings.RemoveHttpsCredentialAsync(_origin, _username);
    }, "HTTPS token removed.");

    private void ClearToken()
    {
        _token = "";
        _tokenField++;
    }

    private async Task ExecuteAsync(Func<Task> operation, string? success = null)
    {
        if (_busy)
            return;

        (_busy, _error, _status) = (true, null, null);
        try
        {
            await CommandHistory.CaptureAsync(operation);
            _status = success;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
