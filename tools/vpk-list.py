import pathlib,struct,sys,re
for filename in sys.argv[1:]:
    p=pathlib.Path(filename);b=p.read_bytes();magic,version,size=struct.unpack_from('<III',b);assert magic==0x55aa1234
    cursor=28 if version==2 else 12
    def string():
        global cursor
        end=b.index(0,cursor);value=b[cursor:end].decode('utf-8');cursor=end+1;return value
    while ext:=string():
        while directory:=string():
            while name:=string():
                crc,preload,archive,offset,length,term=struct.unpack_from('<IHHIIH',b,cursor);cursor+=18+preload
                full=(directory+'/' if directory!=' ' else '')+name+'.'+ext
                if ext=='vmdl_c' and re.search('cube|box|crate_64',full,re.I):print(str(p)+' | '+full)
