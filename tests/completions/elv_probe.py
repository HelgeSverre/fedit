import pty, os, select, time, re, sys

# Probe paths that return plain strings (subcommands / choices / flags /
# dynamic) — avoids edit:complete-filename, whose candidates are maps.
cmds = [
    "eval (slurp < /completions/fedit.elv)",
    "var f = $edit:completion:arg-completer[fedit]",
    "$f fedit plugins '' | each {|c| echo PLG=$c }",
    "$f fedit completions '' | each {|c| echo CMP=$c }",
    "$f fedit completions - | each {|c| echo FLG=$c }",
    "$f fedit plugins remove '' | each {|c| echo REM=$c }",
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

got = {"PLG": [], "CMP": [], "FLG": [], "REM": []}
err = False
for line in out.decode(errors="replace").splitlines():
    s = line.strip()
    m = re.match(r"^(PLG|CMP|FLG|REM)=(.+)$", s)
    if m and "each {" not in s:
        got[m.group(1)].append(m.group(2))
    if "Exception" in s and "each {" not in s and "$f " not in s:
        print("  ELVISH ERROR:", s); err = True

for k in ("PLG", "CMP", "FLG", "REM"):
    print(f"  {k}: {' '.join(got[k])}")

ok = ("install" in got["PLG"] and "validate" in got["PLG"]
      and "xonsh" in got["CMP"] and "--install" in got["FLG"]
      and "alpha" in got["REM"] and "beta" in got["REM"])
print("  elvish functional OK" if ok and not err else "  elvish FAILED")
sys.exit(0 if ok and not err else 1)
