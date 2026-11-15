import { build } from 'esbuild';
import { writeFileSync, mkdirSync } from 'node:fs';
mkdirSync('dist', { recursive: true });
const pages = [['market', 'Scene X · Market square', 'scene-x-market.html'], ['camp', 'Scene Y · Campsite', 'scene-y-campsite.html']];
for (const [entry, title, out] of pages) {
  const r = await build({ entryPoints: [`src/${entry}.js`], bundle: true, minify: true, format: 'iife', write: false, target: 'es2020', legalComments: 'none' });
  const js = r.outputFiles[0].text;
  const html = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${title}</title>
</head>
<body>
<script>
${js}
</script>
</body>
</html>
`;
  writeFileSync(`dist/${out}`, html);
  console.log(out, (html.length / 1024).toFixed(0) + ' KB');
}
