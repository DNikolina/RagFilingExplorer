# Is the reranker mis-scoring, or scoring as designed? For the four A questions it pushed out of the top 5 (A10, A15,
# A17, A27), recomputes ms-marco-MiniLM-L6-v2's scores in Python over each company's hybrid top 25 and compares them with
# the app's logged rerank= values in answer-side-baseline/answers.log; then shows each answer chunk's reranked position
# and where its figure falls in the model's 512-token window. Replay only - Ollama for query embeddings, no chat model.
# From the repo root: python eval/rerank-decision/rerank_check.py > eval/rerank-decision/rerank-check.txt
import os, re, sys
import numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'tools'))
import replay_recall as rr

LOG = r'eval\answer-side-baseline\answers.log'
QS = r'tools\answer-questions.txt'
WANT = {'A10', 'A15', 'A17', 'A27'}
root = os.path.join(os.environ['USERPROFILE'], 'models', 'cross-encoder', 'ms-marco-MiniLM-L6-v2')
tok = Tokenizer.from_file(os.path.join(root, 'tokenizer.json'))
tok.enable_truncation(max_length=512, strategy='only_second')
sess = ort.InferenceSession(os.path.join(root, 'onnx', 'model.onnx'), providers=['CPUExecutionProvider'])

index = rr.load_index('rag.structured.db'); c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}
company_line = {}
for f, content in c.execute("select SourceFiling, Content from chunks where Heading = 'Cover Page'"):
    m = re.match(r'(.+?) - annual report on Form (\S+) .*?\(fiscal year (\d{4})\)\. Common stock trading symbol: (\w+)', content)
    company_line[f] = f'{m.group(1)} ({m.group(4)}), Form {m.group(2)} for fiscal year {m.group(3)}.'
exp = rr.expected_for(QS)
blocks = rr.read_blocks(LOG, 100)

for q in rr.replay(LOG, QS, index, depth=50):
    name = rr.name_of(q['i'], 'answer')
    if name not in WANT:
        continue
    hybrid = rr.interleave([l[:25] for l in q['lists']])
    logged = {int(h): float(s) for s, h in re.findall(r'rerank=(-?[\d.]+) hybrid=#(\d+)', blocks[q['i'] - 1])}
    passages = [f"{company_line[meta[k][0]]}\nExcerpt from {meta[k][0]}, section {heading[k]}\n{meta[k][2]}" for k in hybrid]
    encs = tok.encode_batch([(q['question'], p) for p in passages])
    width = max(len(e.ids) for e in encs)
    pad = lambda xs: [x + [0] * (width - len(x)) for x in xs]
    feed = {'input_ids': np.array(pad([e.ids for e in encs]), dtype=np.int64),
            'attention_mask': np.array(pad([e.attention_mask for e in encs]), dtype=np.int64),
            'token_type_ids': np.array(pad([e.type_ids for e in encs]), dtype=np.int64)}
    py = sess.run(None, feed)[0][:, 0]
    diffs = [abs(py[i] - logged[i + 1]) for i in range(len(hybrid)) if i + 1 in logged]
    order = sorted(range(len(hybrid)), key=lambda i: -py[i])
    print(f'\n=== {name}: {q["question"]}  expect {exp[q["i"]]}')
    print(f'    python vs app rerank=: {len(diffs)} pairs compared, max |diff| {max(diffs):.3f}')
    for r, i in enumerate(order, 1):
        k = hybrid[i]; text = passages[i]
        hit = [f for f in exp[q['i']] if f in meta[k][2]]
        if r > 6 and not hit:
            continue
        note = ''
        if hit:
            pos = text.find(hit[0])
            e = encs[i]
            tokpos = next((j for j, (o, s) in enumerate(zip(e.offsets, e.sequence_ids)) if s == 1 and o[1] > pos), None)
            note = f'  <== ANSWER {hit[0]} at token {tokpos} of {len(e.ids)}'
            line = next(l for l in meta[k][2].split('\n') if hit[0] in l)
            note += f'\n          row: {line.strip()[:150]}'
        print(f'  rr#{r:<2} hybrid#{i + 1:<2} score {py[i]:6.2f}  {meta[k][1]:<20} {heading[k][-60:]}{note}')
