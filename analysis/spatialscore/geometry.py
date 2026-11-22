"""Small geometry toolkit: quaternions, oriented boxes, 2D rigid alignment, box distances.

Conventions: Unity world coordinates (metres), +Y up. Horizontal quantities live in the
XZ plane and are written as 2-vectors (x, z). Quaternions are (x, y, z, w) as Unity stores them.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np


# ----------------------------------------------------------------------------- rotations

def quat_to_matrix(q) -> np.ndarray:
    """3x3 rotation matrix whose columns are the local X, Y, Z axes in world space."""
    x, y, z, w = (float(v) for v in q)
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def unit2(v) -> np.ndarray:
    v = np.asarray(v, dtype=float)
    n = np.linalg.norm(v)
    return v / n if n > 1e-12 else np.array([1.0, 0.0])


def perp2(v) -> np.ndarray:
    """A horizontal vector perpendicular to v (sign is irrelevant for all uses here)."""
    return np.array([-v[1], v[0]])


def angle_deg(a, b) -> float:
    """Unsigned angle between two 2-vectors, 0..180."""
    a, b = unit2(a), unit2(b)
    return math.degrees(math.acos(max(-1.0, min(1.0, float(a @ b)))))


def axis_angle_deg(a, b) -> float:
    """Angle between two undirected axes, 0..90."""
    d = angle_deg(a, b)
    return min(d, 180.0 - d)


def rot2(theta: float) -> np.ndarray:
    c, s = math.cos(theta), math.sin(theta)
    return np.array([[c, -s], [s, c]])


def kabsch_2d(src: np.ndarray, dst: np.ndarray):
    """Rotation R (2x2, det +1) and translation t minimising sum |R src_i + t - dst_i|^2."""
    src = np.asarray(src, float)
    dst = np.asarray(dst, float)
    cs, cd = src.mean(0), dst.mean(0)
    h = (src - cs).T @ (dst - cd)
    u, _, vt = np.linalg.svd(h)
    d = np.sign(np.linalg.det(vt.T @ u.T)) or 1.0
    r = vt.T @ np.diag([1.0, d]) @ u.T
    return r, cd - r @ cs


# ----------------------------------------------------------------------------- boxes

_UNIT_CORNERS = np.array([[sx, sy, sz] for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)], float)


@dataclass
class Box:
    """Oriented box: centre, rotation (columns = local axes in world), half extents."""
    center: np.ndarray
    rot: np.ndarray
    half: np.ndarray
    tag: str = ''
    meta: dict = field(default_factory=dict)

    @staticmethod
    def make(center, rot, size, tag='', **meta) -> 'Box':
        return Box(np.asarray(center, float), np.asarray(rot, float),
                   np.abs(np.asarray(size, float)) / 2.0, tag, dict(meta))

    def corners(self) -> np.ndarray:
        return self.center + (_UNIT_CORNERS * self.half) @ self.rot.T

    @property
    def y_min(self) -> float:
        return float(self.corners()[:, 1].min())

    @property
    def y_max(self) -> float:
        return float(self.corners()[:, 1].max())

    def to_local(self, pts) -> np.ndarray:
        return (np.atleast_2d(pts) - self.center) @ self.rot

    def contains(self, pts, tol=0.0) -> np.ndarray:
        local = self.to_local(pts)
        return np.all(np.abs(local) <= self.half + tol, axis=1)

    def contains_xz(self, xz, tol=0.0) -> bool:
        """True when the vertical line through xz passes through the box's footprint."""
        p = np.array([xz[0], self.center[1], xz[1]])
        local = self.to_local(p)[0]
        # ignore the axis most aligned with world up
        up = int(np.argmax(np.abs(self.rot[1, :])))
        keep = [i for i in range(3) if i != up]
        return bool(np.all(np.abs(local[keep]) <= self.half[keep] + tol))

    def surface_points(self, spacing=0.05, max_per_face=400):
        """Points on the six faces and the area each represents."""
        pts, areas = [], []
        for axis in range(3):
            a, b = [i for i in range(3) if i != axis]
            la, lb = 2 * self.half[a], 2 * self.half[b]
            na = int(min(max(1, round(la / spacing)), math.sqrt(max_per_face)))
            nb = int(min(max(1, round(lb / spacing)), math.sqrt(max_per_face)))
            ua = (np.arange(na) + 0.5) / na * 2 - 1
            ub = (np.arange(nb) + 0.5) / nb * 2 - 1
            ga, gb = np.meshgrid(ua, ub, indexing='ij')
            for sign in (-1, 1):
                local = np.zeros((na * nb, 3))
                local[:, axis] = sign
                local[:, a] = ga.ravel()
                local[:, b] = gb.ravel()
                pts.append(self.center + (local * self.half) @ self.rot.T)
                areas.append(np.full(na * nb, la * lb / (na * nb)))
        return np.vstack(pts), np.concatenate(areas)


def solids(parts) -> list:
    """The boxes that make up the parts' actual geometry.

    A part clipped by a partial region edit carries its convex pieces in meta['pieces'] (one box
    per piece, in the part's frame); an unclipped part is its own box.
    """
    return [piece for p in parts for piece in (p.meta.get('pieces') or [p])]


def point_box_distance(pts, box: Box) -> np.ndarray:
    """Euclidean distance from points to the solid box (0 inside)."""
    local = box.to_local(pts)
    d = np.maximum(np.abs(local) - box.half, 0.0)
    return np.linalg.norm(d, axis=1)


def point_box_surface_distance(pts, box: Box) -> np.ndarray:
    """Distance from points to the box's surface (also for points inside)."""
    local = box.to_local(pts)
    outside = np.maximum(np.abs(local) - box.half, 0.0)
    out_d = np.linalg.norm(outside, axis=1)
    in_d = np.min(box.half - np.abs(local), axis=1)
    return np.where(out_d > 0, out_d, np.maximum(in_d, 0.0))


def penetration_depth(a: Box, b: Box) -> float:
    """Separating-axis test. Smallest overlap along the 15 candidate axes; 0 when separated."""
    axes = [a.rot[:, i] for i in range(3)] + [b.rot[:, i] for i in range(3)]
    for i in range(3):
        for j in range(3):
            c = np.cross(a.rot[:, i], b.rot[:, j])
            if np.linalg.norm(c) > 1e-6:
                axes.append(c / np.linalg.norm(c))
    d = b.center - a.center
    best = math.inf
    for ax in axes:
        ra = float(np.sum(a.half * np.abs(a.rot.T @ ax)))
        rb = float(np.sum(b.half * np.abs(b.rot.T @ ax)))
        overlap = ra + rb - abs(float(d @ ax))
        if overlap <= 0:
            return 0.0
        best = min(best, overlap)
    return best


def box_gap(a: Box, b: Box, spacing=0.03) -> float:
    """Signed gap: positive = distance between the boxes, negative = penetration depth."""
    pen = penetration_depth(a, b)
    if pen > 1e-6:
        return -pen
    pa, _ = a.surface_points(spacing, 225)
    pb, _ = b.surface_points(spacing, 225)
    return float(min(point_box_distance(pa, b).min(), point_box_distance(pb, a).min()))


def min_gap(parts_a, parts_b) -> float:
    """Smallest signed gap between any part of one group and any part of another."""
    best = math.inf
    for a in parts_a:
        for b in parts_b:
            # quick reject on bounding spheres
            if np.linalg.norm(a.center - b.center) - np.linalg.norm(a.half) - np.linalg.norm(b.half) > best:
                continue
            best = min(best, box_gap(a, b))
    return best
