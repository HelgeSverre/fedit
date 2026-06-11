#!/usr/bin/env bash
source /completions/fedit.bash
probe() {
  COMP_WORDS=("$@"); COMP_CWORD=$(( ${#COMP_WORDS[@]} - 1 )); _fedit
  printf '  %-28s => %s\n' "${COMP_WORDS[*]}" "${COMPREPLY[*]}"
}
probe fedit plugins ''
probe fedit plugins remove ''
probe fedit completions ''
# assert dynamic completion produced the stub's plugin names
COMP_WORDS=(fedit plugins remove ''); COMP_CWORD=3; _fedit
case " ${COMPREPLY[*]} " in *" alpha "*) echo "  dynamic OK" ;; *) echo "  dynamic MISSING"; exit 1 ;; esac
# root positional: compgen -f file completion (offered alongside subcommands)
COMP_WORDS=(fedit /tmp/feditc/som); COMP_CWORD=1; _fedit
case " ${COMPREPLY[*]} " in
  *" /tmp/feditc/somefile.txt "*) echo "  root file OK" ;;
  *) echo "  root file MISSING (${COMPREPLY[*]})"; exit 1 ;;
esac
# plugins validate: directories only (compgen -d)
COMP_WORDS=(fedit plugins validate /tmp/feditc/som); COMP_CWORD=3; _fedit
case " ${COMPREPLY[*]} " in
  *" /tmp/feditc/somedir "*) ;;
  *) echo "  validate dir MISSING (${COMPREPLY[*]})"; exit 1 ;;
esac
case " ${COMPREPLY[*]} " in
  *" /tmp/feditc/somefile.txt "*) echo "  validate dir leaked files"; exit 1 ;;
  *) echo "  validate dir OK" ;;
esac
