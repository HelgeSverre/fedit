import pty, os, select, time, re, sys

# Subcommands / choices / flags / dynamic come back as plain strings; the
# root positional goes through edit:complete-filename, whose candidates
# are complex-candidate values — stringify those via $c[stem].
cmds = [
    "eval (slurp < /completions/fedit.elv)",
    "var f = $edit:completion:arg-completer[fedit]",
    "$f fedit plugins '' | each {|c| echo PLG=$c }",
    "$f fedit completions '' | each {|c| echo CMP=$c }",
    "$f fedit completions - | each {|c| echo FLG=$c }",
    "$f fedit plugins remove '' | each {|c| echo REM=$c }",
    "$f fedit /tmp/feditc/som | each {|c| if (eq (kind-of $c) string) { echo FIL=$c } else { echo FIL=$c[stem] } }",
    "echo DONE",
    "exit",
]
pid, fd = pty.fork()
if pid == 0:
    os.execvp("elvish", ["elvish", "-i"])
time.sleep(0.4)
for c in cmds:
    os.write(fd, (c + "\n").encode()); time.sleep(0.3)
out = b""
while True:
    r, _, _ = select.select([fd], [], [], 1.0)
    if not r: break
    try: d = os.read(fd, 4096)
    except OSError: break
    if not d: break
    out += d

got = {"PLG": [], "CMP": [], "FLG": [], "REM": [], "FIL": []}
err = False
for line in out.decode(errors="replace").splitlines():
    s = line.strip()
    m = re.match(r"^(PLG|CMP|FLG|REM|FIL)=(.+)$", s)
    if m and "each {" not in s:
        got[m.group(1)].append(m.group(2))
    if "Exception" in s and "each {" not in s and "$f " not in s:
        print("  ELVISH ERROR:", s); err = True

for k in ("PLG", "CMP", "FLG", "REM", "FIL"):
    print(f"  {k}: {' '.join(got[k])}")

ok = ("install" in got["PLG"] and "validate" in got["PLG"]
      and "xonsh" in got["CMP"] and "--install" in got["FLG"]
      and "alpha" in got["REM"] and "beta" in got["REM"]
      and any("somefile.txt" in v for v in got["FIL"]))
print("  elvish functional OK" if ok and not err else "  elvish FAILED")
sys.exit(0 if ok and not err else 1)
