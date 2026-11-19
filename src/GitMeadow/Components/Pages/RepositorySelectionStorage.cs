using Microsoft.JSInterop;

namespace GitMeadow.Components.Pages;

internal sealed class RepositorySelectionStorage(IJSRuntime js, ILogger logger, string workspace)
{
    private const string ModulePath = "./Components/Pages/Home.razor.js";
    private const string ReadMethod = "readSelection";
    private const string WriteMethod = "writeSelection";

    public async Task<string?> ReadAsync()
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            return await module.InvokeAsync<string?>(ReadMethod, workspace);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Reading the repository selection from browser storage failed.");
            return null;
        }
    }

    public async Task WriteAsync(string repository)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await module.InvokeVoidAsync(WriteMethod, workspace, repository);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Saving the repository selection to browser storage failed.");
        }
    }
}
