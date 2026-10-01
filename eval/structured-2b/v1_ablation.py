# V1 ablation: which excerpt around the answer chunk makes llama3.1:8b read the "attributable to Nasdaq" line ($942)
# instead of "Comprehensive income" ($940)? Builds the app's exact prompt (RagAnswerService: system prompt, excerpt
# header, CRLF line ends from StringBuilder.AppendLine) and calls Ollama's /api/chat directly, unloading the model
# before each call so no prompt prefix is reused from cache.
import json, os, re, sqlite3, subprocess, sys, urllib.request
import numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
sys.path.insert(0, 'tools')
import replay_recall as rr

src = open('RagFilingExplorer.Local/Retrieval/RagAnswerService.cs', encoding='utf-8').read()
raw = src.split('private const string SystemPrompt = """\n', 1)[1].split('\n        """;', 1)[0]
system = '\n'.join(l[8:] if l.startswith('        ') else l for l in raw.split('\n'))

index = rr.load_index('rag.structured.db')
c, _, meta = index
heading = {k: h for k, h in c.execute('select Key, Heading from chunks')}
cover = {}
for f, ct in c.execute("select SourceFiling, Content from chunks where Heading = 'Cover Page'"):
    m = re.match(r'(.+?) - annual report on Form (\S+) .*?\(fiscal year (\d{4})\)\. Common stock trading symbol: (\w+)', ct)
    cover[f] = f'{m.group(1)} ({m.group(4)}), Form {m.group(2)} for fiscal year {m.group(3)}.'

question = rr.read_questions('tools/manual-questions.txt')[37]  # V1
q = next(r for r in rr.replay('eval/structured-5a/main.log', 'tools/manual-questions.txt', index, depth=25) if r['i'] == 38)
hybrid = q['lists'][0]

root = os.path.join(os.environ['USERPROFILE'], 'models', 'cross-encoder', 'ms-marco-MiniLM-L6-v2')
tok = Tokenizer.from_file(root + '/tokenizer.json')
tok.enable_truncation(max_length=512, strategy='only_second')
tok.enable_padding(pad_id=0, pad_token='[PAD]')
sess = ort.InferenceSession(root + '/onnx/model.onnx', providers=['CPUExecutionProvider'])
texts = [f"{cover[meta[k][0]]}\nExcerpt from {meta[k][0]}, section {heading[k]}\n{meta[k][2]}" for k in hybrid]
e = tok.encode_batch([(question, t) for t in texts])
sc = sess.run(None, {'input_ids': np.array([x.ids for x in e], dtype=np.int64),
                     'attention_mask': np.array([x.attention_mask for x in e], dtype=np.int64),
                     'token_type_ids': np.array([x.type_ids for x in e], dtype=np.int64)})[0][:, 0]
reranked = [hybrid[i] for i in sorted(range(len(hybrid)), key=lambda i: -sc[i])][:5]
before = hybrid[:5]


def ask(keys):
    context = ''.join(f"--- Excerpt from {meta[k][0]}, section {heading[k]} ---\r\n{meta[k][2]}\r\n\r\n" for k in keys)
    subprocess.run(['ollama', 'stop', 'llama3.1:8b'], capture_output=True)
    body = {'model': 'llama3.1:8b', 'stream': False,
            'messages': [{'role': 'system', 'content': system},
                         {'role': 'user', 'content': f'Context excerpts:\n\n{context}\nQuestion: {question}'}],
            'options': {'temperature': 0, 'num_predict': 768}}
    req = urllib.request.Request('http://localhost:11434/api/chat', data=json.dumps(body).encode(), headers={'Content-Type': 'application/json'})
    answer = json.load(urllib.request.urlopen(req, timeout=600))['message']['content']
    figure = '942' if '942' in answer else '940' if '940' in answer else '?'
    return figure, answer.split('\n')[0][:110]


def label(k):
    return f"#{hybrid.index(k) + 1} {meta[k][1]}/{heading[k].split('>')[-1].strip()[:28]}"


print('reranked top 5:', [label(k) for k in reranked])
print('step 5a top 5: ', [label(k) for k in before])
runs = [('control: reranked context (app gave 942)', reranked), ('control: step 5a context (app gave 940)', before),
        ('answer chunk alone', reranked[:1])]
runs += [(f'reranked without excerpt {i + 1} ({label(reranked[i])})', reranked[:i] + reranked[i + 1:]) for i in range(1, 5)]
for name, keys in runs:
    figure, answer = ask(keys)
    print(f'{figure}  {name}\n      {answer}')
