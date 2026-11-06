"""Gera assets/icon.ico (raio branco num quadrado arredondado azul→ciano). Requer opencv-python e numpy."""
import struct, sys, os
import cv2
import numpy as np


def render(size):
    S = 1024
    y, x = np.mgrid[0:S, 0:S].astype(np.float32)
    t = (x + y) / (2 * S)
    c1, c2 = np.array([246, 130, 59]), np.array([212, 182, 6])  # BGR: #3B82F6 -> #06B6D4
    img = (c1[None, None, :] * (1 - t[..., None]) + c2[None, None, :] * t[..., None])
    alpha = np.zeros((S, S), np.uint8)
    r, m = 220, 40
    cv2.rectangle(alpha, (m + r, m), (S - m - r, S - m), 255, -1)
    cv2.rectangle(alpha, (m, m + r), (S - m, S - m - r), 255, -1)
    for cx, cy in [(m + r, m + r), (S - m - r, m + r), (m + r, S - m - r), (S - m - r, S - m - r)]:
        cv2.circle(alpha, (cx, cy), r, 255, -1, cv2.LINE_AA)
    bolt = np.array([[560, 150], [280, 560], [480, 560], [420, 880], [750, 420], [550, 420], [640, 150]], np.int32)
    cv2.fillPoly(img, [bolt], (255, 255, 255), cv2.LINE_AA)
    bgra = np.dstack([img.astype(np.uint8), alpha])
    return cv2.resize(bgra, (size, size), interpolation=cv2.INTER_AREA)


def main(out):
    sizes = [16, 24, 32, 48, 64, 128, 256]
    pngs = [cv2.imencode(".png", render(s))[1].tobytes() for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, pngs):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(out, "wb") as f:
        f.write(header + entries + b"".join(pngs))
    cv2.imwrite(os.path.splitext(out)[0] + ".png", render(256))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "icon.ico"))
