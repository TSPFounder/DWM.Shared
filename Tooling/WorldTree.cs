// WorldTree.cs
// What is actually in a world, as a tree: stages, the artifacts they author, the results
// their tools produced, and this session's runs.
//
// THERE IS NO DATABASE BEHIND THIS, and the tree is built the way it is because of that.
// A world's identity lives in %APPDATA%\DWMStudio\worlds.json; its artifacts are ordinary
// files scattered wherever the engineering work happens; its run history is in memory and
// dies with the window. The only SQLite in the project is the EXPORTED package -- an output,
// not a store. So this is a live view over config plus the filesystem, recomputed on demand,
// and it can never be more current than the last time someone asked it to look.
//
// WHICH MAKES ONE RULE NON-NEGOTIABLE: A NODE MAY NOT CLAIM A FILE EXISTS WITHOUT CHECKING.
//
// Every path node here carries an Exists that came from touching the disk, and a missing file
// is SHOWN AS MISSING rather than omitted. Hiding it would be the more attractive tree and
// the wrong one -- "the deck isn't in the list" and "the deck isn't on disk" would look
// identical, and this project has already paid for that confusion in several currencies:
// addpath succeeding on an empty folder, ResolveExecutable returning a filename it never
// found, a tile reporting "Found on disk" about nothing.
//
// It is the same job FEMAP's Model Info tree does, and it inherits that tree's warning too:
// what is drawn is a snapshot, and the thing it describes can change underneath it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DWM.Shared.Tooling
{
    public enum WorldTreeNodeKind
    {
        /// <summary>The world itself. One per tree.</summary>
        World,

        /// <summary>A pipeline stage, named for its tool.</summary>
        Stage,

        /// <summary>A file the stage authors -- a deck, a model, a document.</summary>
        Artifact,

        /// <summary>A file the stage's tool PRODUCED. Never authored by hand.</summary>
        Result,

        /// <summary>One execution from this session.</summary>
        Run,

        /// <summary>
        /// A remark rather than a thing: a truncation notice, an unreadable folder. Present so
        /// the tree can say why it is showing less than the whole truth, instead of quietly
        /// showing less than the whole truth.
        /// </summary>
        Note
    }

    public sealed class WorldTreeNode
    {
        public string Label { get; init; } = string.Empty;

        public WorldTreeNodeKind Kind { get; init; }

        /// <summary>Absolute path, when this node is a file. Null for grouping nodes.</summary>
        public string? Path { get; init; }

        /// <summary>
        /// VERIFIED against the disk, not assumed from the path being non-empty. False on a
        /// grouping node, which is why callers should ask <see cref="IsMissing"/> instead.
        /// </summary>
        public bool Exists { get; init; }

        /// <summary>Second line: a size, a timestamp, a status. Never null on a file node.</summary>
        public string Detail { get; init; } = string.Empty;

        public string? ToolId { get; init; }
        public string? StageId { get; init; }

        public IReadOnlyList<WorldTreeNode> Children { get; init; } = Array.Empty<WorldTreeNode>();

        /// <summary>
        /// A file this node names that is not there. Distinct from <c>!Exists</c>, which is
        /// also true of every grouping node and would paint half the tree red.
        /// </summary>
        public bool IsMissing => Path is not null && !Exists;

        public bool HasChildren => Children.Count > 0;

        public override string ToString() => $"{Kind}: {Label}";

        /// <summary>Depth-first walk including this node. Convenient for tests and searching.</summary>
        public IEnumerable<WorldTreeNode> Descendants()
        {
            yield return this;
            foreach (var child in Children)
                foreach (var n in child.Descendants())
                    yield return n;
        }
    }

    public static class WorldTreeBuilder
    {
        /// <summary>
        /// How many result files one stage may list before the rest are summarised.
        ///
        /// A cap exists because a working folder accumulates hundreds of CSVs and a tree that
        /// renders all of them is not a tree anyone reads. The overflow is REPORTED as a Note
        /// rather than dropped -- silently showing the first 25 of 300 would be a smaller lie
        /// than the ones this file is written to avoid, but it would still be one.
        /// </summary>
        public const int DefaultMaxResults = 25;

        public static WorldTreeNode Build(
            string worldName,
            ProjectPipeline pipeline,
            ToolRegistry registry,
            string projectRoot,
            Func<string, IReadOnlyList<ToolRun>>? runsForStage = null,
            int maxResults = DefaultMaxResults)
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));
            if (registry is null) throw new ArgumentNullException(nameof(registry));

            var stages = new List<WorldTreeNode>();

            foreach (var stage in pipeline.Stages)
            {
                var tool = stage.ToolId is null ? null : registry.Find(stage.ToolId);
                stages.Add(BuildStage(stage, tool, projectRoot, runsForStage, maxResults));
            }

            return new WorldTreeNode
            {
                Label = string.IsNullOrWhiteSpace(worldName) ? "(unnamed world)" : worldName,
                Kind = WorldTreeNodeKind.World,
                Detail = Plural(stages.Count, "stage"),
                Children = stages
            };
        }

        /// <summary>
        /// The same tree for ONE stage, rooted at that stage -- what a tool's own workspace
        /// window shows.
        ///
        /// A workspace window is about a single tool, so the whole-world tree would be mostly
        /// rows it cannot act on. This returns the subtree it can: the deck it works on, the
        /// results beside it, and the runs from this session.
        ///
        /// It takes the <see cref="ToolWorkspaceModel"/> the window is already built from
        /// rather than re-deriving paths, because the artifact path there has ALREADY been
        /// resolved against the project root. Recomputing it independently is how two views of
        /// one stage start disagreeing about where a file is.
        /// </summary>
        public static WorldTreeNode BuildForWorkspace(
            ToolWorkspaceModel workspace,
            ToolRegistry registry,
            IReadOnlyList<ToolRun>? runs = null,
            int maxResults = DefaultMaxResults)
        {
            if (workspace is null) throw new ArgumentNullException(nameof(workspace));
            if (registry is null) throw new ArgumentNullException(nameof(registry));

            var tool = workspace.ToolId is null ? null : registry.Find(workspace.ToolId);

            var stage = new PipelineStageDefinition
            {
                Id = workspace.StageId,
                Label = workspace.Title,
                ToolId = workspace.ToolId,
                ArtifactPath = workspace.ArtifactPath
            };

            // Title already reads "FEA Solve / MYSTRAN", so the tool name must not be appended
            // again -- hence the override rather than letting BuildStage compose it.
            return BuildStage(
                stage, tool, workspace.ProjectRoot,
                _ => runs ?? workspace.Runs, maxResults,
                labelOverride: workspace.Title);
        }

        private static WorldTreeNode BuildStage(
            PipelineStageDefinition stage,
            ToolDescriptor? tool,
            // Nullable, because a world that has never named a project root is an ordinary
            // state and this method already handles it -- declaring it non-null merely moved
            // the problem to a warning at the call site.
            string? projectRoot,
            Func<string, IReadOnlyList<ToolRun>>? runsForStage,
            int maxResults,
            string? labelOverride = null)
        {
            var children = new List<WorldTreeNode>();
            string? artifactPath = null;

            if (stage.ArtifactPath is not null)
            {
                // Path.Combine returns the second argument unchanged when it is already
                // rooted, which is the normal case for an FEA deck -- those live with the
                // analysis rather than under whatever folder is the project root.
                artifactPath = System.IO.Path.Combine(projectRoot ?? string.Empty, stage.ArtifactPath);
                children.Add(FileNode(artifactPath, WorldTreeNodeKind.Artifact, stage, tool));
            }

            children.AddRange(ResultNodes(artifactPath, projectRoot, stage, tool, maxResults));

            var runs = runsForStage?.Invoke(stage.Id) ?? Array.Empty<ToolRun>();
            if (runs.Count > 0)
            {
                children.Add(new WorldTreeNode
                {
                    Label = "Runs",
                    Kind = WorldTreeNodeKind.Note,
                    StageId = stage.Id,
                    // SESSION-SCOPED, AND IT SAYS SO. Run history is an ObservableCollection on
                    // a ViewModel and is persisted nowhere, so this node is empty after every
                    // restart. Unlabelled, that reads as "nothing has ever been run here",
                    // which is a different and false statement.
                    Detail = $"{Plural(runs.Count, "run")} this session -- not saved",
                    Children = runs.Select(RunNode).ToList()
                });
            }

            var label = labelOverride
                        ?? (tool is null ? stage.Label : $"{stage.Label} / {tool.DisplayName}");
            if (stage.IsOptional) label += "  (optional)";

            return new WorldTreeNode
            {
                Label = label,
                Kind = WorldTreeNodeKind.Stage,
                ToolId = stage.ToolId,
                StageId = stage.Id,
                Detail = tool is null ? "No tool for this stage" : DescribeStage(children),
                Children = children
            };
        }

        /// <summary>
        /// Files the stage's tool produced, found by looking rather than by assuming.
        ///
        /// With an artifact, results are its siblings sharing its stem -- wtTowerModal.dat
        /// beside wtTowerModal.f06 and wtTowerModal.OP2 -- which is exactly where MYSTRAN
        /// writes them and why the solve runs in the deck's own folder. Without an artifact,
        /// the project root is scanned instead, which is the MATLAB stage: it authors no
        /// single document and exports channel CSVs into the model folder.
        ///
        /// NOT AN ITERATOR, and that is a language rule rather than a preference: C# forbids
        /// yield return inside a try that has a catch clause (CS1626), and inside a catch at
        /// all (CS1631). Every branch here either scans a directory or reports why it could
        /// not, so the whole body sits inside error handling and it returns a list instead.
        /// </summary>
        private static List<WorldTreeNode> ResultNodes(
            string? artifactPath, string? projectRoot,
            PipelineStageDefinition stage, ToolDescriptor? tool, int maxResults)
        {
            var nodes = new List<WorldTreeNode>();
            if (tool is null || tool.ResultExtensions.Count == 0) return nodes;

            string? folder;
            string? stem = null;

            if (artifactPath is not null)
            {
                folder = SafeDirectoryName(artifactPath);
                stem = System.IO.Path.GetFileNameWithoutExtension(artifactPath);
            }
            else
            {
                folder = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;
            }

            if (folder is null) return nodes;

            List<string> found;
            try
            {
                if (!Directory.Exists(folder))
                {
                    // Not an error worth shouting about -- an FEA folder that does not exist
                    // yet is the ordinary state of a project nobody has solved. Reported so
                    // "no results" and "nowhere to look" stay distinguishable.
                    nodes.Add(new WorldTreeNode
                    {
                        Label = "Folder not found",
                        Kind = WorldTreeNodeKind.Note,
                        StageId = stage.Id,
                        Detail = folder
                    });
                    return nodes;
                }

                found = Directory.EnumerateFiles(folder)
                    .Where(f => tool.ResultExtensions.Any(e =>
                        f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .Where(f => stem is null || System.IO.Path
                        .GetFileNameWithoutExtension(f)
                        .StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    // THE ARTIFACT IS NOT A RESULT OF ITSELF. FEMAP's ResultExtensions include
                    // .dat and .bdf because it WRITES decks -- so without this the deck at the
                    // FEA Mesh stage appears twice, once as the thing authored and once as the
                    // thing produced. Harmless-looking, and it would teach anyone reading the
                    // tree that a duplicated row means nothing in particular.
                    .Where(f => artifactPath is null || !SamePath(f, artifactPath))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A tree that throws takes the window with it. Say what went wrong in the row
                // where the answer would have been.
                nodes.Add(new WorldTreeNode
                {
                    Label = "Could not read folder",
                    Kind = WorldTreeNodeKind.Note,
                    StageId = stage.Id,
                    Detail = FirstLine(ex.Message)
                });
                return nodes;
            }

            foreach (var f in found.Take(maxResults))
                nodes.Add(FileNode(f, WorldTreeNodeKind.Result, stage, tool));

            if (found.Count > maxResults)
            {
                nodes.Add(new WorldTreeNode
                {
                    Label = $"...and {found.Count - maxResults} more",
                    Kind = WorldTreeNodeKind.Note,
                    StageId = stage.Id,
                    Detail = $"{found.Count} result files in {folder}"
                });
            }

            return nodes;
        }

        private static WorldTreeNode FileNode(
            string path, WorldTreeNodeKind kind, PipelineStageDefinition stage, ToolDescriptor? tool)
        {
            bool exists;
            string detail;

            try
            {
                var info = new FileInfo(path);
                exists = info.Exists;
                detail = exists
                    ? $"{Size(info.Length)}, {info.LastWriteTime:yyyy-MM-dd HH:mm}"
                    // NOT blank, and not the path repeated. A file that is not there is the
                    // single most useful thing this tree reports, so it says so in words.
                    : "Missing";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                exists = false;
                detail = "Unreadable: " + FirstLine(ex.Message);
            }

            return new WorldTreeNode
            {
                Label = SafeFileName(path),
                Kind = kind,
                Path = path,
                Exists = exists,
                Detail = detail,
                ToolId = tool?.Id,
                StageId = stage.Id
            };
        }

        private static WorldTreeNode RunNode(ToolRun run) => new()
        {
            Label = $"{run.Status} -- {run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            Kind = WorldTreeNodeKind.Run,
            ToolId = run.ToolId,
            StageId = run.StageId,
            Detail = run.Duration.TotalSeconds >= 1
                ? $"{run.Duration.TotalSeconds:F1} s"
                : $"{run.Duration.TotalMilliseconds:F0} ms",
            // Warnings become children rather than being summarised away. The run-history
            // template collected these for two builds into a control that never rendered them,
            // and the one fact being asked for was the one structurally unreadable.
            Children = run.Warnings.Select(w => new WorldTreeNode
            {
                Label = FirstLine(w),
                Kind = WorldTreeNodeKind.Note,
                StageId = run.StageId,
                Detail = "warning"
            }).ToList()
        };

        private static string DescribeStage(IReadOnlyList<WorldTreeNode> children)
        {
            var missing = children.Count(c => c.IsMissing);
            var files = children.Count(c => c.Path is not null);

            if (files == 0) return "Nothing on disk yet";
            return missing == 0
                ? Plural(files, "file")
                : $"{Plural(files, "file")}, {missing} missing";
        }

        /// <summary>
        /// Compared on the full path, because the artifact arrives as a possibly-relative
        /// combine while the enumerated file is always absolute -- a raw string equality would
        /// answer "different" for the same file and quietly reinstate the duplicate.
        /// </summary>
        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

        private static string Size(long bytes) =>
            bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
            : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
            : $"{bytes} B";

        /// <summary>Path helpers that refuse to throw -- a malformed path is a row, not a crash.</summary>
        private static string SafeFileName(string path)
        {
            try { return System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : path; }
            catch (ArgumentException) { return path; }
        }

        private static string? SafeDirectoryName(string path)
        {
            try { return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        private static string FirstLine(string message)
        {
            var i = message.IndexOf('\n');
            return i < 0 ? message : message[..i];
        }
    }
}
