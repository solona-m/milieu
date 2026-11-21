# Independent BC7 writer + reader for modes 1, 5, 6, written from the Khronos DFS BC7 tables (fast-bc7, ADR 0015).
# Produced the literals in Milieu.Tests/Support/Bc7GoldenBlocks.cs (python golden.py); shares no code with Bc7Block.cs.
W4=[0,4,9,13,17,21,26,30,34,38,43,47,51,55,60,64]; W3=[0,9,18,27,37,46,55,64]; W2=[0,21,43,64]
ROWS={0:[0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1],
      17:[0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0],
      18:[0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0],
      34:[0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0]}
ANCHOR={0:15,17:2,18:8,34:6}
def interp(a,b,w): return (a*(64-w)+b*w+32)>>6
class W:
    def __init__(s): s.v=0; s.p=0
    def put(s,x,n):
        assert 0<=x<(1<<n); s.v|=x<<s.p; s.p+=n
    def bytes(s):
        assert s.p==128; return [(s.v>>(8*i))&255 for i in range(16)]
def mode6(e,p0,p1,idx):
    w=W(); w.put(0b1000000,7)
    for k in e: w.put(k,7)
    w.put(127,7); w.put(127,7); w.put(p0,1); w.put(p1,1)
    assert idx[0]<8
    w.put(idx[0],3)
    for i in idx[1:]: w.put(i,4)
    ex=[(e[k]<<1)|(p0 if k%2==0 else p1) for k in range(6)]
    tex=[]
    for i in idx:
        tex+= [interp(ex[c*2],ex[c*2+1],W4[i]) for c in range(3)]+[255]
    return w.bytes(),tex
def mode1(part,e,p0,p1,idx):
    w=W(); w.put(0b10,2); w.put(part,6)
    for k in e: w.put(k,6)
    w.put(p0,1); w.put(p1,1)
    an=ANCHOR[part]
    for i,x in enumerate(idx):
        if i==0 or i==an: assert x<4; w.put(x,2)
        else: w.put(x,3)
    def ex(c,s,end):
        q=e[c*4+s*2+end]; p=p0 if s==0 else p1; v7=(q<<1)|p; return (v7<<1)|(v7>>6)
    tex=[]
    for i,x in enumerate(idx):
        s=ROWS[part][i]
        tex+=[interp(ex(c,s,0),ex(c,s,1),W3[x]) for c in range(3)]+[255]
    return w.bytes(),tex
def mode5(e,ci):
    w=W(); w.put(0b100000,6); w.put(0,2)
    for k in e: w.put(k,7)
    w.put(255,8); w.put(255,8)
    assert ci[0]<2
    w.put(ci[0],1)
    for x in ci[1:]: w.put(x,2)
    w.put(0,1)
    for _ in range(15): w.put(0,2)
    ex=[(k<<1)|(k>>6) for k in e]
    tex=[]
    for x in ci: tex+=[interp(ex[c*2],ex[c*2+1],W2[x]) for c in range(3)]+[255]
    return w.bytes(),tex
cases=[]
idxA=[3,0,1,2,4,5,6,7,8,9,10,11,12,13,14,15]
cases.append(("Mode6Ascending","mode 6, e0 < e1 in every channel, indices 3,0..15 (pixel 0 anchor MSB 0)",6,0,[10,100,20,90,30,80],[1,1],idxA,mode6([10,100,20,90,30,80],1,1,idxA)))
idxB=[7,15,14,13,12,11,10,9,8,0,1,2,3,4,5,6]
cases.append(("Mode6Descending","mode 6, e0 > e1 (the swapped order the anchor rule produces), p-bits 1",6,0,[120,5,64,63,0,127],[1,1],idxB,mode6([120,5,64,63,0,127],1,1,idxB)))
for part in (0,17,18,34):
    idx=[]
    for i in range(16):
        s=ROWS[part][i]
        idx.append( (i%4) if (i==0 or i==ANCHOR[part]) else (i*3)%8 )
    e=[3,60,40,10, 5,50,63,0, 20,21,1,62]
    cases.append((f"Mode1Partition{part}",f"mode 1, partition {part} (second-subset anchor {ANCHOR[part]}), p-bits 0/1",1,part,e,[0,1],idx,mode1(part,e,0,1,idx)))
e5=[0,0,64,64,127,127]
cases.append(("Mode5SolidZeroChannel","mode 5 solid (0, 129, 255): 7-bit 0/64/127, colour index 0 everywhere",5,0,e5,[],[0]*16,mode5(e5,[0]*16)))
e5b=[30,10,0,0,127,1]
cases.append(("Mode5SolidSwapped","mode 5 solid at colour index 1 (weight 21) with endpoints in swapped order",5,0,e5b,[],[1]*16,mode5(e5b,[1]*16)))
out=[]
for name,doc,mode,part,e,pb,idx,(by,tex) in cases:
    out.append(f"    /// <summary>{doc}.</summary>")
    out.append(f"    public static readonly GoldenBlock {name} = new(Mode: {mode}, Partition: {part},")
    out.append(f"        Endpoints: [{', '.join(map(str,e))}], PBits: [{', '.join(map(str,pb))}],")
    out.append(f"        Indices: [{', '.join(map(str,idx))}],")
    out.append(f"        Bytes: [{', '.join('0x%02X'%b for b in by)}],")
    out.append(f"        Texels: [{', '.join(map(str,tex))}]);")
    out.append("")
print("\n".join(out))
print("ALL:", ", ".join(c[0] for c in cases))
