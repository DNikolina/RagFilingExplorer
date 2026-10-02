# Screen of a second reranker family: BAAI/bge-reranker-v2-m3 against ms-marco-MiniLM-L6-v2 on the 8 questions where
# hybrid and L6 reranking disagree on the top 5 (rerank_blend.py's lists). Same candidates (each company's hybrid top 25)
# and the same passage text as the app. Bar (set before running): every A answer kept in the top 5, and some of R3, H25,
# H29, H34 gained - it kept 1 of 4 A answers, so the full sets weren't run.
# Needs, removed again after the screen: torch 2.14.0 + transformers 5.17.0 (PyPI, --only-binary :all:), and the model's
# official safetensors weights at commit 953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e in
# %USERPROFILE%\models\cross-encoder\bge-reranker-v2-m3 (model.safetensors SHA-256 d9e3e081...5286, tokenizer.json
# 69564b69...9c15). Loaded from local files only, as safetensors, no remote code. Decision-Log.md, "Reranking decided".
# From the repo root: python eval/rerank-decision/bge_screen.py
import os, re, sys, time
import numpy as np, onnxruntime as ort, torch
from tokenizers import Tokenizer
from transformers import AutoTokenizer, AutoModelForSequenceClassification
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', 'tools'))
import replay_recall as rr

SETS = [(r'eval\structured-5a\main.log', r'tools\manual-questions.txt', {'R3'}),
        (r'eval\structured-5a\heldout.log', r'tools\heldout-questions.txt', {'H25', 'H29', 'H34'}),
        (r'eval\answer-side-norerank\answers.log', r'tools\answer-questions.txt', {'A10', 'A15', 'A17', 'A27'})]
models = os.path.join(os.environ['USERPROFILE'], 'models', 'cross-encoder')

l6_tok = Tokenizer.from_file(os.path.join(models, 'ms-marco-MiniLM-L6-v2', 'tokenizer.json'))
l6_tok.enable_truncation(max_length=512, strategy='only_second'); l6_tok.enable_padding(pad_id=0, pad_token='[PAD]')
l6 = ort.InferenceSession(os.path.join(models, 'ms-marco-MiniLM-L6-v2', 'onnx', 'model.onnx'), providers=['CPUExecutionProvider'])
bge_dir = os.path.join(models, 'bge-reranker-v2-m3')
bge_tok = AutoTokenizer.from_pretrained(bge_dir, local_files_only=True)
bge = AutoModelForSequenceClassification.from_pretrained(bge_dir, local_files_only=True, use_safetensors=True,
                                                         trust_remote_code=False).eval()


def score_l6(question, passages):
    encs = l6_tok.encode_batch([(question, p) for p in passages])
    feed = {n: np.array([getattr(e, a) for e in encs], dtype=np.int64)
            for n, a in (('input_ids', 'ids'), ('attention_mask', 'attention_mask'), ('token_type_ids', 'type_ids'))}
    return l6.run(None, feed)[0][:, 0]


def score_bge(question, passages):
    out, longest = [], 0
    with torch.no_grad():
        for i in range(0, len(passages), 5):
            enc = bge_tok([question] * len(passages[i:i + 5]), passages[i:i + 5], padding=True, truncation='only_second',
                          max_length=2048, return_tensors='pt')
            longest = max(longest, int(enc['attention_mask'].sum(1).max()))
            out.extend(bge(**enc).logits[:, 0].tolist())
    return np.array(out), longest


index = rr.load_index('rag.structured.db'); c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}
company_line = {}
for f, content in c.execute("select SourceFiling, Content from chunks where Heading = 'Cover Page'"):
    m = re.match(r'(.+?) - annual report on Form (\S+) .*?\(fiscal year (\d{4})\)\. Common stock trading symbol: (\w+)', content)
    company_line[f] = f'{m.group(1)} ({m.group(4)}), Form {m.group(2)} for fiscal year {m.group(3)}.'

print(f'{"Q":<4} {"hybrid":>6} {"L6":>4} {"bge":>4}   bge s   longest bge pair (tokens)')
for log, qs, want in SETS:
    exp, qset = rr.expected_for(qs), rr.question_set(qs)
    for q in rr.replay(log, qs, index, depth=25):
        name = rr.name_of(q['i'], qset)
        if name not in want:
            continue
        orders = {'hybrid': [], 'L6': [], 'bge': []}
        secs, longest = 0.0, 0
        for lst in q['lists']:
            passages = [f"{company_line[meta[k][0]]}\nExcerpt from {meta[k][0]}, section {heading[k]}\n{meta[k][2]}" for k in lst]
            s6 = score_l6(q['question'], passages)
            t0 = time.perf_counter()
            sb, n = score_bge(q['question'], passages); longest = max(longest, n)
            secs += time.perf_counter() - t0
            orders['hybrid'].append(lst)
            orders['L6'].append([lst[i] for i in sorted(range(len(lst)), key=lambda i: (-s6[i], i))])
            orders['bge'].append([lst[i] for i in sorted(range(len(lst)), key=lambda i: (-sb[i], i))])
        r = {v: rr.rank_of(rr.interleave(o), exp[q['i']], meta) for v, o in orders.items()}
        print(f'{name:<4} {r["hybrid"] or "-":>6} {r["L6"] or "-":>4} {r["bge"] or "-":>4}   {secs:5.1f}   {longest}', flush=True)
