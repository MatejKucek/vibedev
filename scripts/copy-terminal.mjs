import { mkdir, copyFile } from 'node:fs/promises';
const target = 'src/Vibedev/Terminal/vendor';
await mkdir(target, { recursive: true });
for (const [source, name] of [
  ['@xterm/xterm/lib/xterm.js', 'xterm.js'],
  ['@xterm/xterm/css/xterm.css', 'xterm.css'],
  ['@xterm/xterm/LICENSE', 'xterm.LICENSE'],
  ['@xterm/addon-fit/lib/addon-fit.js', 'addon-fit.js'],
  ['@xterm/addon-fit/LICENSE', 'addon-fit.LICENSE']
]) await copyFile(`node_modules/${source}`, `${target}/${name}`);
