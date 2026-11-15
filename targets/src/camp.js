import { THREE, createWorld, tex, pbr, box, cyl, rod, at, faceTo, localXZ, fire, pad, parasol, rng } from './common.js';

// Layout in metres. +X = right, +Z = towards the front camera. Scene X rotated by 90 degrees.
export const LAYOUT = {
  campfire: { x: 0, z: 0 },
  tentA: { x: 0.91, z: -3.38 }, // 3.5 m from the fire
  tentB: { x: 0.91, z: 3.38 }, // 3.5 m from the fire
  cooler: { x: 1.3, z: 5.32 }, // in front of tent B as seen from the front camera
  arch: { x: 6.0, z: 0 }, // trail arch, centred beside the fire and facing it
  lantern: { x: 6.0, z: 2.3 }, // 1 m beside the arch's post
  seat: { x: -2.5, z: 0 }, // log seat 2.5 m from the fire, facing it, on the fire-arch line
  path: { halfWidth: 0.75 }, // 1.5 m corridor from the fire to the arch
};

const world = createWorld({
  title: 'Scene Y · Campsite',
  blurb: 'A campsite at golden hour: two tents around a campfire, logs, a table with a parasol, a cooler, a log seat, a trail arch and a lantern post. Walk through it or use the fixed views.',
  debug: new URLSearchParams(location.search).has('debug'),
  exposure: 1.12,
  envIntensity: 0.7,
  sky: { top: 0x2f4f8f, horizon: 0xf0b680, ground: 0x4a4438, sun: 0xffc98a },
  fog: { color: 0xe3b189, near: 38, far: 140 },
  hemi: { sky: 0xa9bde8, ground: 0x5a4c36, intensity: 0.95 },
  sun: { dir: [-0.55, 0.62, 0.52], color: 0xffc58c, intensity: 3.0 },
  spawn: { x: -4.2, z: 7.6, yaw: -0.5 },
  bounds: { minX: -9.1, maxX: 7.1, minZ: -7.6, maxZ: 8.6 },
  orbit: { dist: 12.5, target: [2.2, 0.9, 0.4] },
});

const m = {
  wood: pbr(tex.wood('#7a5733', 3, 11), { rough: 0.85 }),
  woodDark: pbr(tex.wood('#4f3520', 3, 12), { rough: 0.9 }),
  plank: pbr(tex.wood('#8f6c42', 3, 13), { rough: 0.9 }),
  char: new THREE.MeshStandardMaterial({ color: 0x1c1613, roughness: 1 }),
  bark: pbr(tex.bark(24), { rough: 1, bump: 2 }),
  rock: pbr(tex.stone(26), { rough: 0.95, bump: 1.5, color: 0xa9a59c }),
  iron: new THREE.MeshStandardMaterial({ color: 0x2b2a29, roughness: 0.45, metalness: 0.85 }),
  rope: new THREE.MeshStandardMaterial({ color: 0xc9b98f, roughness: 1 }),
  dark: new THREE.MeshStandardMaterial({ color: 0x12100d, roughness: 1, side: THREE.DoubleSide }),
};
const L = LAYOUT;

// ---------------------------------------------------------------- ground pad (no surrounding scenery)

{
  const g = tex.grass(31);
  const ground = pad(-9.5, 7.5, -8, 9, pbr(g, { rough: 1, bump: 0.6 }), 1 / 3);
  world.add(ground);
  ground.castShadow = false;

  const patch = (x, z, w, d, y) => {
    const t = tex.dirt(33);
    t.repeat.set(w / 1.5, d / 1.5);
    const mat = pbr(t, { rough: 1, bump: 1, transparent: true, alphaMap: tex.softDisc(0.45), depthWrite: false });
    const p = new THREE.Mesh(new THREE.PlaneGeometry(w, d), mat);
    p.rotation.x = -Math.PI / 2;
    p.position.set(x, y, z);
    world.add(p);
    p.castShadow = false;
  };
  patch(0, 0, 5.2, 5.2, 0.012);
  patch(3.1, 0, 7.4, 2.6, 0.016);
  patch(L.tentA.x, L.tentA.z, 4.2, 4.6, 0.02);
  patch(L.tentB.x, L.tentB.z, 4.2, 4.6, 0.02);
}

// ---------------------------------------------------------------- scored objects

// Campfire
{
  const g = new THREE.Group();
  g.name = 'Campfire';
  const r = rng(3);
  for (let i = 0; i < 11; i++) {
    const a = (i / 11) * Math.PI * 2;
    const geo = new THREE.IcosahedronGeometry(0.15 + r() * 0.05, 1);
    const p = geo.attributes.position;
    for (let k = 0; k < p.count; k++) p.setY(k, p.getY(k) * 0.7);
    g.add(at(new THREE.Mesh(geo, m.rock), Math.cos(a) * 0.6, 0.08, Math.sin(a) * 0.6, 0, r() * 6, 0));
  }
  const ember = new THREE.Mesh(new THREE.CircleGeometry(0.42, 20), new THREE.MeshStandardMaterial({ color: 0x2a140a, emissive: 0xff4a0c, emissiveIntensity: 1.6, roughness: 1 }));
  ember.userData.noShadow = true;
  g.add(at(ember, 0, 0.035, 0, -Math.PI / 2, 0, 0));
  for (let i = 0; i < 6; i++) {
    const a = (i / 6) * Math.PI * 2 + 0.3;
    g.add(rod(new THREE.Vector3(Math.cos(a) * 0.36, 0.05, Math.sin(a) * 0.36), new THREE.Vector3(Math.cos(a + 2.6) * 0.07, 0.6, Math.sin(a + 2.6) * 0.07), 0.055, i % 2 ? m.char : m.bark, 7));
  }
  const f = fire(1);
  f.object.traverse((o) => (o.userData.noShadow = true));
  g.add(f.object);
  const light = new THREE.PointLight(0xff7a2c, 30, 14, 2);
  light.position.set(0, 0.75, 0);
  light.castShadow = true;
  light.shadow.mapSize.set(1024, 1024);
  light.shadow.bias = -0.002;
  light.shadow.camera.near = 0.3;
  g.add(light);
  world.onUpdate((t) => {
    f.update(t);
    light.intensity = 30 * (0.86 + 0.09 * Math.sin(t * 11.3) + 0.07 * Math.sin(t * 23.7 + 1.3));
    light.position.x = Math.sin(t * 7.1) * 0.04;
  });
  g.position.set(L.campfire.x, 0, L.campfire.z);
  world.add(g);
  world.circle('Campfire', L.campfire.x, L.campfire.z, 0.78);
}

// A-frame ridge tent; local +Z is the entrance
const TENT = { W: 2.2, half: 1.3, H: 1.5 };
function tent(fabric) {
  const { W, half: Lh, H } = TENT;
  const g = new THREE.Group();
  const pos = [], uv = [], idx = [];
  const quad = (a, b, c, d, u, v) => {
    const i = pos.length / 3;
    pos.push(...a, ...b, ...c, ...d);
    uv.push(0, 0, u, 0, u, v, 0, v);
    idx.push(i, i + 1, i + 2, i, i + 2, i + 3);
  };
  const tri = (a, b, c) => {
    const i = pos.length / 3;
    pos.push(...a, ...b, ...c);
    for (const p of [a, b, c]) uv.push(p[0] + W / 2, p[1]);
    idx.push(i, i + 1, i + 2);
  };
  const slope = Math.hypot(W / 2, H), dw = 0.5, dh = 1.2;
  quad([-W / 2, 0, Lh], [-W / 2, 0, -Lh], [0, H, -Lh], [0, H, Lh], 2 * Lh, slope);
  quad([W / 2, 0, -Lh], [W / 2, 0, Lh], [0, H, Lh], [0, H, -Lh], 2 * Lh, slope);
  tri([W / 2, 0, -Lh], [-W / 2, 0, -Lh], [0, H, -Lh]);
  for (const s of [-1, 1]) {
    tri([s * W / 2, 0, Lh], [s * dw, 0, Lh], [0, dh, Lh]);
    tri([s * W / 2, 0, Lh], [0, dh, Lh], [0, H, Lh]);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  geo.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  geo.setIndex(idx);
  geo.computeVertexNormals();
  g.add(new THREE.Mesh(geo, fabric));

  const door = new THREE.BufferGeometry();
  door.setAttribute('position', new THREE.Float32BufferAttribute([-dw, 0, Lh - 0.03, dw, 0, Lh - 0.03, 0, dh, Lh - 0.03], 3));
  door.computeVertexNormals();
  g.add(new THREE.Mesh(door, m.dark));
  for (const s of [-1, 1]) g.add(rod(new THREE.Vector3(s * dw, 0.02, Lh + 0.03), new THREE.Vector3(s * 0.03, dh, Lh + 0.03), 0.04, fabric));
  g.add(at(box(W - 0.12, 0.02, 2 * Lh - 0.1, m.dark), 0, 0.012, 0));
  for (const s of [-1, 1]) {
    g.add(rod(new THREE.Vector3(0, 0, s * (Lh + 0.04)), new THREE.Vector3(0, H + 0.12, s * (Lh + 0.04)), 0.022, m.wood));
    g.add(rod(new THREE.Vector3(0, H + 0.05, s * (Lh + 0.04)), new THREE.Vector3(0, 0.02, s * (Lh + 0.85)), 0.008, m.rope, 5));
    g.add(rod(new THREE.Vector3(0, 0.14, s * (Lh + 0.9)), new THREE.Vector3(0, -0.04, s * (Lh + 0.82)), 0.014, m.wood, 5));
    for (const sx of [-1, 1]) g.add(rod(new THREE.Vector3(sx * (W / 2 + 0.05), 0.12, s * (Lh - 0.05)), new THREE.Vector3(sx * (W / 2 - 0.02), -0.04, s * (Lh - 0.05)), 0.014, m.wood, 5));
  }
  g.add(rod(new THREE.Vector3(0, H + 0.02, -Lh - 0.04), new THREE.Vector3(0, H + 0.02, Lh + 0.04), 0.02, m.wood));
  return g;
}

const fabA = pbr(tex.fabric('#5f6f45', '#5a6a41', 2, 51), { rough: 0.95, bump: 0.5, side: THREE.DoubleSide });
const fabB = pbr(tex.fabric('#b9743a', '#b26d34', 2, 52), { rough: 0.95, bump: 0.5, side: THREE.DoubleSide });
const rotA = faceTo(L.tentA.x, L.tentA.z, L.campfire.x, L.campfire.z);
const rotB = faceTo(L.tentB.x, L.tentB.z, L.campfire.x, L.campfire.z);
const tentA = tent(fabA);
tentA.name = 'Tent A';
tentA.position.set(L.tentA.x, 0, L.tentA.z);
tentA.rotation.y = rotA;
const tentB = tent(fabB);
tentB.name = 'Tent B';
tentB.position.set(L.tentB.x, 0, L.tentB.z);
tentB.rotation.y = rotB;
world.add(tentA, tentB);
world.obb('Tent A', L.tentA.x, L.tentA.z, TENT.W, TENT.half * 2, rotA);
world.obb('Tent B', L.tentB.x, L.tentB.z, TENT.W, TENT.half * 2, rotB);

// Log pile: touches the left side of tent A
{
  const g = new THREE.Group();
  g.name = 'Logs';
  const ends = new THREE.MeshStandardMaterial({ map: tex.rings(61), roughness: 0.9 });
  const r = rng(6);
  const log = (x, y) => {
    const len = 1.05 + r() * 0.12;
    const l = new THREE.Mesh(new THREE.CylinderGeometry(0.13, 0.13, len, 12), [m.bark, ends, ends]);
    g.add(at(l, x, y, (r() - 0.5) * 0.06, Math.PI / 2, 0, r() * 3));
  };
  [-0.26, 0, 0.26].forEach((x) => log(x, 0.13));
  [-0.13, 0.13].forEach((x) => log(x, 0.355));
  log(0, 0.58);
  const [cx, cz] = localXZ(L.tentA.x, L.tentA.z, rotA, -(TENT.W / 2 + 0.39), 0.2);
  g.position.set(cx, 0, cz);
  g.rotation.y = rotA;
  world.add(g);
  world.obb('Logs', cx, cz, 0.78, 1.15, rotA);
  LAYOUT.logs = { x: cx, z: cz };

  // Bucket resting on top of the log pile
  const b = new THREE.Group();
  b.name = 'Bucket';
  const tin = new THREE.MeshStandardMaterial({ color: 0x9aa0a3, roughness: 0.35, metalness: 0.9, side: THREE.DoubleSide });
  b.add(at(new THREE.Mesh(new THREE.CylinderGeometry(0.15, 0.115, 0.26, 18, 1, true), tin), 0, 0.13, 0));
  b.add(at(new THREE.Mesh(new THREE.CircleGeometry(0.115, 18), tin), 0, 0.004, 0, -Math.PI / 2, 0, 0));
  b.add(at(new THREE.Mesh(new THREE.TorusGeometry(0.15, 0.008, 6, 20, Math.PI), m.iron), 0, 0.26, 0));
  b.position.set(cx, 0.71, cz);
  world.add(b);
  LAYOUT.bucket = { x: cx, y: 0.71, z: cz };
}

// Table behind tent A, parallel to its back with a 0.5 m gap; a parasol pole passes through its top
{
  const g = new THREE.Group();
  g.name = 'Table';
  g.add(at(box(1.4, 0.05, 0.7, m.plank), 0, 0.72, 0));
  for (const sx of [-1, 1]) {
    for (const sz of [-1, 1]) g.add(rod(new THREE.Vector3(sx * 0.6, 0.7, sz * 0.12), new THREE.Vector3(sx * 0.6, 0, sz * -0.3), 0.022, m.iron, 6));
    g.add(at(box(0.05, 0.04, 0.6, m.wood), sx * 0.6, 0.68, 0));
  }
  const [cx, cz] = localXZ(L.tentA.x, L.tentA.z, rotA, 0, -(TENT.half + 0.5 + 0.35));
  g.position.set(cx, 0, cz);
  g.rotation.y = rotA;
  world.add(g);
  world.obb('Table', cx, cz, 1.4, 0.7, rotA);
  LAYOUT.table = { x: cx, z: cz };

  const canopy = pbr(tex.fabric('#c9a23a', '#e9e2cf', 4, 82), { rough: 0.95, bump: 0.4, side: THREE.DoubleSide });
  const ps = parasol(canopy, m.wood, 2.05, 0.9);
  ps.name = 'Parasol';
  ps.position.set(cx, 0, cz);
  world.add(ps);
  LAYOUT.parasol = { x: cx, z: cz };
}

// Log seat: 2.5 m from the fire, facing it, in line with the fire and the arch
{
  const g = new THREE.Group();
  g.name = 'Log seat';
  const ends = new THREE.MeshStandardMaterial({ map: tex.rings(62), roughness: 0.9 });
  const seat = new THREE.Mesh(new THREE.CylinderGeometry(0.2, 0.2, 1.5, 14), [m.bark, ends, ends]);
  seat.scale.set(1, 1, 0.75);
  g.add(at(seat, 0, 0.42, 0, Math.PI / 2, 0, 0));
  for (const s of [-1, 1]) g.add(at(new THREE.Mesh(new THREE.CylinderGeometry(0.17, 0.19, 0.28, 12), [m.bark, ends, ends]), 0, 0.14, s * 0.5));
  g.position.set(L.seat.x, 0, L.seat.z);
  world.add(g);
  world.obb('Log seat', L.seat.x, L.seat.z, 0.42, 1.5);
}

// Cooler: about a third of the tent's height, partly hiding tent B from the front camera
{
  const g = new THREE.Group();
  g.name = 'Cooler';
  const body = new THREE.MeshStandardMaterial({ color: 0x2f6f9c, roughness: 0.42 });
  const lid = new THREE.MeshStandardMaterial({ color: 0xe9e6de, roughness: 0.5 });
  g.add(at(box(0.62, 0.36, 0.38, body), 0, 0.18, 0));
  g.add(at(box(0.65, 0.1, 0.41, lid), 0, 0.41, 0));
  g.add(at(box(0.08, 0.06, 0.02, lid), 0, 0.3, 0.2));
  for (const s of [-1, 1]) g.add(rod(new THREE.Vector3(s * 0.31, 0.3, 0), new THREE.Vector3(s * 0.31, 0.52, 0), 0.012, lid, 6));
  g.add(rod(new THREE.Vector3(-0.31, 0.52, 0), new THREE.Vector3(0.31, 0.52, 0), 0.016, lid, 6));
  g.position.set(L.cooler.x, 0, L.cooler.z);
  g.rotation.y = 0.18;
  world.add(g);
  world.obb('Cooler', L.cooler.x, L.cooler.z, 0.66, 0.42, 0.18);
}

// Trail arch: centred beside the fire and facing it, at the end of the clear path
{
  const g = new THREE.Group();
  g.name = 'Trail arch';
  for (const s of [-1, 1]) {
    g.add(at(cyl(0.11, 0.14, 2.9, m.bark, 12, 1, 3), 0, 1.45, s * 1.15));
    g.add(rod(new THREE.Vector3(0, 2.0, s * 1.15), new THREE.Vector3(0, 2.62, s * 0.55), 0.05, m.wood));
  }
  g.add(at(cyl(0.1, 0.1, 3.0, m.bark, 12, 1, 3), 0, 2.7, 0, Math.PI / 2, 0, 0));
  const face = new THREE.MeshStandardMaterial({ map: tex.label('TRAIL'), roughness: 0.85 });
  const sign = new THREE.Mesh(new THREE.BoxGeometry(0.04, 0.3, 1.2), [face, face, m.wood, m.wood, m.wood, m.wood]);
  g.add(at(sign, 0, 2.3, 0));
  for (const s of [-1, 1]) g.add(rod(new THREE.Vector3(0, 2.62, s * 0.45), new THREE.Vector3(0, 2.45, s * 0.45), 0.008, m.iron, 5));
  g.position.set(L.arch.x, 0, L.arch.z);
  world.add(g);
  world.circle('Trail arch', L.arch.x, L.arch.z - 1.15, 0.14);
  world.circle('Trail arch', L.arch.x, L.arch.z + 1.15, 0.14);
}

// Lantern post beside the arch
{
  const g = new THREE.Group();
  g.name = 'Lantern';
  g.add(at(cyl(0.05, 0.065, 2.1, m.wood, 10, 1, 2), 0, 1.05, 0));
  g.add(rod(new THREE.Vector3(0, 1.98, 0), new THREE.Vector3(0, 1.98, -0.42), 0.022, m.iron, 6));
  g.add(rod(new THREE.Vector3(0, 1.6, 0), new THREE.Vector3(0, 1.98, -0.3), 0.016, m.iron, 6));
  g.add(rod(new THREE.Vector3(0, 1.98, -0.4), new THREE.Vector3(0, 1.84, -0.4), 0.008, m.iron, 5));
  const glass = new THREE.MeshStandardMaterial({ color: 0xffe2a8, emissive: 0xffb84d, emissiveIntensity: 3.2, roughness: 0.4 });
  const lamp = new THREE.Group();
  const core = box(0.12, 0.17, 0.12, glass);
  core.userData.noShadow = true;
  lamp.add(core);
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) lamp.add(at(box(0.016, 0.2, 0.016, m.iron), sx * 0.065, 0, sz * 0.065));
  lamp.add(at(box(0.16, 0.02, 0.16, m.iron), 0, -0.1, 0));
  lamp.add(at(new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.08, 4), m.iron), 0, 0.135, 0, 0, Math.PI / 4, 0));
  lamp.position.set(0, 1.72, -0.4);
  g.add(lamp);
  const light = new THREE.PointLight(0xffc070, 14, 9, 2);
  light.position.set(0, 1.7, -0.4);
  g.add(light);
  g.position.set(L.lantern.x, 0, L.lantern.z);
  world.add(g);
  world.circle('Lantern', L.lantern.x, L.lantern.z, 0.1);
}

window.__layout = LAYOUT;
world.start();
