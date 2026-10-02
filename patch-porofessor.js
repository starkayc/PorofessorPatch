'use strict';

/**
 * Porofessor Standalone ad-removal patch: sets the premium localStorage
 * flags so the renderer stops appending showAdSpace=true (and the ad) to
 * the Home window's iframe URL. The flags live in Chromium's leveldb in
 * %APPDATA%; they are written by appending WriteBatch records to the leveldb
 * WAL — pure JS, zero dependencies, works from node or the packed exe.
 *
 * Usage: patch-porofessor [--patch | --check | --restore | --version]
 *        (no args opens the interactive menu)
 */

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const CONFIG = {
  app: {
    // Default install location; override with POROFESSOR_DIR if yours differs.
    installDir:
      process.env.POROFESSOR_DIR ||
      path.join(
        process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE || '.', 'AppData', 'Local'),
        'Programs',
        'Porofessor Standalone'
      ),
    exeName: 'Porofessor Standalone.exe',
    // Chromium's localStorage leveldb for the app's file:// pages.
    storageDir:
      process.env.POROFESSOR_STORAGE_DIR ||
      path.join(
        process.env.APPDATA || path.join(process.env.USERPROFILE || '.', 'AppData', 'Roaming'),
        'Porofessor Standalone',
        'Local Storage',
        'leveldb'
      ),
  },

  // isOWPremium is only READ by the standalone build (the code path that
  // would write it returns early when isElectron), so it survives forever.
  // isPremium may be rewritten from account data — isOWPremium covers that.
  keys: {
    on: {
      isOWPremium: '{"isPremium":true}',
      isPremium: 'true',
    },
    off: {
      isOWPremium: '{"isPremium":false}',
      isPremium: 'false',
    },
  },
};

// ============================================================================

function banner(text) {
  const box = '  +' + '-'.repeat(62) + '+';
  console.log('\n' + C.grn + box + C.reset);
  console.log(C.grn + '  | ' + padTo(text.toUpperCase(), 60) + ' |' + C.reset);
  console.log(C.grn + box + C.reset);
}
function info(text) {
  console.log('    ' + text);
}
class FailError extends Error {}
function fail(text) {
  throw new FailError(text);
}

// Chromium localStorage leveldb format:
//   key   = '_file://\x00\x01' + <localStorage key name>
//   value = 0x01 + <string bytes>
const lsKey = (name) => Buffer.concat([Buffer.from('_file://\x00\x01'), Buffer.from(name)]);
const lsValue = (s) => Buffer.concat([Buffer.from([0x01]), Buffer.from(s)]);

function runningPids() {
  try {
    const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq ' + CONFIG.app.exeName, '/NH', '/FO', 'CSV'], {
      encoding: 'utf8',
      windowsHide: true,
    });
    return out
      .split(/\r?\n/)
      .filter((line) => line.includes(CONFIG.app.exeName))
      .map((line) => line.split('","')[1])
      .filter(Boolean);
  } catch {
    return [];
  }
}

// Chromium holds the leveldb lock while the app runs — never write to a live
// database.
function assertAppClosed() {
  const pids = runningPids();
  if (pids.length) fail(`Close Porofessor first (still running: PID ${pids.join(', ')}).`);
}

// ============================================================================
// Minimal leveldb WAL access (pure JS).
//
// The leveldb log format (log_format.md): a sequence of records,
//   [uint32 crc][uint16 len][uint8 type][data]
// crc is the masked CRC32C of (type || data); type 1 = FULL. Records never
// cross 32 KiB block boundaries — a writer pads with zeros to the next block.
// A write to the database is a WriteBatch record:
//   [uint64 sequence][uint32 count][per entry: uint8 tag (1=put,0=del),
//    varint32 key len, key, varint32 value len, value]
// The current log file number and last sequence come from the MANIFEST file.
// ============================================================================

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0x82f63b78 ^ (c >>> 1) : c >>> 1;
    t[i] = c;
  }
  return t;
})();
function crc32c(buf) {
  let c = -1;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}
const maskCrc = (crc) => (((crc >>> 15) | (crc << 17)) + 0xa282ead8) >>> 0;

function varint(buf, off) {
  let value = 0;
  let shift = 0;
  for (let i = off; i < buf.length; i++) {
    const b = buf[i];
    value += (b & 0x7f) * 2 ** shift;
    if (!(b & 0x80)) return { value, end: i + 1 };
    shift += 7;
  }
  return { value, end: buf.length };
}

// Scan log-style records from buf (used for both WAL and MANIFEST files).
function scanRecords(buf) {
  const records = [];
  let off = 0;
  while (off + 7 <= buf.length) {
    // Skip zero padding up to the next 32 KiB block boundary.
    const boundary = (Math.floor(off / 32768) + 1) * 32768;
    if (buf[off] === 0) {
      let z = off;
      while (z < boundary && z < buf.length && buf[z] === 0) z++;
      if (z === Math.min(boundary, buf.length)) {
        off = z;
        continue;
      }
    }
    const crc = buf.readUInt32LE(off);
    const len = buf.readUInt16LE(off + 4);
    const type = buf[off + 6];
    if (off + 7 + len > buf.length) break;
    const data = buf.subarray(off + 7, off + 7 + len);
    if (maskCrc(crc32c(Buffer.concat([Buffer.from([type]), data]))) === crc && type === 1) {
      records.push(data);
    }
    off += 7 + len;
  }
  return records;
}

// All WriteBatch records in the WAL files, in write order. Records shorter
// than seq+count (12 bytes) are partial/trailing and skipped.
function walBatches(dbDir) {
  const batches = [];
  for (const f of fs.readdirSync(dbDir).filter((x) => x.endsWith('.log')).sort()) {
    for (const batch of scanRecords(fs.readFileSync(path.join(dbDir, f)))) {
      if (batch.length >= 12) batches.push(batch);
    }
  }
  return batches;
}

function readManifest(dbDir) {
  const files = fs.readdirSync(dbDir).filter((f) => /^MANIFEST-\d+$/.test(f));
  const newest = files.sort((a, b) => Number(b.slice(9)) - Number(a.slice(9)))[0];
  const buf = fs.readFileSync(path.join(dbDir, newest));
  let logNumber = 0;
  let lastSeq = 0;
  for (const edit of scanRecords(buf)) {
    let p = 0;
    while (p < edit.length) {
      const tag = varint(edit, p);
      p = tag.end;
      const t = tag.value;
      // This leveldb fork's tags: 1=cmp 2=log 3=nextfile 4=lastseq
      // 5=compact 6=delfile 7=newfile 9=prevlog (8 reserved).
      if (t === 1) {
        const l = varint(edit, p);
        p = l.end + l.value;
      } else if (t === 5) {
        p = varint(edit, p).end; // level
        const l = varint(edit, p); // pointer key
        p = l.end + l.value;
      } else if (t === 6) {
        p = varint(edit, p).end;
        p = varint(edit, p).end;
      } else if (t === 7) {
        p = varint(edit, p).end;
        p = varint(edit, p).end;
        p = varint(edit, p).end;
        const s = varint(edit, p);
        p = s.end + s.value;
        const l = varint(edit, p);
        p = l.end + l.value;
      } else if (t === 2 || t === 3 || t === 4 || t === 9) {
        const v = varint(edit, p);
        p = v.end;
        if (t === 2) logNumber = v.value;
        else if (t === 4) lastSeq = v.value;
      } else {
        fail(`Unsupported manifest tag ${t} — this leveldb layout is newer than the tool.`);
      }
    }
  }
  return { logNumber, lastSeq };
}

// Only PUT records are written: the bundled leveldb binaries reject DEL
// (tag 0) WriteBatch entries.
function encodeWriteBatch(sequence, entries) {
  const parts = [Buffer.alloc(8), Buffer.alloc(4)];
  parts[0].writeBigUInt64LE(BigInt(sequence));
  parts[1].writeUInt32LE(entries.length);
  for (const e of entries) {
    parts.push(Buffer.from([1])); // PUT
    for (const chunk of [e.key, e.value]) {
      const len = Buffer.alloc(chunk.length < 128 ? 1 : chunk.length < 16384 ? 2 : 3);
      let i = 0;
      let v = chunk.length;
      do {
        len[i++] = (v & 0x7f) | (v > 0x7f ? 0x80 : 0);
        v >>>= 7;
      } while (v);
      parts.push(len, chunk);
    }
  }
  return Buffer.concat(parts);
}

// The manifest's last_sequence can be stale if the app crashed without a
// clean close — take the max over the WAL records too.
function maxWalSequence(dbDir) {
  let max = 0;
  for (const batch of walBatches(dbDir)) {
    const seq = Number(batch.readBigUInt64LE(0));
    if (seq > max) max = seq;
  }
  return max;
}

function appendWal(dbDir, ops) {
  const { logNumber, lastSeq } = readManifest(dbDir);
  const logPath = path.join(dbDir, String(logNumber).padStart(6, '0') + '.log');
  const seq = Math.max(lastSeq, maxWalSequence(dbDir)) + 1000;
  const batch = encodeWriteBatch(seq, ops);
  if (batch.length + 7 > 32768) fail('Write batch too large for one log record.');

  const fd = fs.openSync(logPath, 'a');
  try {
    const off = fs.fstatSync(fd).size % 32768;
    if (off + 7 + batch.length > 32768) fs.writeSync(fd, Buffer.alloc(32768 - off));
    const header = Buffer.alloc(7);
    header.writeUInt32LE(maskCrc(crc32c(Buffer.concat([Buffer.from([1]), batch]))), 0);
    header.writeUInt16LE(batch.length, 4);
    header[6] = 1; // FULL
    fs.writeSync(fd, header);
    fs.writeSync(fd, batch);
    // Flush to disk: the WAL must survive a crash in the window before the
    // app compacts it into an sstable.
    fs.fsyncSync(fd);
  } finally {
    fs.closeSync(fd);
  }
}

// Best-effort readback: apply the WAL records (newest sequence wins) and also
// raw-scan the sstables for the key names (their bytes survive snappy as
// literals more often than not).
function readWalState(dbDir) {
  const state = new Map();
  for (const batch of walBatches(dbDir)) {
    const seq = Number(batch.readBigUInt64LE(0));
    const count = batch.readUInt32LE(8);
    let p = 12;
    for (let i = 0; i < count && p < batch.length; i++) {
      const tag = batch[p++];
      const kl = varint(batch, p);
      p = kl.end;
      const key = batch.subarray(p, p + kl.value);
      p += kl.value;
      const vl = varint(batch, p);
      p = vl.end;
      const value = batch.subarray(p, p + vl.value);
      p += vl.value;
      const name = key.toString('utf8');
      const prev = state.get(name);
      if (!prev || prev.seq <= seq) state.set(name, { seq, tag, value });
    }
  }
  const out = {};
  for (const [name, v] of state) out[name] = v.tag === 1 ? v.value : null;
  return out;
}

function rawScan(dbDir, name) {
  for (const f of fs.readdirSync(dbDir).filter((x) => x.endsWith('.ldb'))) {
    if (fs.readFileSync(path.join(dbDir, f)).includes(Buffer.from(name))) return true;
  }
  return false;
}

// ============================================================================

function backupStorage(dbDir) {
  const stamp = new Date().toISOString().replace(/[:T]/g, '-').slice(0, 19);
  const dest = path.join(__dirname, 'backups', 'storage-' + stamp);
  fs.mkdirSync(dest, { recursive: true });
  for (const f of fs.readdirSync(dbDir)) {
    fs.copyFileSync(path.join(dbDir, f), path.join(dest, f));
  }
  info(`Backed up storage to ${dest}`);
}

function storageOps(state) {
  return Object.entries(CONFIG.keys[state]).map(([name, value]) => ({
    key: lsKey(name),
    value: lsValue(value),
  }));
}

function assertStorage() {
  if (!fs.existsSync(CONFIG.app.storageDir)) {
    fail(
      `Storage not found: ${CONFIG.app.storageDir}\n` +
        'Launch Porofessor once (so it creates its profile), close it, then re-run.'
    );
  }
}

async function doPatch() {
  banner('Porofessor ad-removal patch');
  assertStorage();
  assertAppClosed();
  backupStorage(CONFIG.app.storageDir);
  appendWal(CONFIG.app.storageDir, storageOps('on'));
  info('Wrote: ' + Object.entries(CONFIG.keys.on).map(([k, v]) => `${k}=${v}`).join(', '));
  info('Relaunch Porofessor — the Home iframe URL should no longer carry showAdSpace=true.');
}

async function doCheck() {
  banner('Porofessor patch state');
  const pids = runningPids();
  console.log(`  Running:    ${pids.length ? 'YES (PID ' + pids.join(', ') + ')' : 'no'}`);
  console.log(`  Storage:    ${CONFIG.app.storageDir}`);
  if (!fs.existsSync(CONFIG.app.storageDir)) {
    console.log('  Flags:      storage not found — launch the app once, then re-check');
    return;
  }

  const wal = readWalState(CONFIG.app.storageDir);
  const premium = wal[lsKey('isOWPremium').toString('utf8')];
  const isPremium = wal[lsKey('isPremium').toString('utf8')];
  const walPremium = premium && premium.toString('utf8').includes('"isPremium":true');
  const diskOwd = rawScan(CONFIG.app.storageDir, 'isOWPremium');
  const diskPr = rawScan(CONFIG.app.storageDir, 'isPremium');

  console.log(`  isPremium:   ${isPremium === undefined ? (diskPr ? 'set (in sstables)' : 'absent') : isPremium === null ? 'deleted' : String(isPremium).slice(1)}`);
  console.log(`  isOWPremium: ${premium === undefined ? (diskOwd ? 'set (in sstables)' : 'absent') : premium === null ? 'deleted' : String(premium).slice(1)}`);
  // isOWPremium is never rewritten by the app, so its WAL value (when
  // present) is authoritative; otherwise the key's presence in the sstables
  // decides. isPremium must not gate this: the app can rewrite it (e.g. from
  // account data) while a flushed isOWPremium=true still blocks the ad.
  const premiumSet = premium !== undefined ? walPremium : diskOwd;
  console.log(`  Ads:         ${premiumSet ? 'removed (premium flag set)' : 'present (run: patch-porofessor)'}`);
}

async function doRestore() {
  banner('Porofessor restore');
  assertStorage();
  assertAppClosed();
  backupStorage(CONFIG.app.storageDir);
  appendWal(CONFIG.app.storageDir, storageOps('off'));
  info('Reset the ad flags — ads are back. Re-run the tool to patch again.');
}

// ============================================================================
// Interactive menu (shown when the exe is opened without arguments).
// ============================================================================

// ANSI only when stdout is a real console — piped output stays plain.
const TTY = process.stdout.isTTY;
const C = {
  grn: TTY ? '\x1b[92m' : '',
  bold: TTY ? '\x1b[1m' : '',
  rev: TTY ? '\x1b[7m' : '',
  reset: TTY ? '\x1b[0m' : '',
};

const MENU_W = 62; // inner width between the border pipes
const VERSION = require('./package.json').version;

function padTo(text, width) {
  return text + ' '.repeat(Math.max(0, width - text.length));
}

// Widths are measured on plain text (ANSI stripped) so colored lines cannot
// break the padding math.
const visibleLength = (text) => text.replace(/\x1b\[[0-9;]*m/g, '').length;

function menuBar() {
  return C.grn + '  +' + '-'.repeat(MENU_W) + '+' + C.reset;
}
function menuRow(text) {
  return C.grn + '  | ' + text + ' '.repeat(Math.max(0, MENU_W - 2 - visibleLength(text))) + ' |' + C.reset;
}
function menuRowKey(key, text) {
  return menuRow(' [' + key + '] ' + text);
}

const MENU_ITEMS = [
  { key: '1', text: padTo('PATCH', 10) + 'REMOVE THE AD SPACE', run: doPatch },
  { key: '2', text: padTo('CHECK', 10) + 'PATCH STATUS', run: doCheck },
  { key: '3', text: padTo('UNINSTALL', 10) + 'RESTORE THE AD SPACE', run: doRestore },
];

function drawMenu() {
  console.clear();
  console.log('');
  console.log(menuBar());
  console.log(menuRow(C.bold + 'PorofessorPatch  v' + VERSION));
  console.log(menuBar());
  console.log(menuRow(''));
  for (const item of MENU_ITEMS) {
    console.log(menuRowKey(item.key, item.text));
  }
  console.log(menuRow(''));
  console.log(menuRowKey('0', 'EXIT'));
  console.log(menuRow(''));
  console.log(menuBar());
  console.log('');
  process.stdout.write('  ' + C.grn + 'SELECT OPERATION [0-3]: ' + C.reset);
}

// One-key selection in a TTY; line input when piped.
function waitKey(keys) {
  return new Promise((resolve) => {
    if (process.stdin.isTTY) {
      process.stdin.setRawMode(true);
      process.stdin.resume();
      const onData = (d) => {
        const c = d.toString().trim()[0];
        if (keys.includes(c)) {
          process.stdin.setRawMode(false);
          process.stdin.pause();
          process.stdin.off('data', onData);
          process.stdout.write(c + '\n' + C.reset);
          resolve(c);
        }
      };
      process.stdin.on('data', onData);
    } else {
      let buf = '';
      process.stdin.on('data', (d) => (buf += d.toString()));
      process.stdin.on('end', () => resolve(buf.trim()[0] || '0'));
    }
  });
}

function waitAnyKey() {
  return new Promise((resolve) => {
    if (!process.stdin.isTTY) return resolve();
    console.log('');
    process.stdout.write('  ' + C.grn + C.rev + ' PRESS ANY KEY TO RETURN ' + C.reset);
    process.stdin.setRawMode(true);
    process.stdin.resume();
    const onData = () => {
      process.stdin.setRawMode(false);
      process.stdin.pause();
      process.stdin.off('data', onData);
      console.log(C.reset);
      resolve();
    };
    process.stdin.on('data', onData);
  });
}

async function interactiveMenu() {
  const isTty = process.stdin.isTTY;
  for (;;) {
    drawMenu();
    const key = await waitKey(MENU_ITEMS.map((m) => m.key).concat('0'));
    if (key === '0') {
      console.log(C.reset);
      return;
    }
    const item = MENU_ITEMS.find((m) => m.key === key);
    try {
      await item.run();
    } catch (e) {
      console.error('\nFAILED: ' + (e && e.message ? e.message : e) + '\n');
    }
    if (!isTty) return; // piped: run once and exit
    await waitAnyKey();
  }
}

async function main() {
  const args = process.argv.slice(2);
  if (args.includes('--version')) {
    console.log('Porofessor Patch Tool v' + require('./package.json').version);
    return;
  }
  if (args.includes('--check')) return doCheck();
  if (args.includes('--restore')) return doRestore();
  if (args.length === 0) return interactiveMenu();
  if (args.includes('--patch')) return doPatch();
  console.log('Usage: patch-porofessor [--patch | --check | --restore | --version]');
  process.exit(1);
}

main().catch((e) => {
  if (e instanceof FailError) console.error('\nFAILED: ' + e.message + '\n');
  else console.error('\nFAILED: ' + ((e && e.stack) || e) + '\n');
  process.exit(1);
});
