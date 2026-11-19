using System.Text;

namespace GitMeadow.Git;

public sealed partial class GitWorkspace
{
    private static async Task<string> ReadConflictTextAsync(Repository repo, string path)
    {
        var selected = ValidateFilePath(repo, path, allowFinalLink: false);
        var entries = await UnmergedAsync(repo, selected);
        if (entries.Any(entry => !entry.StartsWith("100", StringComparison.Ordinal)))
            throw new InvalidOperationException("Only regular file conflicts can be edited as text.");

        var attributes = NullFields(await RunAsync(repo.Path, ["check-attr", "-z", "diff", "merge", "binary", "--", selected]));
        if (attributes[2] == "unset" || attributes[5] == "binary" || attributes[8] == "set")
            throw BinaryConflict();
        var driver = attributes[2];
        if (driver is not ("unspecified" or "set") &&
            (await ReadConfigValueAsync(repo, $"diff.{driver}.binary")) is "true" or "yes" or "on" or "1")
            throw BinaryConflict();

        try
        {
            foreach (var entry in entries)
            {
                var objectName = entry.Split(' ', 3)[1];
                var blob = await GitProcess.RunAsync(repo.Path, ["cat-file", "blob", objectName], strictOutput: true);
                RequireText(blob.RequireSuccess());
            }

            var workFile = new FileInfo(Path.Combine(repo.Path, selected));
            if (!workFile.Exists || workFile.Length == 0)
                return "";
            if (workFile.Length > GitProcess.OutputLimit)
                throw new GitOutputLimitException();
            await using var stream = workFile.OpenRead();
            return RequireText(await GitProcess.ReadUtf8Async(stream, strict: true));
        }
        catch (DecoderFallbackException)
        {
            throw BinaryConflict();
        }
        catch (GitOutputLimitException)
        {
            throw new InvalidOperationException("This conflict is larger than 4 MiB. Resolve it outside the text editor or choose one side.");
        }
    }

    private static string RequireText(string contents)
    {
        if (contents.Any(character => char.IsControl(character) && character is not ('\t' or '\n' or '\r' or '\f')))
            throw BinaryConflict();
        return contents;
    }

    private static InvalidOperationException BinaryConflict() =>
        new("This conflict contains binary or non-UTF-8 data. Choose one side or resolve it outside the text editor.");
}
