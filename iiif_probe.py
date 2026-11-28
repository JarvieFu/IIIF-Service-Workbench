#!/usr/bin/env python3
"""IIIF 资源体检：读一个 Manifest 或 Image API 的 info.json，报告版本、规模、版权与图像服务能力。

用法：
    python tools/iiif_probe.py <url>            # 人类可读报告
    python tools/iiif_probe.py <url> --json     # 机器可读 JSON

只依赖标准库（urllib + json），同时适配 Presentation v2 / v3。
"""

from __future__ import annotations

import argparse
import json
import socket
import sys
import urllib.error
import urllib.request

UA = "IIIF-research-probe/1.0 (digital humanities research)"
# 部分机构（如 BnF）会拒绝非浏览器 UA，403 时用这个再试一次
BROWSER_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/126.0 Safari/537.36"
)


def fetch(url: str, timeout: int = 30, retries: int = 2) -> dict:
    last = None
    for attempt in range(retries + 1):
        ua = BROWSER_UA if attempt else UA
        try:
            req = urllib.request.Request(
                url, headers={"User-Agent": ua, "Accept": "application/json"}
            )
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.loads(r.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as e:
            last = e
            if e.code not in (403, 406):
                break
        except (urllib.error.URLError, socket.timeout, json.JSONDecodeError) as e:
            last = e
    raise RuntimeError(f"无法获取 {url}: {last}")

def kind(doc: dict) -> str:
    """判断这是 Manifest / Collection / ImageService 的哪一版。"""
    if "sequences" in doc:
        return "presentation-v2"
    if doc.get("type") == "Manifest" or str(doc.get("type", "")).endswith(":Manifest"):
        return "presentation-v3"
    if doc.get("type") in ("Collection", "sc:Collection"):
        return "collection-v3"
    if "@context" in doc and "image" in str(doc.get("@context", "")):
        return "image-v2"
    if doc.get("type") in ("ImageService2", "ImageService3") or "profile" in doc:
        return "image-v3"
    return "unknown"


def label_text(label) -> str:
    """语言映射 → 可读字符串（v2 里 label 可能直接是字符串）。"""
    if isinstance(label, str):
        return label
    if isinstance(label, dict):
        for lang in ("zh-Hans", "zh", "en", "none"):
            if lang in label:
                return " / ".join(str(x) for x in label[lang])
        for v in label.values():
            if isinstance(v, list) and v:
                return " / ".join(str(x) for x in v)
    return "(无题名)"


def services_of(obj: dict) -> list[dict]:
    svc = obj.get("service") or obj.get("services")
    if isinstance(svc, dict):
        svc = [svc]
    return [s for s in (svc or []) if isinstance(s, dict)]


def canvases(doc: dict) -> list[dict]:
    if "sequences" in doc:
        out = []
        for seq in doc.get("sequences") or []:
            out.extend(seq.get("canvases") or [])
        return out
    return [c for c in (doc.get("items") or []) if c.get("type") == "Canvas"]


def painting_services(canvas: dict) -> list[dict]:
    """取画布上 painting 标注 body 里的图像服务。"""
    if "images" in canvas:  # v2
        out = []
        for img in canvas.get("images") or []:
            res = img.get("resource") or {}
            out.extend(services_of(res))
        return out
    out = []
    for page in canvas.get("items") or []:
        for anno in page.get("items") or []:
            body = anno.get("body") or {}
            out.extend(services_of(body))
    return out


def probe_manifest(doc: dict, url: str) -> dict:
    cs = canvases(doc)
    svcs = []
    for c in cs:
        svcs.extend(painting_services(c))
    ids = []
    for s in svcs:
        sid = s.get("id") or s.get("@id")
        if sid and sid not in ids:
            ids.append(sid)

    rights = doc.get("rights") or doc.get("license") or None
    structures = doc.get("structures") or []
    thumb = doc.get("thumbnail")
    if isinstance(thumb, list):
        thumb = thumb[0] if thumb else None
    thumb_url = (thumb or {}).get("id") or (thumb or {}).get("@id") if isinstance(thumb, dict) else None

    first = cs[0] if cs else {}
    return {
        "url": url,
        "kind": kind(doc),
        "id": doc.get("id") or doc.get("@id"),
        "label": label_text(doc.get("label")),
        "canvases": len(cs),
        "structures": len(structures),
        "rights": rights,
        "provider": label_text((doc.get("provider") or [{}])[0].get("label"))
        if isinstance(doc.get("provider"), list)
        else None,
        "thumbnail": thumb_url,
        "image_services": ids[:5],
        "first_canvas": {
            "id": first.get("id") or first.get("@id"),
            "width": first.get("width"),
            "height": first.get("height"),
            "duration": first.get("duration"),
        },
        "viewing_direction": doc.get("viewingDirection"),
        "behavior": doc.get("behavior") or doc.get("behaviors"),
    }


def probe_image(doc: dict, url: str) -> dict:
    profile = doc.get("profile")
    if isinstance(profile, list):  # v2 常写成 ["http://iiif.io/api/image/2/level2.json"]
        profile = profile[0] if profile else None
    if isinstance(profile, dict):
        level = profile.get("level", profile)
    else:
        level = profile
    return {
        "url": url,
        "kind": kind(doc),
        "id": doc.get("id") or doc.get("@id"),
        "type": doc.get("type") or doc.get("@type"),
        "profile": level,
        "width": doc.get("width"),
        "height": doc.get("height"),
        "max_width": doc.get("maxWidth") or doc.get("maxWidth"),
        "tiles": doc.get("tiles"),
        "sizes": doc.get("sizes"),
        "extra_formats": doc.get("extraFormats"),
        "extra_qualities": doc.get("extraQualities"),
        "rights": doc.get("rights") or doc.get("license"),
    }


def fmt_report(d: dict) -> str:
    lines = []
    if d["kind"].startswith("image"):
        lines += [
            f"IIIF 图像服务  [{d['kind']}]",
            f"  id        {d['id']}",
            f"  profile   {d['profile']}",
            f"  原图尺寸  {d['width']} × {d['height']}",
            f"  maxWidth  {d['max_width']}",
            f"  版权      {d['rights']}",
        ]
        if d.get("extra_formats"):
            lines.append(f"  额外格式  {', '.join(d['extra_formats'])}")
        if d.get("tiles"):
            lines.append(f"  瓦片      {json.dumps(d['tiles'], ensure_ascii=False)[:120]}")
        return "\n".join(lines)

    lines += [
        f"IIIF Manifest  [{d['kind']}]",
        f"  题名      {d['label']}",
        f"  id        {d['id']}",
        f"  画布数    {d['canvases']}    结构(Range)数  {d['structures']}",
        f"  版权      {d['rights']}",
        f"  阅读方向  {d['viewing_direction']}   behavior  {d['behavior']}",
        f"  缩略图    {d['thumbnail']}",
        f"  首画布    {d['first_canvas']}",
        f"  图像服务  {len(d['image_services'])} 个",
    ]
    for s in d["image_services"]:
        lines.append(f"            - {s}")
    return "\n".join(lines)


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")  # Windows 控制台默认 GBK，避免中文乱码
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="IIIF Manifest / info.json 体检")
    ap.add_argument("url")
    ap.add_argument("--json", action="store_true", help="输出 JSON")
    args = ap.parse_args()

    doc = fetch(args.url)
    k = kind(doc)
    if k.startswith("image"):
        out = probe_image(doc, args.url)
    elif k.startswith("presentation") or k.startswith("collection"):
        out = probe_manifest(doc, args.url)
    else:
        out = {"url": args.url, "kind": k, "keys": list(doc)[:20]}

    print(json.dumps(out, ensure_ascii=False, indent=2) if args.json else fmt_report(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
