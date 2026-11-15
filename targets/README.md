# Target scenes X and Y

Two matched target scenes for the user study, built with three.js as self-contained pages.

- `dist/scene-x-market.html`: market square (well, stall A, stall B, crates, sack, cart, parasol, barrel, bench, gate, signpost)
- `dist/scene-y-campsite.html`: campsite (campfire, tent A, tent B, logs, bucket, table, parasol, cooler, log seat, trail arch, lantern)

Open a page in a browser. **Enter scene** starts first-person walking (WASD, mouse or arrow keys,
Shift to run). The toolbar switches to the fixed views (Perspective, Front, Side, Top), which can be
dragged to rotate and scrolled to zoom.

Layout: metres, +X right, +Z towards the front camera. Object positions are the `LAYOUT` constant
at the top of `src/market.js` and `src/camp.js` (also `window.__layout` in the page). Y is X rotated
by 90 degrees: stalls/tents 3.5 m from the centre object and facing it, a 1.5 m clear corridor from
the centre object to the gate/arch, a prop touching A with a prop on top, a cart/table behind A with a parasol pole through it, a prop in front of B, a seat 2.5 m from the centre on the exit line, a post 1 m beside the exit. No surrounding scenery: each scene is its objects on a ground pad.

## Build and test

    npm install
    node build.mjs        # writes dist/*.html
    node test.cjs         # headless Chrome: enter, walk, bump, turn around, collision sweep

`test.cjs` expects Playwright and Google Chrome; adjust the two paths at the top of the file.
