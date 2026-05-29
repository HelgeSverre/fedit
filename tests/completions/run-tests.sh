#!/usr/bin/env bash
set -uo pipefail
fail=0
mark() { if [ "$1" -ne 0 ]; then fail=1; echo "  ^ FAILED"; fi; }

echo "### versions"
nu --version; elvish -version; xonsh --version; yash --version | head -1; murex --version; osh --version | head -1

# stub `fedit` on PATH so DynamicCommand completers (plugins remove) resolve
mkdir -p /stub
printf '#!/bin/sh\n[ "$1" = plugins ] && [ "$2" = list ] && { echo alpha; echo beta; }\n' > /stub/fedit
chmod +x /stub/fedit
export PATH="/stub:$PATH"

# yash autoloads completion/<cmd> by filename from $YASH_LOADPATH.
cp /completions/fedit.yash /usr/share/yash/completion/fedit

echo; echo "### bash/zsh/fish parse (regression)"
bash -n /completions/fedit.bash && echo "bash parse OK"; mark $?
zsh  -n /completions/_fedit     && echo "zsh parse OK";  mark $?
fish --no-execute /completions/fedit.fish && echo "fish parse OK"; mark $?

echo; echo "### bash functional"
bash /bash_probe.sh; mark $?

echo; echo "### osh functional (reuses the bash emitter — no osh-specific script)"
osh /osh_probe.sh; mark $?

echo; echo "### nushell: source + extern registration"
nu --commands 'source /completions/fedit.nu; let n = (scope commands | where name =~ "fedit" and type == "external" | length); print $"nu externs: ($n)"; if $n < 10 { exit 1 }'; mark $?

echo; echo "### xonsh: register + functional"
xonsh --no-rc /xonsh_probe.xsh; mark $?

echo; echo "### elvish: functional (pty harness)"
python3 /elv_probe.py; mark $?

echo; echo "### yash: functional (autoload + pty harness)"
python3 /yash_probe.py; mark $?

echo; echo "### murex: schema accepted + introspected"
murex -c 'source /completions/fedit.mx; autocomplete get fedit' > /tmp/mx.out 2>&1
if grep -q '"plugins"' /tmp/mx.out && grep -q 'FlagValues' /tmp/mx.out; then
  echo "  murex schema OK"
else
  echo "  murex schema FAILED:"; sed 's/^/    /' /tmp/mx.out | head -20; fail=1
fi

echo; echo "### RESULT fail=$fail"
exit $fail
