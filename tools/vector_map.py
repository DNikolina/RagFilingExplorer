# Vector map - one index's chunk vectors drawn in 2D as a standalone HTML page, to see which chunks the embedding model
# puts close together (see docs/Decision-Log.md, "a map of the chunk vectors"). Each dot is a chunk, coloured by
# filing, a primary-statement chunk drawn larger; hover shows its heading and text, a click rings its 5 nearest by
# cosine distance over the full vectors - the distance the app searches with - whatever the 2D layout shows.
#
# Two layouts:
#   PCA (default)  - the two directions the chunks vary most along; distances are proportional, but everything outside
#                    those two directions is lost, so dots that overlap may not be close
#   --tsne         - t-SNE: places each chunk next to its true neighbours; clusters are real, the gaps between them and
#                    their sizes are not to scale; seeded, so a run is repeatable
# Both print how many of each chunk's 5 true nearest are among its 5 nearest dots, as a measure of the layout.
#
# The vectors are read through sqlite-vec's own extension (vec0), taken from the app's build output - build the app
# first. Needs numpy; --tsne also scikit-learn - measured with numpy==2.5.4 and scikit-learn==1.9.1, installed into a
# venv outside the repo rather than the Python markitdown runs from.
# From the repo root:
#   python tools/vector_map.py rag.structured.db OUT.html [--tsne]
import argparse, glob, json, platform, sqlite3, sys
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument('db'); parser.add_argument('out')
parser.add_argument('--tsne', action='store_true')
args = parser.parse_args()

# The app's build output holds sqlite-vec's native library for every platform; SQLite adds the file extension itself.
os_name = {'Windows': 'win', 'Linux': 'linux', 'Darwin': 'osx'}[platform.system()]
arch = 'arm64' if platform.machine().lower() in ('arm64', 'aarch64') else 'x64'
natives = sorted(glob.glob(f'RagFilingExplorer.Cli/bin/*/net*/runtimes/{os_name}-{arch}/native/vec0.*'))
if not natives:
    sys.exit(f'No vec0 library for {os_name}-{arch} under RagFilingExplorer.Cli/bin - build the app first (dotnet build).')

conn = sqlite3.connect(f'file:{args.db}?mode=ro', uri=True)
conn.enable_load_extension(True)
conn.load_extension(natives[0].rsplit('.', 1)[0])
rows = conn.execute('''select v.Key, v.Text, c.SourceFiling, c.Heading, c.StatementType, c.Content
                       from vec_chunks v join chunks c on c.Key = v.Key order by v.Key''').fetchall()

X = np.array([np.frombuffer(r[1], dtype=np.float32) for r in rows])
X /= np.linalg.norm(X, axis=1, keepdims=True)  # cosine: directions, not lengths
sim = X @ X.T
np.fill_diagonal(sim, -2)
neighbours = np.argsort(-sim, axis=1)[:, :5]

if args.tsne:
    try:
        from sklearn.manifold import TSNE
    except ImportError:
        sys.exit('--tsne needs scikit-learn (pip install scikit-learn==1.9.1).')
    xy = TSNE(n_components=2, metric='cosine', init='pca', perplexity=30, random_state=0).fit_transform(X)
    method = "t-SNE (cosine, perplexity 30) - clusters are real, the gaps between clusters aren't to scale"
else:
    centred = X - X.mean(axis=0)
    _, s, vt = np.linalg.svd(centred, full_matrices=False)
    xy = centred @ vt[:2].T
    explained = [round(float(v), 1) for v in s[:2] ** 2 / (s ** 2).sum() * 100]
    method = f'PCA - the axes explain {explained[0]}% + {explained[1]}% of the variance, the rest is lost'

d2 = ((xy[:, None, :] - xy[None, :, :]) ** 2).sum(-1)
np.fill_diagonal(d2, np.inf)
near2d = np.argsort(d2, axis=1)[:, :5]
kept = np.mean([len(set(a) & set(b)) for a, b in zip(neighbours, near2d)])

points = [dict(key=r[0], filing=r[2], heading=r[3], st=r[4], text=r[5], x=float(p[0]), y=float(p[1]),
               nn=neighbours[i].tolist(), d=[round(1 - float(sim[i, j]), 3) for j in neighbours[i]])
          for i, (r, p) in enumerate(zip(rows, xy))]

html = '''<!doctype html><html><head><meta charset="utf-8"><title>Chunk Vector Map</title>
<style>
:root{--bg:#fafaf9;--fg:#1c1917;--mute:#78716c;--line:#e7e5e4}
@media (prefers-color-scheme:dark){:root{--bg:#1c1917;--fg:#f5f5f4;--mute:#a8a29e;--line:#44403c}}
body{background:var(--bg);color:var(--fg);font:14px system-ui,sans-serif;margin:16px}
#legend span{margin-right:16px}#legend i{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:4px}
#main{display:flex;gap:16px;align-items:flex-start}
svg{flex:1 1 0;min-width:0;height:auto;border:1px solid var(--line);border-radius:8px}
#info{flex:0 0 360px;height:calc(100vh - 140px);overflow-y:auto;white-space:pre-wrap;color:var(--mute);
  border:1px solid var(--line);border-radius:8px;padding:12px;box-sizing:border-box;position:sticky;top:16px}
@media (max-width:800px){#main{flex-direction:column}#info{flex-basis:auto;width:100%;height:40vh;position:static}}
circle{cursor:pointer}circle.nn{stroke:var(--fg);stroke-width:2.5}circle.sel{stroke:#dc2626;stroke-width:3.5}
</style></head><body>
<h3>Chunk vectors in 2D - __DB__</h3>
<div id="legend"></div>
<p style="color:var(--mute)">__METHOD__. Of each chunk's 5 true nearest, __KEPT__ are among its 5 nearest dots.
Bigger dots are primary-statement chunks. Click a dot: red = it, black rings = its 5 true nearest (cosine, full vectors).</p>
<div id="main"><svg id="plot" viewBox="0 0 1100 750"></svg><div id="info">Hover or click a dot.</div></div>
<script>
const P=__POINTS__, colors=["#2563eb","#ea580c","#16a34a","#9333ea","#0891b2","#ca8a04"];
const filings=[...new Set(P.map(p=>p.filing))], col=f=>colors[filings.indexOf(f)%colors.length];
document.getElementById("legend").innerHTML=filings.map(f=>`<span><i style="background:${col(f)}"></i>${f}</span>`).join("");
const xs=P.map(p=>p.x), ys=P.map(p=>p.y), [x0,x1,y0,y1]=[Math.min(...xs),Math.max(...xs),Math.min(...ys),Math.max(...ys)];
const sx=x=>20+(x-x0)/(x1-x0)*1060, sy=y=>730-(y-y0)/(y1-y0)*710, svg=document.getElementById("plot"), info=document.getElementById("info");
const show=p=>`#${p.key}  ${p.filing}  [${p.st}]\\n${p.heading}\\n\\n${p.text}`;
P.forEach((p,i)=>{const c=document.createElementNS("http://www.w3.org/2000/svg","circle");
  c.setAttribute("cx",sx(p.x));c.setAttribute("cy",sy(p.y));c.setAttribute("r",p.st==="narrative"?3.5:7);
  c.setAttribute("fill",col(p.filing));c.setAttribute("fill-opacity",.7);c.id="p"+i;
  c.onmouseenter=()=>info.textContent=show(p);
  c.onclick=()=>{svg.querySelectorAll(".sel,.nn").forEach(e=>e.classList.remove("sel","nn"));c.classList.add("sel");
    p.nn.forEach(j=>{const n=document.getElementById("p"+j);n.classList.add("nn");svg.appendChild(n)});svg.appendChild(c);
    info.textContent=show(p)+"\\n\\nNearest: "+p.nn.map((j,k)=>`#${P[j].key} (${P[j].filing}, d=${p.d[k]})`).join(", ");};
  svg.appendChild(c);});
</script></body></html>'''
html = (html.replace('__POINTS__', json.dumps(points)).replace('__DB__', args.db.replace('\\', '/').split('/')[-1])
            .replace('__METHOD__', method).replace('__KEPT__', f'{kept:.2f}'))
with open(args.out, 'w', encoding='utf-8') as f:
    f.write(html)
print(f'{len(points)} chunks, {method}; of each chunk\'s 5 true nearest, {kept:.2f} are among its 5 nearest dots -> {args.out}')
