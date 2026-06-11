source /completions/fedit.xsh
from xonsh.parsers.completion_context import CompletionContext, CommandContext, CommandArg

comp = __xonsh__.completers['fedit']

def probe(parts, prefix):
    args = tuple(CommandArg(p) for p in parts)
    cmd = CommandContext(args=args, arg_index=len(args), prefix=prefix)
    res = comp(CompletionContext(command=cmd))
    vals = sorted(str(c) for c in (res or []))
    print(f"  {' '.join(parts)!r} +{prefix!r} => {vals}")
    return vals

probe(('fedit', 'plugins'), '')
probe(('fedit', 'completions'), '')
probe(('fedit', 'plugins'), 'l')
probe(('fedit', 'completions'), '-')
rem = probe(('fedit', 'plugins', 'remove'), '')

ok = 'alpha' in rem and 'beta' in rem
print('  dynamic OK' if ok else '  dynamic MISSING')

# Full-pipeline checks: the root positional defers to xonsh's own path
# completer (complete_path), so drive Completer.complete the way the
# shell would and assert the merged results.
from xonsh.completer import Completer
_completer = Completer()

def full(line):
    prefix = line.rsplit(' ', 1)[-1]
    begidx = len(line) - len(prefix)
    res = _completer.complete(prefix, line, begidx, len(line), ctx=__xonsh__.ctx,
                              multiline_text=line, cursor_index=len(line))
    if isinstance(res, tuple):
        res = res[0]
    vals = sorted(str(c).strip() for c in (res or []))
    shown = vals if len(vals) <= 8 else vals[:8] + ['...']
    print(f"  full {line!r} => {shown}")
    return vals

subs = full('fedit ')
files = full('fedit /tmp/feditc/som')
ok2 = ('plugins' in subs and 'completions' in subs
       and any('somefile.txt' in v for v in files))
print('  path/subcommand merge OK' if ok2 else '  path/subcommand merge MISSING')

import sys
sys.exit(0 if (ok and ok2) else 1)
