#!/usr/bin/env python3
"""Split the test project's classes across CI runners, and prove every test ran exactly once.

  ci-shards.py plan  LIST WEIGHTS NET10_SHARDS NET48_SHARDS OUT_DIR
      LIST is `dotnet test --list-tests` output (net10.0). Writes OUT_DIR/net10-<i>.txt and OUT_DIR/net48-<i>.txt, one class
      per line, and OUT_DIR/tests.txt (every test method, Namespace.Class.Method). Whole classes go to one shard each,
      the heaviest first onto the lightest shard (WEIGHTS: class, net10 seconds, net48 seconds; a class it does not list is
      weighed by its test count at the mean seconds per test).
  ci-shards.py ran   TRX_OR_XML OUT
      The tests one shard ran (a net10 trx or a net48 xunit v2 xml), one "Class<TAB>Method<TAB>seconds<TAB>outcome" per result.
  ci-shards.py check TFM PLAN_DIR RAN_DIR
      Fails unless every class planned for TFM ran in its own shard and in no other, and (net10) the methods that ran are
      exactly the listed ones. net48 compiles some methods out (#if NET): those are listed, not failed, as a whole class
      missing is.
  ci-shards.py weights RAN_DIR_NET10 RAN_DIR_NET48 OUT
      A fresh WEIGHTS file from the shards' ran files of one run.

The partition only balances the shards: a stale WEIGHTS file makes them uneven, never skips a test. Standard library only.
"""
import collections
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

TRX = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'


def method_of(display):
    """Namespace.Class.Method of a listed or reported test name (theory arguments dropped)."""
    name = display.strip()
    paren = name.find('(')
    return name[:paren] if paren >= 0 else name


def split(method):
    cls, _, name = method.rpartition('.')
    return cls, name


def read_list(path):
    methods = set()
    started = False
    with open(path, encoding='utf-8') as f:
        for line in f:
            if line.startswith('The following Tests are available'):
                started = True
                continue
            if started and line.startswith('    ') and line.strip():
                methods.add(method_of(line))
    if not methods:
        sys.exit(f'ci-shards: no tests listed in {path}')
    return methods


def read_weights(path):
    weights = {}
    if not os.path.exists(path):
        return weights
    with open(path, encoding='utf-8') as f:
        for line in f:
            if not line.strip() or line.startswith('#'):
                continue
            cls, net10, net48 = line.rstrip('\n').split('\t')
            weights[cls] = (float(net10), float(net48))
    return weights


def partition(counts, weights, column, shards):
    known = [(weights[c][column], counts[c]) for c in counts if c in weights]
    per_test = sum(w for w, _ in known) / max(1, sum(n for _, n in known)) if known else 1.0
    weight = {c: weights[c][column] if c in weights else counts[c] * per_test for c in counts}
    loads = [0.0] * shards
    groups = [[] for _ in range(shards)]
    for cls in sorted(counts, key=lambda c: (-weight[c], c)):
        i = min(range(shards), key=lambda s: (loads[s], s))
        loads[i] += weight[cls]
        groups[i].append(cls)
    return groups, loads


def plan(list_path, weights_path, net10_shards, net48_shards, out):
    methods = read_list(list_path)
    counts = collections.Counter(split(m)[0] for m in methods)
    weights = read_weights(weights_path)
    # A net10 shard selects a class by FullyQualifiedName~Class. (a substring): no other class's tests may contain it.
    for cls in counts:
        clash = sorted({split(m)[0] for m in methods if cls + '.' in m and split(m)[0] != cls})
        if clash:
            sys.exit(f'ci-shards: the filter for {cls} would also select {" ".join(clash)}; rename one of them')
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tests.txt'), 'w', encoding='utf-8') as f:
        f.write(''.join(m + '\n' for m in sorted(methods)))
    for tfm, column, shards in (('net10', 0, int(net10_shards)), ('net48', 1, int(net48_shards))):
        groups, loads = partition(counts, weights, column, shards)
        for i, group in enumerate(groups, 1):
            with open(os.path.join(out, f'{tfm}-{i}.txt'), 'w', encoding='utf-8') as f:
                f.write(''.join(c + '\n' for c in sorted(group)))
            print(f'{tfm} shard {i}/{shards}: {len(group)} classes, {sum(counts[c] for c in group)} test methods, '
                  f'about {loads[i - 1]:.0f} s')
    unweighed = sorted(c for c in counts if c not in weights)
    if unweighed:
        print(f'{len(unweighed)} classes not in {weights_path} (weighed by test count): {" ".join(unweighed)}')


def seconds(duration):
    h, m, s = duration.split(':')
    return int(h) * 3600 + int(m) * 60 + float(s)


def ran(path, out):
    rows = []
    root = ET.parse(path).getroot()
    if root.tag.startswith(TRX):
        names = {}
        for test in root.iter(TRX + 'UnitTest'):
            m = test.find(TRX + 'TestMethod')
            names[test.get('id')] = (m.get('className'), method_of(m.get('name')))
        for r in root.iter(TRX + 'UnitTestResult'):
            cls, name = names[r.get('testId')]
            rows.append((cls, name, seconds(r.get('duration', '0:0:0')), r.get('outcome')))
    else:
        for t in root.iter('test'):
            cls, name = split(method_of(t.get('name')))
            rows.append((t.get('type') or cls, method_of(t.get('method') or name), float(t.get('time', 0)), t.get('result')))
    if not rows:
        sys.exit(f'ci-shards: {path} reports no tests')
    with open(out, 'w', encoding='utf-8') as f:
        f.write(''.join(f'{c}\t{m}\t{s:.3f}\t{o}\n' for c, m, s, o in rows))
    print(f'{len(rows)} results from {path}')


def read_ran(path):
    with open(path, encoding='utf-8') as f:
        return [line.rstrip('\n').split('\t') for line in f if line.strip()]


def check(tfm, plan_dir, ran_dir):
    listed = set(open(os.path.join(plan_dir, 'tests.txt'), encoding='utf-8').read().split())
    planned = {}
    for path in glob.glob(os.path.join(plan_dir, f'{tfm}-*.txt')):
        shard = re.search(r'-(\d+)\.txt$', path).group(1)
        for cls in open(path, encoding='utf-8').read().split():
            if cls in planned:
                sys.exit(f'ci-shards: {cls} is planned for {tfm} shards {planned[cls]} and {shard}')
            planned[cls] = shard
    errors = []
    shards_of = collections.defaultdict(set)
    methods = set()
    total = 0
    for path in sorted(glob.glob(os.path.join(ran_dir, f'{tfm}-*.tsv'))):
        shard = re.search(r'-(\d+)\.tsv$', path).group(1)
        for cls, name, _, _ in read_ran(path):
            shards_of[cls].add(shard)
            methods.add(f'{cls}.{name}')
            total += 1
    shards = {p for p in planned.values()}
    reported = {re.search(r'-(\d+)\.tsv$', p).group(1) for p in glob.glob(os.path.join(ran_dir, f'{tfm}-*.tsv'))}
    for shard in sorted(shards - reported, key=int):
        errors.append(f'shard {shard} reported no results')
    for cls, shard in sorted(planned.items()):
        got = shards_of.get(cls, set())
        if not got:
            errors.append(f'{cls} (planned for shard {shard}) did not run')
        elif got != {shard}:
            errors.append(f'{cls} (planned for shard {shard}) ran in shards {" ".join(sorted(got, key=int))}')
    for cls in sorted(set(shards_of) - set(planned)):
        errors.append(f'{cls} ran in shards {" ".join(sorted(shards_of[cls], key=int))} but was never planned')
    extra = sorted(methods - listed)
    missing = sorted(listed - methods)
    if extra:
        errors.append(f'{len(extra)} methods ran that were never listed: {" ".join(extra[:20])}')
    if missing and tfm == 'net10':
        errors.append(f'{len(missing)} listed methods did not run: {" ".join(missing[:20])}')
    elif missing:
        print(f'{len(missing)} listed methods are not in {tfm} (compiled out there): {" ".join(missing)}')
    if errors:
        print(f'ci-shards: {tfm} shards do not run every test exactly once:')
        print('\n'.join('  ' + e for e in errors))
        sys.exit(1)
    print(f'{tfm}: {len(planned)} classes, {total} results over {len(shards)} shards; every class ran once, in its own shard')


def weights(net10_dir, net48_dir, out):
    table = collections.defaultdict(lambda: [0.0, 0.0])
    for column, folder in ((0, net10_dir), (1, net48_dir)):
        for path in glob.glob(os.path.join(folder, '*.tsv')):
            for cls, _, secs, _ in read_ran(path):
                table[cls][column] += float(secs)
    with open(out, 'w', encoding='utf-8') as f:
        f.write('# class\tnet10 seconds\tnet48 seconds: made by tools/ci-shards.py weights from one CI run\n')
        f.write(''.join(f'{c}\t{a:.1f}\t{b:.1f}\n' for c, (a, b) in sorted(table.items())))


if __name__ == '__main__':
    commands = {'plan': (plan, 5), 'ran': (ran, 2), 'check': (check, 3), 'weights': (weights, 3)}
    if len(sys.argv) < 2 or sys.argv[1] not in commands or len(sys.argv) - 2 != commands[sys.argv[1]][1]:
        sys.exit(__doc__)
    commands[sys.argv[1]][0](*sys.argv[2:])
