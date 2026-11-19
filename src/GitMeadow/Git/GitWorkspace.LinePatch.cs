using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    private const string NoNewlineMarker = "\\ No newline at end of file";

    private static List<HunkRow> ParseHunkRows(Match header, string body)
    {
        var oldStart = HunkRangeNumber(header, "old", 0);
        var newStart = HunkRangeNumber(header, "new", 0);
        var oldLine = oldStart;
        var newLine = newStart;
        List<HunkRow> rows = [];
        var rawRows = body.Split('\n');
        // The patch itself ends with a line separator, including when its final row is an EOF marker.
        for (var index = 0; index < rawRows.Length - 1; index++)
        {
            var raw = rawRows[index];
            if (raw == NoNewlineMarker)
            {
                if (rows.Count == 0 || rows[^1].NoNewline)
                    return [];
                rows[^1] = rows[^1] with { NoNewline = true };
                continue;
            }

            // Git may omit the leading context indicator on an empty context row.
            var kind = raw.Length == 0 ? ' ' : raw[0];
            if (kind is not (' ' or '+' or '-'))
                return [];
            var content = raw.Length == 0 ? "" : raw[1..];
            var line = new PatchLine(rows.Count, kind, content.TrimEnd('\r'),
                kind == '+' ? null : oldLine++,
                kind == '-' ? null : newLine++);
            rows.Add(new HunkRow(line, content, NoNewline: false));
        }

        return oldLine - oldStart == HunkRangeNumber(header, "oldCount", 1) &&
               newLine - newStart == HunkRangeNumber(header, "newCount", 1) ? rows : [];
    }

    private static string BuildLinePatch(PatchHunk hunk, bool staged, HashSet<int> selection)
    {
        var header = HunkHeader().Match(hunk.Text);
        var original = ParseHunkRows(header, hunk.Text[(header.Index + header.Length)..]);
        var selected = ProjectLineSelection(original, staged, selection);
        var terminatedRows = original.Where(row => !row.NoNewline);
        var useCrLf = terminatedRows.Any() && terminatedRows.All(row => row.Content.EndsWith('\r'));
        var rows = PreserveNewlineBoundaries(selected, useCrLf);

        var sourceRows = rows.Count(row => row.Kind != '+');
        var targetRows = rows.Count(row => row.Kind != '-');
        var start = HunkRangeNumber(header, staged ? "new" : "old", 0);
        var count = HunkRangeNumber(header, staged ? "newCount" : "oldCount", 1);
        var precedingLines = count == 0 ? start : start - 1;

        var patch = new StringBuilder();
        AppendFileHeader(patch, hunk.Text[..header.Index], staged, targetRows);
        patch.Append(CultureInfo.InvariantCulture,
            $"@@ -{precedingLines + (sourceRows > 0 ? 1 : 0)},{sourceRows} +{precedingLines + (targetRows > 0 ? 1 : 0)},{targetRows} @@\n");
        foreach (var row in rows)
        {
            patch.Append(row.Kind).Append(row.Content).Append('\n');
            if (row.NoNewline)
                patch.Append(NoNewlineMarker).Append('\n');
        }

        return patch.ToString();
    }

    private static List<ProjectedRow> ProjectLineSelection(List<HunkRow> original, bool staged, HashSet<int> selection)
    {
        List<ProjectedRow> projected = [];
        foreach (var row in original)
        {
            // Reverse the meaning of each changed row before deciding which side to keep.
            var kind = staged ? row.Line.Kind switch { '+' => '-', '-' => '+', _ => ' ' } : row.Line.Kind;
            if (!selection.Contains(row.Line.Id))
            {
                if (kind == '+')
                    continue;
                kind = ' ';
            }

            projected.Add(new ProjectedRow(kind, row.Content, row.NoNewline));
        }

        return projected;
    }

    private static List<ProjectedRow> PreserveNewlineBoundaries(List<ProjectedRow> selected, bool useCrLf)
    {
        var lastTarget = selected.FindLastIndex(row => row.Kind != '-');
        List<ProjectedRow> rows = [];
        for (var index = 0; index < selected.Count; index++)
        {
            var row = selected[index];
            if (!row.NoNewline || row.Kind == '-' || index == lastTarget)
            {
                rows.Add(row);
                continue;
            }

            // Keeping a source EOF line before a selected addition terminates it in the target only.
            if (row.Kind == ' ')
                rows.Add(row with { Kind = '-' });
            var content = useCrLf && !row.Content.EndsWith('\r') ? row.Content + '\r' : row.Content;
            rows.Add(new ProjectedRow('+', content, NoNewline: false));
        }

        return rows;
    }

    private static void AppendFileHeader(StringBuilder patch, string original, bool staged, int targetRows)
    {
        const string newMode = "new file mode ";
        const string deletedMode = "deleted file mode ";
        const string missing = "/dev/null";

        var lines = original.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var oldPath = lines.Single(line => line.StartsWith("--- ", StringComparison.Ordinal))[4..];
        var newPath = lines.Single(line => line.StartsWith("+++ ", StringComparison.Ordinal))[4..];
        var added = lines.FirstOrDefault(line => line.StartsWith(newMode, StringComparison.Ordinal));
        var deleted = lines.FirstOrDefault(line => line.StartsWith(deletedMode, StringComparison.Ordinal));
        var originalMode = added?[newMode.Length..] ?? deleted?[deletedMode.Length..];
        var createFile = staged ? deleted is not null : added is not null;
        var deleteFile = !createFile && originalMode is not null && targetRows == 0;

        patch.Append(lines[0]).Append('\n');
        if (createFile || deleteFile)
            patch.Append(createFile ? newMode : deletedMode).Append(originalMode).Append('\n');
        patch.Append("--- ").Append(createFile ? missing : oldPath == missing ? ChangePathPrefix(newPath, 'a') : oldPath).Append('\n');
        patch.Append("+++ ").Append(deleteFile ? missing : newPath == missing ? ChangePathPrefix(oldPath, 'b') : newPath).Append('\n');
    }

    private static string ChangePathPrefix(string path, char prefix) =>
        path.StartsWith('"') ? $"\"{prefix}{path[2..]}" : prefix + path[1..];

    private static int HunkRangeNumber(Match header, string group, int fallback) =>
        header.Groups[group] is { Success: true } number
            ? int.Parse(number.ValueSpan, CultureInfo.InvariantCulture)
            : fallback;

    private sealed record HunkRow(PatchLine Line, string Content, bool NoNewline);
    private sealed record ProjectedRow(char Kind, string Content, bool NoNewline);
}
