"""
Writes the simple placeholder models of the Graveyard (0.179.0): the hut block, the grave plot marker, the
wooden and stone grave markers, and the shrouded body the undertaker carries. Aaron makes the real Graveyard
buildings; these are only stand-ins until he wants to draw his own markers.

Usage: python3 tools/models/graveyard_models.py
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
 # Graveyard: the hut crate with a little headstone and a shovel stuck in a mound.
 "block/hut-graveyard": shape(["sides","planks","stone","log","metal"], [
   B("Crate",[1,0,1],[15,8,15],"sides"), B("Lid",[0,8,0],[16,9,16],"planks"),
   B("Stone",[2.5,9,3],[8.5,15,5],"stone"), B("StoneTop",[3.5,15,3],[7.5,16,5],"stone"),
   B("Mound",[9,9,6],[14,10.5,14],"log"),
   B("ShovelHandle",[11,10.5,9.5],[12,18,10.5],"planks"),
   B("ShovelGrip",[10,18,9.5],[13,18.8,10.5],"planks"),
   B("ShovelBlade",[10,9.5,9.6],[13,12,10.4],"metal"),
 ]),
 # Grave plot marker: a low frame of boards the size of a grave, with a stake at its head (north).
 "block/graveplot": shape(["planks","log"], [
   B("North",[1,0,1],[15,1,2],"planks"), B("South",[1,0,14],[15,1,15],"planks"),
   B("West",[1,0,2],[2,1,14],"planks"), B("East",[14,0,2],[15,1,14],"planks"),
   B("Stake",[7.5,0,1.25],[8.5,5,2.25],"log"),
 ]),
 # Wooden grave marker: a cross at the head of the grave and a low mound.
 "block/gravemarker-wood": shape(["planks","soil"], [
   B("Post",[7,0,1.5],[9,14,3],"planks"), B("Bar",[4,9,1.5],[12,11,3],"planks"),
   B("Mound",[3,0,3.5],[13,1.5,14.5],"soil"),
 ]),
 # Stone headstone with a rounded top and a low mound.
 "block/gravemarker-stone": shape(["stone","soil"], [
   B("Stone",[3,0,1],[13,11,3],"stone"), B("Top",[4,11,1],[12,12.5,3],"stone"), B("Crown",[5.5,12.5,1],[10.5,13.5,3],"stone"),
   B("Base",[2,0,0.5],[14,1,3.5],"stone"),
   B("Mound",[3,0,3.5],[13,1.5,14.5],"soil"),
 ]),
 # A body wrapped in a shroud, tied with rope.
 "item/shroudedbody": shape(["linen","rope"], [
   B("Body",[1,0,5],[15,3,11],"linen"), B("Head",[13,0,5.5],[16,2.8,10.5],"linen"),
   B("Rope1",[4,-0.1,4.9],[4.8,3.1,11.1],"rope"), B("Rope2",[8,-0.1,4.9],[8.8,3.1,11.1],"rope"), B("Rope3",[12,-0.1,4.9],[12.8,3.1,11.1],"rope"),
 ]),
}
for name, data in models.items():
    path=os.path.join(root, name + ".json")
    with open(path,"w") as f: json.dump(data, f, indent=1)
    print("wrote", os.path.relpath(path))
