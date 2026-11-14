import os,sys,json,hashlib
R=r'C:\Users\dom12\Documents\unity_test_hdrp'
HERE=os.path.dirname(os.path.abspath(__file__))
M=json.load(open(os.path.join(HERE,'freeze_md5.json')))
bad=0
def h(p):
    return hashlib.md5(open(p,'rb').read()).hexdigest() if os.path.isfile(p) else None
for k,v in sorted(M.items()):
    n=h(os.path.join(R,k.replace('/','\\')))
    if n==v: print('OK',k)
    else:
        bad+=1; print('CHANGED' if n else 'CHANGED (missing)',k)
print('%d files, %d changed'%(len(M),bad))
sys.exit(1 if bad else 0)
