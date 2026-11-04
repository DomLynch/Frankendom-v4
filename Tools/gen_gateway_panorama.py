"""Procedural placeholder art for the Hades-gate rift (no downloads).
Writes Assets/VFX/Gateway/placeholder_rift_equirect.png (4096x2048 sRGB: violet/cyan nebula, stars, huge ringed planet, small moon, distant sun)
and Assets/VFX/Gateway/placeholder_rift_clouds.png (1024x512 RGBA parallax cloud layer, straight alpha).
Run:  python Tools/gen_gateway_panorama.py [--small]      (--small = 1024x512 preview into Shots/gateway/ only)
Equirect convention (matches Furnace/GatewayRift.shader): u = atan2(x, z)/(2pi) + 0.5, v = asin(y)/pi + 0.5, image row 0 = top (+Y).
The planet sits at u~0.52 so the door (looking +Z) sees it centred."""
import os, sys, time
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OUT = os.path.join(ROOT, "Assets", "VFX", "Gateway")
SMALL = "--small" in sys.argv
W, H = (1024, 512) if SMALL else (4096, 2048)
if SMALL:
    OUT = os.path.join(ROOT, "Shots", "gateway")
os.makedirs(OUT, exist_ok=True)
rng = np.random.default_rng(20261104)


def hash3(ix, iy, iz, seed):
    h = (ix * 73856093) ^ (iy * 19349663) ^ (iz * 83492791) ^ ((seed * 2654435761) & 0xFFFFFFFF)
    h &= 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    h ^= h >> 16
    return (h & 0xFFFF).astype(np.float32) / 65535.0


def vnoise(x, y, z, seed):
    fx, fy, fz = np.floor(x), np.floor(y), np.floor(z)
    tx, ty, tz = x - fx, y - fy, z - fz
    tx, ty, tz = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty), tz * tz * (3 - 2 * tz)
    x0, y0, z0 = fx.astype(np.int64), fy.astype(np.int64), fz.astype(np.int64)

    def h(dx, dy, dz):
        return hash3(x0 + dx, y0 + dy, z0 + dz, seed)

    c00 = h(0, 0, 0) * (1 - tx) + h(1, 0, 0) * tx
    c10 = h(0, 1, 0) * (1 - tx) + h(1, 1, 0) * tx
    c01 = h(0, 0, 1) * (1 - tx) + h(1, 0, 1) * tx
    c11 = h(0, 1, 1) * (1 - tx) + h(1, 1, 1) * tx
    return (c00 * (1 - ty) + c10 * ty) * (1 - tz) + (c01 * (1 - ty) + c11 * ty) * tz


def fbm(x, y, z, octaves, seed, lac=2.03, gain=0.5):
    s = np.zeros_like(x, dtype=np.float32)
    amp, norm, f = 0.5, 0.0, 1.0
    for o in range(octaves):
        s += amp * vnoise(x * f + 11.7 * o, y * f - 5.3 * o, z * f + 2.9 * o, seed + o * 17)
        norm += amp
        amp *= gain
        f *= lac
    return s / norm


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def mix(a, b, t):
    return a + (b - a) * t[..., None] if np.ndim(t) else a + (b - a) * t


def dirs(lat, lon):
    """lat (n,), lon (m,) -> d (n,m,3) float32"""
    cl = np.cos(lat)[:, None]
    return np.stack([cl * np.sin(lon)[None, :], np.broadcast_to(np.sin(lat)[:, None], (len(lat), len(lon))), cl * np.cos(lon)[None, :]], axis=-1).astype(np.float32)


lat_all = ((0.5 - (np.arange(H) + 0.5) / H) * np.pi).astype(np.float32)
lon_all = (((np.arange(W) + 0.5) / W - 0.5) * 2 * np.pi).astype(np.float32)

t0 = time.time()
img = np.zeros((H, W, 3), np.float32)
band_n = np.array([0.30, 0.90, -0.30], np.float32)
band_n /= np.linalg.norm(band_n)
CH = 128 if not SMALL else 64
for r0 in range(0, H, CH):
    d = dirs(lat_all[r0:r0 + CH], lon_all)
    x, y, z = d[..., 0], d[..., 1], d[..., 2]
    q = fbm(x * 1.6, y * 1.6, z * 1.6, 5, 3)
    n1 = fbm(x * 2.3 + 2.2 * q, y * 2.3 + 2.2 * q, z * 2.3 + 2.2 * q, 6, 11)
    n2 = fbm(x * 3.2 + 5.0 + 1.6 * q, y * 3.2 + 1.6 * q, z * 3.2 - 5.0 + 1.6 * q, 5, 29)
    band = np.exp(-(((d @ band_n) / 0.38) ** 2))
    amount = 0.30 + 0.95 * band
    dust = smoothstep(0.52, 0.78, fbm(x * 4.1 + 9, y * 4.1, z * 4.1 - 3, 4, 53))
    lat = lat_all[r0:r0 + CH][:, None]
    zen = smoothstep(-0.2, 1.2, lat / (np.pi / 2) + 0.2)
    sky = np.array([0.012, 0.008, 0.045], np.float32) * (1 - zen[..., None]) + np.array([0.005, 0.004, 0.030], np.float32) * zen[..., None]
    haze = np.exp(-((lat / 0.33) ** 2))
    sky = sky + haze[..., None] * np.array([0.045, 0.12, 0.17], np.float32)
    violet = smoothstep(0.34, 0.84, n1)[..., None] * np.array([0.40, 0.11, 0.82], np.float32) * 0.60
    cyan = smoothstep(0.46, 0.92, n2)[..., None] * np.array([0.05, 0.72, 0.95], np.float32) * 0.52
    core = smoothstep(0.62, 0.95, (n1 * n2 * 1.9))[..., None] * np.array([1.0, 0.52, 0.82], np.float32) * 0.85
    neb = (violet + cyan + core) * (amount * (1 - 0.72 * dust))[..., None]
    img[r0:r0 + CH] = sky + neb
print("nebula", round(time.time() - t0, 1), "s")


def add_star(px, py, col, amp, sx, sy):
    rx, ry = int(np.ceil(sx * 4)), int(np.ceil(sy * 4))
    ys = np.arange(int(py) - ry, int(py) + ry + 1)
    xs = np.arange(int(px) - rx, int(px) + rx + 1)
    yy = np.clip(ys, 0, H - 1)
    xx = xs % W
    g = np.exp(-0.5 * (((ys + 0.5 - py) / sy) ** 2)[:, None] - 0.5 * (((xs + 0.5 - px) / sx) ** 2)[None, :])
    img[np.ix_(yy, xx)] += (g[..., None] * amp) * np.array(col, np.float32)


temps = [(0.70, 0.82, 1.0), (0.9, 0.94, 1.0), (1.0, 1.0, 1.0), (1.0, 0.86, 0.66), (1.0, 0.62, 0.48)]
scale = W / 4096.0
n_faint, n_mid, n_big = int(16000 * scale * scale), int(900 * scale * scale), 36
for n, (lo, hi, sig) in ((n_faint, (0.10, 0.55, 0.0)), (n_mid, (0.5, 1.4, 0.75)), (n_big, (1.4, 3.2, 1.25))):
    lon = rng.uniform(-np.pi, np.pi, n)
    lat = np.arcsin(rng.uniform(-1, 1, n))
    b = lo + (hi - lo) * rng.random(n) ** 3.0
    for i in range(n):
        px, py = (lon[i] / (2 * np.pi) + 0.5) * W, (0.5 - lat[i] / np.pi) * H
        col = temps[rng.integers(len(temps))]
        if sig == 0.0:  # single pixel, bilinear splat
            x0, y0 = int(np.floor(px - 0.5)), int(np.floor(py - 0.5))
            fx, fy = px - 0.5 - x0, py - 0.5 - y0
            for dx, dy, w in ((0, 0, (1 - fx) * (1 - fy)), (1, 0, fx * (1 - fy)), (0, 1, (1 - fx) * fy), (1, 1, fx * fy)):
                yy, xx = y0 + dy, (x0 + dx) % W
                if 0 <= yy < H:
                    img[yy, xx] += np.array(col, np.float32) * b[i] * w
        else:
            stretch = 1.0 / max(0.25, np.cos(lat[i]))
            add_star(px, py, col, b[i], sig * stretch * scale + 0.3, sig * scale + 0.3)
            if sig > 1.0:  # big stars get a soft halo
                add_star(px, py, col, b[i] * 0.10, 7 * stretch * scale, 7 * scale)
print("stars", round(time.time() - t0, 1), "s")


def win(lon0, lat0, half_lon, half_lat):
    cx, cy = (lon0 / (2 * np.pi) + 0.5) * W, (0.5 - lat0 / np.pi) * H
    x0, x1 = int(cx - half_lon / (2 * np.pi) * W), int(cx + half_lon / (2 * np.pi) * W)
    y0, y1 = max(0, int(cy - half_lat / np.pi * H)), min(H, int(cy + half_lat / np.pi * H))
    xs = np.arange(x0, x1) % W
    return y0, y1, xs, x0, x1


def basis(lon0, lat0):
    c = np.array([np.cos(lat0) * np.sin(lon0), np.sin(lat0), np.cos(lat0) * np.cos(lon0)], np.float32)
    east = np.array([np.cos(lon0), 0, -np.sin(lon0)], np.float32)
    north = np.cross(c, east).astype(np.float32)
    return c, east, north


def comp(dst, rgb, a):
    return dst * (1 - a[..., None]) + rgb * a[..., None]


# ---- distant sun / bloom
sl, sb = 0.82, 0.24
y0, y1, xs, x0, x1 = win(sl, sb, 0.5, 0.5)
lat = lat_all[y0:y1]
lon = ((np.arange(x0, x1) + 0.5) / W - 0.5) * 2 * np.pi
d = dirs(lat, lon.astype(np.float32))
c, e, n = basis(sl, sb)
ang = np.arccos(np.clip(d @ c, -1, 1))
glow = (np.exp(-ang / 0.006) * 4.0 + np.exp(-ang / 0.05) * 0.55 + np.exp(-ang / 0.22) * 0.10)[..., None] * np.array([0.80, 0.93, 1.0], np.float32)
img[y0:y1][:, xs] += glow

# ---- planet + ring
pl, pb, R = 0.12, 0.10, 0.17
y0, y1, xs, x0, x1 = win(pl, pb, 0.62, 0.55)
lat = lat_all[y0:y1]
lon = ((np.arange(x0, x1) + 0.5) / W - 0.5) * 2 * np.pi
d = dirs(lat, lon.astype(np.float32))
c, e, n = basis(pl, pb)
dc = np.clip(d @ c, 1e-3, 1)
s = (d @ e) / dc / np.tan(R)
t = (d @ n) / dc / np.tan(R)
bg = img[y0:y1][:, xs].copy()
r2 = s * s + t * t
r = np.sqrt(r2)
# ring geometry (tilted ellipse in the tangent plane)
ang_r, sin_tilt = 0.30, 0.23
cs, sn = np.cos(ang_r), np.sin(ang_r)
u2 = cs * s + sn * t
v2 = (-sn * s + cs * t) / sin_tilt
rr = np.sqrt(u2 * u2 + v2 * v2)
dens = np.zeros_like(rr)
dens += smoothstep(1.22, 1.30, rr) * (1 - smoothstep(1.50, 1.55, rr)) * 0.55
dens += smoothstep(1.55, 1.60, rr) * (1 - smoothstep(1.94, 1.98, rr)) * 0.95
dens += smoothstep(2.03, 2.07, rr) * (1 - smoothstep(2.36, 2.44, rr)) * 0.75
dens *= 0.82 + 0.18 * np.sin(rr * 95.0) * np.sin(rr * 31.0 + 1.0)
ring_col = np.array([0.93, 0.80, 0.62], np.float32) * (0.55 + 0.45 * np.clip(0.5 + 0.5 * (u2 / 2.4), 0, 1))[..., None]
front = v2 < 0
img_w = bg.copy()
img_w = comp(img_w, ring_col, dens * (~front) * 0.9)
# planet disc
inside = r < 1.0
nz = np.sqrt(np.clip(1 - r2, 0, 1))
L = np.array([-0.62, 0.42, 0.66], np.float32)
L /= np.linalg.norm(L)
ndl = s * L[0] + t * L[1] + nz * L[2]
lit = smoothstep(-0.10, 0.45, ndl)
tt = t * np.cos(0.30) - s * np.sin(0.30)
warp = fbm(s * 2.4 + 3, t * 2.4, np.zeros_like(s), 4, 71)
bands = 0.5 + 0.5 * np.sin(tt * 15.0 + 3.2 * warp + 1.3 * np.sin(tt * 4.0))
swirl = fbm(s * 7.0 + warp * 3, t * 22.0, np.zeros_like(s) + 5, 4, 91)
cA, cB, cC = np.array([0.88, 0.58, 0.28], np.float32), np.array([0.50, 0.20, 0.34], np.float32), np.array([0.97, 0.84, 0.60], np.float32)
alb = mix(cA, cB, smoothstep(0.25, 0.8, bands))
alb = mix(alb, cC, smoothstep(0.55, 0.9, swirl) * 0.7)
fres = (1 - nz) ** 3.0
surface = alb * (0.035 + 1.05 * lit[..., None]) + fres[..., None] * np.array([0.10, 0.60, 0.95], np.float32) * (0.25 + 0.9 * smoothstep(-0.3, 0.6, ndl))[..., None]
edge = 1 - smoothstep(0.985, 1.0, r)
img_w = comp(img_w, surface, edge * inside)
img_w = comp(img_w, ring_col, dens * front * 0.92)
# thin atmosphere halo outside the limb
halo = np.exp(-np.clip(r - 1.0, 0, None) / 0.035) * (r >= 1.0) * smoothstep(-0.4, 0.5, ndl) * 0.35
img_w += halo[..., None] * np.array([0.12, 0.55, 0.95], np.float32)
img[y0:y1][:, xs] = img_w

# ---- small moon
ml, mb, mR = pl - 0.40, pb + 0.13, 0.034
y0, y1, xs, x0, x1 = win(ml, mb, 0.1, 0.1)
lat = lat_all[y0:y1]
lon = ((np.arange(x0, x1) + 0.5) / W - 0.5) * 2 * np.pi
d = dirs(lat, lon.astype(np.float32))
c, e, n = basis(ml, mb)
dc = np.clip(d @ c, 1e-3, 1)
s = (d @ e) / dc / np.tan(mR)
t = (d @ n) / dc / np.tan(mR)
r2 = s * s + t * t
nz = np.sqrt(np.clip(1 - r2, 0, 1))
ndl = s * L[0] + t * L[1] + nz * L[2]
cr = fbm(s * 5, t * 5, np.zeros_like(s), 4, 7)
moon = np.array([0.62, 0.70, 0.78], np.float32) * (0.03 + 1.0 * smoothstep(-0.05, 0.5, ndl) * (0.55 + 0.6 * cr))[..., None]
sub = img[y0:y1][:, xs]
img[y0:y1][:, xs] = comp(sub, moon, (1 - smoothstep(0.96, 1.0, np.sqrt(r2))))
print("planet", round(time.time() - t0, 1), "s")

# soft shoulder, dither, sRGB-ish encode (colours were authored directly in display space)
img = np.where(img > 0.80, 0.80 + 0.20 * np.tanh((img - 0.80) / 0.20), img)
img = np.clip(img, 0, 1)
img += (rng.random(img.shape, dtype=np.float32) - 0.5) / 255.0
Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB").save(os.path.join(OUT, "placeholder_rift_equirect.png"))
print("avg colour (gamma space)", np.round(img.reshape(-1, 3).mean(0), 4))

# ---- parallax cloud layer 1024x512 RGBA, straight alpha, seamless in u
CW, CHh = (1024, 512) if not SMALL else (512, 256)
lat = ((0.5 - (np.arange(CHh) + 0.5) / CHh) * np.pi).astype(np.float32)
lon = (((np.arange(CW) + 0.5) / CW - 0.5) * 2 * np.pi).astype(np.float32)
d = dirs(lat, lon)
x, y, z = d[..., 0], d[..., 1] * 2.4, d[..., 2]  # stretch vertically in noise space = long horizontal streaks
q = fbm(x * 2.0, y * 2.0, z * 2.0, 4, 5)
dn = fbm(x * 3.2 + 1.7 * q, y * 3.2 + 1.7 * q, z * 3.2 + 1.7 * q, 6, 17)
latm = lat[:, None]
band = np.exp(-((latm - 0.05) / 0.42) ** 2)
alpha = smoothstep(0.46, 0.80, dn) * band
tone = fbm(x * 4 + 3, y * 4, z * 4 - 2, 3, 41)
lit = 0.45 + 0.75 * smoothstep(0.3, 0.9, tone)
col = mix(np.array([0.30, 0.45, 0.95], np.float32), np.array([0.95, 0.55, 1.0], np.float32), smoothstep(0.35, 0.85, q)) * lit[..., None]
rgba = np.concatenate([np.clip(col, 0, 1), np.clip(alpha, 0, 1)[..., None]], axis=-1)
Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), "RGBA").save(os.path.join(OUT, "placeholder_rift_clouds.png"))
print("done", round(time.time() - t0, 1), "s ->", OUT)
