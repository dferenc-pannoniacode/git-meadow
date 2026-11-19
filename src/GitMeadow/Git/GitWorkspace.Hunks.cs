using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    [GeneratedRegex(@"^@@ -(?<old>[0-9]+)(?:,(?<oldCount>[0-9]+))? \+(?<new>[0-9]+)(?:,(?<newCount>[0-9]+))? @@[^\n]*\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();

    public Task<IReadOnlyList<PatchHunk>> HunksAsync(string repo, string path, bool staged) =>
        WithRepositoryAsync(repo, repository => ReadHunksAsync(repository, path, staged));

    public async Task ApplyHunkAsync(string repo, string path, bool staged, string patchId)
    {
        await WithRepositoryAsync(repo, async repository =>
        {
            var hunk = await ResolveHunkAsync(repository, path, staged, patchId);
            return await RunAsync(repository.Path,
                ["apply", "--cached", "--recount", "--whitespace=nowarn", .. staged ? new[] { "--reverse" } : [], "-"],
                hunk.Text, literalPaths: false);
        });
    }

    public async Task ApplyLinesAsync(string repo, string path, bool staged, string patchId, IReadOnlyList<int> lineIds)
    {
        ArgumentNullException.ThrowIfNull(lineIds);
        await WithRepositoryAsync(repo, async repository =>
        {
            var hunk = await ResolveHunkAsync(repository, path, staged, patchId);
            if (hunk.Lines.Count == 0)
                throw new InvalidOperationException("This hunk does not support selecting individual lines.");

            var selection = lineIds.ToHashSet();
            if (selection.Count == 0 || selection.Count != lineIds.Count ||
                selection.Any(id => id < 0 || id >= hunk.Lines.Count || hunk.Lines[id].Kind is not ('+' or '-')))
                throw new ArgumentException("Select one or more distinct added or removed lines in the current hunk.", nameof(lineIds));

            var patch = BuildLinePatch(hunk, staged, selection);
            return await RunAsync(repository.Path,
                ["apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", "-"],
                patch, literalPaths: false);
        });
    }

    private static async Task<PatchHunk> ResolveHunkAsync(Repository repository, string path, bool staged, string patchId)
    {
        var hunks = await ReadHunksAsync(repository, path, staged);
        return hunks.FirstOrDefault(candidate => candidate.Id == patchId)
            ?? throw new InvalidOperationException("This hunk changed. Refresh the diff before applying it.");
    }

    private static async Task<IReadOnlyList<PatchHunk>> ReadHunksAsync(Repository repo, string path, bool staged)
    {
        var selected = ValidateFilePath(repo, path);
        var changes = await ReadChangesAsync(repo);
        if (changes.Any(change => change.OriginalPath is not null &&
            (change.Path == selected || change.OriginalPath == selected)))
            return [];

        var patch = await ReadDiffAsync(repo, selected, staged);
        // A NUL marks binary content. A replacement character may represent an invalid UTF-8 byte.
        if (patch.IndexOfAny(['\0', '\uFFFD']) >= 0)
            return [];

        var headers = HunkHeader().Matches(patch);
        if (headers.Count == 0)
            return [];

        var fileHeader = patch[..headers[0].Index];
        if (!fileHeader.StartsWith("diff --git ", StringComparison.Ordinal) ||
            fileHeader.Contains("\nrename ", StringComparison.Ordinal) ||
            fileHeader.Contains("\ncopy ", StringComparison.Ordinal) ||
            fileHeader.Contains("\nold mode ", StringComparison.Ordinal) ||
            fileHeader.Contains(" 120000\n", StringComparison.Ordinal) ||
            fileHeader.Contains(" 160000\n", StringComparison.Ordinal) ||
            patch.IndexOf("\ndiff --git ", StringComparison.Ordinal) >= 0)
            return [];

        List<PatchHunk> hunks = [];
        for (var index = 0; index < headers.Count; index++)
        {
            var start = headers[index].Index;
            var end = index + 1 < headers.Count ? headers[index + 1].Index : patch.Length;
            var text = fileHeader + patch[start..end];
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var header = headers[index];
            var body = patch[(start + header.Length)..end];
            var lines = ParseHunkRows(header, body).Select(row => row.Line).ToArray();
            hunks.Add(new PatchHunk(id, text, header.Value.TrimEnd('\r', '\n')) { Lines = lines });
        }
        return hunks;
    }
}
