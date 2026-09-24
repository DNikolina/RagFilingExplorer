# RagFilingExplorer

A local, zero-cost Retrieval-Augmented Generation (RAG) tool that answers questions about public
SEC 10-K filings, built in C#/.NET. Portfolio project — the goal is genuine, demonstrable RAG
understanding, not just a working demo.

## Build plan

See @docs/Implementation_Plan.md for the current-state reference: ground rules, prerequisites, the
pipeline as it ships, live constraints, and already-validated facts. Follow it directly rather than
re-deriving decisions already made there — the technical choices in it (stack, models, fallback
strategies) were validated against Microsoft's own documentation, not just general assumptions.

The full history behind those decisions (each step's outcome, every dead end and bug, how each was
found) is in `docs/Decision-Log.md`. It's deliberately not auto-loaded — read the relevant section
before revisiting a decision, and record new follow-ups there, adding a one-line summary to the plan's
Status or Live constraints only if the follow-up changes them.

## Working style for this project

- Correctness over speed. This is not a race against any deadline.
- Stop and check in at the decision points the plan calls out (e.g. filing selection, chunking
  approach, falling back from a preview library) rather than pushing straight through silently.
- Don't skip the testing step (Step 7) or treat it as optional — it's how "it works" gets verified
  rather than assumed.

## Test conventions (already surveyed - follow these, don't re-derive them)

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

## Environment

- .NET SDK, Ollama installed and running
- Models already pulled: `nomic-embed-text`, `llama3.1:8b` (the shipped default), and `qwen3.5:2b`
  (a local reasoning model, pulled to test as a reference — see Decision-Log.md's "Follow-up:
  reasoning-model support" for what that required)
- Hardware: 12th Gen Intel i7-12800H, 32GB RAM, Intel UHD integrated graphics — CPU-only inference,
  no GPU acceleration
- Python 3.12 + the `markitdown` pip package are also installed (added during Step 3 — see
  Decision-Log.md's Step 3 outcome for why). The pip Scripts directory is on the user PATH, so `markitdown` resolves as
  a bare command; a freshly started shell picks this up, but a shell already open when it was added
  won't until restarted.
