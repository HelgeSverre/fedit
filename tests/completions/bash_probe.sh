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
