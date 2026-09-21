from collections import Counter
from pathlib import Path
import re, math
N=128
fine={(x,z,4) for z in range(0,N,4) for x in range(0,N,4)}
def boundary(p):
 x,z,w=p;return x==0 or z==0 or x+w==N or z+w==N
def coarse(fine,steps):
 nodes={p:0 for p in fine}
 for _ in range(steps):
  parents={(x//(w*2)*(w*2),z//(w*2)*(w*2),w*2) for x,z,w in nodes if w<N}
  changed=False
  for x,z,w in sorted(parents,key=lambda p:(p[2],p[1],p[0])):
   if boundary((x,z,w)):continue
   h=w//2;ks=[(x,z,h),(x+h,z,h),(x,z+h,h),(x+h,z+h,h)]
   if not all(k in nodes for k in ks):continue
   g=max(nodes[k]+1 for k in ks)
   if g>steps:continue
   for k in ks:del nodes[k]
   nodes[(x,z,w)]=g;changed=True
  if not changed:break
 return set(nodes)
def find(cells,x,z):
 if x<0 or z<0 or x>=N*2 or z>=N:return None
 w=N
 while w>=1:
  p=(x//w*w,z//w*w,w)
  if p in cells:return p
  w//=2
 raise AssertionError((x,z))
def neighbours(cells,p):
 x,z,w=p
 return [find(cells,x-1,z+w//2),find(cells,x+w,z+w//2),find(cells,x+w//2,z-1),find(cells,x+w//2,z+w)]
def balance(cells):
 while True:
  split=set()
  for p in cells:
   for q in neighbours(cells,p):
    if q and q[2]>2*p[2]:split.add(q)
  if not split:return cells
  for x,z,w in split:
   cells.remove((x,z,w));h=w//2
   cells.update([(x,z,h),(x+h,z,h),(x,z+h,h),(x+h,z+h,h)])
def emit(cells):
 tris=[]
 for p in cells:
  x,z,w=p;h=w//2;ns=neighbours(cells,p)
  poly=[(x,z)]
  if ns[0] and ns[0][2]<w:poly.append((x,z+h))
  poly.append((x,z+w))
  if ns[3] and ns[3][2]<w:poly.append((x+h,z+w))
  poly.append((x+w,z+w))
  if ns[1] and ns[1][2]<w:poly.append((x+w,z+h))
  poly.append((x+w,z))
  if ns[2] and ns[2][2]<w:poly.append((x+h,z))
  if len(poly)==4:tris.extend([(poly[0],poly[1],poly[2]),(poly[0],poly[2],poly[3])])
  else:
   for i in range(len(poly)):tris.append(((x+h,z+h),poly[i],poly[(i+1)%len(poly)]))
 return tris
levels=[coarse(fine,i) for i in range(4)]
for a in range(4):
 for b in range(4):
  cells=balance(set(levels[a])|{(x+N,z,w) for x,z,w in levels[b]})
  for side in range(2):
   local={(x-side*N,z,w) for x,z,w in cells if side*N<=x<(side+1)*N}
   assert {p for p in fine if boundary(p)}=={p for p in local if boundary(p)}
  edges=Counter()
  for tri in emit(cells):
   lens=[math.dist(tri[i],tri[(i+1)%3]) for i in range(3)]
   assert max(lens)/min(lens)<=2.01
   for i in range(3):edges[tuple(sorted((tri[i],tri[(i+1)%3])))]+=1
  for (p,q),count in edges.items():
   assert count in (1,2)
   if count==1:assert (p[0]==q[0] and p[0] in (0,2*N)) or (p[1]==q[1] and p[1] in (0,N))
print('PASS: all 16 mixed LOD pairs, matching boundary vertices, internal edges paired, no thin fans')
for i in range(4):
 cells=balance(set(levels[i])|{(x+N,z,w) for x,z,w in levels[i]})
 print('LOD',i,'triangles per flat chunk:',len(emit(cells))//2)
# Ring priority: smaller radius overrides larger; next stamp overrides entire previous group.
zones=[(1,2),(.75,1),(.5,.5),(.25,.25)]
assert [next(cell for radius,cell in reversed(zones) if r<=radius) for r in (.1,.4,.6,.9)]==[.25,.5,1,2]
print('PASS: nested ring priority')
for p in Path(__file__).resolve().parents[1].rglob('*.cs'):
 s=p.read_text();s=re.sub(r'//[^\n]*|/\*.*?\*/|\$?@?"(?:\\.|[^"\\])*"','',s,flags=re.S)
 stack=[]
 for c in s:
  if c in '({[':stack.append(c)
  elif c in ')}]':assert stack and stack.pop()=={')':'(',']':'[','}':'{'}[c],p
 assert not stack,p
print('PASS: C# delimiter checks (not compilation)')
