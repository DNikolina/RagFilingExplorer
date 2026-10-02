# Step 5 screen: does one prompt sentence - use the cash flow statement's line for cash paid/spent/received questions -
# fix A10 and A15 without changing the answers it could misfire on? Replay only for retrieval (the app's top 5,
# reproduced from the run's log), then the app's exact prompt sent to Ollama's /api/chat with and without the sentence,
# the model unloaded before every call so no cached prompt prefix carries over. The "current" answer must equal the
# logged one - that checks the harness reproduces the app - and both are graded with tools/grade_answers.py's grade().
# Controls come from the main and A sets only: H16-H35 stays never-tuned.
# From the repo root, with Ollama running: python eval/answer-side-screens/cash_rule_screen.py OUT.json > OUT.txt
import json, os, re, subprocess, sys, time, urllib.request
sys.path.insert(0, 'tools')
import replay_recall as rr
import grade_answers as ga

SENTENCE = ("- When the question asks how much cash was paid, spent or received, use the cash flow statement's line if an\n"
            "  excerpt contains it.")
ANCHOR = "  that line exactly.\n"  # the end of the "similar names" rule - the new rule goes right after it
TARGETS = ['A10', 'A15']
CONTROLS = {'eval/structured-5a/main.log': ('tools/manual-questions.txt', ['Q4', 'Q24', 'T2', 'T8', 'T10']),
            'eval/answer-side-norerank/answers.log': ('tools/answer-questions.txt', ['A9', 'A12', 'A13', 'A16', 'A25'])}
RUNS = [('eval/answer-side-norerank/answers.log', 'tools/answer-questions.txt', TARGETS)] + \
       [(log, qs, ids) for log, (qs, ids) in CONTROLS.items()]

src = open('RagFilingExplorer.Local/Retrieval/RagAnswerService.cs', encoding='utf-8').read()
raw = src.split('private const string SystemPrompt = """\n', 1)[1].split('\n        """;', 1)[0]
current = '\n'.join(l[8:] if l.startswith('        ') else l for l in raw.split('\n'))
assert current.count(ANCHOR) == 1
candidate = current.replace(ANCHOR, ANCHOR + SENTENCE + '\n')
expected = {e['id']: e for e in json.load(open('tools/expected-answers.json', encoding='utf-8'))['questions']}

index = rr.load_index('rag.structured.db'); c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}


def ask(system, question, keys):
    # RagAnswerService: StringBuilder.AppendLine (CRLF on Windows) per header, content and blank line.
    context = ''.join(f"--- Excerpt from {meta[k][0]}, section {heading[k]} ---\r\n{meta[k][2]}\r\n\r\n" for k in keys)
    subprocess.run(['ollama', 'stop', 'llama3.1:8b'], capture_output=True)
    body = {'model': 'llama3.1:8b', 'stream': False,
            'messages': [{'role': 'system', 'content': system},
                         {'role': 'user', 'content': f'Context excerpts:\n\n{context}\nQuestion: {question}'}],
            'options': {'temperature': 0, 'num_predict': 768}}
    req = urllib.request.Request('http://localhost:11434/api/chat', data=json.dumps(body).encode(),
                                 headers={'Content-Type': 'application/json'})
    return json.load(urllib.request.urlopen(req, timeout=900))['message']['content'].strip()


results = []
for log, qs, ids in RUNS:
    qset = rr.question_set(qs)
    by_text = {e['question']: i for i, e in expected.items()}
    questions = rr.read_questions(qs)
    logged = dict(zip([by_text[q.strip()] for q in questions], ga.answers_from_log(log, len(questions))))
    for q in rr.replay(log, qs, index, depth=5):
        name = rr.name_of(q['i'], qset)
        if name not in ids:
            continue
        keys = rr.interleave(q['lists'])[:5]
        row = {'id': name, 'question': q['question'], 'logged': logged[name], 'logged_grade': ga.grade(expected[name], logged[name])[0]}
        for label, system in (('current', current), ('candidate', candidate)):
            t0 = time.time()
            a = ask(system, q['question'], keys)
            row[label] = a
            row[label + '_grade'] = ga.grade(expected[name], a)[0]
            row[label + '_s'] = round(time.time() - t0)
        row['reproduces_log'] = ' '.join(row['current'].split()) == ' '.join(row['logged'].split())
        results.append(row)
        print(f"{name:<4} logged {row['logged_grade']:<10} current {row['current_grade']:<10} "
              f"(same as log: {row['reproduces_log']})  candidate {row['candidate_grade']:<10} "
              f"{row['current_s']}s/{row['candidate_s']}s", flush=True)
        if row['candidate'] != row['current']:
            print(f"      candidate> {' '.join(row['candidate'].split())[:260]}", flush=True)

json.dump({'sentence': SENTENCE, 'results': results}, open(sys.argv[1], 'w', encoding='utf-8'), indent=1)
