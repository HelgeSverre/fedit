; Types

(type_identifier) @type
(predefined_type) @type.builtin

((identifier) @type
 (#match? @type "^[A-Z]"))

(type_arguments
  "<" @punctuation.bracket
  ">" @punctuation.bracket)

; Variables

(required_parameter (identifier) @variable.parameter)
(optional_parameter (identifier) @variable.parameter)

; Keywords

[ "abstract"
  "declare"
  "enum"
  "export"
  "implements"
  "interface"
  "keyof"
  "namespace"
  "private"
  "protected"
  "public"
  "type"
  "readonly"
  "override"
  "satisfies"
] @keyword

; JSX elements

(jsx_opening_element name: (identifier) @type)
(jsx_closing_element name: (identifier) @type)
(jsx_self_closing_element name: (identifier) @type)

(jsx_opening_element name: (member_expression) @type)
(jsx_self_closing_element name: (member_expression) @type)

(jsx_attribute (property_identifier) @attribute)

(jsx_expression
  "{" @punctuation.bracket
  "}" @punctuation.bracket)

(jsx_opening_element
  "<" @punctuation.bracket
  ">" @punctuation.bracket)

(jsx_closing_element
  "</" @punctuation.bracket
  ">" @punctuation.bracket)

(jsx_self_closing_element
  "<" @punctuation.bracket
  "/>" @punctuation.bracket)
