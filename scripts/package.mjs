import { copyFile, rename, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const source = new URL('src-tauri/target/release/flotsam-launcher.exe', root);
const staged = new URL('src-tauri/target/release/flotsam-launcher.staged.exe', root);
const target = new URL('浮游启动器.exe', root);

await copyFile(source, staged);
try {
  await rm(target, { force: true });
  await rename(staged, target);
} catch (err) {
  if (err && (err.code === 'EBUSY' || err.code === 'EPERM')) {
    const parked = new URL(`src-tauri/target/running-${Date.now()}.exe`, root);
    await rename(target, parked);
    await rename(staged, target);
    await rm(parked, { force: true }).catch(() => {});
  } else {
    throw err;
  }
}
console.log(`Packaged -> ${fileURLToPath(target)}`);
