import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const args = ['serve', ...process.argv.slice(2)];
// Vite emits absolute /@fs/ imports containing a literal '%' for this checkout.
// Browsers decode those URLs, so bundle dependencies and omit the Vite client.
// Normal checkout paths retain Angular's default hot reload and prebundling.
if (fileURLToPath(new URL('..', import.meta.url)).includes('%')) {
  args.push('--prebundle=false', '--hmr=false', '--live-reload=false');
  console.info(
    'Checkout path contains %. Using bundled development mode; reload the browser after edits.',
  );
}
const child = spawn(process.execPath, [require.resolve('@angular/cli/bin/ng.js'), ...args], {
  stdio: 'inherit',
});
child.on('error', (error) => {
  console.error(error.message);
  process.exitCode = 1;
});
child.on('exit', (code) => {
  process.exitCode = code ?? 1;
});
process.on('SIGINT', () => child.kill('SIGINT'));
process.on('SIGTERM', () => child.kill('SIGTERM'));
