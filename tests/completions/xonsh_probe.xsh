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
import sys
sys.exit(0 if ok else 1)
