source /completions/fedit.bash
probe() {
  COMP_WORDS=("$@"); COMP_CWORD=$(( ${#COMP_WORDS[@]} - 1 )); _fedit
  printf '  %-26s => %s\n' "${COMP_WORDS[*]}" "${COMPREPLY[*]}"
}
probe fedit plugins ''
probe fedit completions ''
probe fedit plugins l
COMP_WORDS=(fedit plugins remove ''); COMP_CWORD=3; _fedit
case " ${COMPREPLY[*]} " in
  *" alpha "*) echo "  osh dynamic OK" ;;
  *) echo "  osh dynamic MISSING"; exit 1 ;;
esac
