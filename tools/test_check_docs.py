"""Black-box docs checker tests. Only the loopback interface is needed."""

from http.server import BaseHTTPRequestHandler, HTTPServer
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from threading import Thread
import unittest


CHECKER = Path(__file__).with_name("check-docs.py").resolve()
SOURCE = "https://github.com/brendankowitz/fhir-federation-service"


class CheckDocsTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.site = Path(temp.name)
        self.sha = subprocess.check_output(
            ["git", "-C", str(CHECKER.parent), "rev-parse", "HEAD"], text=True
        ).strip()

    def check(self, html):
        (self.site / "index.html").write_text(html, encoding="utf-8")
        return subprocess.run([sys.executable, str(CHECKER), str(self.site)],
                              capture_output=True, text=True, cwd=self.site)

    def test_project_site(self):
        (self.site / "guide").mkdir()
        (self.site / "logo 2.png").touch()
        (self.site / "guide/index.html").write_text(
            '<h1 id="a:b">Guide</h1><img srcset="../logo%202.png 2x">'
            '<a href="/fhir-federation-service/#home">Home</a>', encoding="utf-8")
        result = self.check(f'''
            <h1 id="home">Home</h1><a href="guide/?v=2#a%3Ab">Guide</a>
            <a href="{SOURCE}/blob/{self.sha}/README.md#L1">File</a>
            <a href="{SOURCE}/tree/{self.sha}/tools">Directory</a>
            <a href="https://example.invalid/framework">External</a>''')
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_reports_broken_references_with_locations(self):
        urls = [
            ("href", "index.html#absent"),
            ("src", "missing.png"),
            ("srcset", "missing-2x.png 2x"),
            ("href", "../index.html"),
            ("href", "/wrong-prefix/index.html"),
            ("href", f"{SOURCE}/blob/main/README.md"),
            ("href", f"{SOURCE}/blob/{self.sha}/does-not-exist.md"),
            ("href", f"{SOURCE}/tree/{self.sha}/README.md"),
            ("href", "https://"),
        ]
        result = self.check("\n".join(f'<a {key}="{url}"></a>' for key, url in urls))
        self.assertEqual(result.returncode, 1)
        for line, (_, url) in enumerate(urls, 1):
            self.assertIn(f"index.html:{line}: {url.split()[0]!r}:", result.stderr)


class ExternalDocsTests(unittest.TestCase):
    def test_probes_authored_links_only_and_reports_permanent_failure(self):
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                requests.append(self.path)
                if self.path == "/retry":
                    self.send_response(503 if requests.count("/retry") == 1 else 200)
                else:
                    self.send_response(404)
                self.end_headers()

            def log_message(self, *args):
                pass

        with HTTPServer(("127.0.0.1", 0), Handler) as server, tempfile.TemporaryDirectory() as temp:
            thread = Thread(target=server.serve_forever)
            thread.start()
            try:
                origin = f"http://127.0.0.1:{server.server_port}"
                site = Path(temp)
                for name in ("operator", "status", "client-developer-guide"):
                    (site / f"{name}.html").write_text("<article></article>")
                (site / "contracts.html").write_text(f'<article><a href="{origin}/generated"></a></article>')
                (site / "index.html").write_text(f'''
                    <nav><a href="{origin}/chrome"></a></nav>
                    <article><pre><code><a href="{origin}/example"></a></code></pre>
                    <a href="{SOURCE}/commit/REVISION"></a><a href="{origin}/retry#a"></a>
                    <img src="{origin}/image"><a href="{origin}/retry#b"></a>
                    <a href="{origin}/missing"></a></article>''')
                result = subprocess.run(
                    [sys.executable, str(CHECKER.with_name("check-docs-external.py")), temp],
                    capture_output=True, text=True, timeout=20,
                    env={**os.environ, "no_proxy": "127.0.0.1"})
            finally:
                server.shutdown()
                thread.join()
        self.assertEqual(requests, ["/retry", "/retry", "/missing"])
        self.assertEqual(result.returncode, 1)
        self.assertIn("index.html:6:", result.stderr)
        self.assertIn("404", result.stderr)


if __name__ == "__main__":
    unittest.main()
