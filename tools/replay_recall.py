# Retrieval recall check: for each question in tools/manual-questions.txt, replays the app's retrieval
# against a strategy's index (rag.<strategy>.db) - the stored vectors, the same filters the app printed,
# the same cosine distance, the same per-company interleave for multi-company questions - and reports
# where the expected figure ranks. Deterministic (no LLM), so unlike a full question run it isn't
# affected by the chat model's run-to-run variation: it separates "retrieval missed it" from "the model
# misread it". Verified to reproduce the app's own --verbose scores exactly.
#
# Reported per question group - the original 24 (Q1-Q24), the targeted questions aimed at what the
# Linearized strategy claims to fix (T1-T10), the routing tests whose keyword route excludes the answer
# (R1-R3: misses by design under Vector search's hard filter; hybrid search can reach them), the variants
# (V1-V3), and the held-out (H1-H15, H16-H35) and answer-side (A1-A27) sets in their own files (see below):
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
# What counts as the answer in a chunk is each question's "chunk_expect" in tools/expected-answers.json, matched by the
# question's text (see expected_for below). Pass tools/heldout-questions.txt (and its run's log) for H1-H35 (H16-H35
# appended 2026-09-30, reported as their own group), or tools/answer-questions.txt for A1-A27. Requires the app to have
# been built once (it loads the sqlite-vec extension from bin/).
import sqlite3, json, urllib.request, math, re, struct, glob, sys

TOP = 25
HYBRID_CANDIDATES = 50  # appsettings.json's Retrieval:HybridCandidates
RRF_K = 60  # RankFusion.K


def load_index(db_path):
    """The index's connection (sqlite-vec loaded), stored vectors, and per-chunk (filing, statement type, content)."""
    c = sqlite3.connect(f'file:{db_path}?mode=ro', uri=True)
    c.enable_load_extension(True)
    c.load_extension(glob.glob('RagFilingExplorer.Local/bin/**/win-x64/native/vec0.dll', recursive=True)[0][:-4])
    vec = {k: struct.unpack('768f', b) for k, b in c.execute('select Key, Text from vec_chunks')}
    meta = {k: (f, t, ct) for k, f, t, ct in c.execute('select Key, SourceFiling, StatementType, Content from chunks')}
    return c, vec, meta


def embed(text):
    req = urllib.request.Request('http://localhost:11434/api/embed',
                                 data=json.dumps({"model": "nomic-embed-text", "input": text}).encode(),
                                 headers={'Content-Type': 'application/json'})
    return json.load(urllib.request.urlopen(req))['embeddings'][0]


def dist(a, b):
    d = sum(x * y for x, y in zip(a, b))
    return 1 - d / (math.sqrt(sum(x * x for x in a)) * math.sqrt(sum(y * y for y in b)))


def read_questions(questions_path):
    return [q for q in open(questions_path, encoding='utf-8').read().split('\n') if q.strip()]


def read_blocks(log_path, count):
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
    return [b for b in blocks if b.strip()][:count]


# What marks a question's answer in a chunk: "chunk_expect" in tools/expected-answers.json (moved there from this file in
# v3 step 3, so the .NET evaluator reads the same list) - each value as the chunk prints it. Where it isn't simply the
# answer's figure: H4 asks for a difference the filing states only rounded ("$3.1 billion"), so it ranks at the row
# holding both inputs; V1's 1,124 is the right line's 2023 value (the right line is 940, not the "attributable to
# Nasdaq" 942 - format-independent); A1-A27's are written to be unambiguous ("$604", "(601)"), and an arithmetic
# question (A21-A27) lists every input, so it ranks at its last input - the result is printed nowhere in the filing.
# Negatives (Q15, Q16, V2, H8, H15, H20, H30, A12) have none and aren't scored.
EXPECTED_ANSWERS = 'tools/expected-answers.json'


def question_set(questions_path):
    return 'heldout' if 'heldout' in questions_path else 'answer' if 'answer' in questions_path else 'main'


def expected_for(questions_path):
    """Each question's chunk_expect, keyed by its line number in `questions_path` (1-based)."""
    by_text = {q['question']: q.get('chunk_expect', []) for q in json.load(open(EXPECTED_ANSWERS, encoding='utf-8'))['questions']}
    return {i: by_text[q.strip()] for i, q in enumerate(read_questions(questions_path), 1)}


# `qset` is question_set()'s name ('main', 'heldout' or 'answer'); True/False (held-out or not) are still accepted,
# from before the answer-side set existed.
def group_of(i, qset):
    qset = {True: 'heldout', False: 'main'}.get(qset, qset)
    if qset == 'answer':
        return 'A1-A27'
    if qset == 'heldout':
        return 'H1-H15' if i <= 15 else 'H16-H35'
    return 'Q1-Q24' if i <= 24 else 'T1-T10' if i <= 34 else 'R1-R3' if i <= 37 else 'V1-V3'


def name_of(i, qset):
    qset = {True: 'heldout', False: 'main'}.get(qset, qset)
    if qset == 'answer':
        return f'A{i}'
    if qset == 'heldout':
        return f'H{i}'
    return f'Q{i}' if i <= 24 else f'T{i - 24}' if i <= 34 else f'R{i - 34}' if i <= 37 else f'V{i - 37}'


def replay(log_path, questions_path, index, depth=TOP):
    """One dict per question: its number and text, the filter the app printed (files, statement type, keyword query),
    `lists` - one ranking of chunk keys per filtered company, `depth` deep, exactly as the app ranks them - and
    `score`, the fused RRF score of every candidate (hybrid runs only). A reranker reorders each of `lists`;
    interleave() merges them as the app does."""
    c, vec, meta = index
    questions = read_questions(questions_path)
    for i, (q, b) in enumerate(zip(questions, read_blocks(log_path, len(questions))), 1):
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
                return vector(f, st)[:depth]
            lists = [vector(f, None)[:HYBRID_CANDIDATES], keyword(f)] + ([vector(f, st)[:HYBRID_CANDIDATES]] if st else [])
            fused = {}
            for ranking in lists:
                for r, k in enumerate(ranking, 1):
                    fused[k] = fused.get(k, 0) + 1 / (RRF_K + r)
            score.update(fused)
            return sorted(fused, key=lambda k: -fused[k])[:depth]

        per = [search(f) for f in files] if len(files) > 1 else [search(files[0] if files else None)]
        yield {'i': i, 'question': q, 'files': files, 'st': st, 'kw': kw, 'qv': qv, 'lists': per, 'score': score}


def interleave(lists):
    """The app's merge for a multi-company question: each company's rank 1, then each one's rank 2, ..."""
    if len(lists) == 1:
        return lists[0]
    return [l[r] for r in range(max(map(len, lists))) for l in lists if r < len(l)]


def rank_of(ranked, expected, meta):
    """Where the expected figure ranks in `ranked` - the last one's position if several - or None if any is missing."""
    positions = [next((n for n, k in enumerate(ranked, 1) if e in meta[k][2]), None) for e in expected]
    return None if None in positions else max(positions)


def report(ranks, qset):
    for group in ('Q1-Q24', 'T1-T10', 'R1-R3', 'V1-V3', 'H1-H15', 'H16-H35', 'A1-A27'):
        rs = [r for i, r in ranks.items() if group_of(i, qset) == group]
        if not rs:
            continue
        at = lambda k: sum(1 for r in rs if r and r <= k)
        mrr = sum(1 / r for r in rs if r) / len(rs)
        print(f"{group}: recall@1 {at(1)}/{len(rs)}  recall@3 {at(3)}/{len(rs)}  recall@5 {at(5)}/{len(rs)}  MRR {mrr:.3f}")


def main():
    log_path, questions_path = sys.argv[1], sys.argv[2]
    db_path = sys.argv[3] if len(sys.argv) > 3 else 'rag.markdown.db'
    index = load_index(db_path)
    _, vec, meta = index
    exp = expected_for(questions_path)
    qset = question_set(questions_path)
    ranks = {}
    for r in replay(log_path, questions_path, index):
        i, files, st, kw, score = r['i'], r['files'], r['st'], r['kw'], r['score']
        ranked = interleave(r['lists'])
        top1 = '-' if not ranked else f"{score[ranked[0]]:.4f}" if kw is not None else f"{dist(r['qv'], vec[ranked[0]]):.4f}"
        where = f"{files[0] if len(files) == 1 else ('+'.join(files) if files else 'all')}/{st or '-'}"
        if not exp[i]:
            print(f"{name_of(i, qset):>3} (negative test, not scored)  filter={where}  top1={top1}")
            continue
        rank = rank_of(ranked, exp[i], meta)
        ranks[i] = rank
        print(f"{name_of(i, qset):>3} rank={rank if rank else '>' + str(TOP):>4}  in-top-5={'yes' if rank and rank <= 5 else 'NO '}  "
              f"filter={where}  top1={top1}")
    print()
    report(ranks, qset)


if __name__ == '__main__':
    main()
