# Tools

Run the documentation checks from the repository root:

| Command | Purpose |
| --- | --- |
| `pwsh -File tools/build-docs.ps1` | Install locked site dependencies, generate references, stage topics, build Starlight and run the offline checks. |
| `python tools/check-docs.py docs/site/dist` | Validate built pages, local assets, fragments, and revision-pinned source paths offline. |
| `node docs/site/scripts/check-migration.mjs docs/site/dist` | Execute the built redirects with queries and fragments; verify legacy anchors, homepage revision and Pagefind index. |
| `python tools/check-docs-external.py docs/site/dist` | After the build, opt in to HTTP reachability checks for hyperlinks in the hand-written articles. |
| `python tools/test_check_docs.py -v` | Exercise both checkers, including failures, without an internet connection or a docs build. |

The external check ignores navigation, generated pages, assets, code examples, and links back to this repository. It requests each distinct URL once (excluding the fragment) and retries transient failures once, with an eight-second socket timeout. It does not crawl linked pages.
