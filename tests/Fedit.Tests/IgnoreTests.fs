module Fedit.Tests.IgnoreTests

// Ignore rules: the `.gitignore` matcher (`Ignore`), the scanner that
// applies it (`Runtime.scanWorkspace`), the config keys that carry it, and a
// differential check against git itself as the oracle.

open System
open System.Diagnostics
open System.IO
open Fedit
open Xunit
open FsUnit.Xunit

// -- helpers ------------------------------------------------------------------

let private parse (text: string) = [ Ignore.parseGitignore "/root" text ]

let private matches (text: string) (path: string) (isDirectory: bool) =
    Ignore.matchesGitignore (parse text) path isDirectory

/// A throwaway directory populated from `entries`: `a/b.txt` is a file,
/// `a/b/` a directory. `.gitignore` contents go in as ordinary files.
let private tempTree (entries: (string * string) list) =
    let root =
        Path.Combine(Path.GetTempPath(), "fedit-ignore-" + Path.GetRandomFileName())

    Directory.CreateDirectory root |> ignore

    for relative, contents in entries do
        let full = Path.Combine(root, relative)

        if relative.EndsWith "/" then
            Directory.CreateDirectory full |> ignore
        else
            Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
            File.WriteAllText(full, contents)

    Paths.norm root

let private file relative = relative, ""

/// Root-relative `/` paths of every file the scanner kept, sorted.
let private scannedFiles (rules: IgnoreRules) (root: string) =
    let tree, _ = Runtime.scanWorkspace rules root

    Workspace.collectFiles root [] tree |> List.sort

let private withTree entries (body: string -> unit) =
    let root = tempTree entries

    try
        body root
    finally
        Directory.Delete(root, true)

// -- pattern semantics ----------------------------------------------------------

[<Theory>]
// literal basenames match at any depth
[<InlineData("foo", "/root/foo", false, true)>]
[<InlineData("foo", "/root/a/b/foo", false, true)>]
[<InlineData("foo", "/root/foobar", false, false)>]
[<InlineData("foo", "/root/afoo", false, false)>]
[<InlineData("foo", "/root/foo", true, true)>]
// directory-only
[<InlineData("foo/", "/root/foo", true, true)>]
[<InlineData("foo/", "/root/foo", false, false)>]
[<InlineData("foo/", "/root/a/foo", true, true)>]
[<InlineData("/build/", "/root/build", true, true)>]
[<InlineData("/build/", "/root/build", false, false)>]
[<InlineData("/build/", "/root/a/build", true, false)>]
// anchoring: a slash anywhere but the end anchors to the base
[<InlineData("/foo", "/root/foo", false, true)>]
[<InlineData("/foo", "/root/a/foo", false, false)>]
[<InlineData("a/b", "/root/a/b", false, true)>]
[<InlineData("a/b", "/root/x/a/b", false, false)>]
[<InlineData("a/b", "/root/a/b/c", false, false)>]
// `*` matches within one segment, including nothing and leading dots
[<InlineData("*.log", "/root/x.log", false, true)>]
[<InlineData("*.log", "/root/a/b/x.log", false, true)>]
[<InlineData("*.log", "/root/.log", false, true)>]
[<InlineData("*.log", "/root/x.logx", false, false)>]
[<InlineData("*.log", "/root/x.log/y", false, false)>]
[<InlineData("a/*.js", "/root/a/x.js", false, true)>]
[<InlineData("a/*.js", "/root/a/b/x.js", false, false)>]
[<InlineData("foo*bar", "/root/foobar", false, true)>]
[<InlineData("foo*bar", "/root/fooXYbar", false, true)>]
[<InlineData("foo*bar", "/root/foo/bar", false, false)>]
[<InlineData("*", "/root/anything", false, true)>]
// `?` is exactly one non-slash character
[<InlineData("?.txt", "/root/a.txt", false, true)>]
[<InlineData("?.txt", "/root/ab.txt", false, false)>]
[<InlineData("?.txt", "/root/.txt", false, false)>]
// character classes, negated classes, ranges
[<InlineData("[ab].txt", "/root/a.txt", false, true)>]
[<InlineData("[ab].txt", "/root/c.txt", false, false)>]
[<InlineData("[!ab].txt", "/root/c.txt", false, true)>]
[<InlineData("[!ab].txt", "/root/a.txt", false, false)>]
[<InlineData("[a-c].txt", "/root/b.txt", false, true)>]
[<InlineData("[a-c].txt", "/root/d.txt", false, false)>]
[<InlineData("*.py[cod]", "/root/x.pyc", false, true)>]
[<InlineData("*.py[cod]", "/root/x.py", false, false)>]
[<InlineData("[Bb]in/", "/root/src/Bin", true, true)>]
[<InlineData("[Bb]in/", "/root/src/bin", true, true)>]
[<InlineData("[Bb]in/", "/root/src/bin", false, false)>]
// `**`: leading, trailing, middle, bare
[<InlineData("**/foo", "/root/foo", false, true)>]
[<InlineData("**/foo", "/root/a/b/foo", false, true)>]
[<InlineData("**/foo", "/root/a/foox", false, false)>]
[<InlineData("foo/**", "/root/foo/x", false, true)>]
[<InlineData("foo/**", "/root/foo/a/b/c", false, true)>]
[<InlineData("foo/**", "/root/foo", true, false)>]
[<InlineData("foo/**", "/root/x/foo/a", false, false)>]
[<InlineData("a/**/b", "/root/a/b", false, true)>]
[<InlineData("a/**/b", "/root/a/x/b", false, true)>]
[<InlineData("a/**/b", "/root/a/x/y/z/b", false, true)>]
[<InlineData("a/**/b", "/root/a/xb", false, false)>]
[<InlineData("**", "/root/deep/anything", false, true)>]
// dotfiles are ordinary names
[<InlineData(".env", "/root/.env", false, true)>]
[<InlineData(".env", "/root/sub/.env", false, true)>]
[<InlineData(".env*", "/root/.env.local", false, true)>]
// case-sensitive
[<InlineData("Foo", "/root/foo", false, false)>]
let ``pattern matches like git`` (pattern: string) (path: string) (isDirectory: bool) (expected: bool) =
    matches pattern path isDirectory |> should equal expected

[<Theory>]
[<InlineData("# just a comment\n", "/root/anything", false)>]
[<InlineData("\n\n   \n", "/root/anything", false)>]
[<InlineData("\\#literal", "/root/#literal", true)>]
[<InlineData("\\#literal", "/root/literal", false)>]
[<InlineData("\\!bang", "/root/!bang", true)>]
[<InlineData("!bang", "/root/bang", false)>]
[<InlineData("foo   ", "/root/foo", true)>]
[<InlineData("foo\r\nbar\r\n", "/root/bar", true)>]
[<InlineData("foo\r\nbar\r\n", "/root/foo", true)>]
[<InlineData("foo\\*", "/root/foo*", true)>]
[<InlineData("foo\\*", "/root/foox", false)>]
[<InlineData("/", "/root/anything", false)>]
[<InlineData("!", "/root/anything", false)>]
[<InlineData("a\\ b", "/root/a b", true)>]
let ``parsing handles comments, blanks, escapes, line endings and trailing spaces``
    (text: string)
    (path: string)
    (expected: bool)
    =
    matches text path false |> should equal expected

[<Fact>]
let ``an empty gitignore has no runs and matches nothing`` () =
    let parsed = Ignore.parseGitignore "/root" ""
    parsed.Runs |> should be Empty
    Ignore.matchesGitignore [ parsed ] "/root/x" false |> should equal false

// -- ordering: last match wins ---------------------------------------------------

[<Fact>]
let ``negation after a pattern re-includes`` () =
    let text = "*.log\n!keep.log\n"
    matches text "/root/keep.log" false |> should equal false
    matches text "/root/other.log" false |> should equal true
    matches text "/root/sub/keep.log" false |> should equal false

[<Fact>]
let ``a later pattern overrides an earlier negation`` () =
    let text = "*.log\n!keep.log\nkeep.log\n"
    matches text "/root/keep.log" false |> should equal true

[<Fact>]
let ``negation before the pattern has no effect`` () =
    let text = "!keep.log\n*.log\n"
    matches text "/root/keep.log" false |> should equal true

[<Fact>]
let ``polarity runs interleave correctly across several flips`` () =
    // ignore all .txt, re-include a*, re-ignore ab*, re-include abc.txt
    let text = "*.txt\n!a*.txt\nab*.txt\n!abc.txt\n"
    matches text "/root/x.txt" false |> should equal true
    matches text "/root/a.txt" false |> should equal false
    matches text "/root/ab.txt" false |> should equal true
    matches text "/root/abc.txt" false |> should equal false

[<Fact>]
let ``negated directory-only pattern only re-includes directories`` () =
    let text = "build\n!build/\n"
    matches text "/root/build" true |> should equal false
    matches text "/root/build" false |> should equal true

// -- nesting and base scoping -----------------------------------------------------

[<Fact>]
let ``nested gitignore overrides its ancestors`` () =
    let files =
        [ Ignore.parseGitignore "/root" "*.log"
          Ignore.parseGitignore "/root/keep" "!*.log" ]

    Ignore.matchesGitignore files "/root/keep/a.log" false |> should equal false

    Ignore.matchesGitignore files "/root/keep/deep/a.log" false
    |> should equal false

    Ignore.matchesGitignore files "/root/other/a.log" false |> should equal true

[<Fact>]
let ``nested gitignore can add ignores the root does not have`` () =
    let files =
        [ Ignore.parseGitignore "/root" ""; Ignore.parseGitignore "/root/sub" "*.tmp" ]

    Ignore.matchesGitignore files "/root/sub/x.tmp" false |> should equal true
    Ignore.matchesGitignore files "/root/x.tmp" false |> should equal false

[<Fact>]
let ``a gitignore applies only below its own directory`` () =
    let files = [ Ignore.parseGitignore "/root/sub" "secret" ]
    Ignore.matchesGitignore files "/root/sub/secret" false |> should equal true
    Ignore.matchesGitignore files "/root/other/secret" false |> should equal false
    // the directory holding the file is not "below" it
    Ignore.matchesGitignore files "/root/sub" true |> should equal false
    // and a sibling whose name merely shares the prefix is unaffected
    Ignore.matchesGitignore files "/root/subway/secret" false |> should equal false

[<Fact>]
let ``anchored patterns anchor to the gitignore's own directory`` () =
    let files = [ Ignore.parseGitignore "/root/sub" "/x" ]
    Ignore.matchesGitignore files "/root/sub/x" false |> should equal true
    Ignore.matchesGitignore files "/root/sub/deep/x" false |> should equal false

[<Fact>]
let ``the ordered walk runs outer to inner, so an outer negation loses to an inner ignore`` () =
    let files =
        [ Ignore.parseGitignore "/root" "*.log\n!important.log"
          Ignore.parseGitignore "/root/sub" "important.log" ]

    Ignore.matchesGitignore files "/root/important.log" false |> should equal false

    Ignore.matchesGitignore files "/root/sub/important.log" false
    |> should equal true

// -- IgnoreRules -------------------------------------------------------------------

[<Fact>]
let ``ignored names apply at any depth regardless of gitignore`` () =
    let rules =
        { Names = [ "node_modules"; ".git" ]
          UseGitignore = false }

    Ignore.isIgnored rules [] "/root/node_modules" true |> should equal true
    Ignore.isIgnored rules [] "/root/a/b/node_modules" true |> should equal true
    Ignore.isIgnored rules [] "/root/a/.git" true |> should equal true
    Ignore.isIgnored rules [] "/root/node_modules_backup" true |> should equal false
    Ignore.isIgnored rules [] "/root/a/x.log" false |> should equal false

[<Fact>]
let ``useGitignore toggles the gitignore contribution only`` () =
    let files = parse "*.log"

    let off =
        { Names = [ ".git" ]
          UseGitignore = false }

    let on = { off with UseGitignore = true }

    Ignore.isIgnored off files "/root/a/x.log" false |> should equal false
    Ignore.isIgnored on files "/root/a/x.log" false |> should equal true
    Ignore.isIgnored off files "/root/.git" true |> should equal true
    Ignore.isIgnored on files "/root/.git" true |> should equal true

[<Fact>]
let ``an empty names list ignores nothing by name`` () =
    let rules = { Names = []; UseGitignore = false }
    Ignore.isIgnored rules [] "/root/.git" true |> should equal false

[<Fact>]
let ``defaults skip git metadata and node_modules with gitignore on`` () =
    Ignore.defaults.Names |> should equal [ ".git"; "node_modules" ]
    Ignore.defaults.UseGitignore |> should equal true

// -- the scanner ---------------------------------------------------------------------

[<Fact>]
let ``scanner prunes ignored directories and skips ignored files`` () =
    withTree
        [ ".gitignore", "*.log\nbuild/\n/top-only\n"
          file "keep.txt"
          file "x.log"
          file "top-only"
          file "sub/top-only"
          file "build/out.js"
          file "build/deep/more.js"
          file "sub/build/o.js"
          file "sub/keep.md"
          file "sub/y.log"
          file "notbuild/build"
          "empty-dir/", "" ]
        (fun root ->
            scannedFiles Ignore.defaults root
            |> should equal [ ".gitignore"; "keep.txt"; "notbuild/build"; "sub/keep.md"; "sub/top-only" ])

[<Fact>]
let ``scanner honours nested gitignore files and their negations`` () =
    withTree
        [ ".gitignore", "*.log\n"
          "keep/.gitignore", "!*.log\n*.tmp\n"
          file "a.log"
          file "keep/b.log"
          file "keep/deep/c.log"
          file "keep/d.tmp"
          file "other/e.log"
          file "other/f.tmp" ]
        (fun root ->
            scannedFiles Ignore.defaults root
            |> should
                equal
                [ ".gitignore"
                  "keep/.gitignore"
                  "keep/b.log"
                  "keep/deep/c.log"
                  "other/f.tmp" ])

[<Fact>]
let ``scanner cannot re-include a file inside an ignored directory, as in git`` () =
    withTree
        [ ".gitignore", "build/\n!build/keep.txt\n"
          file "build/keep.txt"
          file "build/drop.txt" ]
        (fun root -> scannedFiles Ignore.defaults root |> should equal [ ".gitignore" ])

[<Fact>]
let ``scanner skips ignored names at any depth even with gitignore off`` () =
    withTree
        [ ".gitignore", "*.log\n"
          file ".git/HEAD"
          file "node_modules/pkg/index.js"
          file "src/node_modules/pkg/index.js"
          file "src/a.log"
          file "src/a.fs" ]
        (fun root ->
            scannedFiles
                { Ignore.defaults with
                    UseGitignore = false }
                root
            |> should equal [ ".gitignore"; "src/a.fs"; "src/a.log" ]

            scannedFiles Ignore.defaults root |> should equal [ ".gitignore"; "src/a.fs" ])

[<Fact>]
let ``scanner with custom names and no gitignore shows everything else`` () =
    withTree [ ".gitignore", "*\n"; file ".git/HEAD"; file "target/out"; file "src/a.fs" ] (fun root ->
        scannedFiles
            { Names = [ "target" ]
              UseGitignore = false }
            root
        |> should equal [ ".git/HEAD"; ".gitignore"; "src/a.fs" ])

[<Fact>]
let ``scanner reports a tree and no skipped entries for a readable ignored tree`` () =
    withTree [ ".gitignore", "*.log\n"; file "a.log"; file "b.txt" ] (fun root ->
        let tree, skipped = Runtime.scanWorkspace Ignore.defaults root
        skipped |> should equal 0
        tree.IsDirectory |> should equal true
        tree.Path |> should equal root)

[<Fact>]
let ``scanner treats an unreadable gitignore as absent`` () =
    withTree [ file "a.log"; file "b.txt" ] (fun root ->
        // A directory named `.gitignore` can't be read as a file.
        Directory.CreateDirectory(Path.Combine(root, "sub", ".gitignore")) |> ignore
        File.WriteAllText(Path.Combine(root, "sub", "c.log"), "")

        scannedFiles Ignore.defaults root
        |> should equal [ "a.log"; "b.txt"; "sub/c.log" ])

// -- config and effect wiring ------------------------------------------------------------

let private loadConfigJson (json: string) =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "config.json")

    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, json)

    try
        ConfigIO.loadFrom path []
    finally
        File.Delete path

[<Fact>]
let ``config parses ignoredNames and useGitignore`` () =
    let config, error =
        loadConfigJson """{ "ignoredNames": [" .git ", "target", ""], "useGitignore": false }"""

    error |> should equal None
    config.Ignore.Names |> should equal [ ".git"; "target" ]
    config.Ignore.UseGitignore |> should equal false

[<Fact>]
let ``config keeps defaults for missing or malformed ignore keys`` () =
    let config, _ =
        loadConfigJson """{ "ignoredNames": "node_modules", "useGitignore": "yes" }"""

    config.Ignore |> should equal Ignore.defaults

[<Fact>]
let ``config accepts an empty ignoredNames list`` () =
    let config, _ = loadConfigJson """{ "ignoredNames": [] }"""
    config.Ignore.Names |> should be Empty
    config.Ignore.UseGitignore |> should equal true

[<Fact>]
let ``startup and rescans carry the configured ignore rules into the scan effect`` () =
    let rules =
        { Names = [ "vendor" ]
          UseGitignore = false }

    let config =
        { Config.defaults Themes.defaultTheme with
            Ignore = rules }

    let model, startupEffects =
        Editor.init "/root" { Width = 80; Height = 24 } config []

    startupEffects |> should contain (ScanWorkspace("/root", rules))

    let _, rescanEffects = Editor.update WorkspaceChangedExternally model
    rescanEffects |> should equal [ ScanWorkspace("/root", rules) ]

// -- differential check against git ------------------------------------------------------

let private git (workingDirectory: string) (arguments: string) =
    let info =
        ProcessStartInfo(
            "git",
            arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    use proc = Process.Start info
    let stdout = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout

let private gitAvailable =
    lazy
        (try
            fst (git (Path.GetTempPath()) "--version") = 0
         with _ ->
             false)

/// Files git would leave untracked-and-unignored, root-relative with `/`.
/// `core.ignorecase` is pinned off (macOS defaults it on) and global
/// excludes are disabled so only the tree's own `.gitignore` files count.
let private gitVisibleFiles (root: string) =
    git root "init -q" |> ignore

    let code, stdout =
        git root "-c core.excludesFile=/dev/null -c core.ignorecase=false ls-files -o --exclude-standard"

    code |> should equal 0

    stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun line -> line.Trim())
    |> Array.toList
    |> List.sort

let private assertAgreesWithGit (entries: (string * string) list) =
    if gitAvailable.Value then
        withTree entries (fun root ->
            let expected = gitVisibleFiles root
            let actual = scannedFiles Ignore.defaults root
            let missing = expected |> List.except actual
            let extra = actual |> List.except expected
            let gitignores = entries |> List.filter (fun (p, _) -> p.EndsWith ".gitignore")

            Assert.True(
                missing.IsEmpty && extra.IsEmpty,
                $"scanner disagrees with git for %A{gitignores}\n  hidden by fedit but not git: %A{missing}\n  shown by fedit but ignored by git: %A{extra}"
            ))

/// A fixed set of paths that exercises every pattern shape in the pools
/// below: nested dirs, files sharing names with dirs, dotfiles, extensions.
let private fixturePaths =
    [ "a.txt"
      "b.log"
      "keep.log"
      "abc.txt"
      "ab.txt"
      ".env"
      ".env.local"
      "#hash"
      "!bang"
      "notes.md"
      "build/out.js"
      "build/deep/x.js"
      "src/build/y.js"
      "y/build"
      "src/main.fs"
      "src/bin/a.o"
      "src/Bin/b.o"
      "src/obj/c.o"
      "docs/a.md"
      "docs/deep/b.md"
      "a/b/c/file.txt"
      "a/x/b/file.txt"
      "y/b"
      "temp/x"
      "x/temp/y"
      "y/temp"
      "logs/2024/app.log"
      "cache/data.tmp"
      "x.pyc"
      "y.pyo"
      "z.py"
      "sub/keep.log"
      "sub/other.log"
      "sub/deep/keep.log"
      "sub/x.tmp"
      "sub/secret"
      "other/secret"
      "foo*"
      "foox"
      "one.txt"
      "two.txt" ]

let private rootPatternPool =
    [| "*.log"
       "!keep.log"
       "build/"
       "/build"
       "**/build"
       "src/build"
       "[Bb]in/"
       "obj"
       "docs/**"
       "a/**/b"
       "**/temp"
       "temp/"
       "*.py[cod]"
       "?.txt"
       "[ab].txt"
       "[!ab].txt"
       "ab*.txt"
       "!abc.txt"
       ".env*"
       "!.env"
       "\\#hash"
       "\\!bang"
       "foo\\*"
       "logs"
       "*.tmp"
       "# comment"
       ""
       "secret"
       "!secret"
       "/a.txt"
       "b" |]

let private nestedPatternPool =
    [| "!*.log"
       "*.tmp"
       "/secret"
       "!keep.log"
       "deep/"
       "other.log"
       "!x.tmp"
       "" |]

let private fixture (seed: int) =
    let random = Random seed

    let pick (pool: string[]) count =
        [ for _ in 1..count -> pool[random.Next pool.Length] ]

    let rootIgnore = String.Join("\n", pick rootPatternPool (random.Next(4, 12))) + "\n"

    let nestedIgnore =
        String.Join("\n", pick nestedPatternPool (random.Next(0, 4))) + "\n"

    [ ".gitignore", rootIgnore
      "sub/.gitignore", nestedIgnore
      yield! fixturePaths |> List.map file ]

[<Fact>]
let ``scanner agrees with git ls-files on hand-written pattern sets`` () =
    assertAgreesWithGit
        [ ".gitignore", "*.log\n!keep.log\nbuild/\n/a.txt\n**/temp\ndocs/**\n[Bb]in/\n*.py[cod]\n?.txt\n"
          "sub/.gitignore", "!*.log\n*.tmp\n/secret\n"
          yield! fixturePaths |> List.map file ]

    assertAgreesWithGit
        [ ".gitignore", "a/**/b\n\\#hash\n\\!bang\nfoo\\*\n.env*\n!.env\n[!ab].txt\nab*.txt\n!abc.txt\n"
          yield! fixturePaths |> List.map file ]

[<Fact>]
let ``scanner agrees with git ls-files across seeded random pattern sets`` () =
    for seed in 1..40 do
        assertAgreesWithGit (fixture seed)

/// Walk up from the test binary to the repository root, whose `.gitignore`
/// is the ~270-pattern `dotnet new gitignore` template — the case that
/// made the first matcher take 13 seconds.
let private repoGitignore () =
    let rec up (dir: string) =
        if isNull dir then
            None
        elif
            File.Exists(Path.Combine(dir, "justfile"))
            && File.Exists(Path.Combine(dir, ".gitignore"))
        then
            Some(File.ReadAllText(Path.Combine(dir, ".gitignore")))
        else
            up (Path.GetDirectoryName dir)

    up AppContext.BaseDirectory

[<Fact>]
let ``scanner agrees with git ls-files on the repository's own gitignore template`` () =
    match repoGitignore () with
    | None -> ()
    | Some template ->
        assertAgreesWithGit
            [ ".gitignore", template
              yield! fixturePaths |> List.map file
              file "bin/Debug/fedit.dll"
              file "obj/project.assets.json"
              file "src/Fedit/bin/x"
              file "src/Fedit/obj/x"
              file "project.user"
              file "a.suo"
              file ".vs/x"
              file "TestResults/r.trx"
              file "packages/x/lib.dll"
              file "node_modules_not_really/x" ]
