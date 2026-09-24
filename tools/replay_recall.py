# Retrieval recall check: for each question in tools/manual-questions.txt, replays the app's retrieval
# against a strategy's index (rag.<strategy>.db) - the stored vectors, the same filters the app printed, the same cosine distance, the
# same per-company interleave for multi-company questions - and reports whether the expected figure is
# in the top-5 context sent to the model. Deterministic (no LLM), so unlike a full question run it
# isn't affected by the chat model's run-to-run variation: it separates "retrieval missed it" from
# "the model misread it". Verified to reproduce the app's own --verbose scores exactly.
#
# The filters are read from a --verbose run's log rather than re-derived, so QueryIntentResolver's
# logic isn't duplicated here. From the repo root, with Ollama running and the index built:
#   dotnet run --project RagFilingExplorer.Local -- --verbose < tools/manual-questions.txt > run.log
#   python tools/replay_recall.py run.log tools/manual-questions.txt [rag.<strategy>.db]
# The index defaults to rag.markdown.db; pass the one matching the run's Chunking:Strategy (the log's
# first line names it).
#
# `expect` below is keyed by line number in manual-questions.txt (Q1-Q13 and Q17-Q22 follow
# docs/Manual-Test-Questions.md's order, then its edge cases, then Q23-Q24) - keep them in sync.
# Requires the app to have been built once (it loads the sqlite-vec extension from bin/).
import sqlite3, json, urllib.request, math, re, struct, glob, sys

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

blocks = open(log_path, encoding='utf-8').read().split('\n> ')[1:]
questions = open(questions_path, encoding='utf-8').read().split('\n')[:24]
expect = {1: ['758,376'], 2: ['43,056'], 3: ['18,821'], 4: ['182,935'], 5: ['31,977'], 6: ['2,255'],
          7: ['442,387'], 8: ['133,812'], 9: ['16,882'], 10: ['2,113'], 11: ['223,000'], 12: ['Austin'],
          13: ['Ernst & Young'], 14: ['45,183,036'], 15: ['10,981,201'], 16: ['55,596,993'],
          17: ['10,149,273'], 18: ['10,038,657'], 19: ['Los Gatos'], 20: ['331,839', '67,357'],
          21: [], 22: [], 23: ['12,227'], 24: ['27,034']}
hits = 0
answerable = 0
for i, (q, b) in enumerate(zip(questions, blocks), 1):
    first = b.split('\n')[0]
    files = re.findall(r'[A-Z]{4}-10K-\d{4}\.html', first) if first.startswith('(') else []
    m = re.search(r'statement type: (\w+)', first)
    st = m.group(1) if m else None
    qv = embed('search_query: ' + q)

    def search(f, top):
        cand = [k for k, (ff, tt, _) in meta.items() if (f is None or ff == f) and (st is None or tt == st)]
        return sorted(cand, key=lambda k: dist(qv, vec[k]))[:top]

    if len(files) <= 1:
        top = search(files[0] if files else None, 5)
    else:
        per = [search(f, math.ceil(5 / len(files))) for f in files]
        top = [l[r] for r in range(max(map(len, per))) for l in per if r < len(l)][:5]
    ctx = '\n'.join(meta[k][2] for k in top)
    missing = [e for e in expect[i] if e not in ctx]
    if expect[i]:
        answerable += 1
        hits += not missing
    print(f"Q{i:>2} in-context={'yes' if not missing else 'NO ' + str(missing)}  top1={dist(qv, vec[top[0]]):.4f}")
print(f"recall@5: {hits}/{answerable} answerable questions have the expected figure in context")
