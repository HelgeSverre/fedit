module Fedit.Tests.Program

// Microsoft.NET.Test.Sdk 18 + xunit.runner.visualstudio 3.x generate the test
// entry point at build time (Microsoft.Testing.Platform). F# allows only one
// [<EntryPoint>] and it must be the last declaration in the last file, so a
// hand-written one collides with the generated Main (FS0433). We let the
// platform own the entry point; tests still run under VSTest via dotnet test.
