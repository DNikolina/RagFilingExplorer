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

## Test conventions

In `.claude/rules/testing.md` - loaded automatically when a test file is read. Before writing a new
test file without having opened an existing one, read that file first.

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
