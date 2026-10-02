# What the model was given for the answer-side misreadings of the rerank-off run (A8, A10, A14, A15): rebuilds each
# question's top 5 (replay, checked against the log's scores) and the app's exact prompt, saves the prompt in full to
# OUT_DIR/prompt-<id>.txt, and prints every excerpt line holding the expected figure (*), a trap or a units line.
# From the repo root, with Ollama running: python eval/answer-side-screens/step5_prompts.py OUT_DIR
import json, os, re, sys
sys.path.insert(0, 'tools')
import replay_recall as rr

OUT = sys.argv[1]
LOG, QS = 'eval/answer-side-norerank/answers.log', 'tools/answer-questions.txt'
WANT = ['A8', 'A10', 'A14', 'A15']
src = open('RagFilingExplorer.Local/Retrieval/RagAnswerService.cs', encoding='utf-8').read()
raw = src.split('private const string SystemPrompt = """\n', 1)[1].split('\n        """;', 1)[0]
system = '\n'.join(l[8:] if l.startswith('        ') else l for l in raw.split('\n'))
spec = {e['id']: e for e in json.load(open('tools/expected-answers.json', encoding='utf-8'))['questions']}
graded = {r['id']: r for r in json.load(open('eval/answer-side-norerank/answers.json', encoding='utf-8'))['results']}

index = rr.load_index('rag.structured.db'); c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}
blocks = rr.read_blocks(LOG, 27)
for q in rr.replay(LOG, QS, index, depth=5):
    name = rr.name_of(q['i'], 'answer')
    if name not in WANT:
        continue
    keys = rr.interleave(q['lists'])[:5]
    logged = [float(s) for s in re.findall(r'^\[\d+\] score=([\d.]+)', blocks[q['i'] - 1], re.M)[:5]]
    mine = [round(q['score'][k], 4) for k in keys]
    s = spec[name]
    figs = s['expect'] + s.get('traps', []) + s.get('conflicts', [])
    context = ''.join(f"--- Excerpt from {meta[k][0]}, section {heading[k]} ---\n{meta[k][2]}\n\n" for k in keys)
    prompt = f"[system]\n{system}\n\n[user]\nContext excerpts:\n\n{context}\nQuestion: {q['question']}"
    open(os.path.join(OUT, f'prompt-{name}.txt'), 'w', encoding='utf-8').write(prompt)
    print(f"\n=== {name}: {q['question']}")
    print(f"    expect {s['expect']}  traps {s.get('traps', [])}   top-5 scores match log: {mine == logged}")
    print(f"    answer: {graded[name]['answer'][:260]}")
    for n, k in enumerate(keys, 1):
        print(f"  [{n}] {meta[k][1]:<20} {heading[k][-70:]}")
        lines = meta[k][2].split('\n')
        for j, line in enumerate(lines):
            if any(f in line for f in figs) or re.search(r'\((in|In) (millions|thousands)|except per share', line):
                hit = [f for f in figs if f in line]
                print(f"       {'*' if any(f in line for f in s['expect']) else ' '} {line.strip()[:200]}" + (f"   <- {hit}" if hit else ''))
