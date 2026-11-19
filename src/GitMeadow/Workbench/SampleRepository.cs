using GitMeadow.Git;

namespace GitMeadow.Workbench;

public sealed class SampleRepository(GitWorkspace workspace)
{
    public async Task<string> CreateAsync()
    {
        var name = $"orchard-sample-{Guid.NewGuid():N}"[..23];
        var repo = await workspace.CreateAsync(name);
        var folder = Path.Combine(workspace.Root, repo);

        await workspace.ExecuteAsync(repo, GitAction.SetIdentity, "Riley Orchard", "riley@example.invalid");
        await SaveAsync("README.md", "# Orchard\n\nA field guide to growing fruit trees.\n", "Start the field guide");
        await SaveAsync("seasons.md", "# Seasons\n\n- Spring: plant new trees\n- Autumn: gather the harvest\n", "Add the orchard calendar");
        var main = (await workspace.ReadAsync(repo)).Branch;
        await workspace.ExecuteAsync(repo, GitAction.Tag, "v0.1.0");
        await workspace.ExecuteAsync(repo, GitAction.Branch, "guide/watering");
        await SaveAsync("README.md", "# Orchard\n\nA field guide to growing fruit trees.\n\nStart with the seasonal calendar.\n", "Link the seasonal calendar");
        await workspace.ExecuteAsync(repo, GitAction.Switch, "guide/watering");
        await SaveAsync("watering.md", "# Watering\n\nWater newly planted trees once a week.\n", "Document weekly watering");
        await workspace.ExecuteAsync(repo, GitAction.Switch, main);
        await workspace.ExecuteAsync(repo, GitAction.Merge, "guide/watering");
        await workspace.ExecuteAsync(repo, GitAction.Amend, "Merge the watering guide");
        await File.AppendAllTextAsync(Path.Combine(folder, "seasons.md"), "- Winter: prune dormant trees\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.md"), "# Field notes\n\nRecord dates of the first blooms.\n");
        return repo;

        async Task SaveAsync(string file, string contents, string message)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, file), contents);
            await workspace.ExecuteAsync(repo, GitAction.Stage, file);
            await workspace.ExecuteAsync(repo, GitAction.Commit, message);
        }
    }
}
