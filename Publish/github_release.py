"""Publish both x64 and ARM64 versioned installers to the BiliTouch repository."""
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
    parser = argparse.ArgumentParser(description="Publish BiliTouch Windows x64 and ARM64 installers")
    parser.add_argument("--version", required=True)
    parser.add_argument("--setup", type=Path, required=True, help="Path to BiliTouch-<version>-setup.exe (x64)")
    parser.add_argument("--setup-arm64", type=Path, default=None, help="Path to BiliTouch-<version>-setup-arm64.exe (ARM64); defaults to sibling of --setup")
    parser.add_argument("--notes", type=Path, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-.][A-Za-z0-9.-]+)?", args.version):
        parser.error("Invalid version")
    expected_x64 = f"BiliTouch-{args.version}-setup.exe"
    expected_arm64 = f"BiliTouch-{args.version}-setup-arm64.exe"
    if args.setup.name != expected_x64 or not args.setup.is_file():
        parser.error(f"x64 installer must be named {expected_x64}")
    setup_arm64 = args.setup_arm64 or args.setup.with_name(expected_arm64)
    if setup_arm64.name != expected_arm64 or not setup_arm64.is_file():
        parser.error(f"ARM64 installer must be named {expected_arm64} and exist at {setup_arm64}")

    upload_plan = [
        (expected_x64, args.setup),
        (expected_arm64, setup_arm64),
    ]
    notes = args.notes.read_text(encoding="utf-8-sig")
    root = Path(__file__).resolve().parents[1]
    remote = subprocess.check_output(["git", "remote", "get-url", "--push", "origin"], cwd=root, text=True).strip()
    if remote != f"https://github.com/{REPOSITORY}.git":
        raise RuntimeError("Refusing to publish to an unexpected repository")
    tag = "v" + args.version
    subprocess.run(["git", "rev-parse", "--verify", "refs/tags/" + tag], cwd=root, check=True, capture_output=True)
    has_source = subprocess.run(["git", "cat-file", "-e", f"{tag}:BiliTouch/BiliTouch.csproj"],
                                cwd=root, capture_output=True).returncode == 0
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

    expected_digests = {}
    expected_sizes = {}
    upload_base = release["upload_url"].split("{")[0]
    for name, path in upload_plan:
        data = path.read_bytes()
        digest = "sha256:" + hashlib.sha256(data).hexdigest()
        expected_digests[name] = digest
        expected_sizes[name] = len(data)
        existing = next((asset for asset in release["assets"] if asset["name"] == name), None)
        if existing:
            if existing.get("digest") != digest:
                raise RuntimeError(f"Existing installer {name} differs; publish a new version")
        else:
            request(upload_base + "?name=" + urllib.parse.quote(name), data,
                    "POST", "application/octet-stream")

    assets = request(release["assets_url"])
    keep_ids = set()
    for name, _ in upload_plan:
        installer = next((asset for asset in assets if asset["name"] == name), None)
        if not installer or installer["size"] != expected_sizes[name] or installer.get("digest") != expected_digests[name]:
            raise RuntimeError(f"Uploaded installer verification failed: {name}")
        keep_ids.add(installer["id"])

    for asset in assets:
        if asset["id"] not in keep_ids:
            request(BASE + "/releases/assets/" + str(asset["id"]), method="DELETE")

    download_x64 = f"https://github.com/{REPOSITORY}/releases/download/{tag}/{expected_x64}"
    download_arm64 = f"https://github.com/{REPOSITORY}/releases/download/{tag}/{expected_arm64}"
    source_label = "本版源码" if has_source else "历史构建归档"
    body = (
        f"[下载安装包（x64 · Intel/AMD）]({download_x64}) · "
        f"[下载安装包（ARM64 · Surface 等 ARM 设备）]({download_arm64}) · "
        f"[{source_label}](https://github.com/{REPOSITORY}/tree/{tag})\n\n"
    )
    if not has_source:
        body += "此版本保留原始安装器；该标签仅包含归档记录，没有可确认匹配的应用源码快照。当前完整源码见 main 分支及最新正式版标签。\n\n"
    body += notes
    body += "\n\n![BiliTouch 首页](https://raw.githubusercontent.com/zhongsanZN/BiliTouch/main/docs/screenshot-home-current.png)"
    release = request(release["url"], {"body": body, "draft": False, "prerelease": False,
                                      "make_latest": "true" if has_source else "false"}, "PATCH")
    remaining = request(release["assets_url"])
    if sorted(asset["name"] for asset in remaining) != sorted([expected_x64, expected_arm64]):
        raise RuntimeError("Release must contain exactly the x64 and ARM64 uploaded installers")
    print(release["html_url"])


if __name__ == "__main__":
    main()
