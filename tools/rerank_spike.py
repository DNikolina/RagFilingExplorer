# Step 2b spike - cross-encoder reranking, replay only (no app code, no chat model). For each question, takes the app's
# own hybrid candidates (tools/replay_recall.py's replay, which reproduces the app's ranking exactly), scores every
# (question, chunk) pair with an ONNX cross-encoder, reorders each company's candidates by that score, and reports where
# the expected figure now ranks - recall@1/3/5 and MRR per question group, against the unreranked order.
#
# Measured per model and per input text, at two candidate depths (Microsoft's guidance: rerank 20-50):
#   depth 25 / 50   - rerank the top 25 or the top 50 hybrid candidates; a chunk is scored once, so both depths share
#                     the same scores (a cross-encoder scores each pair on its own)
#   text "excerpt"  - the chunk as the chat model sees it: "Excerpt from <filing>, section <heading>" + content
#   text "company"  - the same, opened by step 1d's company line ("Oracle Corporation (ORCL), Form 10-K for fiscal
#                     year 2026."), rebuilt here from each filing's Cover Page chunk
#   text "windows"  - "company", but a chunk longer than the model's window is scored twice and keeps the higher score
#                     (MaxP): its head, as "company" scores it, and its tail - the company and excerpt lines again, then
#                     as much of the content's end as fits, cut at a line break when one is in reach. The longest chunk
#                     (~606 WordPiece tokens) is under two windows, so head and tail cover it, overlapping.
# Also reported: CPU seconds per question for 25 and 50 candidates (timed, not extrapolated), and how many candidate
# chunks the model's 512-token window truncates - and whether any expected figure falls in the cut-off part.
#
# Models are read from %USERPROFILE%\models\cross-encoder\<name>\ (onnx/model.onnx + tokenizer.json); provenance and
# checksums in docs/Decision-Log.md, "Step 2b resumed". From the repo root, with Ollama running (query embeddings only):
#   python tools/rerank_spike.py eval/structured-5a/main.log tools/manual-questions.txt rag.structured.db OUT.json
#     [--texts excerpt,company,windows] [--depths 25,50]
import argparse, json, os, re, sys, time
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

sys.path.insert(0, os.path.dirname(__file__))
import replay_recall as rr

MODELS = ['ms-marco-MiniLM-L6-v2', 'ms-marco-MiniLM-L12-v2']
MODEL_ROOT = os.path.join(os.environ['USERPROFILE'], 'models', 'cross-encoder')
MAX_LEN = 512
BATCH = 25

parser = argparse.ArgumentParser()
parser.add_argument('log'); parser.add_argument('questions'); parser.add_argument('db'); parser.add_argument('out')
parser.add_argument('--texts', default='excerpt,company')
parser.add_argument('--depths', default='25,50')
args = parser.parse_args()
log_path, questions_path, db_path, out_path = args.log, args.questions, args.db, args.out
TEXTS = args.texts.split(',')
DEPTHS = [int(d) for d in args.depths.split(',')]
index = rr.load_index(db_path)
c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}

# Step 1d's company line, rebuilt from each filing's Cover Page chunk ("Oracle Corporation - annual report on Form
# 10-K for the fiscal year ended May 31, 2026 (fiscal year 2026). Common stock trading symbol: ORCL, ...").
company_line = {}
for f, content in c.execute("select SourceFiling, Content from chunks where Heading = 'Cover Page'"):
    m = re.match(r'(.+?) - annual report on Form (\S+) .*?\(fiscal year (\d{4})\)\. Common stock trading symbol: (\w+)', content)
    company_line[f] = f'{m.group(1)} ({m.group(4)}), Form {m.group(2)} for fiscal year {m.group(3)}.'


def header(k, variant):
    f = meta[k][0]
    excerpt = f'Excerpt from {f}, section {heading[k]}\n'
    return f'{company_line[f]}\n{excerpt}' if variant in ('company', 'windows') else excerpt


def windows(k, variant, question, measure):
    """The passage(s) a chunk is scored on. One, except under "windows" for a chunk too long for the model: then its head
    (what the tokenizer's truncation keeps anyway) and its tail, each opened by the same header."""
    head_text = header(k, variant) + meta[k][2]
    if variant != 'windows':
        return [head_text]
    budget = MAX_LEN - 3 - len(measure.encode(question, add_special_tokens=False).ids)
    if len(measure.encode(head_text, add_special_tokens=False).ids) <= budget:
        return [head_text]
    content = meta[k][2]
    room = budget - len(measure.encode(header(k, variant), add_special_tokens=False).ids) - 2  # 2: boundary slack
    enc = measure.encode(content, add_special_tokens=False)
    start = enc.offsets[max(0, len(enc.ids) - room)][0]
    # Prefer starting the tail at a line break, so it opens on a whole row - when one leaves most of the room in use.
    nl = content.find('\n', start)
    if nl != -1 and len(content) - nl > 0.75 * (len(content) - start):
        start = nl + 1
    return [head_text, header(k, variant) + content[start:]]


# Every question's candidates, 50 deep, ranked exactly as the app ranks them (embeds each question once).
exp = rr.expected_for(questions_path)
heldout = rr.question_set(questions_path)  # main, heldout or answer
questions = list(rr.replay(log_path, questions_path, index, depth=max(DEPTHS)))

results = {'log': log_path, 'questions': questions_path, 'configs': {}}
baseline = {d: {} for d in DEPTHS}
for q in questions:
    if exp[q['i']]:
        for d in DEPTHS:
            baseline[d][q['i']] = rr.rank_of(rr.interleave([l[:d] for l in q['lists']]), exp[q['i']], meta)
print(f'Unreranked (the app today), {len(questions)} questions:')
rr.report(baseline[25], heldout)

for model in MODELS:
    tok = Tokenizer.from_file(os.path.join(MODEL_ROOT, model, 'tokenizer.json'))
    tok.enable_truncation(max_length=MAX_LEN, strategy='only_second')
    tok.enable_padding(pad_id=0, pad_token='[PAD]')
    measure = Tokenizer.from_file(os.path.join(MODEL_ROOT, model, 'tokenizer.json'))  # no truncation: true lengths
    opts = ort.SessionOptions()
    session = ort.InferenceSession(os.path.join(MODEL_ROOT, model, 'onnx', 'model.onnx'), opts, providers=['CPUExecutionProvider'])
    for variant in TEXTS:
        name = f'{model}/{variant}'
        times25, times50, truncated, total, cut_figures = [], [], 0, 0, []
        windowed, tail_won_answer = 0, []
        ranks = {d: {} for d in DEPTHS}
        per_question = {}
        for q in questions:
            scores = {}
            elapsed = []
            for lst in q['lists']:
                for start in range(0, len(lst), BATCH):
                    keys = lst[start:start + BATCH]
                    pairs = [(k, t) for k in keys for t in windows(k, variant, q['question'], measure)]
                    t0 = time.perf_counter()
                    encs = tok.encode_batch([(q['question'], t) for _, t in pairs])
                    feed = {'input_ids': np.array([e.ids for e in encs], dtype=np.int64),
                            'attention_mask': np.array([e.attention_mask for e in encs], dtype=np.int64),
                            'token_type_ids': np.array([e.type_ids for e in encs], dtype=np.int64)}
                    logits = session.run(None, feed)[0][:, 0]
                    elapsed.append((start, time.perf_counter() - t0))
                    by_key = {}
                    for (k, t), e, s in zip(pairs, encs, logits):
                        by_key.setdefault(k, []).append((float(s), t, e))
                    for k, ws in by_key.items():
                        scores[k] = max(s for s, _, _ in ws)  # MaxP: the best window speaks for the chunk
                        if len(ws) > 1:
                            windowed += 1
                            if ws[1][0] > ws[0][0] and any(fig in meta[k][2] for fig in exp[q['i']]):
                                tail_won_answer.append((rr.name_of(q['i'], heldout), k))
                        # Truncated: no window's kept tokens reach the passage's last character.
                        s0, t, e = ws[-1]
                        kept_end = max((o[1] for o, sid in zip(e.offsets, e.sequence_ids) if sid == 1), default=0)
                        total += 1
                        if kept_end < len(t.rstrip()):
                            truncated += 1
                            for fig in exp[q['i']]:
                                if t.find(fig) >= kept_end and all(fig not in w for _, w, _ in ws[:-1]):
                                    cut_figures.append((rr.name_of(q['i'], heldout), k, fig))
            times25.append(sum(s for st, s in elapsed if st < 25))
            times50.append(sum(s for _, s in elapsed))
            if not exp[q['i']]:
                continue
            per_question[rr.name_of(q['i'], heldout)] = {}
            for d in DEPTHS:
                # Rerank each company's top d by score (ties keep hybrid order), then merge as the app does.
                reranked = [sorted(l[:d], key=lambda k: -scores[k]) for l in q['lists']]
                r = rr.rank_of(rr.interleave(reranked), exp[q['i']], meta)
                ranks[d][q['i']] = r
                per_question[rr.name_of(q['i'], heldout)][f'depth{d}'] = r
            per_question[rr.name_of(q['i'], heldout)]['unreranked'] = baseline[25][q['i']]
        print(f'\n== {name}  (CPU s/question: 25 candidates {np.mean(times25):.2f} (max {max(times25):.2f}), '
              f'50 candidates {np.mean(times50):.2f} (max {max(times50):.2f}))')
        print(f'   truncated at {MAX_LEN} tokens: {truncated}/{total} candidate passages; '
              f'expected figure in the cut-off part: {cut_figures or "none"}')
        if variant == 'windows':
            print(f'   scored in two windows: {windowed}/{total}; tail window decided an answer chunk: {tail_won_answer or "none"}')
        for d in DEPTHS:
            print(f'   depth {d}:')
            rr.report(ranks[d], heldout)
            moved = [(rr.name_of(i, heldout), baseline[d][i], r) for i, r in ranks[d].items()
                     if (baseline[d][i] or 99) <= 5 < (r or 99) or (r or 99) <= 5 < (baseline[d][i] or 99)]
            print(f'   in/out of the top 5 at depth {d} (question, before, after): {moved or "none"}')
        results['configs'][name] = {'s_per_question_25': float(np.mean(times25)), 's_per_question_50': float(np.mean(times50)),
                                    'truncated': [truncated, total], 'figure_cut': cut_figures, 'windowed': windowed,
                                    'tail_won_answer': tail_won_answer, 'ranks': per_question}

json.dump(results, open(out_path, 'w', encoding='utf-8'), indent=1)
