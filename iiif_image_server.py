#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
iiif_image_server.py — 单文件 IIIF 发布服务（Image API 2.0 Level 2 + Presentation API）

两件事：
  1. 图像 API：任意区域 / 缩放 / 旋转 / 灰阶，Level 2 全支持；
  2. 表现 API：按图源自动生成 Manifest（v2 + v3）与 Collection，
     每个图源 = 一个 Manifest，每张图 = 一个 Canvas。

两种图源模式：
  --image id=路径      显式注册单张图（兼容旧用法，id 由你指定）
  --root 目录          像 etu 一样按文件夹发布：
                       目录下的图片 → 一个 Manifest；每个子文件夹 → 各自一个 Manifest

用法：
  python iiif_image_server.py --image scroll="E:\\...\\The 13 Emperors Scroll.png"
  python iiif_image_server.py --root "E:\\DSH_IIIF\\Research" --port 8182

路由：
  /                             索引页（Manifest、Collection、图像）
  /collection.json              Presentation 2 Collection
  /manifest/{key}.json          Presentation 2 Manifest
  /manifest/{key}-v3.json       Presentation 3 Manifest
  /iiif/2/{id}/info.json        Image API 2.0 info.json
  /iiif/2/{id}/{region}/{size}/{rotation}/{quality}.{format}
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import sys
import threading
from collections import OrderedDict
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

from PIL import Image, ImageOps

IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp"}

CONTENT_TYPES = {
    "jpg": "image/jpeg",
    "png": "image/png",
    "webp": "image/webp",
    "tif": "image/tiff",
}


@dataclass
class ImageEntry:
    ident: str
    path: str
    label: str
    width: int
    height: int


@dataclass
class ManifestEntry:
    key: str
    title: str
    images: list = field(default_factory=list)


IMAGES: "dict[str, ImageEntry]" = {}
MANIFESTS: "dict[str, ManifestEntry]" = {}
USED_IMAGES: set = set()
USED_MANIFESTS: set = set()

CACHE: "OrderedDict[str, tuple]" = OrderedDict()
CACHE_LOCK = threading.Lock()
CACHE_MAX = 3


class BadRequest(Exception):
    """400：参数不合法。"""


# --------------------------------------------------------------------------
# 注册图源
# --------------------------------------------------------------------------
def image_size(path: str) -> tuple[int, int]:
    with Image.open(path) as im:
        return im.size


def make_slug(text: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    if not slug:
        slug = "item-" + hashlib.sha1(text.encode("utf-8")).hexdigest()[:8]
    return slug[:60].strip("-") or "item"


def unique(base: str, used: set) -> str:
    key = base
    n = 2
    while key in used:
        key = "%s-%d" % (base, n)
        n += 1
    used.add(key)
    return key


def natural_key(name: str):
    return [int(part) if part.isdigit() else part.lower()
            for part in re.split(r"(\d+)", name)]


def list_images(folder: str) -> list[str]:
    try:
        names = os.listdir(folder)
    except OSError:
        return []
    files = [n for n in names
             if os.path.splitext(n)[1].lower() in IMAGE_EXTS
             and os.path.isfile(os.path.join(folder, n))]
    files.sort(key=natural_key)
    return [os.path.join(folder, n) for n in files]


def add_manifest(key: str, title: str, files: list[str],
                 ids: list[str] | None = None) -> None:
    if not files:
        return
    entry = ManifestEntry(key=key, title=title)
    for index, path in enumerate(files):
        stem = os.path.splitext(os.path.basename(path))[0]
        wanted = ids[index] if ids and index < len(ids) else make_slug(stem)
        ident = unique(wanted, USED_IMAGES)
        width, height = image_size(path)
        image = ImageEntry(ident, path, stem, width, height)
        IMAGES[ident] = image
        entry.images.append(image)
    MANIFESTS[key] = entry


def build_registry(args) -> None:
    for spec in args.image or []:
        if "=" not in spec:
            raise SystemExit(f"--image 需要 id=路径 形式：{spec}")
        ident, path = spec.split("=", 1)
        ident = make_slug(ident.strip())
        path = os.path.abspath(path.strip('"'))
        if not os.path.isfile(path):
            raise SystemExit(f"找不到图像：{path}")
        key = unique(ident, USED_MANIFESTS)
        stem = os.path.splitext(os.path.basename(path))[0]
        add_manifest(key, args.title or stem, [path], ids=[key])

    if args.root:
        root = os.path.abspath(args.root)
        if not os.path.isdir(root):
            raise SystemExit(f"找不到目录：{root}")
        subdirs = [d for d in sorted(os.listdir(root), key=natural_key)
                   if os.path.isdir(os.path.join(root, d))
                   and list_images(os.path.join(root, d))]
        for sub in subdirs:
            key = unique(make_slug(sub), USED_MANIFESTS)
            add_manifest(key, sub, list_images(os.path.join(root, sub)))
        root_files = list_images(root)
        if root_files:
            base_title = os.path.basename(root.rstrip("\\/")) or "root"
            key = unique(make_slug(base_title), USED_MANIFESTS)
            add_manifest(key, args.title or base_title, root_files)

    if not MANIFESTS:
        raise SystemExit("没有可发布的图像：用 --image id=路径 或 --root 目录")


# --------------------------------------------------------------------------
# Manifest 生成
# --------------------------------------------------------------------------
def _service_block(base: str, image: ImageEntry) -> dict:
    return {
        "@context": "http://iiif.io/api/image/2/context.json",
        "@id": f"{base}/iiif/2/{image.ident}",
        "profile": "http://iiif.io/api/image/2/level2.json",
    }


def v2_manifest(base: str, entry: ManifestEntry) -> dict:
    canvases = []
    for image in entry.images:
        canvas_id = f"{base}/canvas/{image.ident}"
        svc = f"{base}/iiif/2/{image.ident}"
        canvases.append({
            "@id": canvas_id,
            "@type": "sc:Canvas",
            "label": image.label,
            "width": image.width,
            "height": image.height,
            "images": [{
                "@id": canvas_id + "/image",
                "@type": "oa:Annotation",
                "motivation": "sc:painting",
                "resource": {
                    "@id": f"{svc}/full/{image.width},{image.height}/0/default.jpg",
                    "@type": "dctypes:Image",
                    "format": "image/jpeg",
                    "width": image.width,
                    "height": image.height,
                    "service": _service_block(base, image),
                },
                "on": canvas_id,
            }],
            "thumbnail": {
                "@id": f"{svc}/full/!200,200/0/default.jpg",
                "@type": "dctypes:Image",
                "service": _service_block(base, image),
            },
        })
    return {
        "@context": "http://iiif.io/api/presentation/2/context.json",
        "@id": f"{base}/manifest/{entry.key}.json",
        "@type": "sc:Manifest",
        "label": entry.title,
        "sequences": [{
            "@id": f"{base}/manifest/{entry.key}/sequence",
            "@type": "sc:Sequence",
            "label": entry.title,
            "canvases": canvases,
        }],
    }


def v3_manifest(base: str, entry: ManifestEntry) -> dict:
    items = []
    for image in entry.images:
        canvas_id = f"{base}/canvas/{image.ident}"
        page_id = canvas_id + "/page/1"
        ann_id = page_id + "/annotation/1"
        svc = f"{base}/iiif/2/{image.ident}"
        items.append({
            "id": canvas_id,
            "type": "Canvas",
            "label": {"none": [image.label]},
            "width": image.width,
            "height": image.height,
            "items": [{
                "id": page_id,
                "type": "AnnotationPage",
                "items": [{
                    "id": ann_id,
                    "type": "Annotation",
                    "motivation": "painting",
                    "body": {
                        "id": f"{svc}/full/max/0/default.jpg",
                        "type": "Image",
                        "format": "image/jpeg",
                        "width": image.width,
                        "height": image.height,
                        "service": [{
                            "id": svc,
                            "type": "ImageService2",
                            "profile": "http://iiif.io/api/image/2/level2.json",
                        }],
                    },
                    "target": canvas_id,
                }],
            }],
        })
    return {
        "@context": "http://iiif.io/api/presentation/3/context.json",
        "id": f"{base}/manifest/{entry.key}-v3.json",
        "type": "Manifest",
        "label": {"none": [entry.title]},
        "items": items,
    }


def v2_collection(base: str) -> dict:
    return {
        "@context": "http://iiif.io/api/presentation/2/context.json",
        "@id": f"{base}/collection.json",
        "@type": "sc:Collection",
        "label": "本地 IIIF 发布（Image API Level 2）",
        "manifests": [
            {
                "@id": f"{base}/manifest/{entry.key}.json",
                "@type": "sc:Manifest",
                "label": entry.title,
            }
            for entry in MANIFESTS.values()
        ],
    }


# --------------------------------------------------------------------------
# 图像处理（Image API）
# --------------------------------------------------------------------------
def norm_format(value: str) -> str:
    value = value.lower()
    if value == "jpeg":
        value = "jpg"
    if value == "tiff":
        value = "tif"
    if value not in CONTENT_TYPES:
        raise BadRequest(f"unsupported format: {value}")
    return value


def parse_region(value: str, w: int, h: int) -> tuple[int, int, int, int]:
    if value == "full":
        return 0, 0, w, h
    if value == "square":
        side = min(w, h)
        return (w - side) // 2, (h - side) // 2, side, side
    if value.startswith("pct:"):
        try:
            x, y, rw, rh = (float(v) for v in value[4:].split(","))
        except ValueError as exc:
            raise BadRequest(f"bad pct region: {value}") from exc
        return (int(round(w * x / 100)), int(round(h * y / 100)),
                max(1, int(round(w * rw / 100))), max(1, int(round(h * rh / 100))))
    try:
        x, y, rw, rh = (int(v) for v in value.split(","))
    except ValueError as exc:
        raise BadRequest(f"bad region: {value}") from exc
    if rw <= 0 or rh <= 0:
        raise BadRequest("region width/height must be positive")
    if x >= w or y >= h:
        raise BadRequest("region outside image")
    return x, y, min(rw, w - x), min(rh, h - y)


def parse_size(value: str, rw: int, rh: int) -> tuple[int, int]:
    if value in ("full", "max"):
        return rw, rh
    if value.startswith("pct:"):
        try:
            n = float(value[4:])
        except ValueError as exc:
            raise BadRequest(f"bad pct size: {value}") from exc
        return max(1, int(round(rw * n / 100))), max(1, int(round(rh * n / 100)))
    if value.startswith("!"):
        try:
            cw, ch = (int(v) for v in value[1:].split(","))
        except ValueError as exc:
            raise BadRequest(f"bad size: {value}") from exc
        scale = min(cw / rw, ch / rh)
        return max(1, int(round(rw * scale))), max(1, int(round(rh * scale)))
    if value.endswith(","):
        try:
            cw = int(value[:-1])
        except ValueError as exc:
            raise BadRequest(f"bad size: {value}") from exc
        return cw, max(1, int(round(rh * cw / rw)))
    if value.startswith(","):
        try:
            ch = int(value[1:])
        except ValueError as exc:
            raise BadRequest(f"bad size: {value}") from exc
        return max(1, int(round(rw * ch / rh))), ch
    try:
        cw, ch = (int(v) for v in value.split(","))
    except ValueError as exc:
        raise BadRequest(f"bad size: {value}") from exc
    if cw <= 0 or ch <= 0:
        raise BadRequest("size must be positive")
    return cw, ch


def parse_rotation(value: str) -> tuple[int, bool]:
    mirror = value.startswith("!")
    raw = value[1:] if mirror else value
    try:
        angle = int(round(float(raw))) % 360
    except ValueError as exc:
        raise BadRequest(f"bad rotation: {value}") from exc
    if angle not in (0, 90, 180, 270):
        raise BadRequest("only 90-degree rotations are supported")
    return angle, mirror


def load_image(ident: str):
    with CACHE_LOCK:
        if ident in CACHE:
            CACHE.move_to_end(ident)
            return CACHE[ident]
    entry = IMAGES.get(ident)
    if not entry or not os.path.isfile(entry.path):
        raise KeyError(ident)

    img = Image.open(entry.path)
    img = ImageOps.exif_transpose(img)
    img.load()
    if img.mode in ("RGBA", "LA") or (img.mode == "P" and "transparency" in img.info):
        rgba = img.convert("RGBA")
        bg = Image.new("RGB", rgba.size, (255, 255, 255))
        bg.paste(rgba, mask=rgba.split()[-1])
        img = bg
    elif img.mode != "RGB":
        img = img.convert("RGB")

    with CACHE_LOCK:
        CACHE[ident] = (img, img.width, img.height)
        CACHE.move_to_end(ident)
        while len(CACHE) > CACHE_MAX:
            CACHE.popitem(last=False)
    return img, img.width, img.height


def render(ident: str, region: str, size: str, rotation: str,
           quality: str, fmt: str) -> bytes:
    img, iw, ih = load_image(ident)
    x, y, rw, rh = parse_region(region, iw, ih)
    tw, th = parse_size(size, rw, rh)
    angle, mirror = parse_rotation(rotation)

    crop = img.crop((x, y, x + rw, y + rh))
    if (tw, th) != (rw, rh):
        crop = crop.resize((tw, th), Image.LANCZOS)
    if mirror:
        crop = ImageOps.mirror(crop)
    if angle:
        crop = crop.rotate(-angle, expand=True)

    quality = quality.lower()
    if quality == "native":
        quality = "default"
    if quality == "gray":
        crop = crop.convert("L")
    elif quality == "bitonal":
        crop = crop.convert("L").point(lambda p: 255 if p > 128 else 0)
    elif quality not in ("default", "color"):
        raise BadRequest(f"unsupported quality: {quality}")

    buf = io.BytesIO()
    if fmt == "jpg":
        if crop.mode not in ("RGB", "L"):
            crop = crop.convert("RGB")
        crop.save(buf, "JPEG", quality=90, optimize=True)
    elif fmt == "png":
        crop.save(buf, "PNG", optimize=True)
    elif fmt == "webp":
        crop.save(buf, "WEBP", quality=90)
    else:
        crop.save(buf, "TIFF")
    return buf.getvalue()


# --------------------------------------------------------------------------
# HTTP
# --------------------------------------------------------------------------
class Handler(BaseHTTPRequestHandler):
    server_version = "IIIFImageServer/1.1"
    protocol_version = "HTTP/1.1"

    def _send(self, code: int, body: bytes, ctype: str, head: bool,
              cache: str = "public, max-age=86400"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Cache-Control", cache)
        self.end_headers()
        if not head:
            self.wfile.write(body)

    def _json(self, payload: dict, head: bool, cache: str = "public, max-age=300"):
        body = json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8")
        self._send(200, body, "application/json; charset=utf-8", head, cache)

    def _error(self, code: int, message: str, head: bool):
        body = json.dumps({"error": message}, ensure_ascii=False).encode("utf-8")
        self._send(code, body, "application/json; charset=utf-8", head, cache="no-store")

    def _base(self) -> str:
        host = self.headers.get("Host")
        if not host:
            addr = self.server.server_address
            host = f"{addr[0]}:{addr[1]}"
        return f"http://{host}"

    def do_GET(self):
        self._handle(head=False)

    def do_HEAD(self):
        self._handle(head=True)

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _handle(self, head: bool):
        path = urlsplit(self.path).path
        try:
            if path in ("/", ""):
                return self._index(head)
            if path == "/collection.json":
                return self._json(v2_collection(self._base()), head)

            if path.startswith("/manifest/") and path.endswith(".json"):
                name = unquote(path[len("/manifest/"):-len(".json")])
                if name.endswith("-v3"):
                    entry = MANIFESTS.get(name[:-3])
                    if not entry:
                        return self._error(404, "unknown manifest", head)
                    return self._json(v3_manifest(self._base(), entry), head)
                entry = MANIFESTS.get(name)
                if not entry:
                    return self._error(404, "unknown manifest", head)
                return self._json(v2_manifest(self._base(), entry), head)

            if not path.startswith("/iiif/2/"):
                return self._error(404, "not found", head)

            rest = unquote(path[len("/iiif/2/"):]).strip("/")
            if not rest:
                return self._error(404, "missing identifier", head)

            if rest.endswith("/info.json"):
                return self._info(rest[:-len("/info.json")], head)

            parts = rest.split("/")
            if len(parts) < 5:
                return self._error(404, "not an image request", head)
            region, size, rotation, quality_ext = parts[-4:]
            ident = "/".join(parts[:-4])
            if "." not in quality_ext:
                raise BadRequest("missing format")
            quality, fmt = quality_ext.rsplit(".", 1)
            fmt = norm_format(fmt)

            body = render(ident, region, size, rotation, quality, fmt)
            self._send(200, body, CONTENT_TYPES[fmt], head)
        except KeyError:
            self._error(404, "unknown image identifier", head)
        except BadRequest as exc:
            self._error(400, str(exc), head)
        except Exception as exc:  # noqa: BLE001
            self._error(500, f"internal error: {exc}", head)

    def _info(self, ident: str, head: bool):
        entry = IMAGES.get(ident)
        if not entry:
            return self._error(404, "unknown image identifier", head)
        try:
            _, w, h = load_image(ident)
        except KeyError:
            return self._error(404, "unknown image identifier", head)

        sizes = [{"width": w, "height": h}]
        for target in (2000, 1000, 400):
            if target < w:
                sizes.append({"width": target,
                              "height": max(1, round(h * target / w))})

        info = {
            "@context": "http://iiif.io/api/image/2/context.json",
            "@id": f"{self._base()}/iiif/2/{ident}",
            "protocol": "http://iiif.io/api/image",
            "width": w,
            "height": h,
            "sizes": sizes,
            "tiles": [{"width": 512, "height": 512,
                       "scaleFactors": [1, 2, 4, 8, 16]}],
            "profile": [
                "http://iiif.io/api/image/2/level2.json",
                {
                    "formats": ["jpg", "png", "webp", "tif"],
                    "qualities": ["default", "color", "gray", "bitonal"],
                    "supports": [
                        "regionByPx", "regionByPct", "regionSquare",
                        "sizeByWh", "sizeByW", "sizeByH", "sizeByPct",
                        "sizeByForcedWh", "sizeByWhListed", "sizeAboveFull",
                        "rotationBy90s", "mirroring", "gray", "bitonal",
                    ],
                },
            ],
        }
        self._json(info, head)

    def _index(self, head: bool):
        base = self._base()
        rows = []
        for entry in MANIFESTS.values():
            rows.append(
                f"<tr><td><b>{entry.title}</b><br><code>{entry.key}</code></td>"
                f"<td>{len(entry.images)} 页</td>"
                f"<td><a href=\"{base}/manifest/{entry.key}.json\">Manifest v2</a><br>"
                f"<a href=\"{base}/manifest/{entry.key}-v3.json\">Manifest v3</a></td></tr>"
            )
        images = "".join(
            f"<li><code>{img.ident}</code> · {img.label} · {img.width}×{img.height} · "
            f"<a href=\"{base}/iiif/2/{img.ident}/info.json\">info.json</a> · "
            f"<a href=\"{base}/iiif/2/{img.ident}/full/800,/0/default.jpg\">预览</a></li>"
            for img in IMAGES.values()
        )
        html = (
            "<!DOCTYPE html><meta charset='utf-8'>"
            "<title>本地 IIIF 发布服务</title>"
            "<style>body{font-family:Segoe UI,Microsoft YaHei,sans-serif;margin:28px;"
            "line-height:1.6}table{border-collapse:collapse;margin-bottom:18px}"
            "td,th{padding:7px 14px;border-bottom:1px solid #ddd;text-align:left;"
            "vertical-align:top}code{background:#f2f2f2;padding:1px 5px}</style>"
            "<h2>本地 IIIF 发布服务</h2>"
            "<p>Image API 2.0 <b>Level 2</b> + Presentation API 2 / 3。"
            f"<a href=\"{base}/collection.json\">Collection</a></p>"
            "<table><tr><th>图源（Manifest）</th><th>规模</th><th>链接</th></tr>"
            + "".join(rows) + "</table>"
            "<h3>图像</h3><ul>" + images + "</ul>"
        ).encode("utf-8")
        self._send(200, html, "text/html; charset=utf-8", head, cache="no-store")

    def log_message(self, fmt, *args):  # noqa: A003
        sys.stderr.write("%s %s\n" % (self.log_date_time_string(), fmt % args))
        sys.stderr.flush()


class Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def main(argv=None):
    ap = argparse.ArgumentParser(description="IIIF 发布服务（Image API 2.0 Level 2 + Presentation）")
    ap.add_argument("--port", type=int, default=8182)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--image", action="append", metavar="ID=PATH",
                    help="显式注册单张图，可重复")
    ap.add_argument("--root", help="按文件夹发布：子文件夹各成一个 Manifest，根目录图片成一个")
    ap.add_argument("--title", help="根目录 Manifest 的标题（默认取文件夹名）")
    args = ap.parse_args(argv)

    build_registry(args)
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:  # noqa: BLE001
        pass

    server = Server((args.host, args.port), Handler)
    print(f"IIIF 发布服务 → http://{args.host}:{args.port}/", flush=True)
    for entry in MANIFESTS.values():
        print(f"  Manifest {entry.title} ({len(entry.images)} 页): "
              f"http://{args.host}:{args.port}/manifest/{entry.key}.json", flush=True)
    for image in IMAGES.values():
        print(f"  Image {image.ident}: {image.width}x{image.height} "
              f"http://{args.host}:{args.port}/iiif/2/{image.ident}/info.json", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
