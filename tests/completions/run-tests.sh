#!/usr/bin/env bash
set -uo pipefail
fail=0
mark() { if [ "$1" -ne 0 ]; then fail=1; echo "  ^ FAILED"; fi; }

echo "### versions"
nu --version; elvish -version; xonsh --version; yash --version | head -1; murex --version; osh --version | head -1; pwsh --version

# stub `fedit` on PATH so DynamicCommand completers (plugins remove) resolve
mkdir -p /stub
printf '#!/bin/sh\n[ "$1" = plugins ] && [ "$2" = list ] && { echo alpha; echo beta; }\n' > /stub/fedit
chmod +x /stub/fedit
export PATH="/stub:$PATH"

# shared fixture for the file-completion probes: every shell completes
# `/tmp/feditc/som` to somefile.txt (files) or somedir (dirs-only).
mkdir -p /tmp/feditc/somedir
touch /tmp/feditc/somefile.txt

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

echo; echo "### zsh functional (compinit autoload + pty harness)"
python3 /zsh_probe.py; mark $?

echo; echo "### fish functional"
fish_check() {
  local label=$1 line=$2; shift 2
  local out missing="" w
  out=$(fish -c "source /completions/fedit.fish; complete -C'$line'" 2>&1)
  for w in "$@"; do
    printf '%s\n' "$out" | grep -q -- "$w" || missing="$missing $w"
  done
  if [ -z "$missing" ]; then
    echo "  fish $label OK"
  else
    echo "  fish $label MISSING:$missing"
    printf '%s\n' "$out" | sed 's/^/    /' | head -10
    return 1
  fi
}
fish_check "subcommands" 'fedit ' plugins completions keybinds themes; mark $?
fish_check "flags" 'fedit --' '--help' '--version' '--log'; mark $?
fish_check "root file" 'fedit /tmp/feditc/som' somefile.txt; mark $?
fish_check "dynamic (plugins remove)" 'fedit plugins remove ' alpha beta; mark $?

echo; echo "### nushell: source + extern registration"
nu --commands 'source /completions/fedit.nu; let n = (scope commands | where name =~ "fedit" and type == "external" | length); print $"nu externs: ($n)"; if $n < 10 { exit 1 }'; mark $?

# Driving nu's interactive completer isn't worth a pty harness; assert
# statically that the root extern types its positional as `path` (which
# is what makes nu offer file completion there).
echo; echo "### nushell: root positional typed as path"
if awk '/^export extern "fedit" \[/ {f=1} f {print; if (/^\]/) exit}' /completions/fedit.nu | grep -q 'path?: path'; then
  echo "  nu root path type OK"
else
  echo "  nu root path type MISSING"; fail=1
fi

echo; echo "### pwsh functional (CommandCompletion engine)"
pwsh -NoProfile -NoLogo -File /pwsh_probe.ps1; mark $?

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
