(fenced_code_block
  (info_string (language) @injection.language)
  (code_fence_content) @injection.content)

((html_block) @injection.content
 (#set! injection.language "html"))
