; Comments (and the shebang line, which tree-sitter-bash parses as a comment).
(comment) @comment

; Keywords
[
  "if"
  "then"
  "else"
  "elif"
  "fi"
  "case"
  "esac"
  "in"
  "for"
  "while"
  "until"
  "do"
  "done"
  "function"
  "select"
] @keyword

; Function definitions
(function_definition
  name: (word) @function)

; Invoked commands (echo, ls, custom functions, …)
(command_name) @function.call

; Variables and expansions
(variable_name) @variable
(special_variable_name) @constant
(simple_expansion) @variable
(expansion) @variable

; Strings and heredocs
(string) @string
(raw_string) @string
(heredoc_body) @string
(heredoc_start) @string

; Test operators ([ -f x ], [[ -z $y ]], …)
(test_operator) @operator

; Pipelines and logical operators
[
  "|"
  "|&"
  "&&"
  "||"
  "="
] @operator

; Statement separators
[
  ";"
  ";;"
] @punctuation
