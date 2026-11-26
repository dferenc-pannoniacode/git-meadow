using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GitMeadow.Components.Pages;

internal sealed class WorkbenchBrowser(IJSRuntime js, ILogger logger, string workspace)
{
    private const string ModulePath = "./Components/Pages/Home.razor.js";
    private const string ReadMethod = "readRepositories";
    private const string WriteMethod = "rememberRepository";
    private const string ResizeMethod = "initializeSidebarResize";

    public async Task<RepositoryPreferences> ReadRepositoriesAsync()
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            return await module.InvokeAsync<RepositoryPreferences>(ReadMethod, workspace);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Reading repository preferences from browser storage failed.");
            return new(null, []);
        }
    }

    public async Task RememberRepositoryAsync(string repository, bool select = true)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await module.InvokeVoidAsync(WriteMethod, workspace, repository, select);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Saving repository preferences to browser storage failed.");
        }
    }

    public async Task InitializeSidebarResizeAsync(ElementReference workbench, ElementReference handle)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await module.InvokeVoidAsync(ResizeMethod, workbench, handle, workspace);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            logger.LogDebug(ex, "The sidebar resize handle could not be initialized.");
        }
    }

    internal sealed record RepositoryPreferences(string? Selected, string[] Remembered);
}
