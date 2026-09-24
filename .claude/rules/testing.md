---
paths:
  - "RagFilingExplorer.Local.Tests/**"
---

# Test conventions (already surveyed - follow these, don't re-derive them)

<!-- Path-scoped: loads only when a file under RagFilingExplorer.Local.Tests/ is read, so sessions that
     never touch tests don't pay for it. CLAUDE.md points here for the case of writing a brand-new test
     file without opening an existing one first, which would not trigger the load. -->

- **Stack:** NUnit 4 + Moq, in `RagFilingExplorer.Local.Tests`. `NUnit.Framework` is a global using (in the
  .csproj) - don't add it per file. Constraint model only: `Assert.That(x, Is/Has/Does...)`, plus
  `Assert.Throws` / `Assert.ThrowsAsync` for exceptions.
- **Layout:** mirror the app's folders - `Chunking/`, `Retrieval/`, `VectorStore/`, root for root types.
  File `<Class>Tests.cs`, namespace `RagFilingExplorer.Local.Tests.<Folder>`, `[TestFixture] public class`.
  App types are `internal` and visible to tests via `InternalsVisibleTo` (`AssemblyInfo.cs`) - test them
  directly; never make a type public for a test.
- **Naming:** `Subject_Condition_ExpectedResult`, e.g. `Split_ItemHeadingWithNoSpaceAfterPeriod_IsStillTreatedAsABoundary`.
  Use `[TestCase(...)]` for input tables (see `QueryIntentResolverTests`).
- **A test that reproduces a real bug gets a comment** naming the filing and what broke, e.g. "NFLX: 21 of
  22 Item headings have no space after the period...". These comments are the regression record.
- **Fully offline:** no Ollama, no `rag.*.db`, no `markitdown`. Use the real tokenizer
  (`TiktokenTokenizer.CreateForModel("gpt-4")`, offline via `Data.Cl100kBase`) in `[OneTimeSetUp]`, and
  compute token budgets from it instead of hard-coding counts (see `TokenChunkerTests`).
- **Mock only the external boundaries:** `VectorStoreCollection<int, FilingChunkRecord>` and `IChatClient`,
  via Moq (`RagAnswerServiceTests.MakeMocks`). Castle needs the `DynamicProxyGenAssembly2` grant, already
  in `AssemblyInfo.cs`.
- **Files on disk:** a per-test temp dir, `Path.Combine(Path.GetTempPath(), $"<Fixture>-{Guid.NewGuid():N}")`,
  created in `[SetUp]` and deleted in `[TearDown]`.
- **Settings tests read the shipped `appsettings.json`** (`AppSettingsTests.LoadShippedSettings()`), so they
  assert what ships: `Chunking:Strategy` must be `"Markdown"` there, or two tests fail. Switch the
  strategy only temporarily, and switch it back.
- **HTML table fixtures** (`HtmlTableLinearizerTests`): build them with the `Tr(...)` helper, reducing a
  real filing's layout to the cells that matter while keeping column positions (colspan included), since
  positions are all the linearizer aligns by.
- **Running:** `dotnet test` from the repo root (`RagFilingExplorer.slnx`; `tools/LinearizeSpike` is
  deliberately outside it). Don't build or test while the app is running - the locked DLL fails the build.
  The test count is quoted in README.md and the plan's Status table - update both when adding tests.
