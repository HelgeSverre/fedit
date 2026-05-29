import pty, os, select, time, re, sys

# Relies on run-tests.sh having copied fedit.yash to a directory on
# $YASH_LOADPATH as `completion/fedit`; yash autoloads it on TAB. No
# dot-sourcing — this mirrors the real install path.
def run(typed):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["PATH"] = "/stub:" + os.environ.get("PATH", "")
        os.execvp("yash", ["yash", "-i"])
    time.sleep(0.6)
    for c in ["set -o emacs\n", typed, "\t\t", "\x03", "exit\n"]:
        os.write(fd, c.encode()); time.sleep(0.5)
    out = b""
    while True:
        r, _, _ = select.select([fd], [], [], 1.0)
        if not r:
            break
        try:
            d = os.read(fd, 4096)
        except OSError:
            break
        if not d:
            break
        out += d
    return re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]", " ", out.decode(errors="replace"))

def check(label, typed, expected):
    txt = run(typed)
    found = [w for w in expected if re.search(r"(?<![\w-])" + re.escape(w) + r"(?![\w-])", txt)]
    ok = len(found) == len(expected)
    print(f"  {label}: {'OK' if ok else 'MISSING'} (found {found})")
    return ok

ok1 = check("fedit plugins", "fedit plugins ", ["install", "remove", "validate"])
ok2 = check("fedit completions", "fedit completions ", ["zsh", "fish", "murex"])
sys.exit(0 if (ok1 and ok2) else 1)
