import * as THREE from 'three';
export { THREE };

// ---------------------------------------------------------------- utilities

export function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function canvasTex(size, draw) {
  const c = document.createElement('canvas');
  c.width = c.height = size;
  draw(c.getContext('2d'), size);
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 8;
  return t;
}

function speckle(g, s, r, n, colors, alpha, max = 2.2) {
  for (let i = 0; i < n; i++) {
    g.fillStyle = colors[(r() * colors.length) | 0];
    g.globalAlpha = alpha * (0.4 + r() * 0.6);
    const d = 0.6 + r() * max;
    g.fillRect(r() * s, r() * s, d, d);
  }
  g.globalAlpha = 1;
}

/** Standard material from a canvas texture; the same canvas doubles as a bump map. */
export function pbr(tex, o = {}) {
  const { rough = 0.9, metal = 0, bump = 1.2, ...rest } = o;
  let bumpMap = null;
  if (tex && bump) {
    bumpMap = new THREE.CanvasTexture(tex.image);
    bumpMap.wrapS = bumpMap.wrapT = THREE.RepeatWrapping;
    bumpMap.repeat.copy(tex.repeat);
    bumpMap.rotation = tex.rotation;
  }
  return new THREE.MeshStandardMaterial({ map: tex, bumpMap, bumpScale: bump, roughness: rough, metalness: metal, ...rest });
}

// ---------------------------------------------------------------- textures (1 tile = 1 m unless noted)

export const tex = {
  wood(base = '#8a6238', planks = 4, seed = 1) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      g.fillStyle = base;
      g.fillRect(0, 0, s, s);
      const pw = s / planks;
      for (let i = 0; i < planks; i++) {
        g.fillStyle = r() < 0.5 ? '#000' : '#fff';
        g.globalAlpha = r() * 0.13;
        g.fillRect(i * pw, 0, pw, s);
        g.globalAlpha = 1;
        for (let k = 0; k < 46; k++) {
          const x = i * pw + r() * pw;
          g.strokeStyle = r() < 0.75 ? '#2a1708' : '#e9c99a';
          g.globalAlpha = 0.05 + r() * 0.16;
          g.lineWidth = 0.6 + r() * 1.6;
          g.beginPath();
          g.moveTo(x, 0);
          const w1 = (r() - 0.5) * 10, w2 = (r() - 0.5) * 10;
          g.bezierCurveTo(x + w1, s * 0.33, x + w2, s * 0.66, x, s);
          g.stroke();
        }
        if (r() < 0.7) {
          const kx = i * pw + pw * (0.25 + r() * 0.5), ky = r() * s;
          g.globalAlpha = 0.5;
          g.fillStyle = '#2b1809';
          g.beginPath();
          g.ellipse(kx, ky, 4 + r() * 5, 8 + r() * 9, 0, 0, 7);
          g.fill();
        }
        g.globalAlpha = 0.85;
        g.fillStyle = '#1c1008';
        g.fillRect(i * pw - 1.5, 0, 3, s);
      }
      g.globalAlpha = 1;
      speckle(g, s, r, 2500, ['#000', '#fff'], 0.06);
    });
  },
  stone(seed = 2) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#4b463f';
      g.fillRect(0, 0, s, s);
      const rows = 4, rh = s / rows;
      for (let j = 0; j < rows; j++) {
        let x = j % 2 ? -s * 0.18 : 0;
        while (x < s) {
          const bw = s * (0.28 + r() * 0.2);
          const v = 118 + r() * 50, warm = r() * 16;
          g.fillStyle = `rgb(${v + warm},${v + warm * 0.5},${v - 6})`;
          const m = 5;
          g.beginPath();
          g.roundRect(x + m, j * rh + m, bw - 2 * m, rh - 2 * m, 9);
          g.fill();
          if (x + bw > s) {
            g.beginPath();
            g.roundRect(x - s + m, j * rh + m, bw - 2 * m, rh - 2 * m, 9);
            g.fill();
          }
          x += bw;
        }
      }
      speckle(g, s, r, 9000, ['#1d1b18', '#d9d2c4', '#6a665c'], 0.16, 3);
      for (let i = 0; i < 26; i++) {
        g.strokeStyle = '#26221d';
        g.globalAlpha = 0.25;
        g.lineWidth = 1;
        g.beginPath();
        let x = r() * s, y = r() * s;
        g.moveTo(x, y);
        for (let k = 0; k < 4; k++) g.lineTo((x += (r() - 0.5) * 30), (y += (r() - 0.5) * 30));
        g.stroke();
      }
      g.globalAlpha = 1;
    });
  },
  cobble(seed = 3) {
    // one tile = 2 m
    return canvasTex(1024, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#3a352e';
      g.fillRect(0, 0, s, s);
      const n = 11, c = s / n;
      for (let j = 0; j < n; j++)
        for (let i = 0; i < n; i++) {
          const cx = (i + 0.5 + (j % 2 ? 0.5 : 0)) * c + (r() - 0.5) * c * 0.18;
          const cy = (j + 0.5) * c + (r() - 0.5) * c * 0.18;
          const v = 112 + r() * 46, warm = r() * 14;
          for (const ox of [0, -s]) {
            g.fillStyle = `rgb(${v + warm},${v + warm * 0.6},${v - 4})`;
            g.beginPath();
            g.ellipse(cx + ox, cy, c * (0.4 + r() * 0.06), c * (0.38 + r() * 0.07), r() * 3, 0, 7);
            g.fill();
            g.fillStyle = 'rgba(255,255,255,0.08)';
            g.beginPath();
            g.ellipse(cx + ox - c * 0.08, cy - c * 0.08, c * 0.22, c * 0.18, 0, 0, 7);
            g.fill();
          }
        }
      speckle(g, s, r, 26000, ['#15130f', '#e6dfd0', '#5c574d'], 0.14, 3);
    });
  },
  grass(seed = 4) {
    // one tile = 3 m
    return canvasTex(1024, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#4a6a2c';
      g.fillRect(0, 0, s, s);
      for (let i = 0; i < 90; i++) {
        g.fillStyle = r() < 0.5 ? '#3c5a22' : '#5d7c36';
        g.globalAlpha = 0.18;
        g.beginPath();
        g.ellipse(r() * s, r() * s, 40 + r() * 90, 30 + r() * 70, r() * 3, 0, 7);
        g.fill();
      }
      g.globalAlpha = 1;
      const cols = ['#2f4a1a', '#6f9140', '#86a04a', '#3f6124', '#9aa860'];
      for (let i = 0; i < 26000; i++) {
        const x = r() * s, y = r() * s, l = 3 + r() * 7;
        g.strokeStyle = cols[(r() * cols.length) | 0];
        g.globalAlpha = 0.35 + r() * 0.4;
        g.lineWidth = 1;
        g.beginPath();
        g.moveTo(x, y);
        g.lineTo(x + (r() - 0.5) * 4, y - l);
        g.stroke();
      }
      g.globalAlpha = 1;
    });
  },
  dirt(seed = 5) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#6b543a';
      g.fillRect(0, 0, s, s);
      speckle(g, s, r, 16000, ['#3d2f1f', '#8d7453', '#a38a66', '#54412c'], 0.5, 3.5);
      for (let i = 0; i < 60; i++) {
        const v = 110 + r() * 60;
        g.fillStyle = `rgb(${v},${v - 8},${v - 20})`;
        g.beginPath();
        g.ellipse(r() * s, r() * s, 2 + r() * 5, 2 + r() * 4, r() * 3, 0, 7);
        g.fill();
      }
    });
  },
  fabric(a = '#b33a2e', b = '#e8dcc0', stripes = 4, seed = 6) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      const sw = s / (stripes * 2);
      for (let i = 0; i < stripes * 2; i++) {
        g.fillStyle = i % 2 ? b : a;
        g.fillRect(i * sw, 0, sw + 1, s);
      }
      g.globalAlpha = 0.07;
      for (let i = 0; i < s; i += 3) {
        g.fillStyle = i % 2 ? '#000' : '#fff';
        g.fillRect(0, i, s, 1);
        g.fillRect(i, 0, 1, s);
      }
      g.globalAlpha = 1;
      for (let i = 0; i < 40; i++) {
        g.fillStyle = '#3a2f20';
        g.globalAlpha = 0.05;
        g.beginPath();
        g.ellipse(r() * s, r() * s, 20 + r() * 60, 15 + r() * 40, r() * 3, 0, 7);
        g.fill();
      }
      g.globalAlpha = 1;
      speckle(g, s, r, 5000, ['#000', '#fff'], 0.05);
    });
  },
  bark(seed = 7) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#4b3a2a';
      g.fillRect(0, 0, s, s);
      for (let k = 0; k < 420; k++) {
        const x = r() * s;
        g.strokeStyle = r() < 0.6 ? '#22170e' : '#7b6248';
        g.globalAlpha = 0.25 + r() * 0.4;
        g.lineWidth = 1 + r() * 3;
        g.beginPath();
        const y = r() * s;
        g.moveTo(x, y);
        g.lineTo(x + (r() - 0.5) * 8, y + 30 + r() * 110);
        g.stroke();
      }
      g.globalAlpha = 1;
    });
  },
  rings(seed = 8) {
    return canvasTex(256, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#b58a55';
      g.fillRect(0, 0, s, s);
      for (let i = 2; i < 124; i += 5 + r() * 5) {
        g.strokeStyle = '#6d4b28';
        g.globalAlpha = 0.5;
        g.lineWidth = 1 + r() * 1.5;
        g.beginPath();
        g.ellipse(s / 2 + r() * 3, s / 2 + r() * 3, i, i * (0.94 + r() * 0.1), 0, 0, 7);
        g.stroke();
      }
      g.globalAlpha = 1;
    });
  },
  shingle(seed = 9) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#2a211a';
      g.fillRect(0, 0, s, s);
      const rows = 6, rh = s / rows, n = 6, w = s / n;
      for (let j = 0; j < rows; j++)
        for (let i = -1; i < n; i++) {
          const v = 70 + r() * 40;
          g.fillStyle = `rgb(${v + 22},${v + 4},${v - 12})`;
          g.fillRect((i + (j % 2 ? 0.5 : 0)) * w + 2, j * rh + 2, w - 4, rh - 3);
        }
      speckle(g, s, r, 6000, ['#000', '#d9b98a'], 0.12, 3);
      g.fillStyle = 'rgba(0,0,0,0.35)';
      for (let j = 0; j < rows; j++) g.fillRect(0, j * rh, s, 5);
    });
  },
  leaves(seed = 12) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#3f6a2a';
      g.fillRect(0, 0, s, s);
      const cols = ['#2c521f', '#4f7f33', '#69963f', '#35602a', '#7fa64c'];
      for (let i = 0; i < 5200; i++) {
        g.fillStyle = cols[(r() * cols.length) | 0];
        g.globalAlpha = 0.55 + r() * 0.35;
        g.beginPath();
        g.ellipse(r() * s, r() * s, 3 + r() * 6, 2 + r() * 3.5, r() * 3.14, 0, 7);
        g.fill();
      }
      g.globalAlpha = 1;
    });
  },
  needles(seed = 10) {
    return canvasTex(512, (g, s) => {
      const r = rng(seed);
      g.fillStyle = '#24401f';
      g.fillRect(0, 0, s, s);
      const cols = ['#16301a', '#31562a', '#3f6a33', '#1c3a1c'];
      for (let i = 0; i < 9000; i++) {
        const x = r() * s, y = r() * s;
        g.strokeStyle = cols[(r() * cols.length) | 0];
        g.globalAlpha = 0.6;
        g.lineWidth = 1.5;
        g.beginPath();
        g.moveTo(x, y);
        g.lineTo(x + (r() - 0.5) * 9, y + 6 + r() * 10);
        g.stroke();
      }
      g.globalAlpha = 1;
    });
  },
  label(text, fg = '#2a1a0c') {
    const c = document.createElement('canvas');
    c.width = 512;
    c.height = 128;
    const g = c.getContext('2d');
    g.fillStyle = '#9a7446';
    g.fillRect(0, 0, 512, 128);
    const r = rng(text.length * 7);
    for (let k = 0; k < 90; k++) {
      g.strokeStyle = r() < 0.7 ? '#3a2410' : '#e6c594';
      g.globalAlpha = 0.12 + r() * 0.15;
      g.lineWidth = 1 + r() * 2;
      g.beginPath();
      const y = r() * 128;
      g.moveTo(0, y);
      g.bezierCurveTo(170, y + (r() - 0.5) * 12, 340, y + (r() - 0.5) * 12, 512, y);
      g.stroke();
    }
    g.globalAlpha = 0.9;
    g.fillStyle = fg;
    g.font = '700 64px Georgia, serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(text, 256, 68);
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    t.anisotropy = 8;
    return t;
  },
  glow() {
    const c = document.createElement('canvas');
    c.width = c.height = 128;
    const g = c.getContext('2d');
    const gr = g.createRadialGradient(64, 64, 2, 64, 64, 62);
    gr.addColorStop(0, 'rgba(255,255,255,1)');
    gr.addColorStop(0.35, 'rgba(255,255,255,0.55)');
    gr.addColorStop(1, 'rgba(255,255,255,0)');
    g.fillStyle = gr;
    g.fillRect(0, 0, 128, 128);
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    return t;
  },
  softDisc(inner = 0.55) {
    const c = document.createElement('canvas');
    c.width = c.height = 256;
    const g = c.getContext('2d');
    const gr = g.createRadialGradient(128, 128, 128 * inner, 128, 128, 126);
    gr.addColorStop(0, 'rgba(255,255,255,1)');
    gr.addColorStop(1, 'rgba(255,255,255,0)');
    g.fillStyle = gr;
    g.fillRect(0, 0, 256, 256);
    return new THREE.CanvasTexture(c);
  },
};

// ---------------------------------------------------------------- geometry helpers

/** Box whose UVs are in metres, so a 1 m texture tile keeps its scale on any box. */
export function box(w, h, d, mat, uv = 1) {
  const geo = new THREE.BoxGeometry(w, h, d);
  const a = geo.attributes.uv;
  const dims = [[d, h], [d, h], [w, d], [w, d], [w, h], [w, h]];
  for (let f = 0; f < 6; f++)
    for (let v = 0; v < 4; v++) {
      const i = f * 4 + v;
      a.setXY(i, a.getX(i) * dims[f][0] * uv, a.getY(i) * dims[f][1] * uv);
    }
  return new THREE.Mesh(geo, mat);
}

export function cyl(rt, rb, h, mat, seg = 16, uvU = 1, uvV = 1) {
  const geo = new THREE.CylinderGeometry(rt, rb, h, seg);
  const a = geo.attributes.uv;
  for (let i = 0; i < a.count; i++) a.setXY(i, a.getX(i) * uvU, a.getY(i) * uvV);
  return new THREE.Mesh(geo, mat);
}

/** Thin cylinder between two points (ropes, poles, rails). */
export function rod(a, b, r, mat, seg = 8) {
  const dir = new THREE.Vector3().subVectors(b, a);
  const m = cyl(r, r, dir.length(), mat, seg, 1, dir.length());
  m.position.copy(a).addScaledVector(dir, 0.5);
  m.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.normalize());
  return m;
}

export function at(mesh, x, y, z, rx = 0, ry = 0, rz = 0) {
  mesh.position.set(x, y, z);
  mesh.rotation.set(rx, ry, rz);
  return mesh;
}

export function group(x, z, rotY, ...children) {
  const g = new THREE.Group();
  g.position.set(x, 0, z);
  g.rotation.y = rotY;
  children.forEach((c) => c && g.add(c));
  return g;
}

/** Rotation that turns a model's local +Z towards a world point. */
export const faceTo = (x, z, tx, tz) => Math.atan2(tx - x, tz - z);

/** World XZ of a point given in a rotated object's local frame. */
export function localXZ(x, z, rotY, lx, lz) {
  const c = Math.cos(rotY), s = Math.sin(rotY);
  return [x + lx * c + lz * s, z - lx * s + lz * c];
}

// ---------------------------------------------------------------- world

const CSS = `
html,body{margin:0;height:100%;overflow:hidden;background:#111;font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:#f3efe6}
canvas{display:block;width:100%;height:100%;outline:none;touch-action:none}
#hud{position:fixed;left:12px;top:12px;display:flex;gap:6px;flex-wrap:wrap;z-index:5}
#hud button{background:rgba(20,18,15,.62);color:#f3efe6;border:1px solid rgba(255,255,255,.22);border-radius:8px;padding:7px 12px;font:500 13px system-ui,sans-serif;cursor:pointer;backdrop-filter:blur(6px)}
#hud button:hover{background:rgba(40,36,30,.8)}
#hud button[aria-pressed=true]{background:#f3efe6;color:#1b1814;border-color:#f3efe6}
#hud button:focus-visible{outline:2px solid #ffd27a;outline-offset:2px}
#title{position:fixed;right:14px;top:14px;font:500 13px system-ui,sans-serif;background:rgba(20,18,15,.55);padding:6px 10px;border-radius:8px;z-index:5}
#hint{position:fixed;left:50%;bottom:16px;transform:translateX(-50%);font:400 13px system-ui,sans-serif;background:rgba(20,18,15,.6);padding:7px 12px;border-radius:8px;z-index:5;text-align:center;max-width:92vw}
#toast{position:fixed;left:50%;bottom:58px;transform:translateX(-50%);font:500 13px system-ui,sans-serif;background:rgba(243,239,230,.92);color:#1b1814;padding:6px 12px;border-radius:8px;z-index:5;opacity:0;transition:opacity .25s}
#toast.on{opacity:1}
#cross{position:fixed;left:50%;top:50%;width:6px;height:6px;margin:-3px 0 0 -3px;border-radius:50%;background:rgba(255,255,255,.8);box-shadow:0 0 0 1px rgba(0,0,0,.5);z-index:4;display:none}
#start{position:fixed;inset:0;display:flex;align-items:center;justify-content:center;background:rgba(12,10,8,.5);z-index:10}
#start div{background:rgba(24,21,17,.92);border:1px solid rgba(255,255,255,.18);border-radius:14px;padding:26px 30px;max-width:420px;text-align:center}
#start h1{font:600 22px system-ui,sans-serif;margin:0 0 6px}
#start p{font:400 14px/1.55 system-ui,sans-serif;margin:0 0 16px;color:#d8d2c5}
#start button{background:#f3efe6;color:#1b1814;border:0;border-radius:10px;padding:11px 22px;font:600 15px system-ui,sans-serif;cursor:pointer}
#start button:focus-visible{outline:2px solid #ffd27a;outline-offset:3px}
kbd{background:rgba(255,255,255,.14);border-radius:4px;padding:1px 5px;font:500 12px ui-monospace,monospace}
`;

export function createWorld(cfg) {
  document.title = cfg.title;
  const style = document.createElement('style');
  style.textContent = CSS;
  document.head.appendChild(style);
  document.body.insertAdjacentHTML(
    'beforeend',
    `<div id="hud" role="toolbar" aria-label="View">
       <button data-v="walk">Walk</button><button data-v="persp">Perspective</button>
       <button data-v="front">Front</button><button data-v="side">Side</button><button data-v="top">Top</button>
     </div>
     <div id="title">${cfg.title}</div><div id="cross"></div><div id="toast" role="status"></div><div id="hint"></div>
     <div id="start"><div><h1>${cfg.title}</h1><p>${cfg.blurb}</p><button id="enter">Enter scene</button></div></div>`
  );

  const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: !!cfg.debug });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = cfg.exposure ?? 1;
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  const canvas = renderer.domElement;
  canvas.tabIndex = 0;
  canvas.setAttribute('aria-label', cfg.title + ' 3D view');
  document.body.prepend(canvas);

  const scene = new THREE.Scene();
  scene.fog = new THREE.Fog(cfg.fog.color, cfg.fog.near, cfg.fog.far);
  const camera = new THREE.PerspectiveCamera(68, 1, 0.08, 400);
  camera.rotation.order = 'YXZ';

  // sky dome + image-based ambient light derived from it
  const sunDir = new THREE.Vector3(...cfg.sun.dir).normalize();
  const skyMat = new THREE.ShaderMaterial({
    side: THREE.BackSide,
    depthWrite: false,
    fog: false,
    uniforms: {
      top: { value: new THREE.Color(cfg.sky.top) },
      horizon: { value: new THREE.Color(cfg.sky.horizon) },
      ground: { value: new THREE.Color(cfg.sky.ground) },
      sunDir: { value: sunDir },
      sunColor: { value: new THREE.Color(cfg.sky.sun) },
      cloudColor: { value: new THREE.Color(cfg.sky.cloud ?? 0xffffff) },
      cloudAmount: { value: cfg.sky.cloudAmount ?? 0 },
    },
    vertexShader: 'varying vec3 vDir; void main(){ vDir = position; gl_Position = projectionMatrix * modelViewMatrix * vec4(position,1.0); }',
    fragmentShader: `varying vec3 vDir; uniform vec3 top, horizon, ground, sunDir, sunColor, cloudColor; uniform float cloudAmount;
      float hash(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
      float noise(vec2 p){ vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
        return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y); }
      float fbm(vec2 p){ float v = 0.0, a = 0.5; for (int i = 0; i < 5; i++) { v += a * noise(p); p = p * 2.03 + 11.7; a *= 0.5; } return v; }
      void main(){ vec3 d = normalize(vDir); float h = d.y;
        vec3 c = mix(horizon, top, pow(max(h, 0.0), 0.55));
        if (h < 0.0) c = mix(horizon, ground, min(-h * 5.0, 1.0));
        float s = max(dot(d, sunDir), 0.0);
        if (h > 0.02) {
          vec2 q = d.xz / (h + 0.12) * 1.3;
          float cl = smoothstep(1.0 - cloudAmount, 1.0 - cloudAmount + 0.28, fbm(q + 3.0));
          float lit = 0.72 + 0.28 * fbm(q * 2.0 + 20.0) + 0.25 * pow(s, 6.0);
          c = mix(c, cloudColor * lit, cl * smoothstep(0.02, 0.22, h) * 0.9);
        }
        c += sunColor * (pow(s, 600.0) * 3.0 + pow(s, 24.0) * 0.35 + pow(s, 4.0) * 0.12);
        gl_FragColor = vec4(c, 1.0);
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
      }`,
  });
  const sky = new THREE.Mesh(new THREE.SphereGeometry(300, 32, 16), skyMat);
  scene.add(sky);
  const pmrem = new THREE.PMREMGenerator(renderer);
  const envScene = new THREE.Scene();
  envScene.add(new THREE.Mesh(sky.geometry, skyMat));
  scene.environment = pmrem.fromScene(envScene, 0.04).texture;
  scene.environmentIntensity = cfg.envIntensity ?? 0.6;

  const hemi = new THREE.HemisphereLight(cfg.hemi.sky, cfg.hemi.ground, cfg.hemi.intensity);
  scene.add(hemi);
  const sun = new THREE.DirectionalLight(cfg.sun.color, cfg.sun.intensity);
  sun.position.copy(sunDir).multiplyScalar(40);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  const sc = sun.shadow.camera;
  sc.left = sc.bottom = -18;
  sc.right = sc.top = 18;
  sc.near = 1;
  sc.far = 110;
  sun.shadow.bias = -0.0005;
  sun.shadow.normalBias = 0.04;
  scene.add(sun, sun.target);

  // ---- colliders (XZ plane)
  const colliders = [];
  const player = { x: cfg.spawn.x, z: cfg.spawn.z, yaw: cfg.spawn.yaw, pitch: 0, r: 0.35, eye: 1.65, bob: 0 };
  const state = { mode: 'walk', entered: false, lastBump: null, bumps: 0, frames: 0 };
  const orbit = { az: 0.6, pol: 1.05, dist: cfg.orbit.dist, target: new THREE.Vector3(...cfg.orbit.target) };
  const keys = new Set();

  function resolve(px, pz) {
    let hit = null;
    for (let it = 0; it < 3; it++) {
      for (const c of colliders) {
        if (c.kind === 'circle') {
          const dx = px - c.x, dz = pz - c.z, min = c.r + player.r, d2 = dx * dx + dz * dz;
          if (d2 < min * min) {
            const d = Math.sqrt(d2) || 1e-4;
            px = c.x + (dx / d) * min;
            pz = c.z + (dz / d) * min;
            hit = c.name;
          }
        } else {
          const dx = px - c.x, dz = pz - c.z;
          const lx = dx * c.cos - dz * c.sin, lz = dx * c.sin + dz * c.cos;
          const qx = Math.max(-c.hw, Math.min(c.hw, lx)), qz = Math.max(-c.hd, Math.min(c.hd, lz));
          let nx = lx - qx, nz = lz - qz;
          const d2 = nx * nx + nz * nz;
          if (d2 >= player.r * player.r) continue;
          let ox, oz;
          if (d2 > 1e-10) {
            const d = Math.sqrt(d2);
            ox = qx + (nx / d) * player.r;
            oz = qz + (nz / d) * player.r;
          } else {
            const ex = c.hw - Math.abs(lx), ez = c.hd - Math.abs(lz);
            if (ex < ez) { ox = Math.sign(lx || 1) * (c.hw + player.r); oz = lz; }
            else { ox = lx; oz = Math.sign(lz || 1) * (c.hd + player.r); }
          }
          px = c.x + ox * c.cos + oz * c.sin;
          pz = c.z - ox * c.sin + oz * c.cos;
          hit = c.name;
        }
      }
    }
    const b = cfg.bounds;
    const cx = Math.max(b.minX, Math.min(b.maxX, px)), cz = Math.max(b.minZ, Math.min(b.maxZ, pz));
    if (cx !== px || cz !== pz) hit = hit || 'Edge of the scene';
    return [cx, cz, hit];
  }

  const world = {
    THREE, scene, camera, renderer, player, state, orbit, colliders, sun, hemi,
    updaters: [],
    add(...objs) {
      for (const o of objs) {
        o.traverse((m) => {
          if (m.isMesh && !m.userData.noShadow) {
            m.castShadow = true;
            m.receiveShadow = true;
          }
        });
        scene.add(o);
      }
      return objs[0];
    },
    circle(name, x, z, r) {
      colliders.push({ kind: 'circle', name, x, z, r });
    },
    /** Oriented box collider: full width (local X) and depth (local Z), rotation as Object3D.rotation.y. */
    obb(name, x, z, w, d, rot = 0) {
      colliders.push({ kind: 'box', name, x, z, hw: w / 2, hd: d / 2, cos: Math.cos(rot), sin: Math.sin(rot) });
    },
    onUpdate(fn) {
      world.updaters.push(fn);
    },
    setMode,
  };

  // ---- UI
  const $ = (id) => document.getElementById(id);
  const hint = $('hint'), toast = $('toast'), cross = $('cross');
  const WALK_HINT = '<kbd>W</kbd><kbd>A</kbd><kbd>S</kbd><kbd>D</kbd> move · mouse or <kbd>←</kbd><kbd>→</kbd> look · <kbd>Shift</kbd> run · <kbd>Esc</kbd> release mouse';
  const ORBIT_HINT = 'Drag to rotate · scroll to zoom · press <kbd>Walk</kbd> to explore on foot';
  const presets = { persp: [0.62, 1.02], front: [0, 1.25], side: [Math.PI / 2, 1.25], top: [0, 0.02] };
  let toastTimer = 0;

  function setMode(v) {
    state.view = v;
    state.mode = v === 'walk' ? 'walk' : 'orbit';
    if (presets[v]) {
      [orbit.az, orbit.pol] = presets[v];
      orbit.dist = v === 'top' ? cfg.orbit.dist * 1.15 : cfg.orbit.dist;
    }
    if (state.mode === 'orbit' && document.pointerLockElement) document.exitPointerLock();
    document.querySelectorAll('#hud button').forEach((b) => b.setAttribute('aria-pressed', b.dataset.v === v));
    hint.innerHTML = state.mode === 'walk' ? WALK_HINT : ORBIT_HINT;
    cross.style.display = state.mode === 'walk' ? 'block' : 'none';
  }
  document.querySelectorAll('#hud button').forEach((b) =>
    b.addEventListener('click', () => {
      setMode(b.dataset.v);
      canvas.focus();
    })
  );

  function lockPointer() {
    if (state.mode !== 'walk' || document.pointerLockElement) return;
    try {
      const p = canvas.requestPointerLock();
      if (p && p.catch) p.catch(() => {});
    } catch (e) {
      /* sandboxed frame: drag-to-look still works */
    }
  }
  $('enter').addEventListener('click', () => {
    $('start').style.display = 'none';
    state.entered = true;
    setMode('walk');
    canvas.focus();
    lockPointer();
  });

  let dragging = false;
  canvas.addEventListener('pointerdown', (e) => {
    canvas.focus();
    dragging = true;
    try { canvas.setPointerCapture(e.pointerId); } catch (err) { /* synthetic events have no capture */ }
    if (state.mode === 'walk' && e.pointerType === 'mouse') lockPointer();
  });
  window.addEventListener('pointerup', () => (dragging = false));
  window.addEventListener('pointermove', (e) => {
    const locked = document.pointerLockElement === canvas;
    if (!locked && !dragging) return;
    const k = locked ? 0.0022 : 0.0045;
    if (state.mode === 'walk') {
      player.yaw -= e.movementX * k;
      player.pitch = Math.max(-1.3, Math.min(1.3, player.pitch - e.movementY * k));
    } else if (dragging) {
      orbit.az -= e.movementX * 0.006;
      orbit.pol = Math.max(0.02, Math.min(1.5, orbit.pol - e.movementY * 0.006));
    }
  });
  canvas.addEventListener('wheel', (e) => {
    if (state.mode !== 'orbit') return;
    e.preventDefault();
    orbit.dist = Math.max(4, Math.min(45, orbit.dist * (1 + Math.sign(e.deltaY) * 0.08)));
  }, { passive: false });
  const MOVE_KEYS = ['KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'KeyQ', 'KeyE', 'ShiftLeft', 'ShiftRight'];
  window.addEventListener('keydown', (e) => {
    if (!state.entered) return;
    if (MOVE_KEYS.includes(e.code)) {
      keys.add(e.code);
      if (e.code.startsWith('Arrow')) e.preventDefault();
    }
  });
  window.addEventListener('keyup', (e) => keys.delete(e.code));
  window.addEventListener('blur', () => keys.clear());

  function resize() {
    const w = window.innerWidth, h = window.innerHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', resize);
  resize();

  // ---- simulation
  function step(dt) {
    if (state.mode === 'walk') {
      const turn = (keys.has('ArrowLeft') || keys.has('KeyQ') ? 1 : 0) - (keys.has('ArrowRight') || keys.has('KeyE') ? 1 : 0);
      player.yaw += turn * 1.9 * dt;
      const f = (keys.has('KeyW') || keys.has('ArrowUp') ? 1 : 0) - (keys.has('KeyS') || keys.has('ArrowDown') ? 1 : 0);
      const s = (keys.has('KeyD') ? 1 : 0) - (keys.has('KeyA') ? 1 : 0);
      let moved = 0;
      if (f || s) {
        const speed = (keys.has('ShiftLeft') || keys.has('ShiftRight') ? 5.2 : 2.8) / Math.hypot(f, s);
        const sin = Math.sin(player.yaw), cos = Math.cos(player.yaw);
        const wantX = player.x + (-sin * f + cos * s) * speed * dt;
        const wantZ = player.z + (-cos * f - sin * s) * speed * dt;
        const [nx, nz, hit] = resolve(wantX, wantZ);
        moved = Math.hypot(nx - player.x, nz - player.z);
        player.x = nx;
        player.z = nz;
        if (hit) {
          if (state.lastBump !== hit || performance.now() - toastTimer > 1500) {
            state.bumps++;
            toast.textContent = hit;
            toast.classList.add('on');
          }
          state.lastBump = hit;
          toastTimer = performance.now();
        }
      }
      if (performance.now() - toastTimer > 1100) toast.classList.remove('on');
      player.bob += moved * 2.2;
      camera.position.set(player.x, player.eye + Math.sin(player.bob) * 0.025, player.z);
      camera.rotation.set(player.pitch, player.yaw, 0);
    } else {
      const sp = Math.sin(orbit.pol);
      camera.position.set(
        orbit.target.x + orbit.dist * sp * Math.sin(orbit.az),
        orbit.target.y + orbit.dist * Math.cos(orbit.pol),
        orbit.target.z + orbit.dist * sp * Math.cos(orbit.az)
      );
      camera.lookAt(orbit.target);
    }
  }
  world.step = step;
  world.resolve = resolve;

  let last = performance.now(), t = 0;
  world.start = () => {
    setMode('persp');
    renderer.setAnimationLoop(() => {
      const now = performance.now();
      const dt = Math.min((now - last) / 1000, 0.05);
      last = now;
      t += dt;
      step(dt);
      for (const u of world.updaters) u(t, dt);
      renderer.render(scene, camera);
      state.frames++;
    });
  };
  window.__world = world;
  return world;
}

// ---------------------------------------------------------------- shared props

export function tree(kind, x, z, scale, mats, seed) {
  const r = rng(seed);
  const g = new THREE.Group();
  g.position.set(x, 0, z);
  g.rotation.y = r() * 6.28;
  g.scale.setScalar(scale);
  if (kind === 'pine') {
    g.add(at(cyl(0.12, 0.22, 2.2, mats.bark, 8, 1, 2), 0, 1.1, 0));
    let y = 1.5, rad = 1.7;
    for (let i = 0; i < 5; i++) {
      const h = 1.7 - i * 0.12;
      const cone = new THREE.Mesh(new THREE.ConeGeometry(rad, h, 9), mats.leaf);
      const uv = cone.geometry.attributes.uv;
      for (let k = 0; k < uv.count; k++) uv.setXY(k, uv.getX(k) * 4, uv.getY(k) * 1.5);
      g.add(at(cone, 0, y + h / 2, 0, 0, r() * 3, 0));
      y += h * 0.52;
      rad *= 0.78;
    }
  } else {
    g.add(at(cyl(0.16, 0.26, 2.6, mats.bark, 8, 1, 2), 0, 1.3, 0));
    g.add(rod(new THREE.Vector3(0, 2.3, 0), new THREE.Vector3(0.7, 3.4, 0.3), 0.09, mats.bark));
    g.add(rod(new THREE.Vector3(0, 2.2, 0), new THREE.Vector3(-0.6, 3.3, -0.4), 0.09, mats.bark));
    for (let i = 0; i < 7; i++) {
      const rad = 1.0 + r() * 0.6, a = r() * 6, b = r() * 6, c = r() * 6;
      const geo = new THREE.SphereGeometry(rad, 14, 10);
      const p = geo.attributes.position, uv = geo.attributes.uv;
      for (let k = 0; k < p.count; k++) {
        const x = p.getX(k), y = p.getY(k), z = p.getZ(k);
        const f = 1 + 0.16 * Math.sin(x * 3.1 + a) * Math.cos(y * 2.7 + b) + 0.1 * Math.sin(z * 4.3 + c);
        p.setXYZ(k, x * f, y * f * 0.85, z * f);
        uv.setXY(k, uv.getX(k) * 4, uv.getY(k) * 2);
      }
      geo.computeVertexNormals();
      const ang = (i / 7) * 6.28, off = i === 0 ? 0 : 0.9 + r() * 0.5;
      g.add(at(new THREE.Mesh(geo, mats.leaf), Math.cos(ang) * off, 3.5 + r() * 1.2 + (i === 0 ? 0.8 : 0), Math.sin(ang) * off));
    }
  }
  return g;
}

/** Additive sprite fire with rising smoke; returns { object, update }. */
export function fire(scale = 1) {
  const g = new THREE.Group();
  const glow = tex.glow();
  const flames = [], smoke = [];
  const r = rng(77);
  for (let i = 0; i < 16; i++) {
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: glow, color: 0xff8a2a, blending: THREE.AdditiveBlending, depthWrite: false, transparent: true }));
    s.userData = { o: r(), sx: (r() - 0.5) * 0.3, sz: (r() - 0.5) * 0.3, sp: 0.8 + r() * 0.6 };
    flames.push(s);
    g.add(s);
  }
  for (let i = 0; i < 7; i++) {
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: glow, color: 0x6b6660, depthWrite: false, transparent: true, opacity: 0.2 }));
    s.userData = { o: r(), sx: (r() - 0.5) * 0.25, sz: (r() - 0.5) * 0.25 };
    smoke.push(s);
    g.add(s);
  }
  const c1 = new THREE.Color(0xfff0a8), c2 = new THREE.Color(0xff5a14);
  return {
    object: g,
    update(t) {
      for (const s of flames) {
        const u = (t * s.userData.sp + s.userData.o) % 1;
        s.position.set(s.userData.sx * (1 - u) + Math.sin(t * 5 + s.userData.o * 20) * 0.03, 0.18 + u * 0.95 * scale, s.userData.sz * (1 - u));
        const k = (0.55 - u * 0.4) * scale;
        s.scale.set(k, k * 1.5, 1);
        s.material.color.copy(c1).lerp(c2, u);
        s.material.opacity = (1 - u) * 0.9;
      }
      for (const s of smoke) {
        const u = (t * 0.22 + s.userData.o) % 1;
        s.position.set(s.userData.sx + u * 0.5, 1.0 + u * 3.2, s.userData.sz + Math.sin(t + s.userData.o * 9) * 0.1);
        const k = 0.5 + u * 1.6;
        s.scale.set(k, k, 1);
        s.material.opacity = 0.22 * Math.sin(u * Math.PI);
      }
    },
  };
}

/** Crate built from planks with corner slats. */
export function crate(size, mats) {
  const g = new THREE.Group();
  g.add(at(box(size, size, size, mats.plank, 1.6), 0, size / 2, 0));
  const t = 0.045, e = size / 2;
  for (const sx of [-1, 1])
    for (const sz of [-1, 1]) g.add(at(box(t * 1.6, size + 0.01, t * 1.6, mats.frame), sx * (e - t * 0.4), size / 2, sz * (e - t * 0.4)));
  for (const y of [t / 2, size - t / 2])
    for (const s of [-1, 1]) {
      g.add(at(box(size + 0.02, t, t * 1.4, mats.frame), 0, y, s * (e - t * 0.3)));
      g.add(at(box(t * 1.4, t, size + 0.02, mats.frame), s * (e - t * 0.3), y, 0));
    }
  return g;
}

/** Ground pad: the only thing under the target objects. Top surface at y = 0. */
export function pad(minX, maxX, minZ, maxZ, mat, uv) {
  const p = box(maxX - minX, 0.4, maxZ - minZ, mat, uv);
  p.position.set((minX + maxX) / 2, -0.2, (minZ + maxZ) / 2);
  return p;
}

/** Parasol whose pole runs from the ground up through whatever stands at its position. */
export function parasol(fabricMat, poleMat, canopyY, radius) {
  const g = new THREE.Group();
  g.add(at(cyl(0.028, 0.028, canopyY + 0.42, poleMat, 10, 1, 3), 0, (canopyY + 0.42) / 2, 0));
  const cone = new THREE.Mesh(new THREE.ConeGeometry(radius, 0.4, 8, 1, true), fabricMat);
  const uv = cone.geometry.attributes.uv;
  for (let k = 0; k < uv.count; k++) uv.setXY(k, uv.getX(k) * 4, uv.getY(k));
  g.add(at(cone, 0, canopyY + 0.2, 0));
  for (let i = 0; i < 8; i++) {
    const a = (i / 8) * Math.PI * 2 + Math.PI / 8;
    g.add(rod(new THREE.Vector3(0, canopyY - 0.25, 0), new THREE.Vector3(Math.sin(a) * radius * 0.6, canopyY + 0.14, Math.cos(a) * radius * 0.6), 0.008, poleMat, 5));
  }
  g.add(at(new THREE.Mesh(new THREE.SphereGeometry(0.04, 10, 8), poleMat), 0, canopyY + 0.43, 0));
  return g;
}
