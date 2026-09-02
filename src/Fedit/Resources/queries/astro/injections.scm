((frontmatter (frontmatter_js_block) @injection.content)
 (#set! injection.language "typescript"))

((style_element (raw_text) @injection.content)
 (#set! injection.language "css"))

((script_element (raw_text) @injection.content)
 (#set! injection.language "typescript"))
