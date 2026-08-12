# Security policy

## Supported versions

`fedit` is small and pre-1.0; only the latest tagged release receives
security fixes. Older tags are not patched.

| Version | Supported |
| ------- | --------- |
| latest  | ✅        |
| older   | ❌        |

## Reporting a vulnerability

Please report security issues privately by emailing
**helge.sverre@gmail.com** with `fedit security:` in the subject line.

Include:

- A short description of the issue
- Reproduction steps or a proof of concept
- The fedit version (`fedit --version`) and host OS

I'll acknowledge within 7 days and aim for a fix or mitigation within
30 days for confirmed issues. Critical issues affecting data integrity
or arbitrary code execution will be prioritised.

## Security boundaries

Repository contents and language-server protocol output are treated as
untrusted availability inputs. Configured language-server executables,
plugins, keybindings, and macro files are operator-controlled local state.
Resource-limit opt-outs in `config.json` are explicit operator choices.

Please do **not** file public GitHub issues for suspected vulnerabilities.
