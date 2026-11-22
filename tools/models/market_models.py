"""
Writes the simple placeholder model of the Market's hut block (economy step 1): the hut crate with a little stall,
a striped awning on two poles, a basket of bread and a folded cloth. Aaron makes the real Market building; this is
only a stand-in.

Usage: python3 tools/models/market_models.py
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
root=os.path.join(os.path.dirname(__file__), "..", "..", "assets", "createacolony", "shapes")
models = {
 "block/hut-market": shape(["sides","planks","log","cloth","stripe","bread"], [
   B("Crate",[1,0,1],[15,8,15],"sides"), B("Lid",[0,8,0],[16,9,16],"planks"),
   B("PoleL",[1.5,9,1.5],[2.5,17,2.5],"log"), B("PoleR",[13.5,9,1.5],[14.5,17,2.5],"log"),
   B("Awning1",[1,17,1],[15,17.8,4],"cloth"), B("Awning2",[1,16.6,4],[15,17.4,7],"stripe"),
   B("Awning3",[1,16.2,7],[15,17,9],"cloth"),
   B("Basket",[3,9,8],[8,11,13],"planks"), B("Bread1",[3.5,11,8.5],[7.5,12.2,10.5],"bread"),
   B("Bread2",[4,11,10.8],[7,12,12.5],"bread"),
   B("Cloth",[9.5,9,8.5],[14,10.2,13],"stripe"), B("Cloth2",[10,10.2,9],[13.5,11,12.5],"cloth"),
 ]),
}
if __name__ == "__main__":
    for name, model in models.items():
        json.dump(model, open(os.path.join(root, name + ".json"), "w"), indent=1)
