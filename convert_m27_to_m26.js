const fs = require('fs');
const zlib = require('zlib');
const MaddenRosterHelper = require('./lib/helpers/MaddenRosterHelper');

const SRC_M27 = process.argv[2] || 'C:\\Users\\Ninja\\Documents\\Madden NFL 27\\Saves\\ROSTER-Official';
const TPL_M26 = process.argv[3] || 'C:\\Users\\Ninja\\Documents\\Madden NFL 26\\saves\\ROSTER-Official26';
const OUT = process.argv[4] || 'C:\\Users\\Ninja\\Documents\\Madden NFL 26\\saves\\ROSTER-Official27TEST2';
const SIZE = 6291530;

function zeroField(field) {
    field.value = 0;
}

async function main() {
    console.log('loading M27 source:', SRC_M27);
    const h27 = new MaddenRosterHelper();
    const m27 = await h27.load(SRC_M27);

    console.log('loading M26 template:', TPL_M26);
    const h26 = new MaddenRosterHelper();
    const m26 = await h26.load(TPL_M26);

    const get = (f, n) => f.tables.find(t => t.name === n);

    // ---- 1. Build M27 lookups ----
    const play27 = get(m27, 'PLAY');
    const m27ByPGID = {};
    for (const rec of play27.records) {
        m27ByPGID[rec.fields.PGID.value] = rec.fields;
    }
    console.log('M27 players:', Object.keys(m27ByPGID).length);

    const plct27 = get(m27, 'PLCT');
    const plctByPGID = {};
    for (const rec of plct27.records) {
        plctByPGID[rec.fields.PGID.value] = rec.fields;
    }
    console.log('M27 PLCT entries:', Object.keys(plctByPGID).length);

    // ---- 2. Update M26 PLAY: matched players get M27 stats ----
    const play26 = get(m26, 'PLAY');
    const m26Fields = Object.keys(play26.records[0].fields);
    const m27PlayFields = Object.keys(play27.records[0].fields);
    const sharedFields = m26Fields.filter(f => m27PlayFields.includes(f));

    const m27PGIDs = new Set(play27.records.map(r => r.fields.PGID.value));

    let matched = 0;
    let unmatchedSlots = [];
    for (const rec26 of play26.records) {
        const pgid = rec26.fields.PGID.value;
        const rec27 = m27ByPGID[pgid];
        if (rec27) {
            matched++;
            for (const fname of sharedFields) {
                if (fname === 'PGID') continue;
                if (rec27[fname] !== undefined) {
                    rec26.fields[fname].value = rec27[fname].value;
                }
            }
            const plct = plctByPGID[pgid];
            if (plct) {
                if (plct.PCON && rec26.fields.PCON) rec26.fields.PCON.value = plct.PCON.value;
                for (let i = 0; i <= 7; i++) {
                    const fn = 'PSA' + i;
                    if (plct[fn] && rec26.fields[fn]) rec26.fields[fn].value = plct[fn].value;
                }
                for (let i = 0; i <= 6; i++) {
                    const fn = 'PSB' + i;
                    if (plct[fn] && rec26.fields[fn]) rec26.fields[fn].value = plct[fn].value;
                }
            }
        } else {
            unmatchedSlots.push(rec26);
        }
    }
    console.log('Matched:', matched, 'Unmatched M26 slots:', unmatchedSlots.length);

    // ---- 3. Place M27-only players (rookies) into unused slots ----
    const m26PGIDs = new Set(play26.records.map(r => r.fields.PGID.value));
    const m27Only = play27.records.filter(r => !m26PGIDs.has(r.fields.PGID.value));
    console.log('M27 rookies to place:', m27Only.length);

    const zeroFields = ['PSBO', 'PSKI', 'PSTY', 'PGHE', 'TRBH', 'TRBR', 'TRCB', 'TRCL',
        'TRDO', 'TRDS', 'TRFB', 'TRFK', 'TRFY', 'TRHM', 'TRJR', 'TRSB', 'TRSW',
        'TRTA', 'TRTL', 'TRTS', 'TRWU', 'PSB5', 'PSB6'];

    for (let i = 0; i < m27Only.length && i < unmatchedSlots.length; i++) {
        const m27Fields = m27Only[i].fields;
        const slot = unmatchedSlots[i];
        const pgid = m27Fields.PGID.value;

        // Zero out all shared fields first
        for (const fname of sharedFields) {
            if (slot.fields[fname]) {
                slot.fields[fname].value = slot.fields[fname].type === 1 ? '' : 0;
            }
        }

        // Copy M27 values
        for (const fname of sharedFields) {
            if (m27Fields[fname] !== undefined && slot.fields[fname]) {
                const v = m27Fields[fname].value;
                if (typeof v === 'object' || v === undefined || v === null) continue;
                slot.fields[fname].value = v;
            }
        }

        // PSXP (portrait ID) - set to 0 for rookies (silhouette)
        if (slot.fields.PSXP) slot.fields.PSXP.value = 0;

        // Merge salary from PLCT
        const plct = plctByPGID[pgid];
        if (plct) {
            if (plct.PCON && slot.fields.PCON) slot.fields.PCON.value = plct.PCON.value;
            for (let j = 0; j <= 7; j++) {
                const fn = 'PSA' + j;
                if (plct[fn] && slot.fields[fn]) slot.fields[fn].value = plct[fn].value;
            }
            for (let j = 0; j <= 6; j++) {
                const fn = 'PSB' + j;
                if (plct[fn] && slot.fields[fn]) slot.fields[fn].value = plct[fn].value;
            }
        }
    }

    // ---- 4. Clear remaining unused slots ----
    const cleared = unmatchedSlots.slice(m27Only.length);
    for (const slot of cleared) {
        for (const fname of m26Fields) {
            if (fname === 'PGID') { slot.fields.PGID.value = 0; continue; }
            if (slot.fields[fname]) {
                slot.fields[fname].value = slot.fields[fname].type === 1 ? '' : 0;
            }
        }
    }
    console.log('Rookies placed:', Math.min(m27Only.length, unmatchedSlots.length), 'Slots cleared:', cleared.length);

    // ---- 5. Fix BLBM: reassign orphaned entries to rookies ----
    const blob = m26.tables.find(t => t.name === 'BLOB');
    const blbm = blob.records[0].fields.BLBM.value;

    const finalPGIDs = new Set();
    for (const rec of play26.records) {
        const pgid = rec.fields.PGID.value;
        if (pgid !== 0) finalPGIDs.add(pgid);
    }
    console.log('Final active PGIDs:', finalPGIDs.size);

    // Find BLBM records whose index doesn't match any active PGID
    const orphans = blbm.records.filter(r => !finalPGIDs.has(r.index));
    console.log('Orphaned BLBM records:', orphans.length);

    // Find rookies that have no BLBM entry
    const blbmIndices = new Set(blbm.records.map(r => r.index));
    const rookiesNeedingBLBM = play26.records.filter(r => {
        const pgid = r.fields.PGID.value;
        return pgid !== 0 && !blbmIndices.has(pgid);
    });
    console.log('Rookies needing BLBM:', rookiesNeedingBLBM.length);

    // Reassign orphan BLBM records to rookies
    let reassigned = 0;
    for (let i = 0; i < Math.min(rookiesNeedingBLBM.length, orphans.length); i++) {
        const rookie = rookiesNeedingBLBM[i];
        const orphan = orphans[i];
        const newIdx = rookie.fields.PGID.value;
        // Change record index (this IS the CNID the game looks up)
        orphan.index = newIdx;
        reassigned++;
    }
    console.log('BLBM records reassigned:', reassigned);

    // ---- 6. Save through M26 container ----
    await h26.save(OUT);

    // Normalize
    let fb = fs.readFileSync(OUT);
    if (fb.length > SIZE) fs.writeFileSync(OUT, fb.subarray(0, SIZE));
    else if (fb.length < SIZE) fs.writeFileSync(OUT, Buffer.concat([fb, Buffer.alloc(SIZE - fb.length)]));
    fb = fs.readFileSync(OUT);

    // ---- 7. Verify CRC + len ----
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

    // ---- 8. Verify ----
    const hv = new MaddenRosterHelper();
    const v = await hv.load(OUT);
    const vPlay = v.tables.find(t => t.name === 'PLAY');
    const activePlayers = vPlay.records.filter(r => r.fields.PGID.value !== 0);
    console.log('reload: ' + activePlayers.length + ' active players');
    const vp = vPlay.records[0].fields;
    console.log('PLAY[0]:', 'PGID=' + vp.PGID.value, 'PFN=' + (vp.PFNA ? vp.PFNA.value : '?'), 'PLN=' + (vp.PLNA ? vp.PLNA.value : '?'));
    // Spot check a rookie
    const rookie = vPlay.records.find(r => r.fields.PFNA && r.fields.PFNA.value === 'Alex');
    if (rookie) console.log('Rookie check:', rookie.fields.PGID.value, rookie.fields.PFNA.value, rookie.fields.PLNA.value);

    console.log('wrote:', OUT);
}

main().catch(e => { console.error(e); process.exit(1); });
