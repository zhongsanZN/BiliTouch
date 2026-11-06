"""Publish one versioned setup.exe to the BiliTouch repository."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = "zhongsanZN/BiliTouch"
BASE = "https://api.github.com/repos/" + REPOSITORY


def main():
    parser = argparse.ArgumentParser(description="Publish only the BiliTouch Windows installer")
    parser.add_argument("--version", required=True)
    parser.add_argument("--setup", type=Path, required=True)
    parser.add_argument("--notes", type=Path, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-.][A-Za-z0-9.-]+)?", args.version):
        parser.error("Invalid version")
    expected = f"BiliTouch-{args.version}-setup.exe"
    if args.setup.name != expected or not args.setup.is_file():
        parser.error(f"Installer must be named {expected}")
    notes = args.notes.read_text(encoding="utf-8-sig")
    root = Path(__file__).resolve().parents[1]
    remote = subprocess.check_output(["git", "remote", "get-url", "--push", "origin"], cwd=root, text=True).strip()
    if remote != f"https://github.com/{REPOSITORY}.git":
        raise RuntimeError("Refusing to publish to an unexpected repository")
    tag = "v" + args.version
    subprocess.run(["git", "rev-parse", "--verify", "refs/tags/" + tag], cwd=root, check=True, capture_output=True)
    credential = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n\n", cwd=root,
                                text=True, capture_output=True, check=True, env={**os.environ, "GIT_TERMINAL_PROMPT": "0", "GCM_INTERACTIVE": "Never"})
    fields = dict(line.split("=", 1) for line in credential.stdout.splitlines() if "=" in line)
    headers = {"Authorization": "Bearer " + fields["password"], "User-Agent": "BiliTouch-Release",
               "Accept": "application/vnd.github+json"}

    def request(url, data=None, method=None, content="application/json"):
        if urllib.parse.urlsplit(url).hostname not in {"api.github.com", "uploads.github.com"}:
            raise RuntimeError("Unexpected release endpoint")
        if isinstance(data, dict):
            data = json.dumps(data, ensure_ascii=False).encode()
        req = urllib.request.Request(url, data=data, method=method, headers={**headers, "Content-Type": content})
        with urllib.request.urlopen(req, timeout=180) as response:
            body = response.read()
            return json.loads(body) if body else None

    try:
        release = request(BASE + "/releases/tags/" + urllib.parse.quote(tag))
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = request(BASE + "/releases", {"tag_name": tag, "name": "BiliTouch " + args.version, "draft": True})
    data = args.setup.read_bytes()
    digest = "sha256:" + hashlib.sha256(data).hexdigest()
    existing = next((asset for asset in release["assets"] if asset["name"] == expected), None)
    if existing:
        if existing.get("digest") != digest:
            raise RuntimeError("Existing installer differs; publish a new version")
    else:
        request(release["upload_url"].split("{")[0] + "?name=" + urllib.parse.quote(expected), data,
                "POST", "application/octet-stream")
    assets = request(release["assets_url"])
    installer = next(asset for asset in assets if asset["name"] == expected)
    if installer["size"] != len(data) or installer.get("digest") != digest:
        raise RuntimeError("Uploaded installer verification failed")
    for asset in assets:
        if asset["id"] != installer["id"]:
            request(BASE + "/releases/assets/" + str(asset["id"]), method="DELETE")
    download = f"https://github.com/{REPOSITORY}/releases/download/{tag}/{expected}"
    body = f"[下载安装包]({download}) · [本版源码](https://github.com/{REPOSITORY}/tree/{tag})\n\n"
    body += notes
    body += "\n\n![BiliTouch 首页](https://raw.githubusercontent.com/zhongsanZN/BiliTouch/main/docs/screenshot-home-current.png)"
    release = request(release["url"], {"body": body, "draft": False, "prerelease": False,
                                      "make_latest": "true"}, "PATCH")
    remaining = request(release["assets_url"])
    if [asset["name"] for asset in remaining] != [expected]:
        raise RuntimeError("Release must contain exactly one uploaded installer")
    print(release["html_url"])


if __name__ == "__main__":
    main()
