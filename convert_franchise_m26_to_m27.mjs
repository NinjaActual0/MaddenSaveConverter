import pkg from 'madden-franchise';
const FranchiseFile = pkg.FranchiseFile;
import fs from 'fs';

async function convert(srcPath, tplPath, outPath){
  console.log(`Loading src ${srcPath}`);
  const src = new FranchiseFile(srcPath);
  await src.parse();
  console.log(`src tables ${src.tables.length}`);
  console.log(`Loading tpl ${tplPath}`);
  const tpl = new FranchiseFile(tplPath);
  await tpl.parse();
  console.log(`tpl tables ${tpl.tables.length}`);

  let common=0, copied=0, skipped=0;
  for(let srcTable of src.tables){
    let name = srcTable.header.name;
    if(!name || name.startsWith('\0')) continue;
    let tplTable = tpl.tables.find(t=> t.header.name===name);
    if(!tplTable) continue;
    // Load records
    try{ await srcTable.readRecords(); }catch(e){ skipped++; continue; }
    try{ await tplTable.readRecords(); }catch(e){ skipped++; continue; }
    if(srcTable.records.length===0 || tplTable.records.length===0) continue;
    common++;
    let n = Math.min(srcTable.records.length, tplTable.records.length);
    for(let i=0;i<n;i++){
      let sRec = srcTable.records[i];
      let tRec = tplTable.records[i];
      if(sRec.isEmpty && tRec.isEmpty) continue;
      if(sRec.isEmpty || tRec.isEmpty) continue;
      let sKeys = Object.keys(sRec);
      for(let k of sKeys){
        if(k.startsWith('_') || k==='isEmpty' || k==='index' || k==='header') continue;
        if(!(k in tRec)) continue;
        try{ tRec[k] = sRec[k]; copied++; }catch(e){}
      }
    }
  }
  console.log(`common tables ${common}, fields copied ${copied}`);
  await tpl.save(outPath);
  console.log(`saved to ${outPath} size ${fs.statSync(outPath).size}`);
}

const args = process.argv.slice(2);
if(args.length < 3){
  console.log("usage: node convert_franchise_m26_to_m27.mjs <src> <tpl> <out>");
  process.exit(1);
}
await convert(args[0], args[1], args[2]);
