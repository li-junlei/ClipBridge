"""Remove only fixed-size native assertion path prefixes from PE read-only data.

Rust source locations are remapped by rustc. MSVC's wide __FILE__ literals in
transitive C libraries need separate handling. No code, offsets or file sizes change.
"""
from pathlib import Path
import sys,struct,hashlib,ctypes
file=Path(sys.argv[1]);data=bytearray(file.read_bytes());pe=struct.unpack_from('<I',data,0x3c)[0]
assert data[pe:pe+4]==b'PE\0\0'
count=struct.unpack_from('<H',data,pe+6)[0];optional=struct.unpack_from('<H',data,pe+20)[0];sections=[]
for i in range(count):
 pos=pe+24+optional+i*40;name=bytes(data[pos:pos+8]).rstrip(b'\0');size,offset=struct.unpack_from('<II',data,pos+16);sections.append((name,offset,size))
code=[(offset,size,hashlib.sha256(data[offset:offset+size]).digest()) for name,offset,size in sections if name==b'.text']
patched=0
sources = list(dict.fromkeys(variant for source in sys.argv[2:] for variant in (source, source.replace('\\', '\\\\'), source.replace('\\', '/'))))
for source in sources:
 for encoding in ['utf-8','utf-16le']:
  needle=source.encode(encoding);start=0
  label='C:\\build\\'+'_'*max(0,len(source)-len('C:\\build\\'))
  replacement=label[:len(source)].encode(encoding)
  assert len(replacement)==len(needle)
  while True:
   pos=data.find(needle,start)
   if pos<0:break
   assert any(name==b'.rdata' and offset<=pos and pos+len(needle)<=offset+size for name,offset,size in sections),'Build path outside read-only data; inspect manually'
   data[pos:pos+len(needle)]=replacement;start=pos+len(needle);patched+=1
for offset,size,digest in code:assert hashlib.sha256(data[offset:offset+size]).digest()==digest,'Executable code changed'
assert len(data)==file.stat().st_size
# Update the ordinary PE checksum after changing diagnostic strings.
checksum_offset=pe+24+64;struct.pack_into('<I',data,checksum_offset,0)
buffer=(ctypes.c_ubyte*len(data)).from_buffer(data);old=ctypes.c_ulong();new=ctypes.c_ulong()
check=ctypes.windll.imagehlp.CheckSumMappedFile;check.argtypes=[ctypes.c_void_p,ctypes.c_ulong,ctypes.POINTER(ctypes.c_ulong),ctypes.POINTER(ctypes.c_ulong)];check.restype=ctypes.c_void_p
assert check(buffer,len(data),ctypes.byref(old),ctypes.byref(new))
struct.pack_into('<I',data,checksum_offset,new.value)
file.write_bytes(data)
print('Sanitized',patched,'native diagnostic prefixes; executable code bytes unchanged')
