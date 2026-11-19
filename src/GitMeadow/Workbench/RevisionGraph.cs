using GitMeadow.Git;

namespace GitMeadow.Workbench;

internal sealed record GraphLine(int From, int To, int Color, bool Junction);
internal sealed record GraphRow(int Lane, int Color, bool Incoming, IReadOnlyList<GraphLine> Lines);
internal sealed record RevisionGraph(IReadOnlyDictionary<string, GraphRow> Rows, int Width)
{
    public static RevisionGraph Build(IReadOnlyList<GitCommit> commits)
    {
        var loaded = commits.Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);
        var active = new List<(string Hash, int Color)>();
        var rows = new Dictionary<string, GraphRow>(StringComparer.Ordinal);
        var nextColor = 0;
        var width = 0;

        foreach (var commit in commits)
        {
            var lane = active.FindIndex(entry => entry.Hash == commit.Hash);
            var incoming = lane >= 0;
            if (!incoming)
            {
                lane = active.Count;
                active.Add((commit.Hash, nextColor++));
            }

            var before = active.ToArray();
            var current = before[lane];
            active.RemoveAt(lane);
            var insertion = lane;
            for (var i = 0; i < commit.Parents.Count; i++)
            {
                var parent = commit.Parents[i];
                if (!loaded.Contains(parent) || active.Exists(entry => entry.Hash == parent))
                    continue;

                active.Insert(insertion++, (parent, i == 0 ? current.Color : nextColor++));
            }

            var lines = new List<GraphLine>();
            for (var i = 0; i < before.Length; i++)
            {
                if (i == lane)
                    continue;

                var to = active.FindIndex(entry => entry.Hash == before[i].Hash);
                lines.Add(new(i, to, before[i].Color, false));
            }

            foreach (var parent in commit.Parents)
            {
                var to = active.FindIndex(entry => entry.Hash == parent);
                if (to >= 0)
                    lines.Add(new(lane, to, active[to].Color, true));
            }

            rows.Add(commit.Hash, new(lane, current.Color, incoming, lines));
            width = Math.Max(width, Math.Max(before.Length, active.Count));
        }

        return new(rows, Math.Max(72, 16 + width * 16));
    }
}
