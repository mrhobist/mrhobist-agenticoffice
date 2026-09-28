"""Turn the raw ChatGPT sprite sheets in assets/raw into a clean atlas for the UI.

Usage:
    python scripts/build-sprites.py            # writes ui/public/sprites/*
    python scripts/build-sprites.py --inspect  # only dumps numbered tileset boxes

Why a script and not hand-cropping: the sheets are not on a regular grid, so
the frames are found by connected components on the alpha channel, then
aligned bottom-center into fixed cells. Re-running after a new asset drop
regenerates everything; nothing is hand-edited in ui/public/sprites.

Only numpy + Pillow are needed (no scipy).
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
RAW = ROOT / "assets" / "raw"
OUT = ROOT / "ui" / "public" / "sprites"
SCRATCH = ROOT / "assets" / "_inspect"

# World = 1448 x 1086 logical px (the reference scene). Sprites are stored at
# WORLD_SCALE x world size so they stay crisp on hi-dpi screens.
WORLD_SCALE = 2

# Target sizes in world px. Characters use CHAR_V2_HEIGHT_WORLD (catalog section).
CAT_HEIGHT_WORLD = 46      # a standing cat, side view
BUBBLE_WIDTH_WORLD = 46
DOOR_HEIGHT_WORLD = 130

ALPHA_MIN = 128


# --------------------------------------------------------------------------- #
# Connected components (pure numpy + BFS on a downsampled mask)
# --------------------------------------------------------------------------- #
def alpha_mask(img: Image.Image) -> np.ndarray:
    a = np.asarray(img.convert("RGBA"))[:, :, 3]
    return a >= ALPHA_MIN


def components(mask: np.ndarray, step: int, merge_px: int) -> list[tuple[int, int, int, int]]:
    """Return boxes (x0, y0, x1, y1) exclusive, in full-res coordinates.

    The mask is reduced by `step` (any-pooling), dilated by merge_px/step so that
    parts of one object that sit a few px apart join, then flood-filled.
    """
    h, w = mask.shape
    hs, ws = (h + step - 1) // step, (w + step - 1) // step
    small = np.zeros((hs, ws), dtype=bool)
    for dy in range(step):
        for dx in range(step):
            sub = mask[dy::step, dx::step]
            small[: sub.shape[0], : sub.shape[1]] |= sub
    r = max(0, merge_px // step)
    if r:
        pad = np.pad(small, r)
        dil = np.zeros_like(small)
        for dy in range(-r, r + 1):
            for dx in range(-r, r + 1):
                dil |= pad[r + dy : r + dy + hs, r + dx : r + dx + ws]
        small = dil
    seen = np.zeros_like(small)
    boxes = []
    for y in range(hs):
        for x in range(ws):
            if not small[y, x] or seen[y, x]:
                continue
            q = deque([(y, x)])
            seen[y, x] = True
            x0 = x1 = x
            y0 = y1 = y
            while q:
                cy, cx = q.popleft()
                x0, x1 = min(x0, cx), max(x1, cx)
                y0, y1 = min(y0, cy), max(y1, cy)
                for ny, nx in ((cy - 1, cx), (cy + 1, cx), (cy, cx - 1), (cy, cx + 1)):
                    if 0 <= ny < hs and 0 <= nx < ws and small[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        q.append((ny, nx))
            boxes.append((x0 * step, y0 * step, min(w, (x1 + 1) * step), min(h, (y1 + 1) * step)))
    return [tighten(mask, b) for b in boxes]


def tighten(mask: np.ndarray, box: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    x0, y0, x1, y1 = box
    sub = mask[y0:y1, x0:x1]
    ys = np.where(sub.any(axis=1))[0]
    xs = np.where(sub.any(axis=0))[0]
    if len(ys) == 0 or len(xs) == 0:
        return box
    return (x0 + int(xs[0]), y0 + int(ys[0]), x0 + int(xs[-1]) + 1, y0 + int(ys[-1]) + 1)


def area(b: tuple[int, int, int, int]) -> int:
    return (b[2] - b[0]) * (b[3] - b[1])


# --------------------------------------------------------------------------- #
# Grid sheets (characters, cat)
# --------------------------------------------------------------------------- #
def bands(profile: np.ndarray, expected: int, min_gap: int = 6) -> list[tuple[int, int]]:
    """Split a 1-D any() profile into `expected` occupied bands; tiny gaps are ignored."""
    idx = np.where(profile)[0]
    out: list[list[int]] = [[int(idx[0]), int(idx[0])]]
    for i in idx[1:]:
        if i - out[-1][1] > min_gap:
            out.append([int(i), int(i)])
        else:
            out[-1][1] = int(i)
    # Merge the narrowest bands into their nearest neighbour until the count fits.
    while len(out) > expected:
        j = min(range(len(out)), key=lambda k: out[k][1] - out[k][0])
        if j == 0:
            out[1][0] = out[0][0]; out.pop(0)
        elif j == len(out) - 1:
            out[-2][1] = out[-1][1]; out.pop()
        else:
            left_gap = out[j][0] - out[j - 1][1]
            right_gap = out[j + 1][0] - out[j][1]
            if left_gap <= right_gap:
                out[j - 1][1] = out[j][1]; out.pop(j)
            else:
                out[j + 1][0] = out[j][0]; out.pop(j)
    if len(out) != expected:
        raise SystemExit(f"expected {expected} bands, found {len(out)}: {out}")
    return [(lo, hi + 1) for lo, hi in out]


def grid_frames(img: Image.Image, cols: int, rows: int) -> list[list[Image.Image]]:
    """Regular sheet: rows from the horizontal projection, columns from the vertical one."""
    mask = alpha_mask(img)
    row_b = bands(mask.any(axis=1), rows)
    col_b = bands(mask.any(axis=0), cols)
    grid = []
    for (y0, y1) in row_b:
        row = []
        for (x0, x1) in col_b:
            row.append(img.crop(tighten(mask, (x0, y0, x1, y1))))
        grid.append(row)
    return grid


def recolor(img: Image.Image, hue_band: tuple[int, int], hue_to: int, min_sat: int = 90) -> Image.Image:
    """Shift the hue of saturated pixels inside hue_band (0-255 PIL scale) to hue_to.

    Used to derive the two missing role outfits from existing sheets; skin and
    hair stay untouched because they are either desaturated or outside the band.
    """
    rgba = np.asarray(img.convert("RGBA")).copy()
    hsv = np.asarray(img.convert("RGB").convert("HSV")).copy()
    h, s = hsv[:, :, 0].astype(int), hsv[:, :, 1].astype(int)
    lo, hi = hue_band
    in_band = (h >= lo) & (h <= hi) if lo <= hi else (h >= lo) | (h <= hi)
    m = in_band & (s >= min_sat) & (rgba[:, :, 3] > 0)
    hsv[:, :, 0][m] = hue_to
    rgb = np.asarray(Image.fromarray(hsv, "HSV").convert("RGB"))
    rgba[:, :, :3][m] = rgb[m]
    return Image.fromarray(rgba, "RGBA")


def recolor_ops(img: Image.Image, ops: list[dict]) -> Image.Image:
    """Several HSV edits in one pass; each op selects pixels by hue band + saturation + value range.

    Unlike `recolor`, an op can also darken/brighten (black or blonde hair cannot be reached by a hue
    shift alone) and select by value, which separates dark brown hair from lighter skin of similar hue.
    Op keys (PIL scale 0-255): hue=(lo, hi) (lo > hi wraps), sat=(min, max), val=(min, max),
    to_hue, sat_mul, val_mul, val_add. Selection always uses the ORIGINAL pixel, so ops don't chain.
    """
    rgba = np.asarray(img.convert("RGBA")).copy()
    hsv = np.asarray(img.convert("RGB").convert("HSV")).astype(float)
    h, s, v = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    out = hsv.copy()
    for op in ops:
        lo, hi = op.get("hue", (0, 255))
        m = ((h >= lo) & (h <= hi)) if lo <= hi else ((h >= lo) | (h <= hi))
        smin, smax = op.get("sat", (0, 255))
        vmin, vmax = op.get("val", (0, 255))
        m &= (s >= smin) & (s <= smax) & (v >= vmin) & (v <= vmax) & (rgba[:, :, 3] > 0)
        if "to_hue" in op:
            out[:, :, 0][m] = op["to_hue"]
        out[:, :, 1][m] = np.clip(s[m] * op.get("sat_mul", 1.0), 0, 255)
        out[:, :, 2][m] = np.clip(v[m] * op.get("val_mul", 1.0) + op.get("val_add", 0), 0, 255)
    rgb = np.asarray(Image.fromarray(out.astype(np.uint8), "HSV").convert("RGB"))
    rgba[:, :, :3] = rgb
    return Image.fromarray(rgba, "RGBA")


def pack_grid(frames: list[list[Image.Image]], target_h_world: int, name: str) -> dict:
    """Align every frame bottom-center into equal cells and scale to target height."""
    rows, cols = len(frames), len(frames[0])
    max_w = max(f.width for row in frames for f in row)
    max_h = max(f.height for row in frames for f in row)
    scale = (target_h_world * WORLD_SCALE) / max_h
    cw = int(round(max_w * scale)) + 4
    ch = int(round(max_h * scale)) + 2
    sheet = Image.new("RGBA", (cw * cols, ch * rows), (0, 0, 0, 0))
    for r, row in enumerate(frames):
        for c, f in enumerate(row):
            fs = f.resize((max(1, int(round(f.width * scale))), max(1, int(round(f.height * scale)))), Image.LANCZOS)
            x = c * cw + (cw - fs.width) // 2
            y = r * ch + (ch - fs.height) - 1
            sheet.alpha_composite(fs, (x, y))
    sheet.save(OUT / f"{name}.png", optimize=True)
    return {"image": f"{name}.png", "frameW": cw, "frameH": ch, "cols": cols, "rows": rows}


def single(img: Image.Image, target_world: int, by: str, name: str) -> dict:
    mask = alpha_mask(img)
    b = tighten(mask, (0, 0, img.width, img.height))
    crop = img.crop(b)
    ref = crop.height if by == "h" else crop.width
    scale = (target_world * WORLD_SCALE) / ref
    out = crop.resize((max(1, int(round(crop.width * scale))), max(1, int(round(crop.height * scale)))), Image.LANCZOS)
    out.save(OUT / f"{name}.png", optimize=True)
    return {"image": f"{name}.png", "w": out.width, "h": out.height}


def strip(imgs: list[Image.Image], target_world: int, by: str, name: str) -> dict:
    crops = []
    for img in imgs:
        b = tighten(alpha_mask(img), (0, 0, img.width, img.height))
        crops.append(img.crop(b))
    ref = max(c.height for c in crops) if by == "h" else max(c.width for c in crops)
    scale = (target_world * WORLD_SCALE) / ref
    scaled = [c.resize((max(1, int(round(c.width * scale))), max(1, int(round(c.height * scale)))), Image.LANCZOS) for c in crops]
    cw = max(s.width for s in scaled) + 2
    ch = max(s.height for s in scaled) + 2
    sheet = Image.new("RGBA", (cw * len(scaled), ch), (0, 0, 0, 0))
    for i, s in enumerate(scaled):
        sheet.alpha_composite(s, (i * cw + (cw - s.width) // 2, ch - s.height - 1))
    sheet.save(OUT / f"{name}.png", optimize=True)
    return {"image": f"{name}.png", "frameW": cw, "frameH": ch, "cols": len(scaled), "rows": 1}


# --------------------------------------------------------------------------- #
# Object tileset -> packed atlas
# --------------------------------------------------------------------------- #
# Named by a point INSIDE each object (source px), not by index: box order
# shifts when the threshold changes, a point does not. "split" cuts a box that
# merged several touching tiles into equal columns.
# Objects are stored at their SOURCE resolution; the layout gives each prop a
# target width in world px so per-item scale mismatches in the sheet don't matter.
OBJECT_POINTS: list[tuple[str, int, int, int]] = [
    # name, cx, cy, split
    ("desk-wide", 116, 142, 1),
    ("desk-chair", 288, 131, 1),
    ("desk-monitor", 478, 132, 1),
    ("desk-plant-mug", 695, 133, 1),
    ("desk-double", 1040, 127, 1),
    ("chair-a", 62, 312, 1), ("chair-b", 163, 308, 1), ("chair-c", 258, 309, 1),
    ("chair-d", 357, 313, 1), ("chair-side", 460, 316, 1),
    ("monitor-a", 573, 305, 1), ("monitor-b", 694, 309, 1), ("monitor-off", 808, 308, 1),
    ("laptop", 929, 310, 1),
    ("divider-gray", 1046, 322, 1), ("divider-blue-a", 1126, 322, 1), ("divider-blue-b", 1199, 322, 1),
    ("binder", 66, 428, 1), ("books-red", 149, 428, 1), ("books-green", 233, 426, 1),
    ("book-purple", 327, 428, 1), ("mug-white", 417, 423, 1), ("mug-red", 480, 432, 1),
    ("plant-a", 548, 422, 1), ("plant-b", 615, 434, 1), ("plant-c", 679, 432, 1),
    ("plant-d", 750, 426, 1), ("plant-hanging", 820, 436, 1), ("plant-e", 899, 423, 1),
    ("coffee-bar", 185, 546, 1), ("coffee-machine", 478, 543, 1),
    ("counter-a", 684, 558, 1), ("counter-b", 815, 555, 1),
    ("stool-a", 914, 553, 1), ("stool-b", 1006, 554, 1), ("stool-c", 1097, 554, 1),
    ("trash", 1182, 539, 1),
    ("meeting-table", 159, 743, 1),
    ("glass-narrow", 385, 728, 1), ("glass-wide", 524, 728, 1), ("glass-mid", 678, 728, 1),
    ("door-a", 808, 728, 1), ("sprint-board", 1063, 727, 1),
    ("sofa-cat", 186, 971, 1), ("lamp", 389, 920, 1), ("bookshelf", 527, 932, 1),
    ("water-cooler", 685, 926, 1), ("printer", 830, 946, 1),
    ("plant-large-a", 967, 940, 1), ("plant-large-b", 1079, 940, 1), ("plant-medium", 1191, 966, 1),
    ("door-b", 62, 1164, 1), ("bench", 242, 1166, 1),
    ("window", 568, 1142, 4), ("window-e", 793, 1142, 1),
    ("painting", 875, 1095, 1), ("wall-shelf", 995, 1088, 1), ("floor-mat", 1155, 1095, 1),
    ("floor-a", 883, 1188, 1), ("floor-b", 985, 1188, 1), ("floor-c", 1138, 1188, 2),
]


def tileset_boxes(img: Image.Image) -> list[tuple[int, int, int, int]]:
    mask = alpha_mask(img)
    boxes = [b for b in components(mask, step=2, merge_px=4) if area(b) > 900]
    # Row-major order by center; rows are clustered with a tolerance.
    boxes.sort(key=lambda b: (b[1] + b[3]) / 2)
    rows: list[list[tuple[int, int, int, int]]] = []
    for b in boxes:
        cy = (b[1] + b[3]) / 2
        if rows and abs(cy - np.mean([(r[1] + r[3]) / 2 for r in rows[-1]])) < 70:
            rows[-1].append(b)
        else:
            rows.append([b])
    ordered = []
    for r in rows:
        ordered.extend(sorted(r, key=lambda b: (b[0] + b[2]) / 2))
    return ordered


def inspect_tileset(img: Image.Image) -> None:
    SCRATCH.mkdir(parents=True, exist_ok=True)
    boxes = tileset_boxes(img)
    dbg = Image.new("RGBA", img.size, (20, 20, 28, 255))
    dbg.alpha_composite(img)
    d = ImageDraw.Draw(dbg)
    for i, (x0, y0, x1, y1) in enumerate(boxes):
        d.rectangle((x0, y0, x1 - 1, y1 - 1), outline=(255, 64, 64, 255), width=2)
        d.rectangle((x0, y0, x0 + 34, y0 + 18), fill=(255, 64, 64, 255))
        d.text((x0 + 3, y0 + 3), str(i), fill=(255, 255, 255, 255))
    dbg.save(SCRATCH / "tileset-boxes.png")
    for i, b in enumerate(boxes):
        print(f"{i:3d}  x={b[0]:4d} y={b[1]:4d} w={b[2]-b[0]:4d} h={b[3]-b[1]:4d}")
    print(f"{len(boxes)} boxes -> {SCRATCH / 'tileset-boxes.png'}")


def pack_objects(img: Image.Image) -> dict:
    boxes = tileset_boxes(img)
    items: list[tuple[str, Image.Image]] = []
    for name, cx, cy, split in OBJECT_POINTS:
        hit = [b for b in boxes if b[0] <= cx < b[2] and b[1] <= cy < b[3]]
        if not hit:
            raise SystemExit(f"no tileset box contains point for {name!r} ({cx},{cy}); re-run --inspect")
        b = hit[0]
        if split == 1:
            items.append((name, img.crop(b)))
        else:
            w = (b[2] - b[0]) / split
            for i in range(split):
                sub = (int(b[0] + i * w), b[1], int(b[0] + (i + 1) * w), b[3])
                items.append((f"{name}-{i + 1}", img.crop(tighten(alpha_mask(img), sub))))
    # Shelf packing, tallest first.
    items.sort(key=lambda t: t[1].height, reverse=True)
    atlas_w = 2048
    x = y = shelf_h = 0
    placed = {}
    for name, im in items:
        if x + im.width + 2 > atlas_w:
            x, y, shelf_h = 0, y + shelf_h + 2, 0
        placed[name] = (x, y, im)
        x += im.width + 2
        shelf_h = max(shelf_h, im.height)
    atlas = Image.new("RGBA", (atlas_w, y + shelf_h + 2), (0, 0, 0, 0))
    frames = {}
    for name, (px, py, im) in placed.items():
        atlas.alpha_composite(im, (px, py))
        frames[name] = {"x": px, "y": py, "w": im.width, "h": im.height}
    atlas.save(OUT / "objects.png", optimize=True)
    return {"image": "objects.png", "frames": frames}


# --------------------------------------------------------------------------- #
# V2 catalogs: opaque "asset pack" pictures (assets/reference/v2-catalogs/simN.png)
# 2 x 5 panels: walk S, SW, W, NW, N / NE, E, SE, sit-stand (8), typing (6).
# Frames sit on a flat light-gray checker inside dark-blue panels. The checker is
# removed by flood-filling from each panel's border: character outlines are closed,
# so white shirts (which look like checker pixels) are never reached.
# --------------------------------------------------------------------------- #
CATALOGS = ROOT / "assets" / "reference" / "v2-catalogs"
PANEL_ORDER = ["S", "SW", "W", "NW", "N", "NE", "E", "SE", "SIT", "TYPE"]
PANEL_FRAMES = {"SIT": 8, "TYPE": 6}
DIR_ROWS_8 = {"down": 0, "downleft": 1, "left": 2, "upleft": 3, "up": 4, "upright": 5, "right": 6, "downright": 7}
# 2026-09-24 (kullanici: "simler masaya gore kucuk, masaya uzak oturuyor"): 78 -> 94. Olcum: masanin on yuzu ~55 dunya px,
# 78 px'lik karakter oturunca basi masa ustunun kenarina hic yetismiyordu. Oturma noktalari da masaya yanastirildi (scene.json).
CHAR_V2_HEIGHT_WORLD = 94

# HSV ops shared by derived characters (PIL hue 0-255: red 0, orange ~20, yellow ~42, green ~85, blue ~170, purple ~200).
# Brown hair in these packs sits at hue 5-30 and is darker than skin of the same hue, hence the val cap.
_HAIR = {"hue": (5, 30), "sat": (60, 255), "val": (0, 175)}
_NAVY = {"hue": (145, 190), "sat": (40, 255)}      # skirt, tie, trousers (sim1, sim3)
_JEANS = {"hue": (130, 185), "sat": (30, 255)}     # jeans (sim4)
HAIR_BLONDE = {**_HAIR, "to_hue": 26, "sat_mul": 1.1, "val_mul": 1.7, "val_add": 30}
HAIR_RED = {**_HAIR, "to_hue": 6, "sat_mul": 1.25, "val_mul": 1.15}
HAIR_AUBURN = {**_HAIR, "to_hue": 250, "sat_mul": 1.1}
HAIR_BLACK = {**_HAIR, "sat_mul": 0.3, "val_mul": 0.42}

# key -> (catalog file, recolor): None, a legacy (hue_band, hue_to) tuple, or a list of `recolor_ops` ops.
# Derived characters (2026-09-23, user request "new characters, mostly women"): palette variants of the
# complete catalogs -- the extra packs (sim7, karma) have truncated or tiny panels and don't slice cleanly.
CHARACTERS_V2: dict[str, tuple[str, tuple[tuple[int, int], int] | list[dict] | None]] = {
    "shirt-tie": ("sim1.png", None),
    "green-hoodie": ("sim2.png", None),
    "ponytail": ("sim3.png", None),
    "bun": ("sim4.png", None),
    "blond-maroon": ("sim5.png", None),
    "curly-yellow": ("sim6.png", None),
    "hipster": ("sim8.png", None),
    "blue-hoodie": ("sim2.png", ((55, 115), 150)),
    "ponytail-blonde": ("sim3.png", [HAIR_BLONDE, {**_NAVY, "to_hue": 245, "sat_mul": 1.1}]),
    "ponytail-red": ("sim3.png", [HAIR_RED, {**_NAVY, "to_hue": 95}]),
    "ponytail-black": ("sim3.png", [HAIR_BLACK, {**_NAVY, "sat_mul": 0.15, "val_mul": 1.35}]),
    "bun-black": ("sim4.png", [HAIR_BLACK, {**_JEANS, "to_hue": 225, "sat_mul": 1.2, "val_mul": 0.8}]),
    "bun-auburn": ("sim4.png", [HAIR_AUBURN, {**_JEANS, "to_hue": 120, "val_mul": 0.9}]),
    "shirt-tie-blond": ("sim1.png", [HAIR_BLONDE, {**_NAVY, "to_hue": 250, "sat_mul": 1.2}]),
}



def checker_mask(a: np.ndarray) -> np.ndarray:
    v = a[:, :, :3].astype(int)
    sat = v.max(axis=2) - v.min(axis=2)
    val = v.mean(axis=2)
    return (sat <= 14) & (val >= 200) & (val <= 250)


def flood_from_border(mask: np.ndarray) -> np.ndarray:
    """Pixels of `mask` reachable from the array border (4-connected). Iterative dilation."""
    reach = np.zeros_like(mask)
    reach[0, :] = mask[0, :]; reach[-1, :] = mask[-1, :]
    reach[:, 0] = mask[:, 0]; reach[:, -1] = mask[:, -1]
    while True:
        grown = reach.copy()
        grown[1:, :] |= reach[:-1, :]
        grown[:-1, :] |= reach[1:, :]
        grown[:, 1:] |= reach[:, :-1]
        grown[:, :-1] |= reach[:, 1:]
        grown &= mask
        if np.array_equal(grown, reach):
            return reach
        reach = grown


def catalog_panels(img: Image.Image) -> list[tuple[int, int, int, int]]:
    a = np.asarray(img.convert("RGB"))
    m = checker_mask(a)
    boxes = [b for b in components(m, step=2, merge_px=0) if (b[2] - b[0]) > 150 and (b[3] - b[1]) > 150]
    if len(boxes) != 10:
        raise SystemExit(f"expected 10 panels, found {len(boxes)}: {boxes}")
    boxes.sort(key=lambda b: (b[1] + b[3]) / 2)
    rows = [sorted(boxes[:5], key=lambda b: b[0]), sorted(boxes[5:], key=lambda b: b[0])]
    return rows[0] + rows[1]


def panel_frames(img: Image.Image, box: tuple[int, int, int, int], expected: int) -> list[Image.Image]:
    x0, y0, x1, y1 = box
    # Cut the dark-blue file-name label at the panel bottom: first row in the lower 45%
    # where more than half of the pixels are dark. Frames never touch it after this.
    rgb = np.asarray(img.convert("RGB").crop(box)).astype(int)
    dark = (rgb.max(axis=2) < 120).mean(axis=1)
    start = int(dark.shape[0] * 0.55)
    hit = np.where(dark[start:] > 0.85)[0]
    if len(hit):
        y1 = y0 + start + int(hit[0]) - 6
        box = (x0, y0, x1, y1)
    pw, ph = x1 - x0, y1 - y0
    crop = np.asarray(img.convert("RGBA").crop(box)).copy()
    bg = flood_from_border(checker_mask(crop))
    fg = ~bg
    crop[:, :, 3] = np.where(bg, 0, 255).astype(np.uint8)
    cands = [tighten(fg, b) for b in components(fg, step=2, merge_px=0) if area(b) > 600]
    # A frame is roughly a 48x48 cell drawn at ~2x: taller than a digit, narrower than the
    # panel's label bar, shorter than two stacked rows.
    cands = [b for b in cands if 60 <= (b[3] - b[1]) <= ph * 0.45 and (b[2] - b[0]) <= pw * 0.45]
    if len(cands) < expected:
        raise ValueError(f"only {len(cands)} frame candidates for {expected}")
    if len(cands) > expected:
        # Detached bits (a shadow, a hand) join the nearest big frame.
        cands.sort(key=area, reverse=True)
        keep, extra = cands[:expected], cands[expected:]
        for e in extra:
            ecx, ecy = (e[0] + e[2]) / 2, (e[1] + e[3]) / 2
            j = min(range(len(keep)), key=lambda i: ((keep[i][0] + keep[i][2]) / 2 - ecx) ** 2 + ((keep[i][1] + keep[i][3]) / 2 - ecy) ** 2)
            k = keep[j]
            keep[j] = (min(k[0], e[0]), min(k[1], e[1]), max(k[2], e[2]), max(k[3], e[3]))
        cands = keep
    cands.sort(key=lambda b: (b[1] + b[3]) / 2)
    per_row = expected // 2
    grid = sorted(cands[:per_row], key=lambda b: b[0]) + sorted(cands[per_row:], key=lambda b: b[0])
    out = Image.fromarray(crop, "RGBA")
    return [out.crop(b) for b in grid]


_CATALOG_CACHE: dict[str, tuple[dict[str, list[Image.Image]], float]] = {}


# Bu kataloglarda "SW" paneli neredeyse onden cizilmis (yuz kameraya, ayaklar asagi): sol-asagi yuruyen karakter
# bize dogru kayiyor gibi gorunuyordu, "SE" ise duzgun yan profil. SW, SE'nin aynasindan uretilir (olcum 2026-09-24).
MIRROR_SW_FROM_SE = {"sim1.png", "sim5.png", "sim6.png"}


def catalog_frames(key: str) -> tuple[dict[str, list[Image.Image]], float]:
    """Bir katalogun 10 panelinden kareler + o karakterin dunya olcegi. Bagisci de buradan gelir."""
    if key in _CATALOG_CACHE:
        return _CATALOG_CACHE[key]
    file, recolor_spec = CHARACTERS_V2[key]
    img = Image.open(CATALOGS / file).convert("RGBA")
    if isinstance(recolor_spec, list):
        img = recolor_ops(img, recolor_spec)
    elif recolor_spec:
        img = recolor(img, recolor_spec[0], recolor_spec[1])
    panels = catalog_panels(img)
    frames: dict[str, list[Image.Image]] = {}
    for name, box in zip(PANEL_ORDER, panels):
        frames[name] = panel_frames(img, box, PANEL_FRAMES.get(name, 6))
    if file in MIRROR_SW_FROM_SE:
        frames["SW"] = [f.transpose(Image.Transpose.FLIP_LEFT_RIGHT) for f in frames["SE"]]
    # One scale for the whole character: standing height from the walk-south frames.
    stand_h = max(f.height for f in frames["S"])
    scale = (CHAR_V2_HEIGHT_WORLD * WORLD_SCALE) / stand_h
    _CATALOG_CACHE[key] = (frames, scale)
    return frames, scale


# Kaynak katalogda "yazma" paneli sandalyesiz cizilmis karakterler (sim8: ayakta, elinde laptop). Masada arkadan oturma
# kareleri (TYPE 3-5) bagiscinin sandalyesi + karakterin arkadan yuruyus karesinin ust govdesiyle uretilir.
# Bagisci secimi olcumle: yesil kapusonun koyu tonlari ve koyu sac sandalye maskesine karisiyor, "bun"un sandalyesi temiz kesiliyor.
SEATED_BACK_DONOR: dict[str, str] = {"hipster": "bun"}


def _chair_masks(rgba: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """(lacivert, lacivert|koyu dis cizgi) maskeleri (PIL HSV). Ust sinir yalniz lacivertten bulunur: koyu sac da 'koyu'dur."""
    hsv = np.asarray(Image.fromarray(rgba[:, :, :3], "RGB").convert("HSV")).astype(int)
    h, s_, v = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    opaque = rgba[:, :, 3] > 128
    navy = (h >= 140) & (h <= 190) & (s_ >= 35) & (v <= 190) & opaque
    return navy, (navy | (v <= 70)) & opaque


def synth_seated_back(key: str, frames: dict[str, list[Image.Image]], scale: float) -> None:
    donor_frames, donor_scale = catalog_frames(SEATED_BACK_DONOR[key])
    k = donor_scale / scale  # bagisci karesi -> bu karakterin kaynak pikseli
    torso_src = frames["N"][0]
    tb = torso_src.getchannel("A").point(lambda a: 255 if a > 128 else 0).getbbox()
    torso_src = torso_src.crop(tb)
    torso = torso_src.crop((0, 0, torso_src.width, int(torso_src.height * 0.62)))  # bas + govde, kalca sandalye arkasinda kalir
    out = list(frames["TYPE"])
    for i in (3, 4, 5):
        d = donor_frames["TYPE"][i]
        d = d.resize((max(1, round(d.width * k)), max(1, round(d.height * k))), Image.LANCZOS)
        a = np.asarray(d.convert("RGBA")).copy()
        navy, chair = _chair_masks(a)
        row_frac = navy.mean(axis=1)
        rows = np.where(row_frac >= 0.45)[0]
        top = int(rows[0]) if len(rows) else a.shape[0] // 3
        keep = np.zeros_like(chair)
        keep[top:, :] = chair[top:, :]
        # Bagiscinin kol dis cizgileri sandalyenin yaninda kalir: lacivertin yatay sinirinin disindaki koyu pikseller atilir.
        cols = np.where(navy[top:, :].any(axis=0))[0]
        if len(cols):
            keep[:, : max(0, int(cols[0]) - 1)] = False
            keep[:, int(cols[-1]) + 2 :] = False
        a[:, :, 3] = np.where(keep, a[:, :, 3], 0)
        chair_img = Image.fromarray(a, "RGBA")
        head_top = d.getchannel("A").point(lambda v: 255 if v > 128 else 0).getbbox()[1]
        w = max(chair_img.width, torso.width)
        canvas = Image.new("RGBA", (w, chair_img.height), (0, 0, 0, 0))
        canvas.alpha_composite(torso, ((w - torso.width) // 2, head_top))
        canvas.alpha_composite(chair_img, ((w - chair_img.width) // 2, 0))
        out[i] = canvas
    frames["TYPE"] = out


def build_character_v2(key: str) -> dict:
    frames, scale = catalog_frames(key)
    if key in SEATED_BACK_DONOR:
        frames = dict(frames)
        synth_seated_back(key, frames, scale)

    def pack(rows: list[list[Image.Image]], out_name: str) -> dict:
        max_w = max(f.width for r in rows for f in r)
        max_h = max(f.height for r in rows for f in r)
        cw = int(round(max_w * scale)) + 4
        ch = int(round(max_h * scale)) + 2
        sheet = Image.new("RGBA", (cw * len(rows[0]), ch * len(rows)), (0, 0, 0, 0))
        for r, row in enumerate(rows):
            for c, f in enumerate(row):
                fs = f.resize((max(1, int(round(f.width * scale))), max(1, int(round(f.height * scale)))), Image.LANCZOS)
                sheet.alpha_composite(fs, (c * cw + (cw - fs.width) // 2, r * ch + (ch - fs.height) - 1))
        sheet.save(OUT / f"{out_name}.png", optimize=True)
        return {"image": f"{out_name}.png", "frameW": cw, "frameH": ch, "cols": len(rows[0]), "rows": len(rows)}

    walk = pack([frames[d] for d in ["S", "SW", "W", "NW", "N", "NE", "E", "SE"]], f"char-{key}-walk")
    walk["dirRows"] = dict(DIR_ROWS_8)
    sit = pack([frames["SIT"]], f"char-{key}-sit")
    typ = pack([frames["TYPE"]], f"char-{key}-type")
    return {"walk": walk, "sit": sit, "type": typ}


def erase_regions(bg: Image.Image) -> Image.Image:
    """Cover baked-in furniture with clean floor copied from an offset donor rect
    (config/scene.json background.erase: [x, y, w, h, dx, dy]). Props then take its place."""
    cfg = json.loads((ROOT / "config" / "scene.json").read_text(encoding="utf-8"))
    rects = (cfg.get("background") or {}).get("erase") or []
    out = bg.convert("RGBA").copy()
    for r in rects:
        if len(r) == 6:
            x, y, w, h, dx, dy = r
            donor = out.crop((x + dx, y + dy, x + dx + w, y + dy + h))
            out.paste(donor, (x, y))
        else:
            # Tiled: repeat a small donor patch (sx, sy, sw, sh) across the target rect.
            x, y, w, h, sx, sy, sw, sh = r
            patch = out.crop((sx, sy, sx + sw, sy + sh))
            for ty in range(y, y + h, sh):
                for tx in range(x, x + w, sw):
                    piece = patch.crop((0, 0, min(sw, x + w - tx), min(sh, y + h - ty)))
                    out.paste(piece, (tx, ty))
    return out


def stretch_regions(bg: Image.Image) -> Image.Image:
    """Buyut bir esyayi 9-dilim ile (config/scene.json background.stretch:
    {src:[x,y,w,h], dst:[x,y,w,h], border}). Koseler aynen tasinir, kenarlar ve ic
    alan gerilir; boylece cerceve kalinligi bozulmadan esya arkasindaki alani kaplar."""
    cfg = json.loads((ROOT / "config" / "scene.json").read_text(encoding="utf-8"))
    items = (cfg.get("background") or {}).get("stretch") or []
    out = bg.convert("RGBA").copy()
    for it in items:
        sx, sy, sw, sh = it["src"]
        dx, dy, dw, dh = it["dst"]
        b = int(it.get("border", 8))
        piece = out.crop((sx, sy, sx + sw, sy + sh))
        grown = Image.new("RGBA", (dw, dh))
        spans_s = [(0, b), (b, sw - b), (sw - b, sw)], [(0, b), (b, sh - b), (sh - b, sh)]
        spans_d = [(0, b), (b, dw - b), (dw - b, dw)], [(0, b), (b, dh - b), (dh - b, dh)]
        for (ry0, ry1), (ty0, ty1) in zip(spans_s[1], spans_d[1]):
            for (rx0, rx1), (tx0, tx1) in zip(spans_s[0], spans_d[0]):
                part = piece.crop((rx0, ry0, rx1, ry1))
                tw, th = tx1 - tx0, ty1 - ty0
                if (part.width, part.height) != (tw, th):
                    part = part.resize((tw, th), Image.NEAREST)
                grown.paste(part, (tx0, ty0))
        out.paste(grown, (dx, dy))
    return out


def key_window_glass(bg: Image.Image) -> Image.Image:
    """Make the window glass transparent inside config/scene.json `window` so the UI can
    draw a time-of-day sky behind it. Mullions (dark) and plants (green) are untouched."""
    cfg = json.loads((ROOT / "config" / "scene.json").read_text(encoding="utf-8"))
    win = cfg.get("window")
    if not win:
        return bg
    a = np.asarray(bg.convert("RGBA")).copy()
    x0, y0, x1, y1 = win["x"], win["y"], win["x"] + win["w"], win["y"] + win["h"]
    sub = a[y0:y1, x0:x1].astype(int)
    r, g, b = sub[:, :, 0], sub[:, :, 1], sub[:, :, 2]
    # Warm gray glass with reflection streaks: R > G > B, low saturation, mid-high value.
    # Upper bound 100 (was 75): the brightest streak in the 4th pane reaches R-B ~96 and
    # stayed opaque as a tan wedge over the sky. Wood/wall inside the rect are far warmer.
    glass = (r - b >= 20) & (r - b <= 100) & (r >= g) & (g >= b) & (b >= 95) & (r <= 245)
    sub[:, :, 3] = np.where(glass, 0, sub[:, :, 3])
    a[y0:y1, x0:x1] = sub.astype(np.uint8)
    return Image.fromarray(a, "RGBA")


# --------------------------------------------------------------------------- #
def load(name: str) -> Image.Image:
    return Image.open(RAW / name).convert("RGBA")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--inspect", action="store_true", help="dump numbered tileset boxes and exit")
    ap.add_argument("--only", help="comma-separated character keys: build just these and merge into the existing atlas.json "
                    "(other files untouched, so a Pillow upgrade doesn't rewrite every PNG)")
    args = ap.parse_args()

    if args.only:
        keys = [k.strip() for k in args.only.split(",") if k.strip()]
        unknown = [k for k in keys if k not in CHARACTERS_V2]
        if unknown:
            print(f"unknown character(s): {', '.join(unknown)}", file=sys.stderr)
            return 2
        atlas_file = OUT / "atlas.json"
        atlas = json.loads(atlas_file.read_text(encoding="utf-8"))
        for key in keys:
            atlas["characters"][key] = build_character_v2(key)
            w = atlas["characters"][key]["walk"]
            print("character", key, w["frameW"], w["frameH"])
        atlas_file.write_text(json.dumps(atlas, indent=2), encoding="utf-8")
        print("atlas ->", atlas_file)
        return 0

    tileset = load("objects-tileset.png")
    if args.inspect:
        inspect_tileset(tileset)
        return 0

    OUT.mkdir(parents=True, exist_ok=True)
    atlas: dict = {"worldScale": WORLD_SCALE, "characters": {}, "cat": {}, "fx": {}, "objects": {}}

    for key in CHARACTERS_V2:
        try:
            atlas["characters"][key] = build_character_v2(key)
        except ValueError as ex:
            print(f"WARNING: character {key} skipped: {ex}", file=sys.stderr)
            continue
        w = atlas["characters"][key]["walk"]
        print("character", key, w["frameW"], w["frameH"])

    # Background (V2 "Ana Sahne"): opaque, copied as-is; the layout in config/scene.json
    # references it and only adds what it lacks (chairs come with the seated frames).
    bgimg = key_window_glass(stretch_regions(erase_regions(load("background.png"))))
    bgimg.save(OUT / "background.png", optimize=True)
    atlas["background"] = {"image": "background.png", "w": bgimg.width, "h": bgimg.height}

    walk = grid_frames(load("cat-walk.png"), cols=3, rows=4)
    meta = pack_grid(walk, CAT_HEIGHT_WORLD, "cat-walk")
    meta["dirRows"] = {"down": 0, "left": 1, "right": 2, "up": 3}
    atlas["cat"]["walk"] = meta
    atlas["cat"]["sleep"] = single(load("cat-sleep.png"), 34, "h", "cat-sleep")
    atlas["cat"]["sit"] = single(load("cat-sit-front.png"), CAT_HEIGHT_WORLD, "h", "cat-sit")
    atlas["cat"]["lie"] = single(load("cat-lie-top.png"), 56, "h", "cat-lie")

    atlas["fx"]["bubble"] = strip([load("bubble-1.png"), load("bubble-2.png"), load("bubble-3.png")], BUBBLE_WIDTH_WORLD, "w", "bubble")
    atlas["fx"]["door"] = strip([load("door-closed.png"), load("door-half.png"), load("door-open.png")], DOOR_HEIGHT_WORLD, "h", "door")

    atlas["objects"]["tileset"] = pack_objects(tileset)
    print("objects", len(atlas["objects"]["tileset"]["frames"]))

    (OUT / "atlas.json").write_text(json.dumps(atlas, indent=2), encoding="utf-8")
    print("atlas ->", OUT / "atlas.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
