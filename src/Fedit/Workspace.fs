namespace Fedit

open System
open System.IO
open System.Collections.Concurrent
open System.Text
open System.Text.RegularExpressions

/// What the workspace scan and the file watcher skip. Persisted in
/// config.json as `ignoredNames` / `useGitignore`.
type IgnoreRules =
    {
        /// Basenames skipped at any depth (`.git`, `node_modules`).
        Names: string list
        /// Honour every `.gitignore` under the root, nearest file winning.
        UseGitignore: bool
    }

[<RequireQualifiedAccess>]
module Ignore =
    let defaults =
        { Names = [ ".git"; "node_modules" ]
          UseGitignore = true }

    /// Patterns of one shape (anchored? dir-only?) within a run: exact
    /// names go in a set, wildcards in one alternation regex.
    type PatternSet =
        { Literals: Collections.Generic.HashSet<string>
          Wildcards: Regex option }

    /// A run of consecutive patterns with the same polarity. `Basename`
    /// sets (patterns without `/`) test the last segment; `Path` sets test
    /// the path relative to the `.gitignore` directory.
    type GitignoreRun =
        { Negated: bool
          Basename: PatternSet
          BasenameDirOnly: PatternSet
          Path: PatternSet
          PathDirOnly: PatternSet }

    /// One parsed `.gitignore`; `Base` is the `/`-canonical directory it
    /// lives in. Runs are in file order — the last match wins, as in git.
    /// `Any` merges every pattern regardless of polarity: most entries
    /// match nothing, so that one check short-circuits the ordered walk.
    type Gitignore =
        { Base: string
          Runs: GitignoreRun list
          Any: GitignoreRun }

    let private isLiteral (glob: string) =
        glob.IndexOfAny [| '*'; '?'; '['; '\\' |] < 0

    /// gitignore glob → regex fragment (no anchors).
    let private globToRegex (glob: string) =
        let sb = StringBuilder()
        let mutable i = 0

        while i < glob.Length do
            match glob[i] with
            | '*' when i + 1 < glob.Length && glob[i + 1] = '*' ->
                if i + 2 < glob.Length && glob[i + 2] = '/' then
                    sb.Append "(.*/)?" |> ignore
                    i <- i + 3
                else
                    sb.Append ".*" |> ignore
                    i <- i + 2
            | '*' ->
                sb.Append "[^/]*" |> ignore
                i <- i + 1
            | '?' ->
                sb.Append "[^/]" |> ignore
                i <- i + 1
            | '[' ->
                match glob.IndexOf(']', i + 1) with
                | close when close > i ->
                    sb.Append(glob.Substring(i, close - i + 1).Replace("[!", "[^")) |> ignore
                    i <- close + 1
                | _ ->
                    sb.Append "\\[" |> ignore
                    i <- i + 1
            | '\\' when i + 1 < glob.Length ->
                sb.Append(Regex.Escape(string glob[i + 1])) |> ignore
                i <- i + 2
            | c ->
                sb.Append(Regex.Escape(string c)) |> ignore
                i <- i + 1

        sb.ToString()

    /// A `.gitignore` line as (negated, anchored, dirOnly, glob); None for
    /// blanks and comments.
    let private parseLine (raw: string) =
        let line = raw.TrimEnd('\r', ' ')

        if line.Length = 0 || line.StartsWith("#", StringComparison.Ordinal) then
            None
        else
            let negated = line.StartsWith("!", StringComparison.Ordinal)
            let body = (if negated then line.Substring 1 else line)

            // `\#` and `\!` are literal leading characters, not syntax.
            let body =
                if
                    body.StartsWith("\\#", StringComparison.Ordinal)
                    || body.StartsWith("\\!", StringComparison.Ordinal)
                then
                    body.Substring 1
                else
                    body

            let dirOnly = body.EndsWith("/", StringComparison.Ordinal)
            let body = body.TrimEnd '/'
            let anchored = body.Contains '/'
            let body = body.TrimStart '/'

            if body.Length = 0 then
                None
            else
                Some(negated, anchored, dirOnly, body)

    /// Compiled alternations by pattern text: every scan re-parses every
    /// `.gitignore`, and vendored trees repeat the same files many times.
    let private compiledAlternations = ConcurrentDictionary<string, Regex>()

    /// One pattern set per shape. Literal names (most of any `.gitignore`)
    /// are a hash lookup; wildcards share one non-backtracking (DFA) regex
    /// per shape: linear-time matching, which beats the interpreted engine
    /// several times over across a large tree, at a one-off construction
    /// cost the cache amortizes. The automaton size limit falls back to
    /// interpreting rather than failing the scan.
    let private compileRun (negated: bool) (patterns: (bool * bool * string) list) =
        let patternSet (globs: string list) =
            let literals, wildcards = globs |> List.partition isLiteral

            let wildcards =
                match wildcards with
                | [] -> None
                | _ ->
                    let pattern = "^(?:" + String.Join("|", wildcards |> List.map globToRegex) + ")$"

                    compiledAlternations.GetOrAdd(
                        pattern,
                        fun text ->
                            try
                                Regex(text, RegexOptions.NonBacktracking ||| RegexOptions.CultureInvariant)
                            with :? NotSupportedException ->
                                Regex(text, RegexOptions.CultureInvariant)
                    )
                    |> Some

            { Literals = Collections.Generic.HashSet<string>(literals, StringComparer.Ordinal)
              Wildcards = wildcards }

        let pick anchored dirOnly =
            patterns
            |> List.choose (fun (a, d, glob) -> if a = anchored && d = dirOnly then Some glob else None)
            |> patternSet

        { Negated = negated
          Basename = pick false false
          BasenameDirOnly = pick false true
          Path = pick true false
          PathDirOnly = pick true true }

    /// Parse `.gitignore` text: comments, negation (`!`), directory-only
    /// (`dir/`), anchoring (`/x`, `a/b`), `*`, `?`, `**`, `[...]`.
    let parseGitignore (baseDir: string) (text: string) : Gitignore =
        let runs = ResizeArray<GitignoreRun>()
        let mutable current: (bool * bool * string) list = []
        let mutable currentNegated = false

        let flush () =
            if not current.IsEmpty then
                runs.Add(compileRun currentNegated (List.rev current))
                current <- []

        for raw in text.Split '\n' do
            match parseLine raw with
            | Some(negated, anchored, dirOnly, fragment) ->
                if negated <> currentNegated then
                    flush ()
                    currentNegated <- negated

                current <- (anchored, dirOnly, fragment) :: current
            | None -> ()

        flush ()

        let all =
            [ for raw in text.Split '\n' do
                  match parseLine raw with
                  | Some(_, anchored, dirOnly, fragment) -> anchored, dirOnly, fragment
                  | None -> () ]

        { Base = baseDir
          Runs = List.ofSeq runs
          Any = compileRun false all }

    let private hits (set: PatternSet) (input: string) =
        set.Literals.Contains input
        || (match set.Wildcards with
            | Some regex -> regex.IsMatch input
            | None -> false)

    let private runMatches (run: GitignoreRun) (name: string) (relative: string) (isDirectory: bool) =
        hits run.Basename name
        || hits run.Path relative
        || (isDirectory && (hits run.BasenameDirOnly name || hits run.PathDirOnly relative))

    /// Outer files first, last matching run wins — so a nested
    /// `.gitignore` overrides its ancestors, as in git.
    let matchesGitignore (files: Gitignore list) (path: string) (isDirectory: bool) =
        let name = path.Substring(path.LastIndexOf '/' + 1)
        let mutable ignored = false

        for file in files do
            if path.StartsWith(file.Base + "/", StringComparison.Ordinal) then
                let relative = path.Substring(file.Base.Length + 1)

                if runMatches file.Any name relative isDirectory then
                    for run in file.Runs do
                        if runMatches run name relative isDirectory then
                            ignored <- not run.Negated

        ignored

    let isIgnored (rules: IgnoreRules) (files: Gitignore list) (path: string) (isDirectory: bool) =
        let name = path.Substring(path.LastIndexOf '/' + 1)

        List.contains name rules.Names
        || (rules.UseGitignore && matchesGitignore files path isDirectory)


type FileNode =
    { Path: string
      Name: string
      IsDirectory: bool
      Children: FileNode list }

type WorkspaceEntry =
    { Path: string
      Name: string
      Depth: int
      IsDirectory: bool
      IsExpanded: bool
      IsSelected: bool }

type WorkspaceState =
    {
        RootPath: string
        Tree: FileNode option
        /// Flat path → node lookup, populated once in `setTree`. Replaces
        /// the recursive `tryPick` walk in `findNodeByPath`, which used to
        /// run several times per sidebar keypress.
        ByPath: Map<string, FileNode>
        /// Root-relative paths of every file (not directory) in sorted tree
        /// order, populated once in `setTree`. The file-picker completions
        /// used to re-flatten the whole tree on every prompt keystroke.
        Files: string list
        Expanded: Set<string>
        SelectedPath: string option
        /// Type-ahead search query (Finder / VS Code Explorer style).
        /// Empty when no search is in progress.
        SearchBuffer: string
    }

type SidebarAction =
    | SidebarNoOp
    | SidebarOpenFile of string

[<RequireQualifiedAccess>]
module Workspace =
    let create rootPath =
        { RootPath = rootPath
          Tree = None
          ByPath = Map.empty
          Files = []
          Expanded = Set.singleton rootPath
          SelectedPath = None
          SearchBuffer = "" }

    let private sortChildren (nodes: FileNode list) =
        nodes
        |> List.sortBy (fun node -> (not node.IsDirectory, node.Name.ToLowerInvariant()))

    let rec private flatten selected expanded depth (node: FileNode) =
        let entry =
            { Path = node.Path
              Name = node.Name
              Depth = depth
              IsDirectory = node.IsDirectory
              IsExpanded = node.IsDirectory && Set.contains node.Path expanded
              IsSelected = Some node.Path = selected }

        if node.IsDirectory && Set.contains node.Path expanded then
            entry :: (node.Children |> List.collect (flatten selected expanded (depth + 1)))
        else
            [ entry ]

    let visibleEntries workspace =
        match workspace.Tree with
        | Some tree -> flatten workspace.SelectedPath workspace.Expanded 0 tree
        | None -> []

    let private ensureSelected workspace =
        let visible = visibleEntries workspace

        match workspace.SelectedPath, visible with
        | Some selectedPath, _ when visible |> List.exists (fun entry -> entry.Path = selectedPath) -> workspace
        | _, first :: _ ->
            { workspace with
                SelectedPath = Some first.Path }
        | _ -> workspace

    /// Recursively sort a tree's children so `visibleEntries` doesn't have
    /// to sort on every keypress.
    let rec private preSort (node: FileNode) : FileNode =
        if node.IsDirectory then
            { node with
                Children = node.Children |> List.map preSort |> sortChildren }
        else
            node

    let rec private collectByPath (acc: Map<string, FileNode>) (node: FileNode) =
        let acc = Map.add node.Path node acc

        if node.IsDirectory then
            node.Children |> List.fold collectByPath acc
        else
            acc

    let rec collectFiles (rootPath: string) (acc: string list) (node: FileNode) =
        if node.IsDirectory then
            node.Children |> List.fold (collectFiles rootPath) acc
        else
            // Canonical `/` relative paths — GetRelativePath emits the OS
            // separator, so normalize for a platform-independent file list.
            Paths.norm (Path.GetRelativePath(rootPath, node.Path)) :: acc

    /// Pre-sort the tree, build the ByPath map, and collect file paths.
    /// Designed to run on the thread pool so the main dispatch thread
    /// only does a cheap assignment when WorkspaceLoaded arrives.
    let preCompute (rootPath: string) (tree: FileNode) : FileNode * Map<string, FileNode> * string list =
        let sorted = preSort tree
        sorted, collectByPath Map.empty sorted, collectFiles rootPath [] sorted |> List.rev

    /// Apply pre-computed tree data (from preCompute on the thread pool).
    let setTreeFromPrecomputed (sorted: FileNode, byPath: Map<string, FileNode>, files: string list) workspace =
        { workspace with
            Tree = Some sorted
            ByPath = byPath
            Files = files
            Expanded =
                if sorted.IsDirectory then
                    Set.add sorted.Path workspace.Expanded
                else
                    workspace.Expanded }
        |> ensureSelected

    let setTree (tree: FileNode) workspace =
        setTreeFromPrecomputed (preCompute workspace.RootPath tree) workspace

    let selectPath path workspace =
        { workspace with
            SelectedPath = Some path }
        |> ensureSelected

    /// Expand every ancestor directory of `path` and select it. Paths
    /// outside the workspace (or not yet scanned) are a no-op; revealing
    /// the root just selects it. ByPath membership is the in-tree test, so
    /// the walk terminates at the root (whose parent is never in ByPath).
    let revealPath (path: string) workspace =
        if not (Map.containsKey path workspace.ByPath) then
            workspace
        else
            let rec collect acc (current: string) =
                // Walk ancestors with the canonical `/` parent (not
                // Path.GetDirectoryName, which emits the OS separator and would
                // miss the `/`-keyed ByPath on Windows).
                match Paths.parent current with
                | Some parent when Map.containsKey parent workspace.ByPath -> collect (Set.add parent acc) parent
                | _ -> acc

            { workspace with
                Expanded = Set.union (collect Set.empty path) workspace.Expanded }
            |> selectPath path

    let moveSelection delta workspace =
        let visible = visibleEntries workspace

        match visible with
        | [] -> workspace
        | _ ->
            let currentIndex =
                workspace.SelectedPath
                |> Option.bind (fun path -> visible |> List.tryFindIndex (fun entry -> entry.Path = path))
                |> Option.defaultValue 0

            { workspace with
                SelectedPath = Some visible[max 0 (min (visible.Length - 1) (currentIndex + delta))].Path }

    let moveHome workspace =
        match visibleEntries workspace with
        | first :: _ ->
            { workspace with
                SelectedPath = Some first.Path }
        | [] -> workspace

    let moveEnd workspace =
        match visibleEntries workspace |> List.tryLast with
        | Some last ->
            { workspace with
                SelectedPath = Some last.Path }
        | None -> workspace

    let private findNodeByPath path workspace = Map.tryFind path workspace.ByPath

    let expandSelected workspace =
        match
            workspace.SelectedPath
            |> Option.bind (fun path -> findNodeByPath path workspace)
        with
        | Some node when node.IsDirectory ->
            { workspace with
                Expanded = Set.add node.Path workspace.Expanded }
        | _ -> workspace

    let tryCollapseSelected workspace =
        match
            visibleEntries workspace
            |> List.tryFind (fun entry -> Some entry.Path = workspace.SelectedPath)
        with
        | Some entry when entry.IsDirectory && entry.IsExpanded ->
            Some
                { workspace with
                    Expanded = Set.remove entry.Path workspace.Expanded }
        | _ -> None

    let selectParent workspace =
        match
            visibleEntries workspace
            |> List.tryFind (fun entry -> Some entry.Path = workspace.SelectedPath)
        with
        | Some entry ->
            match Paths.parent entry.Path with
            | Some parent -> selectPath parent workspace
            | None -> workspace
        | None -> workspace

    let activateSelected workspace =
        match
            workspace.SelectedPath
            |> Option.bind (fun path -> findNodeByPath path workspace)
        with
        | Some node when node.IsDirectory ->
            let expanded =
                if Set.contains node.Path workspace.Expanded then
                    Set.remove node.Path workspace.Expanded
                else
                    Set.add node.Path workspace.Expanded

            { workspace with Expanded = expanded }, SidebarNoOp
        | Some node -> workspace, SidebarOpenFile node.Path
        | None -> workspace, SidebarNoOp

    let clearSearch workspace =
        if workspace.SearchBuffer = "" then
            workspace
        else
            { workspace with SearchBuffer = "" }

    let private matchesIn (entries: WorkspaceEntry list) (needle: string) =
        if String.IsNullOrEmpty needle then
            []
        else
            entries
            |> List.filter (fun entry -> entry.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))

    /// VS Code / Finder-style type-ahead: extend the buffer if the extended
    /// query still matches anything; otherwise restart with just the new char;
    /// otherwise drop the buffer entirely (next press starts fresh).
    /// If the same query matches multiple entries and the current selection is
    /// already one of them, advance to the next match.
    let appendSearch (c: char) workspace =
        let entries = visibleEntries workspace
        let extended = workspace.SearchBuffer + string c
        let single = string c

        let newBuffer, matched =
            match matchesIn entries extended with
            | _ :: _ as ms -> extended, ms
            | [] ->
                match matchesIn entries single with
                | _ :: _ as ms -> single, ms
                | [] -> "", []

        if matched.IsEmpty then
            { workspace with SearchBuffer = "" }
        else
            let currentIsMatch =
                workspace.SelectedPath
                |> Option.exists (fun path -> matched |> List.exists (fun m -> m.Path = path))

            let target =
                if currentIsMatch && workspace.SearchBuffer = newBuffer then
                    // Same query re-typed — cycle to next matching entry.
                    let currentIdx =
                        matched |> List.findIndex (fun m -> Some m.Path = workspace.SelectedPath)

                    matched[(currentIdx + 1) % matched.Length]
                else
                    List.head matched

            { workspace with
                SearchBuffer = newBuffer
                SelectedPath = Some target.Path }

    /// Drop the last character from the search buffer and re-select the first
    /// match of the shortened query. If the buffer was empty or becomes empty,
    /// just clears it.
    let backspaceSearch workspace =
        if workspace.SearchBuffer.Length = 0 then
            workspace
        else
            let shorter = workspace.SearchBuffer.Substring(0, workspace.SearchBuffer.Length - 1)

            if shorter.Length = 0 then
                { workspace with SearchBuffer = "" }
            else
                let entries = visibleEntries workspace
                let matched = matchesIn entries shorter

                match matched with
                | first :: _ ->
                    { workspace with
                        SearchBuffer = shorter
                        SelectedPath = Some first.Path }
                | [] ->
                    { workspace with
                        SearchBuffer = shorter }
