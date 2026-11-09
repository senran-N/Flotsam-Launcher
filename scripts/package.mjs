import { copyFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const source = new URL('src-tauri/target/release/flotsam-launcher.exe', root);
const target = new URL('浮游启动器.exe', root);

await copyFile(source, target);
console.log(`Packaged -> ${fileURLToPath(target)}`);
