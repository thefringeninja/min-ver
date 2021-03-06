namespace MinVer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using LibGit2Sharp;

    public static class Versioner
    {
        public static Version GetVersion(string path, bool verbose, string tagPrefix, int major, int minor, string buildMetadata)
        {
            if (verbose)
            {
                Log($"MinVer {typeof(Versioner).Assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>().Single().InformationalVersion}");
            }

            // Repository.ctor(string) throws RepositoryNotFoundException in this case
            if (!Directory.Exists(path))
            {
                // Substring of RepositoryNotFoundException.Message $"Path '{path}' doesn't point at a valid Git repository or workdir."
                throw new Exception($"Path '{path}' doesn't point at a valid workdir.");
            }

            Repository repo = default;
            var testPath = path;
            while (testPath != default)
            {
                try
                {
                    repo = new Repository(testPath);
                    break;
                }
                catch (RepositoryNotFoundException)
                {
                    testPath = Directory.GetParent(testPath)?.FullName;
                }
            }

            if (repo != default)
            {
                try
                {
                    return GetVersion(repo, verbose, tagPrefix, major, minor, buildMetadata);
                }
                finally
                {
                    repo.Dispose();
                }
            }

            // Includes substring of RepositoryNotFoundException.Message $"Path '{path}' doesn't point at a valid Git repository or workdir."
            Log($"warning MINVER0001: Path '{path}' doesn't point at a valid Git repository. Using default version.");
            return new Version();
        }

        private static Version GetVersion(Repository repo, bool verbose, string tagPrefix, int major, int minor, string buildMetadata)
        {
            var commit = repo.Commits.FirstOrDefault();

            if (commit == default)
            {
                return new Version();
            }

            var tagsAndVersions = repo.Tags
                .Select(tag => (tag, Version.ParseOrDefault(tag.FriendlyName, tagPrefix)))
                .Where(tagAndVersion => tagAndVersion.Item2 != default)
                .OrderByDescending(tagAndVersion => tagAndVersion.Item2)
                .ToList();

            var commitsChecked = new HashSet<string>();
            var count = 0;
            var height = 0;
            var candidates = new List<Candidate>();
            var commitsToCheck = new Stack<(Commit, int)>();

            while (true)
            {
                if (commitsChecked.Add(commit.Sha))
                {
                    ++count;

                    var (tag, commitVersion) = tagsAndVersions.FirstOrDefault(tagAndVersion => tagAndVersion.tag.Target.Sha == commit.Sha);

                    if (commitVersion != default)
                    {
                        candidates.Add(new Candidate { Commit = commit.Sha, Height = height, Tag = tag.FriendlyName, Version = commitVersion, });
                    }
                    else
                    {
                        foreach (var parent in commit.Parents.Reverse())
                        {
                            commitsToCheck.Push((parent, height + 1));
                        }

                        if (commitsToCheck.Count == 0 || commitsToCheck.Peek().Item2 <= height)
                        {
                            candidates.Add(new Candidate { Commit = commit.Sha, Height = height, Tag = "(none)", Version = new Version(), });
                        }
                    }
                }

                if (commitsToCheck.Count == 0)
                {
                    break;
                }

                (commit, height) = commitsToCheck.Pop();
            }

            if (verbose)
            {
                Log($"{count:N0} commits checked.");
            }

            var orderedCandidates = candidates.OrderBy(candidate => candidate.Version).ToList();

            var tagWidth = verbose ? orderedCandidates.Max(candidate => candidate.Tag.Length) : 0;
            var versionWidth = verbose ? orderedCandidates.Max(candidate => candidate.Version.ToString().Length) : 0;
            var heightWidth = verbose ? orderedCandidates.Max(candidate => candidate.Height).ToString().Length : 0;

            if (verbose)
            {
                foreach (var candidate in orderedCandidates.Take(orderedCandidates.Count - 1))
                {
                    Log($"Ignoring {candidate.ToString(tagWidth, versionWidth, heightWidth)}.");
                }
            }

            var selectedCandidate = orderedCandidates.Last();
            Log($"Using{(verbose && orderedCandidates.Count > 1 ? "    " : " ")}{selectedCandidate.ToString(tagWidth, versionWidth, heightWidth)}.");

            var baseVersion = selectedCandidate.Version.IsBefore(major, minor) ?
                new Version(major, minor)
                : selectedCandidate.Version;

            if (baseVersion != selectedCandidate.Version)
            {
                Log($"Bumping version to {baseVersion} to satisfy {major}.{minor} range.");
            }

            var calculatedVersion = baseVersion.WithHeight(selectedCandidate.Height).AddBuildMetadata(buildMetadata);
            if (verbose)
            {
                Log($"Calculated version {calculatedVersion}.");
            }

            return calculatedVersion;
        }

        private class Candidate
        {
            public string Commit { get; set; }

            public int Height { get; set; }

            public string Tag { get; set; }

            public Version Version { get; set; }

            public string ToString(int tagWidth, int versionWidth, int heightWidth) =>
                $"{{ {nameof(this.Commit)}: {this.Commit.Substring(0, 7)}, {nameof(this.Tag)}: {$"'{this.Tag}',".PadRight(tagWidth + 3)} {nameof(this.Version)}: {$"{this.Version.ToString()},".PadRight(versionWidth + 1)} {nameof(this.Height)}: {this.Height.ToString().PadLeft(heightWidth)} }}";
        }

        private static void Log(string message) => Console.Error.WriteLine($"MinVer: {message}");
    }
}
