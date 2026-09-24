# XBRL column check for the Linearized chunking strategy (see docs/Decision-Log.md, "linearized tables").
#
# Uses the filings' inline-XBRL tags as a ground-truth oracle for column alignment - the linearizer never
# reads them for alignment, it only carries each value's contextRef through, so this check is independent
# of what it verifies.
#
# Label-free by design (no matching of header text to dates, which would be a heuristic of its own):
#   - Each table has a column aspect: the XBRL aspect that is constant within every column and different
#     between columns - the period for year columns (income statement, balance sheet), the dimensions or
#     the concept for component columns (the equity statement's roll-forward, fair-value tables). It's
#     chosen as the aspect giving the most distinct per-column values. A value shifted into a neighbouring
#     column breaks its column's constancy; two columns with the same key are reported as indistinct.
#     (Accepting "any constant aspect" instead would be vacuous: a plain statement has no dimensions, so
#     every column is trivially dimension-constant.)
#   - Roll-forwards mix a duration with its opening and closing instants: a column's period key is its
#     single duration, with instants allowed only at that duration's end or the day before its start.
#   - Single-column tables have nothing to align and are reported as such, not counted as passing.
# A secondary, label-based check - column label with a single 4-digit year vs the period's end year - is
# reported as a warning only (fiscal-year naming conventions vary by filer).
# Coverage is reported next to accuracy: untagged tables (mostly MD&A) get no automatic check.
#
# From the repo root, after running the spike:
#   dotnet run --project tools/LinearizeSpike -- <out dir>
#   python tools/xbrl_column_check.py <out dir> [--verbose]
import json, re, sys, glob, os, collections, datetime

out_dir = sys.argv[1]
verbose = '--verbose' in sys.argv


def contexts(html):
    result = {}
    for m in re.finditer(r'(?is)<xbrli:context\b[^>]*\bid="([^"]+)"[^>]*>(.*?)</xbrli:context>', html):
        cid, body = m.group(1), m.group(2)
        instant = re.search(r'(?is)<xbrli:instant>\s*([^<\s]+)', body)
        start = re.search(r'(?is)<xbrli:startdate>\s*([^<\s]+)', body)
        end = re.search(r'(?is)<xbrli:enddate>\s*([^<\s]+)', body)
        period = ('I', instant.group(1)) if instant else ('D', start.group(1) if start else '', end.group(1) if end else '')
        dims = frozenset(
            (d.group(1).lower(), re.sub(r'(?s)<[^>]+>', '', d.group(2)).strip().lower())
            for d in re.finditer(r'(?is)<xbrldi:(?:explicit|typed)member\b[^>]*\bdimension="([^"]+)"[^>]*>(.*?)</xbrldi:(?:explicit|typed)member>', body))
        result[cid] = (period, dims)
    return result


def day_before(iso):
    return (datetime.date.fromisoformat(iso) - datetime.timedelta(days=1)).isoformat()


def period_key(periods):
    durations = {p for p in periods if p[0] == 'D'}
    instants = {p[1] for p in periods if p[0] == 'I'}
    if len(durations) == 1:
        d = next(iter(durations))
        return d if instants <= {d[2], day_before(d[1])} else None
    if not durations and len(instants) == 1:
        return ('I', next(iter(instants)))
    return None


def single(values):
    values = set(values)
    return next(iter(values)) if len(values) == 1 else None


totals = collections.Counter()
for json_path in sorted(glob.glob(os.path.join(out_dir, '*.linearized.json'))):
    stem = os.path.basename(json_path).replace('.linearized.json', '')
    html = open(os.path.join('data', stem + '.html'), encoding='utf-8', errors='replace').read()
    ctx = contexts(html)
    tables = json.load(open(json_path, encoding='utf-8'))

    stats = collections.Counter()
    samples = []
    for table in tables:
        if table['kind'] != 'Financial':
            continue
        columns = collections.defaultdict(list)
        for row in table['rows']:
            for v in row['values']:
                if v['ctx'] and v['ctx'] in ctx:
                    period, dims = ctx[v['ctx']]
                    columns[v['col']].append({'period': period, 'dims': dims, 'concept': (v['concept'] or '').lower(), 'unit': (v.get('unit') or '').lower(),
                                              'label': row['label'], 'text': v['text'], 'colLabel': v['colLabel']})
        tagged = sum(len(c) for c in columns.values())
        if tagged == 0:
            continue
        scope = ['all'] + (['primary'] if table['statement'] != '-' else [])

        def add(key, n=1):
            for s in scope:
                stats[s + ':' + key] += n

        add('tables')
        add('values', tagged)
        checkable = {c: v for c, v in columns.items() if len(v) >= 2}
        add('not_checkable', tagged - sum(len(v) for v in checkable.values()))
        if len(checkable) < 2:
            add('single_column_tables')
            add('not_checkable', sum(len(v) for v in checkable.values()))
            continue

        simple = {
            'period': {c: period_key([x['period'] for x in v]) for c, v in checkable.items()},
            'dims': {c: single(x['dims'] for x in v) for c, v in checkable.items()},
            'concept': {c: single(x['concept'] for x in v) for c, v in checkable.items()},
            'unit': {c: single(x['unit'] for x in v) for c, v in checkable.items()},
        }
        # Columns can be defined by a pair of aspects ("2026 Gross | 2026 Net": period + concept; "Shares |
        # $" per year: period + unit). A pair is only constant where both parts are, so it's never looser
        # per column - it only lets genuinely distinct columns be recognised as distinct.
        keys = dict(simple)
        names = list(simple)
        for i, a in enumerate(names):
            for b in names[i + 1:]:
                keys[a + '+' + b] = {c: (simple[a][c], simple[b][c]) if simple[a][c] is not None and simple[b][c] is not None else None
                                     for c in checkable}
        # Most distinct keys wins; ties go to the aspect covering more columns, then to a single aspect.
        aspect = max(keys, key=lambda a: (len({k for k in keys[a].values() if k is not None}),
                                          sum(k is not None for k in keys[a].values()), '+' not in a))
        chosen = keys[aspect]
        add('aspect_' + aspect)
        bad = False
        for col, key in chosen.items():
            vals = checkable[col]
            if key is None:
                bad = True
                add('misaligned', len(vals))
                samples.append(f"  MISALIGNED table {table['index']} [{table['statement']}] by {aspect}, col '{vals[0]['colLabel']}': "
                               + '; '.join(f"{x['label']}={x['text']} {x['period']}" for x in vals[:4]))
                continue
            add('aligned', len(vals))
            years = set(re.findall(r'\b(?:19|20)\d{2}\b', vals[0]['colLabel']))
            if aspect == 'period' and len(years) == 1:
                add('year_checked')
                end_year = int((key[1] if key[0] == 'I' else key[2])[:4])
                if int(next(iter(years))) != end_year:
                    add('year_mismatch')
                    samples.append(f"  year? table {table['index']} [{table['statement']}] col '{vals[0]['colLabel']}' vs {key}")
        for key, n in collections.Counter(k for k in chosen.values() if k is not None).items():
            if n > 1:
                bad = True
                add('indistinct')
                samples.append(f"  INDISTINCT table {table['index']} [{table['statement']}] {n} columns share {aspect} {key}")
        if bad:
            add('bad_tables')

    print(f"=== {stem} ===")
    for s in ('primary', 'all'):
        checked = stats[s + ':aligned'] + stats[s + ':misaligned']
        print(f"  {s:>7}: tagged tables {stats[s + ':tables']} (single-column {stats[s + ':single_column_tables']}), with a problem {stats[s + ':bad_tables']} | "
              f"aligned {stats[s + ':aligned']}/{checked} checked values (not checkable {stats[s + ':not_checkable']}) | "
              f"indistinct {stats[s + ':indistinct']} | year mismatches {stats[s + ':year_mismatch']}/{stats[s + ':year_checked']} | "
              "aspects: " + ', '.join(f"{k.split(':aspect_', 1)[1]} {v}" for k, v in sorted(stats.items()) if k.startswith(s + ':aspect_')))
    for line in samples[: (1000 if verbose else 10)]:
        print(line)
    totals.update(stats)

print("=== total ===")
for s in ('primary', 'all'):
    checked = totals[s + ':aligned'] + totals[s + ':misaligned']
    print(f"  {s:>7}: aligned {totals[s + ':aligned']}/{checked} checked tagged values "
          f"(not checkable {totals[s + ':not_checkable']} of {totals[s + ':values']}); "
          f"tables with a problem {totals[s + ':bad_tables']}/{totals[s + ':tables']}; "
          f"year mismatches {totals[s + ':year_mismatch']}/{totals[s + ':year_checked']}")
