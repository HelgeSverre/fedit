[
  (comment)
  (block_comment)
] @comment

(string) @string
(number) @number
(boolean) @boolean
(missing_value) @constant.builtin

[
  (identifier)
  (piped_identifier)
] @variable

[
  (handler_definition name: (_) @function)
  (command_call command: (_) @function.call)
]

(handler_call (identifier) @function.call)
(handler_call (piped_identifier) @function.call)

[
  (additive_operator)
  (comparison_operator)
  (logical_operator)
  (multiplicative_operator)
  (range_operator)
  (unary_operator)
] @operator

[
  (keyword_application)
  (keyword_considering)
  (keyword_continue)
  (keyword_copy)
  (keyword_else)
  (keyword_else_if)
  (keyword_end)
  (keyword_error)
  (keyword_exit)
  (keyword_function)
  (keyword_global)
  (keyword_if)
  (keyword_ignoring)
  (keyword_local)
  (keyword_log)
  (keyword_my)
  (keyword_on)
  (keyword_on_error)
  (keyword_property)
  (keyword_repeat)
  (keyword_return)
  (keyword_script)
  (keyword_set)
  (keyword_tell)
  (keyword_then)
  (keyword_to)
  (keyword_try)
  (keyword_use)
  (keyword_using_terms_from)
  (keyword_with_timeout)
  (keyword_with_transaction)
] @keyword
