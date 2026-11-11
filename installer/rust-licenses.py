from pathlib import Path
import subprocess,json,sys
manifest=Path(sys.argv[1]);out=Path(sys.argv[2]);out.mkdir(parents=True,exist_ok=True)
metadata=json.loads(subprocess.check_output(['cargo','metadata','--locked','--format-version','1','--manifest-path',str(manifest)],encoding='utf-8'))
parts=[]
for p in sorted(metadata['packages'],key=lambda p:(p['name'],p['version'])):
 root=Path(p['manifest_path']).parent
 files=sorted({f for pattern in ['LICENSE*','LICENCE*','COPYING*','NOTICE*'] for f in root.glob(pattern) if f.is_file()})
 text='\n'.join(f.read_text(encoding='utf-8',errors='replace') for f in files)
 parts.append(p['name']+' '+p['version']+' | '+str(p.get('license') or 'See upstream license')+'\n'+str(p.get('repository') or '')+'\n'+text)
(out/'RUST-THIRD-PARTY-NOTICES.txt').write_text('\n\n'+'\n\n'.join(parts),encoding='utf-8')
print('Collected licenses for',len(parts),'Rust packages')
