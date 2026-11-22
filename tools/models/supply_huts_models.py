"""
Writes the simple placeholder models of the six huts of 0.184.0: Charcoal Burner, Stonemason, Bakery, Fisher,
Fletcher and Composter. Each is the hut crate with a small sign of its trade on the lid. Aaron makes the real
buildings; these are only stand-ins.

Usage: python3 tools/models/supply_huts_models.py
"""
import json, os
def box(name, f, t, tex, tex_top=None):
    x1,y1,z1=f; x2,y2,z2=t
    w,h,d=x2-x1,y2-y1,z2-z1
    def uv(a,b): return [0,0,round(a,2),round(b,2)]
    faces={
      "north":{"texture":"#"+tex,"uv":uv(w,h)},
      "east":{"texture":"#"+tex,"uv":uv(d,h)},
      "south":{"texture":"#"+tex,"uv":uv(w,h)},
      "west":{"texture":"#"+tex,"uv":uv(d,h)},
      "up":{"texture":"#"+(tex_top or tex),"uv":uv(w,d)},
      "down":{"texture":"#"+tex,"uv":uv(w,d)},
    }
    return {"name":name,"from":f,"to":t,"faces":faces}
def shape(textures, elements):
    return {"textureWidth":16,"textureHeight":16,"textures":{k:k for k in textures},"elements":elements}
B=box
def crate():
    return [B("Crate",[1,0,1],[15,8,15],"sides"), B("Lid",[0,8,0],[16,9,16],"planks")]
root=os.path.join(os.path.dirname(__file__), "..", "..", "assets", "createacolony", "shapes")
T=["sides","planks","a","b","c"]
models = {
 # Charcoal Burner: a little earth mound with a smoke hole, and a shovel.
 "block/hut-charcoalburner": shape(T, crate() + [
   B("Mound",[2.5,9,2.5],[10.5,12,10.5],"a"), B("MoundTop",[4,12,4],[9,13.5,9],"a"),
   B("Hole",[5.75,13.5,5.75],[7.25,14,7.25],"b"),
   B("Coal1",[11,9,11],[13.5,10.5,13.5],"b"), B("Coal2",[12,9,8.5],[14,10,10.5],"b"),
   B("ShovelHandle",[12,9,3],[13,16,4],"planks"), B("ShovelBlade",[11,9,3.1],[14,11.5,3.9],"c"),
 ]),
 # Stonemason: a stack of cut stone bricks and a chisel.
 "block/hut-stonemason": shape(T, crate() + [
   B("Brick1",[2,9,2],[8,11,5],"a"), B("Brick2",[2,9,5.5],[8,11,8.5],"a"), B("Brick3",[3,11,3],[9,13,6],"a"),
   B("Block",[9.5,9,8],[14.5,14,13],"a"),
   B("ChiselHandle",[3,9,11],[8,10,12],"planks"), B("ChiselBlade",[8,9.2,11.2],[11,9.8,11.8],"c"),
 ]),
 # Bakery: a round loaf and a flour sack.
 "block/hut-bakery": shape(T, crate() + [
   B("Loaf",[2.5,9,3],[8.5,11.5,7],"a"), B("LoafTop",[3.5,11.5,3.75],[7.5,12.2,6.25],"a"),
   B("Sack",[9.5,9,8.5],[14,15,13],"b"), B("SackTie",[10.5,15,9.5],[13,16,12],"b"),
   B("Board",[2,9,9],[8,9.5,14],"planks"),
 ]),
 # Fisher: a fishing pole lying across the lid, a fish and a little basket.
 "block/hut-fisher": shape(T, crate() + [
   B("Pole",[1,9,3],[15,9.8,3.8],"planks"), B("Reel",[3,9.2,2.5],[4.5,10.5,4.3],"c"),
   B("Fish",[3,9,7],[10,10.5,9.5],"a"), B("Tail",[10,9,6.75],[12,10.2,9.75],"a"),
   B("Basket",[9.5,9,10.5],[14.5,13,14.5],"b"),
 ]),
 # Fletcher: a bundle of arrows and a bow.
 "block/hut-fletcher": shape(T, crate() + [
   B("Shaft1",[2,9,3],[13,9.6,3.6],"planks"), B("Shaft2",[2,9.6,4],[13,10.2,4.6],"planks"), B("Shaft3",[2,9,5],[13,9.6,5.6],"planks"),
   B("Tips",[13,9,2.8],[14.5,10.4,5.8],"c"), B("Feathers",[1.5,9,2.6],[3.5,10.6,6],"a"),
   B("BowMid",[7,9,9],[9,9.8,14],"planks"), B("BowTop",[2,9,10],[7,9.8,10.8],"planks"), B("BowBot",[9,9,10],[14,9.8,10.8],"planks"),
   B("String",[2,9.2,13.2],[14,9.5,13.5],"b"),
 ]),
 # Composter: a small heap of compost in a slatted box.
 "block/hut-composter": shape(T, crate() + [
   B("BoxN",[2,9,2],[12,13,3],"planks"), B("BoxS",[2,9,11],[12,13,12],"planks"),
   B("BoxW",[2,9,3],[3,13,11],"planks"), B("BoxE",[11,9,3],[12,13,11],"planks"),
   B("Heap",[3,9,3],[11,12,11],"a"), B("HeapTop",[4.5,12,4.5],[9.5,13,9.5],"a"),
   B("Grass",[12.5,9,4],[15,10.5,13],"b"),
 ]),
}
for name, data in models.items():
    path=os.path.join(root, name + ".json")
    with open(path,"w") as f: json.dump(data, f, indent=1)
    print("wrote", os.path.relpath(path))
