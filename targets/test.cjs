const { chromium } = require('/opt/npm-tools/node_modules/playwright');
const path = require('path');
const assert = (c, msg) => { if (!c) throw new Error('ASSERT: ' + msg); console.log('  ok  ' + msg); };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function run(file, tag, target) {
  console.log('== ' + file);
  const browser = await chromium.launch({ executablePath: '/usr/bin/google-chrome', args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
  const page = await browser.newPage({ viewport: { width: 1120, height: 630 } });
  const errors = [];
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') errors.push(m.type() + ': ' + m.text()); });
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.goto('file://' + path.resolve('dist', file) + '?debug');
  await page.waitForFunction(() => window.__world && window.__world.state.frames > 3, null, { timeout: 240000 });
  const S = () => page.evaluate(() => { const w = window.__world; return { ...w.state, x: w.player.x, z: w.player.z, yaw: w.player.yaw, fps: 0 }; });
  const shot = async (n) => { await wait(400); await page.screenshot({ path: `shots/${tag}-${n}.png` }); };

  await shot('0-start');
  for (const v of ['front', 'side', 'top', 'persp']) {
    await page.evaluate((v) => { document.getElementById('start').style.display = 'none'; window.__world.setMode(v); }, v);
    await shot('view-' + v);
  }
  await page.evaluate(() => { document.getElementById('start').style.display = 'flex'; });

  // 1 enter
  await page.click('#enter');
  let s = await S();
  assert(s.entered && s.mode === 'walk', 'entering the scene switches to walk mode');
  const p0 = s;
  await shot('1-entered');

  // 2 walk forward
  await page.keyboard.down('KeyW');
  await page.waitForFunction((p) => Math.hypot(window.__world.player.x - p.x, window.__world.player.z - p.z) > 1.5, p0, { timeout: 150000 });
  s = await S();
  assert(Math.hypot(s.x - p0.x, s.z - p0.z) > 1.5, `walked forward ${Math.hypot(s.x - p0.x, s.z - p0.z).toFixed(2)} m`);

  // 3 bump into the central object and get stopped by it
  await page.waitForFunction(() => window.__world.state.bumps > 0, null, { timeout: 240000 });
  await wait(1200);
  await page.keyboard.up('KeyW');
  s = await S();
  const c = await page.evaluate((n) => window.__world.colliders.find((c) => c.name === n), target);
  const d = Math.hypot(s.x - c.x, s.z - c.z);
  assert(s.lastBump === target, `bumped into "${s.lastBump}"`);
  assert(d >= c.r + 0.35 - 1e-3, `stopped outside the ${target} (distance ${d.toFixed(2)} m >= ${(c.r + 0.35).toFixed(2)} m)`);
  const toast = await page.evaluate(() => document.getElementById('toast').textContent);
  assert(toast === target, 'bump message shown: ' + toast);
  await shot('2-bump');

  // still blocked when pushing on
  await page.keyboard.down('KeyW'); await wait(800); await page.keyboard.up('KeyW');
  let s2 = await S();
  assert(Math.hypot(s2.x - c.x, s2.z - c.z) >= c.r + 0.35 - 1e-3, 'cannot push through the object');

  // 4 turn around: keyboard
  const yaw0 = s2.yaw;
  await page.keyboard.down('ArrowLeft');
  await page.waitForFunction((y) => Math.abs(window.__world.player.yaw - y) >= Math.PI, yaw0, { timeout: 150000 });
  await page.keyboard.up('ArrowLeft');
  s = await S();
  assert(Math.abs(s.yaw - yaw0) >= Math.PI - 0.01, `turned around with the keyboard (${((s.yaw - yaw0) * 180 / Math.PI).toFixed(0)} deg)`);
  await shot('3-turned');

  // look with the mouse (drag fallback or pointer lock)
  const yaw1 = s.yaw;
  await page.mouse.move(640, 360); await page.mouse.down(); await page.mouse.move(840, 360, { steps: 10 }); await page.mouse.up();
  s = await S();
  assert(Math.abs(s.yaw - yaw1) > 0.2, `mouse look changes heading (${((s.yaw - yaw1) * 180 / Math.PI).toFixed(0)} deg)`);
  await page.evaluate((y) => { window.__world.player.yaw = y; }, yaw1);

  // walk away after turning
  await page.keyboard.down('KeyW');
  await page.waitForFunction((a) => Math.hypot(window.__world.player.x - a.c.x, window.__world.player.z - a.c.z) > a.d + 2, { c, d }, { timeout: 150000 });
  await page.keyboard.up('KeyW');
  s = await S();
  assert(Math.hypot(s.x - c.x, s.z - c.z) > d + 2, 'walked away from the object after turning around');

  // 5 sweep: walk at every collider from 4 sides, never end up inside one, never leave bounds
  const sweep = await page.evaluate(() => {
    const w = window.__world, bad = [];
    for (const c of w.colliders) {
      for (let a = 0; a < 8; a++) {
        const ang = (a / 8) * Math.PI * 2;
        let x = c.x + Math.cos(ang) * 6, z = c.z + Math.sin(ang) * 6;
        for (let i = 0; i < 400; i++) {
          const dx = c.x - x, dz = c.z - z, dd = Math.hypot(dx, dz) || 1;
          [x, z] = w.resolve(x + (dx / dd) * 0.05, z + (dz / dd) * 0.05);
        }
        for (const k of w.colliders) {
          let inside;
          if (k.kind === 'circle') inside = Math.hypot(x - k.x, z - k.z) < k.r + 0.35 - 0.02;
          else { const ex = x - k.x, ez = z - k.z, lx = ex * k.cos - ez * k.sin, lz = ex * k.sin + ez * k.cos; const qx = Math.max(-k.hw, Math.min(k.hw, lx)), qz = Math.max(-k.hd, Math.min(k.hd, lz)); inside = Math.hypot(lx - qx, lz - qz) < 0.35 - 0.02; }
          if (inside) bad.push(`${c.name}@${a} ends inside ${k.name}`);
        }
      }
    }
    return { n: w.colliders.length, bad };
  });
  assert(sweep.bad.length === 0, `collision sweep over ${sweep.n} colliders x 8 directions: no penetration ${sweep.bad.slice(0, 3).join('; ')}`);

  const fps = await page.evaluate(async () => { const f0 = window.__world.state.frames; await new Promise((r) => setTimeout(r, 2000)); return (window.__world.state.frames - f0) / 2; });
  console.log('  software-rendered fps: ' + fps.toFixed(1));
  const info = await page.evaluate(() => window.__world.renderer.info.render);
  console.log('  draw calls ' + info.calls + ', triangles ' + info.triangles);
  console.log(errors.length ? '  console issues:\n   ' + [...new Set(errors)].join('\n   ') : '  no console errors or warnings');
  await browser.close();
  if (errors.some((e) => e.startsWith('pageerror') || e.startsWith('error'))) throw new Error('console errors');
}

(async () => {
  require('fs').mkdirSync('shots', { recursive: true });
  const which = process.argv[2];
  if (!which || which === 'x') await run('scene-x-market.html', 'x', 'Well');
  if (!which || which === 'y') await run('scene-y-campsite.html', 'y', 'Campfire');
  console.log('ALL PASSED');
})().catch((e) => { console.error(e.message); process.exit(1); });
