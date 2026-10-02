# Answer grader: grades each answer in a question-run log against tools/expected-answers.json, using the
# manual pass's rules (docs/Manual-Test-Questions.md, "Grading"):
#   Correct  - the expected figure is stated (an alternative line's figure counts only if the answer names
#              that line, e.g. Q10's "attributable to Nasdaq")
#   Complete - its unit is stated: "million"/"thousand" after the number (or anywhere in the answer when the
#              number itself carries none - "an answer listing bare figures but concluding with the unit"),
#              "$" before a per-share figure, "%" after a rate. A different unit attached to the number is
#              wrong-unit: the digits are right but the figure is off by 1,000x.
#   Reliable - both. A decline is correct for negatives, passes for routing tests, and is "declined" otherwise.
# The period ("for the year ended ...") is not checked: the manual grading didn't enforce it either.
#
# It replaces reading every answer, not reviewing them: statuses other than reliable/correct-decline print
# the answer, and "check" marks an answer that states the expected figure next to a lookalike one (Q10's
# 2,113/2,114), which only a reader can settle. Validated against the four hand-graded 2026-09-25/28 runs.
#
# From the repo root, after a run (the log's order must match the questions file):
#   dotnet run --project RagFilingExplorer.Local -- --verbose < tools/manual-questions.txt > run.log
#   python tools/grade_answers.py run.log tools/manual-questions.txt [--json results.json]
import json, re, sys

NUMBER = re.compile(r'\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?')
SCALE_AFTER = re.compile(r'\)?\s*(million|thousand|billion)', re.I)
# ['’]: since the app writes UTF-8 (2026-10-01) a curly apostrophe reaches the log; before, it arrived as "'".
DECLINE = re.compile(r"(?:don['’]t|do not|does not|doesn['’]t) (?:contain|include|provide|mention|state|specify)"
                     r"|not (?:explicitly |directly |specifically )?"
                     r"(?:stated|provided|available|included|mentioned|found|specified|reported|disclosed)"
                     r"|no (?:information|data|mention)|unable to|cannot (?:find|determine|answer)|there is no", re.I)
# The prompt-v1 failure: the units rule came out as the whole answer ("The unit for the figures is not stated.").
UNIT_ONLY = re.compile(r"^\W*the units?\b[^.]{0,40}\bnot stated\W*$", re.I)

# ID patterns: H16-H35 were added after step 2 (2026-09-30), so they report apart from H1-H15 and older runs stay comparable.
# A1-A27 (2026-10-01): the answer-side set - lookalike lines, per-share vs total, MD&A vs statement, units, arithmetic.
GROUPS = [(r'Q', 'Main Q1-Q24'), (r'T', 'Targeted T1-T10'), (r'R', 'Routing R1-R3'), (r'V', 'Variants V1-V3'),
          (r'H(?:[1-9]|1[0-5])$', 'Held-out H1-H15'), (r'H(?:1[6-9]|2\d|3[0-5])$', 'Held-out H16-H35'),
          (r'A', 'Answer-side A1-A27')]


def answers_from_log(path, count):
    # Same block splitting as replay_recall.py: a real prompt block opens with the filter line or, for an
    # unfiltered --verbose question, the retrieved-chunks header; a "> " the model wrote as a Markdown
    # blockquote is merged back into the block before it.
    blocks = []
    # errors='replace': logs from before the app set Console.OutputEncoding (2026-10-01) carry the console code
    # page's bytes for non-ASCII characters (a non-breaking space as 0xFF). Figures, units and the grader's
    # patterns are ASCII, so a replaced character never changes a grade.
    for b in open(path, encoding='utf-8', errors='replace').read().split('\n> ')[1:]:
        if b.startswith('(') or b.startswith('--- Retrieved chunks') or not blocks:
            blocks.append(b)
        else:
            blocks[-1] += '\n> ' + b
    blocks = [b for b in blocks if b.strip()][:count]
    answers = []
    for b in blocks:
        if '--- Answer ---' in b:
            answers.append(b.split('--- Answer ---', 1)[1].strip())
        else:
            answers.append('(no results)' if '(no results)' in b else '(no answer)')
    return answers


def tokens(text):
    return [(m.group(), m.start(), m.end()) for m in NUMBER.finditer(text)]


def has_number(text, value):
    return any(t == value for t, _, _ in tokens(text))


def rounded_billions(answer, value, unit):
    """True if the answer states `value` (in millions) in billions, rounded to the digits it gives - the
    filing's own wording is often "$3.1 billion" for 3,074 million (H4)."""
    if unit != 'million':
        return False
    expected = float(value.replace(',', '')) / 1000
    for t, _, e in tokens(answer):
        if re.match(r'\s*billion', answer[e:e + 10], re.I):
            decimals = len(t.split('.')[1]) if '.' in t else 0
            if decimals and round(expected, decimals) == float(t.replace(',', '')):
                return True
    return False


def unit_status(answer, value, unit):
    """'ok', 'missing' or 'wrong' for the first occurrence of `value` that carries a unit, else the first."""
    if unit is None:
        return 'ok'
    hits = [(s, e) for t, s, e in tokens(answer) if t == value]
    for s, e in hits:
        if unit == '%':
            if re.match(r'\)?\s*(%|percent)', answer[e:e + 10], re.I):
                return 'ok'
        elif unit == '$':
            if re.search(r'\$\s*\(?\s*$', answer[max(0, s - 3):s]):
                return 'ok'
        else:
            m = SCALE_AFTER.match(answer[e:e + 12])
            if m:
                return 'ok' if m.group(1).lower() == unit else 'wrong'
    if unit in ('million', 'thousand') and re.search(rf'\b{unit}s?\b', answer, re.I):
        return 'ok'
    return 'missing'


def stated_figures(answer, question):
    """Numbers the answer states as figures: not years, not numbers from the question, not citation text."""
    text = re.sub(r'\(Source:[^)]*\)|[A-Z]{4}-10K-\d{4}\.html|\b(?:Item|Note|PART)\s+\w+', ' ', answer)
    asked = {t for t, _, _ in tokens(question)}
    return [t for t, _, _ in tokens(text) if not re.fullmatch(r'(19|20)\d\d', t) and t not in asked]


def grade(entry, answer):
    kind, expect = entry['kind'], entry.get('expect', [])
    if UNIT_ONLY.match(answer):
        return 'malformed', 'the units rule as the whole answer'
    declines = bool(DECLINE.search(answer)) or answer in ('(no results)', '(no answer)')
    figures = stated_figures(answer, entry['question'])
    # A decline that still states a figure - T9's "$0 ... as there is no mention", T8's "not stated, but the
    # year's average was $85.47" - is neither a clean decline nor a clean answer: a reader decides.
    declined = declines and not figures
    if kind == 'negative':
        if declined:
            return 'decline-ok', ''
        return ('check', 'declines but states figures') if declines else ('wrong', 'answered a negative')
    if kind == 'fact':
        missing = [e for e in expect if e.lower() not in answer.lower() and not has_number(answer, e)]
        if not missing:
            conflict = [c for c in entry.get('conflicts', []) if c.lower() in answer.lower()]
            return ('check', f'also names {", ".join(conflict)}') if conflict else ('reliable', '')
        if declined:
            return 'declined', ''
        return ('check', 'declines but states figures') if declines else ('wrong', f'missing {", ".join(missing)}')

    unit = entry.get('unit')
    found = [e for e in expect if has_number(answer, e)]
    for alt in entry.get('accept', []):
        if not found and has_number(answer, alt['value']) and alt['if_label'].lower() in answer.lower():
            found, expect = [alt['value']], [alt['value']]
    # exact: an MD&A-rounding trap or asked-for arithmetic, where "$55.7 billion" is the wrong answer, not a rounding of it.
    if expect and not found and not entry.get('exact') and all(rounded_billions(answer, e, unit) for e in expect):
        return 'reliable', 'rounded, in billions'
    if len(found) == len(expect) and expect:
        units = [unit_status(answer, e, unit) for e in expect]
        conflict = [c for c in entry.get('conflicts', []) if c not in expect and has_number(answer, c)]
        if conflict:
            return 'check', f'also states {", ".join(conflict)}'
        if 'wrong' in units:
            return 'wrong-unit', f'expected {unit}'
        if 'missing' in units:
            return 'no-unit', f'expected {unit}'
        return 'reliable', ''
    if kind == 'routing' and declined:
        return 'decline-ok', 'routing test'
    if declined and not found:
        return 'declined', ''
    traps = [t for t in entry.get('traps', []) if has_number(answer, t)]
    if declines and not found:
        return 'check', 'declines but states ' + ', '.join(traps or figures)
    if found:
        return 'wrong', f'only {", ".join(found)} of {", ".join(expect)}'
    return 'wrong', f'trap {", ".join(traps)}' if traps else 'expected figure absent'


def main():
    args = sys.argv[1:]
    json_out = None
    if '--json' in args:
        i = args.index('--json')
        json_out = args[i + 1]
        del args[i:i + 2]
    log_path, questions_path = args
    expected = {e['question']: e for e in json.load(open('tools/expected-answers.json', encoding='utf-8'))['questions']}
    questions = [q.strip() for q in open(questions_path, encoding='utf-8').read().split('\n') if q.strip()]
    unknown = [q for q in questions if q not in expected]
    if unknown:
        sys.exit('No expected answer for: ' + '; '.join(unknown))
    answers = answers_from_log(log_path, len(questions))
    # A held-out run from before H16-H35 were appended (2026-09-30) answers only the first 15: grade those. Any other
    # shortfall, or more answers than questions, still means the wrong log.
    if len(answers) == 15 < len(questions) and 'heldout' in questions_path:
        print(f'(a run from before H16-H35: grading the first {len(answers)} of {len(questions)} questions)\n')
        questions = questions[:len(answers)]
    if len(answers) != len(questions):
        sys.exit(f'{len(answers)} answers in the log for {len(questions)} questions - is it the right log?')

    results = []
    for q, a in zip(questions, answers):
        entry = expected[q]
        status, note = grade(entry, a)
        results.append({'id': entry['id'], 'kind': entry['kind'], 'status': status, 'note': note, 'answer': a})
        line = f"{entry['id']:>4}  {status:<11} {note}"
        if status not in ('reliable', 'decline-ok') or note:
            line += f"\n      > {' '.join(a.split())[:300]}"
        print(line)

    print()
    for prefix, name in GROUPS:
        rs = [r for r in results if re.match(prefix, r['id'])]
        if not rs:
            continue
        count = lambda *s: sum(1 for r in rs if r['status'] in s)
        correct = count('reliable', 'decline-ok', 'no-unit', 'wrong-unit')
        print(f"{name}: reliable {count('reliable', 'decline-ok')}/{len(rs)}  correct {correct}/{len(rs)}  "
              f"(no unit {count('no-unit')}, wrong unit {count('wrong-unit')})  declined {count('declined')}  "
              f"wrong {count('wrong')}  malformed {count('malformed')}  check {count('check')}")
    if json_out:
        json.dump({'log': log_path, 'questions': questions_path, 'results': results},
                  open(json_out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)


if __name__ == '__main__':
    # A console that can't show a character (a cp1252 console and an old log's U+FFFD) prints '?' instead of crashing.
    sys.stdout.reconfigure(errors='replace')
    main()
