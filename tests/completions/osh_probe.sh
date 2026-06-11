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
# root positional: compgen -f file completion (offered alongside subcommands)
COMP_WORDS=(fedit /tmp/feditc/som); COMP_CWORD=1; _fedit
case " ${COMPREPLY[*]} " in
  *" /tmp/feditc/somefile.txt "*) echo "  osh root file OK" ;;
  *) echo "  osh root file MISSING (${COMPREPLY[*]})"; exit 1 ;;
esac
# plugins validate: directories only (compgen -d)
COMP_WORDS=(fedit plugins validate /tmp/feditc/som); COMP_CWORD=3; _fedit
case " ${COMPREPLY[*]} " in
  *" /tmp/feditc/somedir "*) ;;
  *) echo "  osh validate dir MISSING (${COMPREPLY[*]})"; exit 1 ;;
esac
case " ${COMPREPLY[*]} " in
  *" /tmp/feditc/somefile.txt "*) echo "  osh validate dir leaked files"; exit 1 ;;
  *) echo "  osh validate dir OK" ;;
esac
