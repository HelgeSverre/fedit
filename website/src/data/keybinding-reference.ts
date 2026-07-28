import generated from "./keybindings.json";

export interface KeybindingReference {
  stroke: string;
  action: string;
  context: string;
  category: string;
  description: string;
  bound: boolean;
}

const generatedReference = generated
  .filter((binding) => !(binding.action === "quit" && !binding.bound))
  .map((binding) => {
    if (binding.stroke === "ctrl+t" && binding.context === "global") {
      return {
        ...binding,
        description: "Reveal and focus the sidebar when hidden; focus it when already visible",
      };
    }

    if (binding.stroke === "ctrl+t" && binding.context === "sidebar") {
      return {
        ...binding,
        description: "Hide the sidebar and return focus to the editor",
      };
    }

    return binding;
  });

export const keybindingReference: KeybindingReference[] = [
  {
    stroke: "ctrl+q",
    action: "quit",
    context: "reserved",
    category: "file",
    description:
      "Quit with the dirty-buffer guard; handled before the configurable keymap so it remains available if the keymap is broken",
    bound: true,
  },
  ...generatedReference,
];
