"""Check built documentation as served by GitHub Pages. Run from a Git checkout.

Usage: python tools/check-docs.py PATH_TO_BUILT_SITE
External HTTP(S) URLs are checked for syntax, not availability: generated API
references, redirects and rate limits must not make a local build flaky.
"""

import argparse
from html.parser import HTMLParser
from pathlib import Path
import posixpath
import re
import subprocess
import sys
from urllib.parse import quote, unquote, urljoin, urlsplit


REPO = Path(__file__).resolve().parents[1]
REPO_PATH = "/brendankowitz/fhir-federation-service"
PAGES_HOST = "brendankowitz.github.io"
PREFIX = "/fhir-federation-service/"


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.anchors = set()
        self.links = []
        self.base = None

    def handle_starttag(self, tag, attrs):
        for key, value in attrs:
            if value is None:
                continue
            if key == "id" or (tag == "a" and key == "name"):
                self.anchors.add(value)
            if tag == "base" and key == "href":
                if self.base is None:
                    self.base = value
            elif key in ("href", "src", "poster") or (tag == "object" and key == "data"):
                self.links.append((self.getpos()[0], value))
            elif key == "srcset":
                # Commas inside URL tokens (e.g. data: images) aren't separators.
                for match in re.finditer(r"(?:^|,)\s*([^\s,]+(?:,[^\s,]+)*)(?:\s+[^,]*)?", value):
                    self.links.append((self.getpos()[0], match[1]))


def git(*args):
    return subprocess.check_output(["git", "-C", str(REPO), *args])


def check_link(base, raw, files, pages, sha, sources):
    ref = urlsplit(raw.strip())
    if ref.scheme and ref.scheme not in ("http", "https"):
        return None
    if (ref.scheme or raw.startswith("//")) and not ref.hostname:
        return "missing HTTP host"
    url = urlsplit(urljoin(base, raw))
    if not url.hostname or any(c.isspace() for c in url.netloc):
        return "invalid HTTP host"
    # Accessing .port validates its syntax and range in urllib.
    _ = url.port
    path = unquote(url.path)
    if url.hostname == "github.com":
        parts = path.split("/", 5)
        if (len(parts) >= 4 and "/".join(parts[:3]).lower() == REPO_PATH
                and parts[3] in ("blob", "tree", "raw")):
            if len(parts) < 5 or parts[4] != sha:
                return f"source link must use build SHA {sha}"
            target = parts[5].rstrip("/") if len(parts) == 6 else ""
            kind = "tree" if parts[3] == "tree" else "blob"
            if sources.get(target) != kind:
                return f"no tracked {kind} at {sha}:{target}"
    if url.hostname != PAGES_HOST:
        return None
    normalized = posixpath.normpath(path)
    if normalized != PREFIX[:-1] and not normalized.startswith(PREFIX):
        return f"outside Pages prefix {PREFIX}"
    target = normalized[len(PREFIX):]
    if path.endswith("/") or target not in files:
        index = posixpath.join(target, "index.html")
        target = target + ".html" if not path.endswith("/") and target + ".html" in files else index
    if target not in files:
        return "missing site target"
    fragment = unquote(url.fragment).split(":~:text=", 1)[0]
    if target in pages and fragment and fragment.lower() != "top" and fragment not in pages[target].anchors:
        return f"missing fragment in {target}"
    return None


def check(site):
    files = {p.relative_to(site).as_posix() for p in site.rglob("*") if p.is_file()}
    if "index.html" not in files:
        raise ValueError(f"{site}: no index.html at site root")
    pages = {}
    for name in sorted(files):
        if name.lower().endswith((".htm", ".html")):
            page = Page()
            page.feed((site / name).read_text(encoding="utf-8-sig"))
            page.close()
            pages[name] = page

    sha = git("rev-parse", "HEAD").decode().strip()
    sources = {"": "tree"}
    for entry in git("ls-tree", "-rtz", "--full-tree", sha).decode().split("\0"):
        if entry:
            metadata, name = entry.split("\t", 1)
            sources[name] = metadata.split()[1]

    errors = 0
    for name, page in pages.items():
        base = urljoin(f"https://{PAGES_HOST}{PREFIX}{quote(name)}", page.base or "")
        for line, raw in page.links:
            try:
                reason = check_link(base, raw, files, pages, sha, sources)
            except ValueError as exc:
                reason = str(exc)
            if reason:
                print(f"{name}:{line}: {raw!r}: {reason}", file=sys.stderr)
                errors += 1
    print(f"Checked {len(pages)} HTML pages; {errors} broken references.")
    return errors == 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path)
    try:
        sys.exit(0 if check(parser.parse_args().site) else 1)
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        sys.exit(str(exc))
