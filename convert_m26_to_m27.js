const fs = require('fs');
const zlib = require('zlib');
const path = require('path');
const MaddenRosterHelper = require('./lib/helpers/MaddenRosterHelper');
const TDB2Record = require('./lib/filetypes/TDB2/TDB2Record');
const TDB2Field = require('./lib/filetypes/TDB2/TDB2Field');

// ---- config ----
const SRC_M26 = process.argv[2] || 'C:\\Users\\Ninja\\Documents\\Madden NFL 26\\saves\\ROSTER-2019';
const TPL_M27 = 'C:\\Users\\Ninja\\Documents\\Madden NFL 27\\Saves\\ROSTER-Official';
const OUT = process.argv[3] || 'C:\\Users\\Ninja\\AppData\\Local\\Temp\\opencode\\m26to27\\ROSTER-M26CONVERTED';
const SIZE = 6291530;

// clone a donor field and give it a new int value (value setter re-encodes LEB raw)
function intField(donor, value) {
    const f = new TDB2Field();
    f.key = donor.key;
    f.rawKey = Buffer.from(donor.rawKey);
    f.type = donor.type;
    f.value = value === undefined || value === null ? 0 : value;
    return f;
}

function buildTableFromDonor(tplTable, rows) {
    // rows: array of { fieldName -> intValue }
    const donor = tplTable.records[0].fields;
    const out = [];
    for (const row of rows) {
        const rec = new TDB2Record();
        for (const fname of Object.keys(donor)) {
            if (!row.hasOwnProperty(fname)) throw new Error('missing value for ' + fname);
            rec.fields[fname] = intField(donor[fname], row[fname]);
        }
        out.push(rec);
    }
    tplTable.records.length = 0;
    for (const r of out) tplTable.records.push(r);
    tplTable.numEntries = out.length;
    return tplTable;
}

async function main() {
    console.log('loading M26 source:', SRC_M26);
    const h26 = new MaddenRosterHelper();
    const m26 = await h26.load(SRC_M26);

    console.log('loading M27 template:', TPL_M27);
    const h27 = new MaddenRosterHelper();
    const m27 = await h27.load(TPL_M27);

    const get = (f, n) => f.tables.find(t => t.name === n);
    const play26 = get(m26, 'PLAY');
    console.log('M26 PLAY records:', play26.numEntries);

    // ---- 1. build PLCT (contracts) from M26 PLAY salary fields ----
    const tplPLCT = get(m27, 'PLCT');
    const plctRows = [];
    for (const r of play26.records) {
        const f = r.fields;
        const pgid = f.PGID ? f.PGID.value : null;
        if (pgid === null) continue;
        plctRows.push({
            PGID: pgid,
            PCON: f.PCON ? f.PCON.value : 0,
            PSA0: f.PSA0 ? f.PSA0.value : 0,
            PSA1: f.PSA1 ? f.PSA1.value : 0,
            PSA2: f.PSA2 ? f.PSA2.value : 0,
            PSA3: f.PSA3 ? f.PSA3.value : 0,
            PSA4: f.PSA4 ? f.PSA4.value : 0,
            PSA5: f.PSA5 ? f.PSA5.value : 0,
            PSA6: f.PSA6 ? f.PSA6.value : 0,
            PSA7: 0,
            PSB0: f.PSB0 ? f.PSB0.value : 0,
            PSB1: f.PSB1 ? f.PSB1.value : 0,
            PSB2: f.PSB2 ? f.PSB2.value : 0,
            PSB3: f.PSB3 ? f.PSB3.value : 0,
            PSB4: f.PSB4 ? f.PSB4.value : 0,
            CNTC: 0, CSGY: 0, CTCY: 0, CTVY: 0
        });
    }
    console.log('building PLCT rows:', plctRows.length);
    buildTableFromDonor(tplPLCT, plctRows);

    // ---- 2. synthesize PRSN (personas) keyed by PGID, zeroed DNA ----
    const tplPRSN = get(m27, 'PRSN');
    const prsnRows = plctRows.map(r => ({
        PGID: r.PGID, PRFC: 0,
        DNA0: 0, DNA1: 0, DNA2: 0, DNA3: 0,
        DNA4: 0, DNA5: 0, DNA6: 0, DNA7: 0
    }));
    console.log('building PRSN rows:', prsnRows.length);
    buildTableFromDonor(tplPRSN, prsnRows);

    // ---- 3. assemble new table set in M27 template order ----
    const names26 = ['BLOB', 'DCHT', 'DFTP', 'INJY', 'PLAY', 'TEAM'];
    const newTables = [];
    for (const n of ['BLOB', 'DCHT', 'DFTP', 'INJY', 'PLAY', 'PLCT', 'PRSN', 'TEAM']) {
        if (names26.includes(n)) {
            const t = get(m26, n);
            if (!t) {
                if (n === 'INJY') { newTables.push(get(m27, n)); continue; }
                throw new Error('M26 source missing table ' + n);
            }
            newTables.push(t);
        } else {
            newTables.push(n === 'PLCT' ? tplPLCT : tplPRSN);
        }
    }

    // ---- 4. save through M27 container (header/CRC recomputed) ----
    m27.tables = newTables;
    await h27.save(OUT);

    // normalize to exact save size
    let fb = fs.readFileSync(OUT);
    if (fb.length > SIZE) fs.writeFileSync(OUT, fb.subarray(0, SIZE));
    else if (fb.length < SIZE) fs.writeFileSync(OUT, Buffer.concat([fb, Buffer.alloc(SIZE - fb.length)]));
    fb = fs.readFileSync(OUT);

    // ---- 5. verify: CRC + len like the game checks ----
    const inner = zlib.inflateSync(fb.subarray(0x4A));
    const storedLen = fb.readUInt32LE(0x12);
    const storedCrc = fb.readUInt32LE(0x1A);
    const CRCPOLY_BE = 0x04c11db7;
    let crc = 0x80000000, crcTableBe = [0];
    for (let i = 1; i < 1 << 4; i <<= 1) {
        crc = (crc << 1) ^ (((crc & 0x80000000) != 0) ? CRCPOLY_BE : 0);
        for (let j = 0; j < i; j++) crcTableBe[i + j] = crc ^ crcTableBe[j];
    }
    let state = (0 ^ 0xFFFFFFFF) >>> 0;
    for (let x = 0; x < inner.length; x++) {
        state = (state ^ (inner[x] << 24)) >>> 0;
        state = ((state << 4) ^ crcTableBe[state >>> 28]) >>> 0;
        state = ((state << 4) ^ crcTableBe[state >>> 28]) >>> 0;
    }
    const calc = (state ^ 0xFFFFFFFF) >>> 0;
    console.log('size:', fb.length, '(must be ' + SIZE + ')');
    console.log('len: stored=' + storedLen + ' actual=' + inner.length + ' match=' + (storedLen === inner.length));
    console.log('crc: stored=0x' + storedCrc.toString(16).padStart(8, '0') + ' calc=0x' + calc.toString(16).padStart(8, '0') + ' match=' + (storedCrc === calc));

    // ---- 6. verify: full reload of output ----
    const hv = new MaddenRosterHelper();
    const v = await hv.load(OUT);
    console.log('reload tables:', v.tables.map(t => `${t.name}(type=${t.type},entries=${t.numEntries})`).join(' '));

    // spot-check a player survived the trip
    const vp = v.tables.find(t => t.name === 'PLAY').records[0].fields;
    console.log('PLAY[0]:', 'PGID=' + vp.PGID.value, 'POVR=' + (vp.POVR ? vp.POVR.value : '?'), 'PSBO=' + vp.PSBO.value);
    const vplct = v.tables.find(t => t.name === 'PLCT').records.find(r => r.fields.PGID.value === vp.PGID.value);
    if (vplct) console.log('PLCT match for PGID ' + vp.PGID.value + ':', 'PCON=' + vplct.fields.PCON.value, 'PSA0=' + vplct.fields.PSA0.value, 'PSB0=' + vplct.fields.PSB0.value);
    console.log('wrote:', OUT);
}

main().catch(e => { console.error(e); process.exit(1); });
