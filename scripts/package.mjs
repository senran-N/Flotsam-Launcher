import { access, copyFile, mkdir, readFile, rename, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const pkg = JSON.parse(await readFile(new URL('package.json', root), 'utf8'));
const version = pkg.version || '0.0.1-pre';

const releaseDir = new URL('release/', root);
await mkdir(releaseDir, { recursive: true });

async function exists(url) {
  try {
    await access(url);
    return true;
  } catch {
    return false;
  }
}

async function stageAndMove(source, target, label) {
  const staged = new URL(`src-tauri/target/release/staged-${label}-${Date.now()}.exe`, root);
  await copyFile(source, staged);
  try {
    await rm(target, { force: true });
    await rename(staged, target);
  } catch (err) {
    if (err && (err.code === 'EBUSY' || err.code === 'EPERM')) {
      const parked = new URL(`src-tauri/target/running-${label}-${Date.now()}.exe`, root);
      await rename(target, parked);
      await rename(staged, target);
      await rm(parked, { force: true }).catch(() => {});
    } else {
      await rm(staged, { force: true }).catch(() => {});
      throw err;
    }
  }
  console.log(`Packaged -> ${fileURLToPath(target)}`);
}

const portableSource = new URL('src-tauri/target/release/flotsam-launcher.exe', root);
await stageAndMove(portableSource, new URL('浮游启动器.exe', root), 'root-portable');
await stageAndMove(
  portableSource,
  new URL(`release/Flotsam-Launcher_${version}_x64-portable.exe`, root),
  'release-portable'
);

const nsisSource = new URL(`src-tauri/target/release/bundle/nsis/浮游_${version}_x64-setup.exe`, root);
if (await exists(nsisSource)) {
  await stageAndMove(
    nsisSource,
    new URL(`release/Flotsam-Launcher_${version}_x64-setup.exe`, root),
    'release-setup-en'
  );
  await stageAndMove(
    nsisSource,
    new URL(`release/浮游_${version}_x64-setup.exe`, root),
    'release-setup-zh'
  );
}

