# Tools

Run the documentation checks from the repository root:

| Command | Purpose |
| --- | --- |
| `python tools/check-docs.py artifacts/docs/_site` | Validate built pages, local assets, fragments, and revision-pinned source paths offline. |
| `python tools/check-docs-external.py artifacts/docs/_site` | After the build, opt in to HTTP reachability checks for hyperlinks in the four hand-written articles. |
| `python tools/test_check_docs.py -v` | Exercise both checkers, including failures, without an internet connection or a docs build. |

The external check ignores navigation, generated pages, assets, code examples, and links back to this repository. It requests each distinct URL once (excluding the fragment) and retries transient failures once, with an eight-second socket timeout. It does not crawl linked pages.
