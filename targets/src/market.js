import { THREE, createWorld, tex, pbr, box, cyl, rod, at, faceTo, localXZ, crate, pad, parasol, rng } from './common.js';

// Layout in metres. +X = right, +Z = towards the front camera. Frozen target values for scoring.
export const LAYOUT = {
  well: { x: 0, z: 0 },
  stallA: { x: -3.38, z: -0.91 }, // 3.5 m from the well
  stallB: { x: 3.38, z: -0.91 }, // 3.5 m from the well
  barrel: { x: 3.38, z: 0.95 }, // in front of stall B as seen from the front camera
  gate: { x: 0, z: -6 },
  signpost: { x: 2.5, z: -5.45 }, // 1 m right of the gate's right pillar
  bench: { x: 0, z: 2.5 }, // 2.5 m from the well, facing it, on the well-gate line
  path: { halfWidth: 0.75 }, // 1.5 m corridor from the well to the gate
};

const world = createWorld({
  title: 'Scene X · Market square',
  blurb: 'A small market square: two stalls around a well, a stack of crates, a keg, a gate and a signpost. Walk through it or use the fixed views.',
  debug: new URLSearchParams(location.search).has('debug'),
  exposure: 0.92,
  envIntensity: 0.55,
  sky: { top: 0x3b74c4, horizon: 0xc9dcec, ground: 0x77726a, sun: 0xfff2d8 },
  fog: { color: 0xc9dcec, near: 45, far: 170 },
  hemi: { sky: 0xc4dbff, ground: 0x6d6150, intensity: 0.6 },
  sun: { dir: [-0.5, 0.74, 0.5], color: 0xfff0d2, intensity: 3.3 },
  spawn: { x: -5.5, z: 6.5, yaw: -0.7 },
  bounds: { minX: -7.6, maxX: 7.6, minZ: -7.1, maxZ: 8.6 },
  orbit: { dist: 12.5, target: [0, 1.0, -1.6] },
});

const m = {
  wood: pbr(tex.wood('#7a5733', 3, 11), { rough: 0.85 }),
  woodDark: pbr(tex.wood('#56391f', 3, 12), { rough: 0.9 }),
  woodLight: pbr(tex.wood('#a27a4a', 4, 13), { rough: 0.8 }),
  plank: pbr(tex.wood('#a9834f', 4, 14), { rough: 0.85 }),
  stone: pbr(tex.stone(21), { rough: 0.95, bump: 2.2 }),
  stoneCap: pbr(tex.stone(22), { rough: 0.9, bump: 1.5, color: 0xd9d2c4 }),
  shingle: pbr(tex.shingle(23), { rough: 0.95, bump: 2 }),
  iron: new THREE.MeshStandardMaterial({ color: 0x2b2a29, roughness: 0.45, metalness: 0.85 }),
  rope: new THREE.MeshStandardMaterial({ color: 0xb59a6a, roughness: 1 }),
  water: new THREE.MeshStandardMaterial({ color: 0x1d3a3e, roughness: 0.08, metalness: 0.1 }),
  clay: new THREE.MeshStandardMaterial({ color: 0xa5583a, roughness: 0.7 }),
  bread: new THREE.MeshStandardMaterial({ color: 0xc58c4a, roughness: 0.9 }),
  wicker: pbr(tex.wood('#b08a52', 12, 15), { rough: 1 }),
};
m.frame = m.woodDark;

// ---------------------------------------------------------------- ground pad (no surrounding scenery)

{
  const c = tex.cobble(32);
  const ground = pad(-8, 8, -7.5, 9, pbr(c, { rough: 0.92, bump: 3 }), 0.5);
  world.add(ground);
  ground.castShadow = false;
}

// ---------------------------------------------------------------- scored objects

function stall(awning, goods) {
  const g = new THREE.Group();
  for (const sx of [-1, 1]) {
    g.add(at(box(0.1, 2.15, 0.1, m.wood), sx * 1.15, 1.075, 0.7));
    g.add(at(box(0.1, 2.55, 0.1, m.wood), sx * 1.15, 1.275, -0.7));
    g.add(at(box(0.04, 0.94, 1.4, m.woodLight), sx * 1.15, 0.5, 0));
    g.add(rod(new THREE.Vector3(sx * 1.15, 2.1, 0.7), new THREE.Vector3(sx * 1.15, 2.5, -0.7), 0.035, m.wood));
  }
  g.add(at(box(2.5, 0.06, 0.75, m.woodLight), 0, 1.0, 0.42));
  g.add(at(box(2.3, 0.94, 0.04, m.woodLight), 0, 0.5, 0.72));
  g.add(at(box(2.3, 0.8, 0.04, m.woodLight), 0, 0.42, -0.72));
  g.add(at(box(2.3, 0.05, 0.55, m.wood), 0, 0.84, -0.42));
  g.add(at(box(2.4, 0.08, 0.08, m.wood), 0, 2.12, 0.7));
  g.add(at(box(2.4, 0.08, 0.08, m.wood), 0, 2.52, -0.7));
  g.add(at(box(2.8, 0.03, 2.01, awning, 0.5), 0, 2.38, 0.125, 0.241, 0, 0));
  g.add(at(box(2.8, 0.2, 0.02, awning, 0.5), 0, 2.04, 1.1));

  const r = rng(goods === 'fruit' ? 41 : 42);
  if (goods === 'fruit') {
    const cols = [0xb3261e, 0xd98a1f, 0xd9c03a, 0x6f9a2c];
    for (let t = 0; t < 4; t++) {
      const x = -0.9 + t * 0.6;
      g.add(at(box(0.5, 0.1, 0.42, m.plank, 2), x, 1.08, 0.42, 0.12, 0, 0));
      const mat = new THREE.MeshStandardMaterial({ color: cols[t], roughness: 0.55 });
      for (let i = 0; i < 4; i++)
        for (let j = 0; j < 3; j++) {
          const f = new THREE.Mesh(new THREE.SphereGeometry(0.052, 12, 8), mat);
          g.add(at(f, x - 0.18 + i * 0.12 + (r() - 0.5) * 0.02, 1.17 + (1 - j) * -0.015 + r() * 0.01, 0.3 + j * 0.12));
        }
    }
  } else {
    for (let i = 0; i < 3; i++) {
      const x = -0.85 + i * 0.5;
      g.add(at(cyl(0.2, 0.16, 0.14, m.wicker, 14, 3, 1), x, 1.1, 0.42));
      for (let k = 0; k < 4; k++) {
        const loaf = new THREE.Mesh(new THREE.SphereGeometry(0.1, 12, 8), m.bread);
        loaf.scale.set(1.6, 0.6, 0.75);
        g.add(at(loaf, x + (r() - 0.5) * 0.12, 1.18 + k * 0.035, 0.42 + (r() - 0.5) * 0.1, 0, r() * 3, 0));
      }
    }
    const jug = [[0, 0], [0.09, 0], [0.14, 0.12], [0.13, 0.26], [0.06, 0.36], [0.075, 0.42], [0.06, 0.42]].map(([a, b]) => new THREE.Vector2(a, b));
    for (let i = 0; i < 3; i++) g.add(at(new THREE.Mesh(new THREE.LatheGeometry(jug, 16), m.clay), 0.75 + (i % 2) * 0.28, 1.03, 0.3 + i * 0.13));
  }
  const lamp = new THREE.PointLight(0xffd9a0, 2.2, 4.5, 2);
  lamp.position.set(0, 1.9, 0.1);
  g.add(lamp);
  return g;
}

const L = LAYOUT;

// Stalls face the well
const awnA = pbr(tex.fabric('#a8382b', '#e6dabb', 4, 51), { rough: 0.95, bump: 0.4, side: THREE.DoubleSide });
const awnB = pbr(tex.fabric('#2f5d7c', '#e6dabb', 4, 52), { rough: 0.95, bump: 0.4, side: THREE.DoubleSide });
const rotA = faceTo(L.stallA.x, L.stallA.z, L.well.x, L.well.z);
const rotB = faceTo(L.stallB.x, L.stallB.z, L.well.x, L.well.z);
const stallA = stall(awnA, 'fruit');
stallA.position.set(L.stallA.x, 0, L.stallA.z);
stallA.rotation.y = rotA;
stallA.name = 'Stall A';
const stallB = stall(awnB, 'bread');
stallB.position.set(L.stallB.x, 0, L.stallB.z);
stallB.rotation.y = rotB;
stallB.name = 'Stall B';
world.add(stallA, stallB);
world.obb('Stall A', L.stallA.x, L.stallA.z, 2.4, 1.5, rotA);
world.obb('Stall B', L.stallB.x, L.stallB.z, 2.4, 1.5, rotB);

// Well
{
  const g = new THREE.Group();
  g.name = 'Well';
  const profile = [[0.62, 0], [0.95, 0], [0.95, 0.74], [1.0, 0.77], [1.0, 0.88], [0.6, 0.88], [0.6, 0.8], [0.62, 0.77], [0.62, 0]].map(([a, b]) => new THREE.Vector2(a, b));
  const st = tex.stone(61);
  st.repeat.set(6, 1.6);
  g.add(new THREE.Mesh(new THREE.LatheGeometry(profile, 32), pbr(st, { rough: 0.95, bump: 2.2, side: THREE.DoubleSide })));
  const water = new THREE.Mesh(new THREE.CircleGeometry(0.62, 28), m.water);
  g.add(at(water, 0, 0.42, 0, -Math.PI / 2, 0, 0));
  for (const sx of [-1, 1]) g.add(at(box(0.13, 1.6, 0.13, m.wood), sx * 0.8, 1.68, 0));
  const axle = cyl(0.06, 0.06, 1.95, m.woodDark, 10, 1, 2);
  g.add(at(axle, 0.08, 2.0, 0, 0, 0, Math.PI / 2));
  g.add(at(cyl(0.1, 0.1, 0.5, m.rope, 14), 0, 2.0, 0, 0, 0, Math.PI / 2));
  g.add(rod(new THREE.Vector3(0, 1.95, 0.09), new THREE.Vector3(0, 1.25, 0.09), 0.012, m.rope));
  g.add(at(cyl(0.17, 0.13, 0.3, m.plank, 14, 2, 1), 0, 1.1, 0.09));
  g.add(at(new THREE.Mesh(new THREE.TorusGeometry(0.155, 0.01, 6, 18), m.iron), 0, 1.04, 0.09, Math.PI / 2, 0, 0));
  g.add(at(new THREE.Mesh(new THREE.TorusGeometry(0.168, 0.01, 6, 18), m.iron), 0, 1.2, 0.09, Math.PI / 2, 0, 0));
  g.add(rod(new THREE.Vector3(1.05, 2.0, 0), new THREE.Vector3(1.05, 1.75, 0), 0.02, m.iron));
  g.add(rod(new THREE.Vector3(1.05, 1.75, 0), new THREE.Vector3(1.25, 1.75, 0), 0.02, m.iron));
  for (const s of [-1, 1]) g.add(at(box(2.2, 0.05, 0.95, m.shingle), 0, 2.72, s * 0.37, s * 0.62, 0, 0));
  g.add(at(box(2.25, 0.07, 0.1, m.woodDark), 0, 2.99, 0));
  g.position.set(L.well.x, 0, L.well.z);
  world.add(g);
  world.circle('Well', L.well.x, L.well.z, 1.0);
}

// Crates touch the camera-facing side of stall A; a sack rests on top of them
{
  const g = new THREE.Group();
  g.name = 'Crates';
  const c1 = crate(0.6, m), c2 = crate(0.6, m), c3 = crate(0.5, m);
  c1.position.set(0, 0, -0.31);
  c2.position.set(0, 0, 0.31);
  c3.position.set(0.02, 0.6, -0.33);
  c3.rotation.y = 0.3;
  g.add(c1, c2, c3);
  const [cx, cz] = localXZ(L.stallA.x, L.stallA.z, rotA, -1.2 - 0.3, 0.05);
  g.position.set(cx, 0, cz);
  g.rotation.y = rotA;
  world.add(g);
  world.obb('Crates', cx, cz, 0.6, 1.22, rotA);
  LAYOUT.crates = { x: cx, z: cz };

  const sack = new THREE.Group();
  sack.name = 'Sack';
  const burlap = pbr(tex.fabric('#b39a6b', '#a88f62', 6, 81), { rough: 1, bump: 1.5 });
  const body = new THREE.Mesh(new THREE.SphereGeometry(0.2, 16, 12), burlap);
  body.scale.set(1.0, 0.95, 0.85);
  sack.add(at(body, 0, 0.19, 0));
  sack.add(at(new THREE.Mesh(new THREE.ConeGeometry(0.07, 0.14, 10), burlap), 0, 0.42, 0, Math.PI, 0, 0));
  sack.add(at(new THREE.Mesh(new THREE.TorusGeometry(0.035, 0.008, 6, 14), m.rope), 0, 0.37, 0, Math.PI / 2, 0, 0));
  const [sx, sz] = localXZ(L.stallA.x, L.stallA.z, rotA, -1.5, 0.05 + 0.31);
  sack.position.set(sx, 0.6, sz);
  world.add(sack);
  LAYOUT.sack = { x: sx, y: 0.6, z: sz };
}

// Cart behind stall A, parallel to its back with a 0.5 m gap; a parasol pole passes through its bed
{
  const g = new THREE.Group();
  g.name = 'Cart';
  g.add(at(box(1.8, 0.08, 0.9, m.plank), 0, 0.74, 0));
  for (const s of [-1, 1]) {
    g.add(at(box(1.8, 0.22, 0.04, m.woodLight), 0, 0.89, s * 0.43));
    g.add(at(box(0.04, 0.22, 0.9, m.woodLight), s * 0.88, 0.89, 0));
    const wheel = new THREE.Group();
    wheel.add(at(new THREE.Mesh(new THREE.TorusGeometry(0.42, 0.035, 8, 28), m.woodDark), 0, 0, 0));
    for (let i = 0; i < 4; i++) wheel.add(at(box(0.8, 0.045, 0.03, m.wood, 2), 0, 0, 0, 0, 0, (i / 4) * Math.PI));
    wheel.add(at(cyl(0.07, 0.07, 0.1, m.woodDark, 10), 0, 0, 0, Math.PI / 2, 0, 0));
    wheel.position.set(0.15, 0.455, s * 0.53);
    g.add(wheel);
    g.add(rod(new THREE.Vector3(-0.9, 0.72, s * 0.38), new THREE.Vector3(-1.75, 0.82, s * 0.38), 0.025, m.wood));
    g.add(at(box(0.07, 0.7, 0.07, m.wood), -0.75, 0.35, s * 0.36));
  }
  g.add(rod(new THREE.Vector3(0.15, 0.455, -0.53), new THREE.Vector3(0.15, 0.455, 0.53), 0.03, m.iron));
  const [cx, cz] = localXZ(L.stallA.x, L.stallA.z, rotA, 0, -0.75 - 0.5 - 0.45);
  g.position.set(cx, 0, cz);
  g.rotation.y = rotA;
  world.add(g);
  world.obb('Cart', ...localXZ(cx, cz, rotA, -0.4, 0), 2.7, 1.15, rotA);
  LAYOUT.cart = { x: cx, z: cz };

  const canopy = pbr(tex.fabric('#c9a23a', '#e6dabb', 4, 82), { rough: 0.95, bump: 0.4, side: THREE.DoubleSide });
  const ps = parasol(canopy, m.wood, 2.78, 0.75);
  ps.name = 'Parasol';
  ps.position.set(cx, 0, cz);
  world.add(ps);
  LAYOUT.parasol = { x: cx, z: cz };
}

// Bench: 2.5 m in front of the well, facing it, in line with the well and the gate
{
  const g = new THREE.Group();
  g.name = 'Bench';
  for (const z of [-0.13, 0, 0.13]) g.add(at(box(1.5, 0.045, 0.115, m.woodLight), 0, 0.45, z));
  for (const y of [0.68, 0.84]) g.add(at(box(1.5, 0.11, 0.04, m.woodLight), 0, y, 0.2, -0.12, 0, 0));
  for (const s of [-1, 1]) {
    g.add(at(box(0.06, 0.45, 0.4, m.iron), s * 0.62, 0.225, 0));
    g.add(at(box(0.05, 0.5, 0.05, m.iron), s * 0.62, 0.68, 0.2, -0.12, 0, 0));
  }
  g.position.set(L.bench.x, 0, L.bench.z);
  world.add(g);
  world.obb('Bench', L.bench.x, L.bench.z + 0.03, 1.5, 0.48);
}

// Keg (small barrel): half the counter height, partly hiding stall B from the front camera
{
  const g = new THREE.Group();
  g.name = 'Barrel';
  const pts = [[0, 0], [0.17, 0], [0.205, 0.12], [0.22, 0.25], [0.205, 0.38], [0.17, 0.5], [0, 0.5]].map(([a, b]) => new THREE.Vector2(a, b));
  const w = tex.wood('#7b5530', 4, 71);
  w.repeat.set(3, 1);
  g.add(new THREE.Mesh(new THREE.LatheGeometry(pts, 24), pbr(w, { rough: 0.8 })));
  for (const [y, rr] of [[0.07, 0.19], [0.17, 0.215], [0.33, 0.215], [0.43, 0.19]])
    g.add(at(new THREE.Mesh(new THREE.TorusGeometry(rr, 0.011, 6, 28), m.iron), 0, y, 0, Math.PI / 2, 0, 0));
  g.position.set(L.barrel.x, 0, L.barrel.z);
  world.add(g);
  world.circle('Barrel', L.barrel.x, L.barrel.z, 0.24);
}

// Gate: centred behind the well and facing it, leaves swung open towards the square
{
  const g = new THREE.Group();
  g.name = 'Gate';
  for (const sx of [-1, 1]) {
    g.add(at(box(0.5, 2.6, 0.5, m.stone), sx * 1.25, 1.3, 0));
    g.add(at(box(0.64, 0.12, 0.64, m.stoneCap), sx * 1.25, 2.66, 0));
    const cap = new THREE.Mesh(new THREE.ConeGeometry(0.4, 0.3, 4), m.stoneCap);
    g.add(at(cap, sx * 1.25, 2.87, 0, 0, Math.PI / 4, 0));
    const leaf = new THREE.Group();
    leaf.add(at(box(0.95, 2.0, 0.07, m.woodDark), sx * -0.475, 1.05, 0));
    for (const y of [0.45, 1.65]) leaf.add(at(box(0.9, 0.07, 0.085, m.iron), sx * -0.475, y, 0));
    leaf.add(at(new THREE.Mesh(new THREE.TorusGeometry(0.06, 0.012, 6, 14), m.iron), sx * -0.82, 1.05, 0.05));
    leaf.position.set(sx * 1.0, 0, 0.2);
    leaf.rotation.y = sx * 1.745;
    g.add(leaf);
  }
  g.add(at(box(3.1, 0.24, 0.3, m.woodDark), 0, 2.84, 0));
  g.add(at(box(2.0, 0.1, 0.5, m.stoneCap), 0, 0.02, 0));
  g.position.set(L.gate.x, 0, L.gate.z);
  world.add(g);
  world.obb('Gate', L.gate.x - 1.25, L.gate.z, 0.5, 0.5);
  world.obb('Gate', L.gate.x + 1.25, L.gate.z, 0.5, 0.5);
  world.obb('Gate', L.gate.x - 1.09, L.gate.z + 0.68, 0.14, 0.98);
  world.obb('Gate', L.gate.x + 1.09, L.gate.z + 0.68, 0.14, 0.98);
}

// Signpost beside the gate
{
  const g = new THREE.Group();
  g.name = 'Signpost';
  g.add(at(cyl(0.055, 0.07, 2.3, m.wood, 10, 1, 2), 0, 1.15, 0));
  g.add(at(new THREE.Mesh(new THREE.ConeGeometry(0.075, 0.12, 10), m.woodDark), 0, 2.36, 0));
  const board = (text, y, rot) => {
    const face = new THREE.MeshStandardMaterial({ map: tex.label(text), roughness: 0.85 });
    const b = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.22, 0.04), [m.wood, m.wood, m.wood, m.wood, face, face]);
    const h = new THREE.Group();
    h.add(at(b, 0.42, 0, 0.06));
    h.add(at(box(0.155, 0.155, 0.038, m.wood, 3), 0.87, 0, 0.06, 0, 0, Math.PI / 4));
    h.position.y = y;
    h.rotation.y = rot;
    g.add(h);
  };
  board('MARKET', 2.0, Math.PI * 0.82);
  board('HARBOUR', 1.68, -Math.PI * 0.42);
  g.position.set(L.signpost.x, 0, L.signpost.z);
  world.add(g);
  world.circle('Signpost', L.signpost.x, L.signpost.z, 0.12);
}

window.__layout = LAYOUT;
world.start();
