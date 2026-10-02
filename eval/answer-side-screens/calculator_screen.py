# Step 3 screen: offered one calculate(expression) tool, does llama3.1:8b call it - with the right numbers - on the
# arithmetic questions? Targets A27 (divided wrong) and V3 (never added); controls A21-A26 (right without a tool). The
# app's top 5 (replayed from each run's log) and exact prompt, unchanged; the tool goes through Ollama's native tool
# support (/api/chat "tools"), evaluated here exactly as the planned C# tool would: numbers, + - * /, brackets, in
# Decimal - no eval. The model is unloaded before each question. Graded with tools/grade_answers.py's grade().
# From the repo root, with Ollama running: python eval/answer-side-screens/calculator_screen.py OUT.json > OUT.txt
import ast, decimal, json, re, subprocess, sys, time, urllib.request
sys.path.insert(0, 'tools')
import replay_recall as rr
import grade_answers as ga

RUNS = [('eval/structured-5a/main.log', 'tools/manual-questions.txt', ['V3']),
        ('eval/answer-side-norerank/answers.log', 'tools/answer-questions.txt', ['A21', 'A22', 'A23', 'A24', 'A25', 'A26', 'A27'])]
TOOL = {'type': 'function', 'function': {
    'name': 'calculate',
    'description': 'Evaluates an arithmetic expression exactly and returns the result. Use it for any sum, difference, '
                   'ratio or percentage instead of calculating yourself. Numbers, + - * / and brackets only.',
    'parameters': {'type': 'object', 'required': ['expression'], 'properties': {
        'expression': {'type': 'string', 'description': 'The expression, e.g. "(17087 / 67357) * 100"'}}}}}
NUM_CTX = 4096   # the app doesn't set num_ctx; Ollama's default
MAX_OUT = 768    # Retrieval:MaxOutputTokens

src = open('RagFilingExplorer.Local/Retrieval/RagAnswerService.cs', encoding='utf-8').read()
raw = src.split('private const string SystemPrompt = """\n', 1)[1].split('\n        """;', 1)[0]
system = '\n'.join(l[8:] if l.startswith('        ') else l for l in raw.split('\n'))
expected = {e['id']: e for e in json.load(open('tools/expected-answers.json', encoding='utf-8'))['questions']}
index = rr.load_index('rag.structured.db'); c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}
decimal.getcontext().prec = 28


def calculate(expression):
    """Numbers (thousands separators allowed), + - * /, unary minus, brackets - anything else is an error."""
    text = re.sub(r'(?<=\d),(?=\d{3}\b)', '', expression)

    def ev(n):
        if isinstance(n, ast.Expression):
            return ev(n.body)
        if isinstance(n, ast.Constant) and isinstance(n.value, (int, float)) and not isinstance(n.value, bool):
            return decimal.Decimal(str(n.value))
        if isinstance(n, ast.UnaryOp) and isinstance(n.op, (ast.USub, ast.UAdd)):
            return -ev(n.operand) if isinstance(n.op, ast.USub) else ev(n.operand)
        if isinstance(n, ast.BinOp) and isinstance(n.op, (ast.Add, ast.Sub, ast.Mult, ast.Div)):
            a, b = ev(n.left), ev(n.right)
            return {ast.Add: a + b, ast.Sub: a - b, ast.Mult: a * b}.get(type(n.op)) if not isinstance(n.op, ast.Div) else a / b
        raise ValueError(f'unsupported: {ast.dump(n)[:60]}')
    try:
        return format(ev(ast.parse(text, mode='eval')).quantize(decimal.Decimal('0.000001')).normalize(), 'f')
    except Exception as e:
        return f'error: {e}'


def chat(messages):
    body = {'model': 'llama3.1:8b', 'stream': False, 'messages': messages, 'tools': [TOOL],
            'options': {'temperature': 0, 'num_predict': MAX_OUT, 'num_ctx': NUM_CTX}}
    req = urllib.request.Request('http://localhost:11434/api/chat', data=json.dumps(body).encode(),
                                 headers={'Content-Type': 'application/json'})
    return json.load(urllib.request.urlopen(req, timeout=900))


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
        context = ''.join(f"--- Excerpt from {meta[k][0]}, section {heading[k]} ---\r\n{meta[k][2]}\r\n\r\n" for k in keys)
        messages = [{'role': 'system', 'content': system},
                    {'role': 'user', 'content': f"Context excerpts:\n\n{context}\nQuestion: {q['question']}"}]
        subprocess.run(['ollama', 'stop', 'llama3.1:8b'], capture_output=True)
        t0, calls, prompt_tokens = time.time(), [], []
        for _ in range(4):  # at most three tool rounds, then the answer
            r = chat(messages)
            prompt_tokens.append(r.get('prompt_eval_count'))
            m = r['message']
            if not m.get('tool_calls'):
                break
            messages.append({'role': 'assistant', 'content': m.get('content', ''), 'tool_calls': m['tool_calls']})
            for tc in m['tool_calls']:
                expr = str(tc['function']['arguments'].get('expression', tc['function']['arguments']))
                out = calculate(expr) if tc['function']['name'] == 'calculate' else 'error: unknown tool'
                calls.append({'expression': expr, 'result': out})
                messages.append({'role': 'tool', 'content': out, 'tool_name': 'calculate'})
        answer = m.get('content', '').strip()
        row = {'id': name, 'question': q['question'], 'logged_grade': ga.grade(expected[name], logged[name])[0],
               'calls': calls, 'answer': answer, 'grade': ga.grade(expected[name], answer)[0],
               'prompt_tokens': prompt_tokens, 'seconds': round(time.time() - t0)}
        results.append(row)
        print(f"{name:<4} without tool {row['logged_grade']:<10} with tool {row['grade']:<10} {row['seconds']}s  "
              f"prompt tokens {prompt_tokens}", flush=True)
        for cl in calls:
            print(f"      calculate({cl['expression']}) = {cl['result']}", flush=True)
        print(f"      answer> {' '.join(answer.split())[:280]}", flush=True)

json.dump({'tool': TOOL, 'results': results}, open(sys.argv[1], 'w', encoding='utf-8'), indent=1)
