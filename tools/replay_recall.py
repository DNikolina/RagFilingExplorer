# Retrieval recall check: for each question in tools/manual-questions.txt, replays the app's retrieval
# against a strategy's index (rag.<strategy>.db) - the stored vectors, the same filters the app printed,
# the same cosine distance, the same per-company interleave for multi-company questions - and reports
# where the expected figure ranks. Deterministic (no LLM), so unlike a full question run it isn't
# affected by the chat model's run-to-run variation: it separates "retrieval missed it" from "the model
# misread it". Verified to reproduce the app's own --verbose scores exactly.
#
# Reported per question group - the original 24 (Q1-Q24), the targeted questions aimed at what the
# Linearized strategy claims to fix (T1-T10), and the routing tests whose keyword route excludes the
# answer (R1-R3, misses by design until routing changes):
#   recall@1/@3/@5 - share of answerable questions whose expected figure(s) are all within the top k
#   MRR            - mean of 1/rank (0 beyond the top 25); a question with several expected figures
#                    (Q20's two companies) ranks at its *last* figure's position
# recall@5 is what the model actually sees (GenerationTopK = 5). The rank metrics exist because
# recall@5 saturated at 22/22 on both strategies, so it could no longer tell them apart.
#
# The filters are read from a --verbose run's log rather than re-derived, so QueryIntentResolver's
# logic isn't duplicated here. A hybrid run (Retrieval:Search = Hybrid, v2 step 2) is recognised by the "(keywords: ...)"
# line under each filter line and replayed as the app runs it: the vector ranking and the FTS5 bm25 ranking (the
# logged query, against the index's own chunks_fts table) within the company filter, plus - when a statement type
# was resolved - the vector ranking within that statement, each cut to HYBRID_CANDIDATES and fused by reciprocal
# rank fusion (k = 60). The app creates chunks_fts on its first hybrid start. From the repo root, with Ollama running and the index built:
#   dotnet run --project RagFilingExplorer.Local -- --verbose < tools/manual-questions.txt > run.log
#   python tools/replay_recall.py run.log tools/manual-questions.txt [rag.<strategy>.db]
# The index defaults to rag.markdown.db; pass the one matching the run's Chunking:Strategy (the log's
# first line names it).
#
# `expect` below is keyed by line number in manual-questions.txt (Q1-Q13 and Q17-Q22 follow
# docs/Manual-Test-Questions.md's order, then its edge cases, then Q23-Q24, then T1-T10 and R1-R3) -
# keep them in sync. Pass tools/heldout-questions.txt (and its run's log) for H1-H15, whose expected figures
# are in `expect_heldout`. Requires the app to have been built once (it loads the sqlite-vec extension from bin/).
import sqlite3, json, urllib.request, math, re, struct, glob, sys

TOP = 25
HYBRID_CANDIDATES = 50  # appsettings.json's Retrieval:HybridCandidates
RRF_K = 60  # RankFusion.K

log_path, questions_path = sys.argv[1], sys.argv[2]
db_path = sys.argv[3] if len(sys.argv) > 3 else 'rag.markdown.db'
c = sqlite3.connect(f'file:{db_path}?mode=ro', uri=True)
c.enable_load_extension(True)
c.load_extension(glob.glob('RagFilingExplorer.Local/bin/**/win-x64/native/vec0.dll', recursive=True)[0][:-4])
vec = {k: struct.unpack('768f', b) for k, b in c.execute('select Key, Text from vec_chunks')}
meta = {k: (f, t, ct) for k, f, t, ct in c.execute('select Key, SourceFiling, StatementType, Content from chunks')}


def embed(text):
    req = urllib.request.Request('http://localhost:11434/api/embed',
                                 data=json.dumps({"model": "nomic-embed-text", "input": text}).encode(),
                                 headers={'Content-Type': 'application/json'})
    return json.load(urllib.request.urlopen(req))['embeddings'][0]


def dist(a, b):
    d = sum(x * y for x, y in zip(a, b))
    return 1 - d / (math.sqrt(sum(x * x for x in a)) * math.sqrt(sum(y * y for y in b)))


questions = [q for q in open(questions_path, encoding='utf-8').read().split('\n') if q.strip()]
# Split the log at the app's "> " prompts. Two things look like a prompt and aren't: the final, empty
# "> " (the blank line that ends the session), and an answer line the model wrote as a Markdown
# blockquote ("> Share repurchase program — ..."), which once shifted every later question onto the
# wrong block. A real prompt block always opens with the filter line "(filtering ..."/"(searching ..."
# or, for an unfiltered question in a --verbose run, the retrieved-chunks header; anything else is
# merged back into the block before it.
blocks = []
for b in open(log_path, encoding='utf-8', errors='replace').read().split('\n> ')[1:]:
    if b.startswith('(') or b.startswith('--- Retrieved chunks') or not blocks:
        blocks.append(b)
    else:
        blocks[-1] += '\n> ' + b
blocks = [b for b in blocks if b.strip()][:len(questions)]
expect = {1: ['758,376'], 2: ['43,056'], 3: ['18,821'], 4: ['182,935'], 5: ['31,977'], 6: ['2,255'],
          7: ['442,387'], 8: ['133,812'], 9: ['16,882'], 10: ['2,113'], 11: ['223,000'], 12: ['Austin'],
          13: ['Ernst & Young'], 14: ['45,183,036'], 15: ['10,981,201'], 16: ['55,596,993'],
          17: ['10,149,273'], 18: ['10,038,657'], 19: ['Los Gatos'], 20: ['331,839', '67,357'],
          21: [], 22: [], 23: ['12,227'], 24: ['27,034'],
          # T1-T10: targeted at mid-table rows, split layouts and MD&A/notes tables
          25: ['2,243'], 26: ['3,500'], 27: ['466,095'], 28: ['3,603'], 29: ['48,562'], 30: ['7,952'],
          31: ['4,301'], 32: ['91.47'], 33: ['4,743'], 34: ['96,795'],
          # R1-R3: routing tests - the keyword route excludes every chunk holding the answer
          35: ['9,916'], 36: ['1,274'], 37: ['137,791'],
          # V1-V3: found in the 2026-09-25 manual pass - Q10's mislabel for 2024 (the right line is 940,
          # not the 'attributable to Nasdaq' 942; 1,124 is the same row's 2023 value, format-independent), a real
          # year absent from the filing, and asked-for arithmetic
          38: ['1,124'], 39: [], 40: ['10,149,273']}

# H1-H15, keyed by line number in tools/heldout-questions.txt (expected answers in docs/Manual-Test-Questions.md).
# Each value is the figure as the filing prints it. H4 asks for a difference the filing states only rounded
# ("$3.1 billion"), so it ranks at the row holding both inputs; H8 and H15 are negatives.
expect_heldout = {1: ['35,562'], 2: ['19%'], 3: ['12,405'], 4: ['35,562', '32,488'], 5: ['141,000'],
                  6: ['10,272'], 7: ['5.83'], 8: [], 9: ['5,249'], 10: ['9,525'], 11: ['New York, New York'],
                  12: ['16,000'], 13: ['9,033,681'], 14: ['1,776'], 15: []}
heldout = 'heldout' in questions_path
if heldout:
    expect = expect_heldout


def group_of(i):
    if heldout:
        return 'H1-H15'
    return 'Q1-Q24' if i <= 24 else 'T1-T10' if i <= 34 else 'R1-R3' if i <= 37 else 'V1-V3'


def name_of(i):
    if heldout:
        return f'H{i}'
    return f'Q{i}' if i <= 24 else f'T{i - 24}' if i <= 34 else f'R{i - 34}' if i <= 37 else f'V{i - 37}'


ranks = {}
for i, (q, b) in enumerate(zip(questions, blocks), 1):
    lines = b.split('\n')
    first = lines[0] if not lines[0].startswith('(keywords: ') else ''
    files = re.findall(r'[A-Z]{4}-10K-\d{4}\.html', first) if first.startswith('(') else []
    m = re.search(r'statement type: (\w+)', first)
    st = m.group(1) if m else None
    kw = next((l[len('(keywords: '):-1] for l in lines[:2] if l.startswith('(keywords: ')), None)
    qv = embed('search_query: ' + q)
    score = {}

    def vector(f, s):
        cand = [k for k, (ff, tt, _) in meta.items() if (f is None or ff == f) and (s is None or tt == s)]
        return sorted(cand, key=lambda k: dist(qv, vec[k]))

    def keyword(f):
        if kw == 'none':
            return []
        return [k for (k,) in c.execute(
            'select c.Key from chunks_fts join chunks c on c.Key = chunks_fts.rowid '
            'where chunks_fts match ? and (? is null or c.SourceFiling = ?) order by bm25(chunks_fts), c.Key limit ?',
            (kw, f, f, HYBRID_CANDIDATES))]

    def search(f):
        if kw is None:
            return vector(f, st)[:TOP]
        lists = [vector(f, None)[:HYBRID_CANDIDATES], keyword(f)] + ([vector(f, st)[:HYBRID_CANDIDATES]] if st else [])
        fused = {}
        for ranking in lists:
            for r, k in enumerate(ranking, 1):
                fused[k] = fused.get(k, 0) + 1 / (RRF_K + r)
        score.update(fused)
        return sorted(fused, key=lambda k: -fused[k])[:TOP]

    if len(files) <= 1:
        ranked = search(files[0] if files else None)
    else:
        per = [search(f) for f in files]
        ranked = [l[r] for r in range(max(map(len, per))) for l in per if r < len(l)]
    top1 = '-' if not ranked else f"{score[ranked[0]]:.4f}" if kw is not None else f"{dist(qv, vec[ranked[0]]):.4f}"
    where = f"{files[0] if len(files) == 1 else ('+'.join(files) if files else 'all')}/{st or '-'}"
    if not expect[i]:
        print(f"{name_of(i):>3} (negative test, not scored)  filter={where}  top1={top1}")
        continue
    positions = [next((n for n, k in enumerate(ranked, 1) if e in meta[k][2]), None) for e in expect[i]]
    rank = None if None in positions else max(positions)
    ranks[i] = rank
    print(f"{name_of(i):>3} rank={rank if rank else '>' + str(TOP):>4}  in-top-5={'yes' if rank and rank <= 5 else 'NO '}  "
          f"filter={where}  top1={top1}")

print()
for group in ('Q1-Q24', 'T1-T10', 'R1-R3', 'V1-V3', 'H1-H15'):
    rs = [r for i, r in ranks.items() if group_of(i) == group]
    if not rs:
        continue
    at = lambda k: sum(1 for r in rs if r and r <= k)
    mrr = sum(1 / r for r in rs if r) / len(rs)
    print(f"{group}: recall@1 {at(1)}/{len(rs)}  recall@3 {at(3)}/{len(rs)}  recall@5 {at(5)}/{len(rs)}  MRR {mrr:.3f}")
