"""Optional network check: python tools/check-docs-external.py artifacts/docs/_site"""

import argparse
from html.parser import HTMLParser
from pathlib import Path
import sys
import time
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


# Only hand-written articles staged by build-docs.ps1, not generated references.
ARTICLES = ("index.html", "operator.html", "status.html", "client-developer-guide.html")
REPO_PATH = "/brendankowitz/fhir-federation-service"


class ArticleLinks(HTMLParser):
    def __init__(self):
        super().__init__()
        self.in_article = False
        self.code_depth = 0
        self.saw_article = False
        self.links = {}

    def handle_starttag(self, tag, attrs):
        if tag == "article":
            self.in_article = self.saw_article = True
        elif tag in ("pre", "code"):
            self.code_depth += 1
        elif tag == "a" and self.in_article and not self.code_depth:
            href = dict(attrs).get("href") or ""
            if href.startswith("//"):
                href = "https:" + href
            url = urlsplit(href)
            if url.scheme not in ("https", "http") or url.hostname == "brendankowitz.github.io":
                return
            if url.hostname == "github.com" and (url.path.lower() + "/").startswith(REPO_PATH + "/"):
                return
            self.links.setdefault(url._replace(fragment="").geturl(), self.getpos()[0])

    def handle_endtag(self, tag):
        if tag == "article":
            self.in_article = False
        elif tag in ("pre", "code"):
            self.code_depth -= 1


def probe(url):
    # Use GET because some documentation hosts reject HEAD. Close after headers;
    # neither download the document body nor follow any links on the target page.
    request = Request(url, headers={"User-Agent": "fhir-federation-service-docs/1.0"})
    for attempt in range(2):
        try:
            with urlopen(request, timeout=8):
                return
        except HTTPError as exc:
            exc.close()
            if attempt or (exc.code not in (408, 429) and exc.code < 500):
                raise
        except OSError:
            if attempt:
                raise
        time.sleep(1)


def check(site):
    targets = {}
    for name in ARTICLES:
        parser = ArticleLinks()
        parser.feed((site / name).read_text(encoding="utf-8-sig"))
        parser.close()
        if not parser.saw_article:
            raise ValueError(f"{name}: missing DocFX article")
        for url, line in parser.links.items():
            targets.setdefault(url, f"{name}:{line}")
    failures = 0
    for url, location in targets.items():
        try:
            probe(url)
        except (OSError, ValueError) as exc:
            print(f"{location}: {url}: {exc}", file=sys.stderr)
            failures += 1
    print(f"Checked {len(targets)} authored external URLs; {failures} unreachable.")
    return failures == 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path)
    try:
        sys.exit(0 if check(parser.parse_args().site) else 1)
    except (OSError, ValueError) as exc:
        sys.exit(str(exc))
