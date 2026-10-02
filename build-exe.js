'use strict';

/**
 * Bundles patch-porofessor.js into dist/PorofessorPatch.exe — a standalone
 * Windows exe that needs no Node.js installation.
 *
 * Recipe (Node's Single Executable Application feature):
 *   esbuild bundles the tool + deps -> a SEA blob -> injected into a copy of
 *   node.exe via postject -> version metadata stamped -> sha256 written.
 */

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { createHash } = require('crypto');

const ROOT = __dirname;
const DIST = path.join(ROOT, 'dist');
const OUT_EXE = path.join(DIST, 'PorofessorPatch.exe');
const VERSION = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8')).version;

function run(cmd, args) {
  console.log('>', path.basename(cmd), args.join(' '));
  execFileSync(cmd, args, { stdio: 'inherit', cwd: ROOT, windowsHide: true });
}

function stampVersionInfo() {
  const { NtExecutable, NtExecutableResource, Resource } = require('resedit');
  // node.exe is Authenticode-signed; resedit needs ignoreCert to touch it.
  const exe = NtExecutable.from(fs.readFileSync(OUT_EXE), { ignoreCert: true });
  const res = NtExecutableResource.from(exe);
  const vi = Resource.VersionInfo.fromEntries(res.entries)[0];
  if (!vi) throw new Error('no version info resource found');
  vi.setStringValues(
    { lang: 1033, codepage: 1200 },
    {
      FileDescription: 'Porofessor Patch Tool',
      FileVersion: VERSION,
      LegalCopyright: 'Open source (MIT)',
      OriginalFilename: 'PorofessorPatch.exe',
      ProductName: 'Porofessor Patch Tool',
      ProductVersion: VERSION,
    }
  );
  vi.outputToResourceEntries(res.entries);
  res.outputResource(exe);
  fs.writeFileSync(OUT_EXE, Buffer.from(exe.generate()));
  console.log('> stamped version info v' + VERSION);
}

// Replace node.exe's icon with the Porofessor one (icon.ico in the repo).
function stampIcon() {
  const { NtExecutable, NtExecutableResource, Resource, Data } = require('resedit');
  const iconFile = Data.IconFile.from(fs.readFileSync(path.join(ROOT, 'icon.ico')));
  const exe = NtExecutable.from(fs.readFileSync(OUT_EXE), { ignoreCert: true });
  const res = NtExecutableResource.from(exe);
  for (let i = res.entries.length - 1; i >= 0; i--) {
    const t = res.entries[i].type;
    if (t === 3 || t === 14) res.entries.splice(i, 1); // RT_ICON / RT_GROUP_ICON
  }
  Resource.IconGroupEntry.replaceIconsForResource(
    res.entries,
    1,
    1033,
    iconFile.icons.map((item) => item.data)
  );
  res.outputResource(exe);
  fs.writeFileSync(OUT_EXE, Buffer.from(exe.generate()));
  console.log(`> stamped icon (${iconFile.icons.length} sizes)`);
}

function main() {
  fs.mkdirSync(DIST, { recursive: true });

  run(process.execPath, [
    path.join(ROOT, 'node_modules', 'esbuild', 'bin', 'esbuild'),
    'patch-porofessor.js',
    '--bundle',
    '--platform=node',
    '--target=node20',
    '--format=cjs',
    '--outfile=' + path.join(DIST, 'bundle.cjs'),
  ]);

  fs.writeFileSync(
    path.join(DIST, 'sea-config.json'),
    JSON.stringify({
      main: path.join(DIST, 'bundle.cjs'),
      output: path.join(DIST, 'sea-prep.blob'),
      disableExperimentalSEAWarning: true,
    })
  );
  run(process.execPath, ['--experimental-sea-config', path.join(DIST, 'sea-config.json')]);

  fs.copyFileSync(process.execPath, OUT_EXE); // node.exe becomes the shell
  run(process.execPath, [
    path.join(ROOT, 'node_modules', 'postject', 'dist', 'cli.js'),
    OUT_EXE,
    'NODE_SEA_BLOB',
    path.join(DIST, 'sea-prep.blob'),
    '--sentinel-fuse',
    'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2',
  ]);

  try {
    stampVersionInfo(); // best effort: exe works without it
  } catch (e) {
    console.warn('! Could not stamp version info (exe still works):', e.message);
  }

  try {
    stampIcon();
  } catch (e) {
    console.warn('! Could not stamp icon (exe still works):', e.message);
  }

  const sha = createHash('sha256').update(fs.readFileSync(OUT_EXE)).digest('hex');
  fs.writeFileSync(OUT_EXE + '.sha256', sha + '  PorofessorPatch.exe\n');
  console.log(`\nBuilt ${OUT_EXE} (${(fs.statSync(OUT_EXE).size / 1048576).toFixed(1)} MB)`);
  console.log('sha256:', sha);
}

main();
