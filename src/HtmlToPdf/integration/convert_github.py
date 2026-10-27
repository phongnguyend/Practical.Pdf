"""Exercise a running engine with a live URL and its unmodified downloaded HTML."""

import argparse
import hashlib
import io
import json
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

from pypdf import PdfReader


SOURCE_URL = "https://github.com/phongnguyend"


def request(url, data=None, content_type=None):
    headers = {"User-Agent": "HtmlToPdf-Integration-Test"}
    if content_type:
        headers["Content-Type"] = content_type
    req = urllib.request.Request(url, data=data, headers=headers)
    with urllib.request.urlopen(req, timeout=120) as response:
        return response.read(), response.headers.get_content_type()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", required=True)
    parser.add_argument("--base-url", default="http://127.0.0.1:18080")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"engine": args.engine, "source_url": SOURCE_URL, "results": []}
    failures = []
    base_url = args.base_url.rstrip("/")

    for attempt in range(60):
        try:
            with urllib.request.urlopen(base_url + "/health", timeout=2):
                break
        except (OSError, urllib.error.URLError):
            time.sleep(1)
    else:
        raise RuntimeError("Engine did not become healthy within the startup deadline.")

    html = None
    try:
        html, content_type = request(SOURCE_URL)
        (args.output / "phongnguyend.html").write_bytes(html)
        if content_type != "text/html" or b"phongnguyend" not in html.lower():
            raise ValueError("GitHub did not return the expected profile HTML.")
        report["html_sha256"] = hashlib.sha256(html).hexdigest()
    except Exception as error:
        failures.append(f"Download: {error}")
        html = None

    for mode in ["url", "file"]:
        result = {"mode": mode}
        started = time.monotonic()
        try:
            if mode == "url":
                data = json.dumps({"url": SOURCE_URL}).encode()
                content_type = "application/json"
            else:
                if html is None:
                    raise RuntimeError("Cannot convert file because the HTML download failed.")
                boundary = uuid.uuid4().hex
                data = (
                    f'--{boundary}\r\nContent-Disposition: form-data; name="file"; '
                    'filename="phongnguyend.html"\r\nContent-Type: text/html\r\n\r\n'
                ).encode() + html + f"\r\n--{boundary}--\r\n".encode()
                content_type = f"multipart/form-data; boundary={boundary}"
            pdf, response_type = request(base_url + f"/convert-{mode}", data, content_type)
            output = args.output / f"{args.engine}-{mode}.pdf"
            output.write_bytes(pdf)
            if response_type != "application/pdf" or not pdf.startswith(b"%PDF-"):
                raise ValueError("Response is not a PDF.")
            document = PdfReader(io.BytesIO(pdf))
            text = "\n".join(page.extract_text() or "" for page in document.pages)
            (args.output / f"{args.engine}-{mode}.txt").write_text(text, encoding="utf-8")
            if not document.pages or "phongnguyend" not in text.lower():
                raise ValueError("PDF has no pages or is missing the GitHub profile text.")
            result.update(status="passed", pages=len(document.pages), bytes=len(pdf))
        except Exception as error:
            result.update(status="failed", error=str(error))
            failures.append(f"{mode}: {error}")
            if isinstance(error, urllib.error.HTTPError):
                (args.output / f"{mode}-error.txt").write_bytes(error.read())
        result["seconds"] = round(time.monotonic() - started, 2)
        report["results"].append(result)
        print(json.dumps(result), flush=True)

    report["failures"] = failures
    (args.output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    if failures:
        raise SystemExit("\n".join(failures))


if __name__ == "__main__":
    main()
