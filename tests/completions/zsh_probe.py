import fcntl, os, pty, re, select, struct, sys, termios, time

# Drives `zsh -i` through a pty with a controlled ZDOTDIR whose .zshrc
# puts /completions on $fpath and runs compinit — the real autoload path
# for the generated `_fedit` (its `#compdef fedit` line registers it).
# Each check waits for the prompt, types a line, hits TAB, and scans the
# echoed line plus the completion listing for the expected names.
# (Unlike the yash probe, zsh needs prompt-synchronised reads: compinit
# startup is slow enough that blind sleeps drop the early output.)

ZDOT = "/tmp/zdot"
PROMPT = b"zp%"
os.makedirs(ZDOT, exist_ok=True)
with open(os.path.join(ZDOT, ".zshrc"), "w") as f:
    f.write(
        "PS1='zp%% '\n"
        "fpath=(/completions $fpath)\n"
        "autoload -Uz compinit\n"
        "compinit -u\n"
        # No menu selection and no "do you wish to see all N" pause —
        # candidates must land in the scrollback for us to read.
        "zstyle ':completion:*' menu no\n"
        "LISTMAX=1000\n"
    )

def drain(fd, idle=1.0, total=8.0):
    """Read until the stream goes idle for `idle` seconds (or `total` cap)."""
    out = b""
    end = time.time() + total
    while time.time() < end:
        r, _, _ = select.select([fd], [], [], idle)
        if not r:
            break
        try:
            d = os.read(fd, 4096)
        except OSError:
            break
        if not d:
            break
        out += d
    return out

def read_until(fd, token, timeout=10.0):
    out = b""
    end = time.time() + timeout
    while time.time() < end and token not in out:
        r, _, _ = select.select([fd], [], [], 0.2)
        if not r:
            continue
        try:
            d = os.read(fd, 4096)
        except OSError:
            break
        if not d:
            break
        out += d
    return out

def run(typed):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["ZDOTDIR"] = ZDOT
        os.environ["PATH"] = "/stub:" + os.environ.get("PATH", "")
        os.execvp("zsh", ["zsh", "-i"])
    # A real window size: with 0x0 zsh may prompt before listing matches.
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 120, 0, 0))
    out = read_until(fd, PROMPT)          # wait for compinit + first prompt
    os.write(fd, typed.encode())
    out += drain(fd, idle=0.4, total=3.0)  # echoed line
    # Two TABs: the first inserts the unambiguous prefix, the second
    # lists the remaining candidates (AUTO_LIST only fires when nothing
    # could be inserted).
    os.write(fd, b"\t")
    out += drain(fd, idle=1.0, total=8.0)
    os.write(fd, b"\t")
    out += drain(fd, idle=1.0, total=8.0)
    os.write(fd, b"\x03")
    os.write(fd, b"exit\n")
    out += drain(fd, idle=0.4, total=3.0)
    os.close(fd)
    try:
        os.waitpid(pid, 0)
    except ChildProcessError:
        pass
    return re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]|\x1b[=>]", " ", out.decode(errors="replace"))

def check(label, typed, expected, absent=()):
    txt = run(typed)
    found = [w for w in expected if w in txt]
    leaked = [w for w in absent if w in txt]
    ok = len(found) == len(expected) and not leaked
    detail = f"found {found}" + (f", leaked {leaked}" if leaked else "")
    print(f"  {label}: {'OK' if ok else 'MISSING'} ({detail})")
    return ok

# Root word 1 offers subcommands AND completes real files; `plugins
# validate` is directories-only (`_files -/`).
ok1 = check("root subcommands", "fedit ", ["plugins", "completions", "keybinds", "themes"])
ok2 = check("root file", "fedit /tmp/feditc/som", ["somefile.txt"])
ok3 = check(
    "validate dirs only",
    "fedit plugins validate /tmp/feditc/som",
    ["somedir"],
    absent=["somefile.txt"],
)
sys.exit(0 if (ok1 and ok2 and ok3) else 1)
