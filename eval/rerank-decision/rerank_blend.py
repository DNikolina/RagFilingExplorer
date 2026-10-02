# Replay-only comparison of four orderings of each company's hybrid top 25 (ms-marco-MiniLM-L6-v2, the app's passage text):
#   hybrid - no reranking;  rerank - the reranker's order alone (step 2b as built)
#   blend2 - RRF of two lists, k = 60: the 25 in hybrid order and the same 25 in reranker order
#   blend4 - the reranker's order as a fourth list in the app's own fusion: hybrid's fused RRF score + 1/(60 + rerank rank)
# Ties keep hybrid order. k is RankFusion.K, not tuned - choosing it here would tune on the held-out sets.
# From the repo root, on a hybrid run's log without reranking:
#   python eval/rerank-decision/rerank_blend.py LOG QUESTIONS OUT.json > OUT.txt
import os, re, sys, json
import numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'tools'))
import replay_recall as rr

log_path, questions_path, out_path = sys.argv[1:4]
K, DEPTH = 60, 25
root = os.path.join(os.environ['USERPROFILE'], 'models', 'cross-encoder', 'ms-marco-MiniLM-L6-v2')
tok = Tokenizer.from_file(os.path.join(root, 'tokenizer.json'))
tok.enable_truncation(max_length=512, strategy='only_second')
tok.enable_padding(pad_id=0, pad_token='[PAD]')
sess = ort.InferenceSession(os.path.join(root, 'onnx', 'model.onnx'), providers=['CPUExecutionProvider'])

index = rr.load_index('rag.structured.db'); c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}
company_line = {}
for f, content in c.execute("select SourceFiling, Content from chunks where Heading = 'Cover Page'"):
    m = re.match(r'(.+?) - annual report on Form (\S+) .*?\(fiscal year (\d{4})\)\. Common stock trading symbol: (\w+)', content)
    company_line[f] = f'{m.group(1)} ({m.group(4)}), Form {m.group(2)} for fiscal year {m.group(3)}.'
exp = rr.expected_for(questions_path)
qset = rr.question_set(questions_path)

VARIANTS = ['hybrid', 'rerank', 'blend2', 'blend4']
ranks = {v: {} for v in VARIANTS}
per_question = {}
for q in rr.replay(log_path, questions_path, index, depth=DEPTH):
    orders = {v: [] for v in VARIANTS}
    for lst in q['lists']:
        passages = [f"{company_line[meta[k][0]]}\nExcerpt from {meta[k][0]}, section {heading[k]}\n{meta[k][2]}" for k in lst]
        encs = tok.encode_batch([(q['question'], p) for p in passages])
        feed = {'input_ids': np.array([e.ids for e in encs], dtype=np.int64),
                'attention_mask': np.array([e.attention_mask for e in encs], dtype=np.int64),
                'token_type_ids': np.array([e.type_ids for e in encs], dtype=np.int64)}
        s = sess.run(None, feed)[0][:, 0]
        hyb = {k: r for r, k in enumerate(lst, 1)}
        rer_order = sorted(lst, key=lambda k: (-s[hyb[k] - 1], hyb[k]))
        rer = {k: r for r, k in enumerate(rer_order, 1)}
        orders['hybrid'].append(lst)
        orders['rerank'].append(rer_order)
        orders['blend2'].append(sorted(lst, key=lambda k: (-(1 / (K + hyb[k]) + 1 / (K + rer[k])), hyb[k])))
        orders['blend4'].append(sorted(lst, key=lambda k: (-(q['score'][k] + 1 / (K + rer[k])), hyb[k])))
    if not exp[q['i']]:
        continue
    name = rr.name_of(q['i'], qset)
    per_question[name] = {}
    for v in VARIANTS:
        r = rr.rank_of(rr.interleave(orders[v]), exp[q['i']], meta)
        ranks[v][q['i']] = r
        per_question[name][v] = r

for v in VARIANTS:
    print(f'\n== {v}')
    rr.report(ranks[v], qset)
print('\nQuestions whose answer is in the top 5 under some variants but not others (rank per variant):')
for name, r in per_question.items():
    top = [(x or 99) <= 5 for x in r.values()]
    if any(top) and not all(top):
        print(f'  {name:<4} ' + '  '.join(f'{v} {r[v] or "-":>2}' for v in VARIANTS))
json.dump({'log': log_path, 'questions': questions_path, 'k': K, 'depth': DEPTH, 'ranks': per_question},
          open(out_path, 'w', encoding='utf-8'), indent=1)
