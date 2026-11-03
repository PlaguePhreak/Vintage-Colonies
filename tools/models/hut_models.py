"""
Writes the simple placeholder models of the hut blocks (and the Supply Camp and Field marker) to
assets/createacolony/shapes/block/. Each hut is a crate with a few boxes on top that show what the hut is for.
The Town Hall and Farmer's Hut models were made by hand by Aaron's friend and are not written here.

Usage: python3 tools/models/hut_models.py [output folder]
"""
import json, sys
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
base=[box("Crate",[1,0,1],[15,8,15],"sides"),box("Lid",[0,8,0],[16,9,16],"planks")]
smelter=shape(["sides","planks","stone","coal","metal"], base+[
  box("Furnace",[4,9,4],[12,14,12],"stone"),
  box("Coals",[5,14,5],[11,14.5,11],"coal"),
  box("Ingot1",[1,9,11.5],[4,10,13.5],"metal"),
  box("Ingot2",[1,10,12],[3.5,10.8,13.2],"metal"),
])
smith=shape(["sides","planks","metal","stone"], base+[
  box("Block",[5,9,5],[11,10.5,11],"stone"),
  box("Waist",[6.5,10.5,6.5],[9.5,12,9.5],"metal"),
  box("Face",[3,12,6],[12,14,10],"metal"),
  box("Horn",[12,12.5,7],[14.5,13.6,9],"metal"),
])
B=lambda *a: box(*a)
models = {
 # Builder: a toolbox with a hammer and a plank leaning on it.
 "hut-builder": (["sides","planks","metal","top"], [
   B("Box",[3,9,5],[13,12,11],"planks","top"),
   B("BoxHandle",[7,12,7.5],[9,13.5,8.5],"planks"),
   B("HammerHandle",[4,12,4],[12,12.8,4.8],"planks"),
   B("HammerHead",[10.5,11.6,3.4],[12.5,13.6,5.4],"metal"),
   B("Plank",[1.5,9,11.5],[14.5,10,14],"planks"),
 ]),
 # Cook: a pot on a little fire.
 "hut-cook": (["sides","planks","stone","coal","metal","cloth"], [
   B("Ring",[4,9,4],[12,10,12],"stone"),
   B("Coals",[5,10,5],[11,10.5,11],"coal"),
   B("Pot",[5.5,10.5,5.5],[10.5,14,10.5],"metal"),
   B("Rim",[5,14,5],[11,14.5,11],"metal"),
   B("Cloth",[1,9,11.5],[4,9.6,14.5],"cloth"),
 ]),
 # Forester: a stump with an axe in it.
 "hut-forester": (["sides","planks","log","top","metal"], [
   B("Stump",[4,9,4],[12,14,12],"log","top"),
   B("AxeHandle",[7.5,14,7.5],[8.5,20,8.5],"planks"),
   B("AxeHead",[8.5,17.5,7.6],[11,19.5,8.4],"metal"),
 ]),
 # Guard Tower: a little lookout tower with a blue roof.
 "hut-guardtower": (["sides","planks","log","cloth"], [
   B("Leg1",[4,9,4],[5,15,5],"log"), B("Leg2",[11,9,4],[12,15,5],"log"),
   B("Leg3",[4,9,11],[5,15,12],"log"), B("Leg4",[11,9,11],[12,15,12],"log"),
   B("Floor",[3.5,15,3.5],[12.5,16,12.5],"planks"),
   B("Roof",[3,18,3],[13,19,13],"cloth"),
   B("Post",[4,16,4],[5,18,5],"log"), B("Post2",[11,16,11],[12,18,12],"log"),
 ]),
 # Miner: a pickaxe and a pile of ore.
 "hut-miner": (["sides","planks","stone","coal","metal"], [
   B("Rock1",[2,9,3],[8,12,9],"stone"),
   B("Rock2",[4,12,4],[7,13.5,7],"stone"),
   B("Ore",[8,9,8],[11,11,11],"coal"),
   B("PickHandle",[9,9.1,2],[10,10.1,14],"planks"),
   B("PickHead",[6,9.1,11],[13,10.6,12],"metal"),
 ]),
 # Potter: three pots.
 "hut-potter": (["sides","planks","pot","top"], [
   B("Pot1",[2.5,9,2.5],[7.5,14,7.5],"pot"), B("Neck1",[3.5,14,3.5],[6.5,15,6.5],"pot"),
   B("Pot2",[9,9,3],[13,12,7],"pot"),
   B("Pot3",[5,9,9],[11,13,13],"pot"), B("Neck3",[6,13,10],[10,14,12],"pot"),
 ]),
 # Prospector: a rock with a propick leaning on it, and a map.
 "hut-prospector": (["sides","planks","stone","metal","cloth"], [
   B("Rock",[3,9,3],[9,13,9],"stone"),
   B("PickHandle",[9.5,9,5],[10.5,19,6],"planks"),
   B("PickHead",[7.5,18,5],[13.5,19,6],"metal"),
   B("Map",[2,9,10],[12,9.4,14],"cloth"),
 ]),
 # Quarry: stacked stone blocks and a pick.
 "hut-quarry": (["sides","planks","stone","metal"], [
   B("Stone1",[2,9,2],[8,13,8],"stone"), B("Stone2",[8.5,9,2],[14,13,8],"stone"),
   B("Stone3",[4,13,3],[10,17,8],"stone"),
   B("PickHandle",[3,9.1,10],[14,10.1,11],"planks"),
   B("PickHead",[12,9.1,8.5],[13,10.6,13.5],"metal"),
 ]),
 # Residence: a little bed.
 "hut-residence": (["sides","planks","cloth","linen"], [
   B("Frame",[2,9,4],[14,11,12],"planks"),
   B("Blanket",[2.2,11,4.2],[10,12,11.8],"cloth"),
   B("Pillow",[10.5,11,5],[13.5,12.5,11],"linen"),
   B("Head",[13.5,9,3.5],[14.5,14,12.5],"planks"),
 ]),
 # Tavern: a barrel and two mugs.
 "hut-tavern": (["sides","planks","metal","top"], [
   B("Barrel",[2,9,3],[10,17,11],"planks","top"),
   B("Hoop1",[1.8,10.5,2.8],[10.2,11.3,11.2],"metal"), B("Hoop2",[1.8,14.7,2.8],[10.2,15.5,11.2],"metal"),
   B("Mug1",[11,9,4],[13.5,12,6.5],"planks","top"), B("Mug2",[11,9,9],[13.5,12,11.5],"planks","top"),
 ]),
 # Warehouse: stacked crates.
 "hut-warehouse": (["sides","planks","top"], [
   B("Crate1",[2,9,2],[8,15,8],"sides","top"), B("Crate2",[8.5,9,3],[14,14,8.5],"sides","top"),
   B("Crate3",[3,15,3],[7.5,19,7.5],"sides","top"), B("Sack",[4,9,9.5],[12,13,14],"planks"),
 ]),
}

# Supply camp: a crate with a small tent beside it (no lid crate base).
camp=shape(["sides","planks","cloth","top"], [
  B("Crate",[1,0,8],[8,7,15],"sides","top"),
  B("TentL",[8,0,1],[9,10,15],"cloth"), B("TentR",[15,0,1],[16,10,15],"cloth"),
  B("TentTop",[8,10,1],[16,11,15],"cloth"),
  B("Pole",[11.5,0,0.5],[12.5,12,1.5],"planks"),
  B("Sack",[2,0,2],[7,5,6.5],"planks"),
])

# Field marker: a scarecrow two blocks tall.
crow=shape(["planks","cloth","hay","linen"], [
  B("Post",[7,0,7],[9,28,9],"planks"),
  B("Arms",[1,19,7.25],[15,20.5,8.75],"planks"),
  B("Body",[4.5,14,6],[11.5,23,10],"cloth"),
  B("SleeveL",[1.5,18.5,6.75],[4.5,21,9.25],"cloth"),
  B("SleeveR",[11.5,18.5,6.75],[14.5,21,9.25],"cloth"),
  B("Head",[5.5,23,5.5],[10.5,28,10.5],"linen"),
  B("HatBrim",[4,28,4],[12,28.6,12],"hay"),
  B("HatTop",[5.5,28.6,5.5],[10.5,31,10.5],"hay"),
  B("Straw",[5,12.5,6.5],[11,14,9.5],"hay"),
])
saw=shape(["sides","planks","bark","metal"], base+[
  box("Horse1",[3,9,5],[4.5,12,11],"planks"),
  box("Horse2",[11.5,9,5],[13,12,11],"planks"),
  box("Log",[1,12,6],[15,15,9],"bark"),
  box("Blade",[7.5,12.5,4],[8,16,11.5],"metal"),
  box("Handle",[7.25,15,11.5],[8.25,16,13.5],"planks"),
])
el=base+[
  box("RackBase",[3,9,6],[13,10,10],"planks"),
  box("RackPostL",[3,10,7.5],[4,15,8.5],"planks"),
  box("RackPostR",[12,10,7.5],[13,15,8.5],"planks"),
  box("RackBar",[3,14,7.5],[13,15,8.5],"planks"),
]
for i,x in enumerate([5.5,7.75,10]):
  el.append(box(f"Spear{i}",[x,10,7.6],[x+0.8,16,8.4],"planks"))
  el.append(box(f"Tip{i}",[x-0.1,16,7.5],[x+0.9,17.5,8.5],"metal"))
el.append(box("Flag",[3.2,15,8.5],[3.8,18,13],"cloth"))
barracks=shape(["sides","planks","metal","cloth"], el)

# Explorer: a rolled map, a backpack, a walking stick and a spyglass.
explorer=shape(["sides","planks","cloth","linen","metal","leather"], base+[
  box("Pack",[2,9,3],[8,15,7],"leather"),
  box("PackFlap",[2,13,2.5],[8,15,3],"leather"),
  box("Bedroll",[2,15,3.5],[8,17,6.5],"cloth"),
  box("Map",[9,9,3],[14,10.5,12],"linen"),
  box("MapRoll",[9,9,12],[14,11,13.5],"linen"),
  box("Spyglass",[3,9,10],[8,10.5,11.5],"metal"),
  box("Stick",[13,9,14],[14,22,15],"planks"),
])
# Gatherer: a basket of berries with a mushroom beside it.
gatherer=shape(["sides","planks","basket","berry","stem","cap"], base+[
  box("Basket",[2,9,3],[10,13,11],"basket"),
  box("Berries",[2.5,12,3.5],[9.5,13.5,10.5],"berry"),
  box("Handle1",[5.5,13,3],[6.5,17,4],"basket"), box("Handle2",[5.5,13,10],[6.5,17,11],"basket"),
  box("HandleTop",[5.5,16,4],[6.5,17,10],"basket"),
  box("Stem",[11.5,9,11.5],[13,11.5,13],"stem"),
  box("Cap",[10.5,11.5,10.5],[14,13,14],"cap"),
])
# Hunter: a rack with two spears, a hide stretched on a frame and a cut of meat.
hunter=shape(["sides","planks","log","metal","hide","meat"], base+[
  box("RackL",[2,9,3],[3,17,4],"log"), box("RackR",[2,9,12],[3,17,13],"log"),
  box("RackBar",[2,16,3],[3,17,13],"log"),
  box("Spear1",[3,9,5.5],[3.8,20,6.3],"planks"), box("Tip1",[2.9,20,5.4],[3.9,21.5,6.4],"metal"),
  box("Spear2",[3,9,9.5],[3.8,20,10.3],"planks"), box("Tip2",[2.9,20,9.4],[3.9,21.5,10.4],"metal"),
  box("FrameL",[7,9,13],[8,18,14],"log"), box("FrameR",[14,9,13],[15,18,14],"log"),
  box("Hide",[8,10,13.3],[14,17,13.7],"hide"),
  box("Meat",[8,9,5],[13,11,9],"meat"),
])
# Primitive Craftsman: a flat knapping stone with flint on it, and a stone axe and spear leaning by it.
craftsman=shape(["sides","planks","stone","flint","log"], base+[
  box("Anvil",[4,9,4],[11,11,10],"stone"),
  box("Flint1",[5,11,5],[7.5,12,7],"flint"), box("Flint2",[8,11,6.5],[9.5,11.8,8.5],"flint"),
  box("Chip1",[11.5,9,4],[12.3,9.3,4.8],"flint"), box("Chip2",[3,9,11],[3.8,9.3,11.6],"flint"),
  box("Haft",[12.5,9,11],[13.3,18,11.8],"log"), box("AxeHead",[11,16,10.9],[12.5,18,11.9],"flint"),
  box("Spear",[2,9,12.5],[2.8,21,13.3],"log"), box("SpearTip",[1.9,21,12.4],[2.9,22.5,13.4],"flint"),
])
# Tailor's Hut: a bolt of cloth, a spool of twine and a pair of shears.
tailor=shape(["sides","planks","cloth","twine","metal"], base+[
  box("Bolt",[3,9,4],[12,12,8],"cloth"),
  box("BoltEnd",[2.5,9.5,4.5],[3,11.5,7.5],"planks"),
  box("Spool",[10,9,10],[12.5,12,12.5],"twine"),
  box("SpoolTop",[9.6,12,9.6],[12.9,12.5,12.9],"planks"),
  box("Blade1",[3,9,11],[8,9.4,11.6],"metal"), box("Blade2",[3.4,9,12],[8,9.4,12.6],"metal"),
  box("Grip",[8,9,10.8],[9.5,9.5,12.8],"planks"),
])
# Jeweller's Workshop: a small anvil, a gold ring and a gem.
jeweller=shape(["sides","planks","metal","gold","gem"], base+[
  box("AnvilBase",[5,9,5],[9,10,8],"metal"),
  box("AnvilTop",[4,10,5.5],[10.5,11.2,7.5],"metal"),
  box("RingA",[11,9,10],[13,9.5,10.5],"gold"), box("RingB",[11,9,11.5],[13,9.5,12],"gold"),
  box("RingC",[11,9,10.5],[11.5,9.5,11.5],"gold"), box("RingD",[12.5,9,10.5],[13,9.5,11.5],"gold"),
  box("Gem",[3,9,11],[4.5,10.5,12.5],"gem"),
])
# Healer's Hut: a red cross on the lid, a rolled bandage and a little jar of salve.
healer=shape(["sides","planks","cross","roll","jar"], base+[
  box("CrossA",[3,9,6],[8,9.3,7.5],"cross"), box("CrossB",[4.75,9,4.25],[6.25,9.3,9.25],"cross"),
  box("Roll",[9,9,3],[13,12,6],"roll"), box("RollEnd",[8.6,9.5,3.5],[9,11.5,5.5],"planks"),
  box("Jar",[9.5,9,9.5],[12.5,12,12.5],"jar"), box("JarLid",[9.2,12,9.2],[12.8,12.6,12.8],"planks"),
])
# Armourer's Hut: a helmet on a stand and a coil of chain.
armourer=shape(["sides","planks","metal","dark"], base+[
  box("Stand",[7,9,7],[9,13,9],"planks"),
  box("Helm",[5,13,5],[11,16.5,11],"metal"), box("Crest",[7.5,16.5,5],[8.5,17.5,11],"dark"),
  box("Chain1",[1.5,9,11],[5.5,9.8,14.5],"dark"), box("Chain2",[2.5,9.8,12],[4.5,10.4,13.5],"metal"),
])
# Weaponsmith's Hut: a sword and a spear on a little rack.
weaponsmith=shape(["sides","planks","metal","grip"], base+[
  box("RackL",[3,9,7],[4,13,9],"planks"), box("RackR",[12,9,7],[13,13,9],"planks"),
  box("Blade",[2,12,7.6],[11,12.6,8.4],"metal"), box("Guard",[11,11.5,6.8],[11.6,13.1,9.2],"metal"),
  box("Grip",[11.6,11.9,7.6],[14,12.7,8.4],"grip"),
  box("Spear",[2,10,10.6],[14,10.6,11.4],"planks"), box("Tip",[0.5,9.9,10.5],[2,10.7,11.5],"metal"),
])
# Royal Mint: a stack of coins, a small coin anvil and a hammer.
mint=shape(["sides","planks","metal","coin"], base+[
  box("AnvilBase",[4,9,4],[8,10.5,7],"metal"), box("AnvilTop",[3.5,10.5,4.2],[8.5,11.5,6.8],"metal"),
  box("Coins1",[10,9,9],[13,9.6,12],"coin"), box("Coins2",[10.3,9.6,9.3],[12.7,10.2,11.7],"coin"),
  box("Coins3",[10,10.2,9],[13,10.8,12],"coin"), box("Coin",[4.5,9,10.5],[6.5,9.4,12.5],"coin"),
  box("Handle",[9,9,4],[14,9.8,4.8],"planks"), box("Head",[13,9,3.2],[14.6,10.6,5.6],"metal"),
])
# Archive: an open book on a stand, a candle and a rolled scroll.
archive=shape(["sides","planks","paper","cover","wax"], base+[
  box("Stand",[4,9,5],[11,10,11],"planks"),
  box("CoverL",[3.6,10,4.6],[7.5,10.4,11.4],"cover"), box("CoverR",[7.5,10,4.6],[11.4,10.4,11.4],"cover"),
  box("PagesL",[4,10.4,5],[7.4,10.9,11],"paper"), box("PagesR",[7.6,10.4,5],[11,10.9,11],"paper"),
  box("Candle",[12,9,2],[13.5,12.5,3.5],"wax"), box("Wick",[12.6,12.5,2.6],[12.9,13.1,2.9],"cover"),
  box("Scroll",[2,9,12.5],[13,10.6,14.1],"paper"), box("ScrollEnd",[13,8.9,12.4],[13.6,10.7,14.2],"planks"),
])
# Mineshaft Entrance: a timber frame over a dark hole, with a ladder going down.
entrance=shape(["sides","planks","log","dark","ladder"], base+[
  box("Hole",[4,9,4],[12,9.2,12],"dark"),
  box("PostL",[3,9,7],[4.5,19,8.5],"log"), box("PostR",[11.5,9,7],[13,19,8.5],"log"),
  box("Beam",[2,19,6.5],[14,20.5,9],"log"),
  box("RailL",[5,9.2,9],[5.6,15,9.6],"ladder"), box("RailR",[10.4,9.2,9],[11,15,9.6],"ladder"),
  box("Rung1",[5.6,10.5,9.1],[10.4,11,9.5],"ladder"), box("Rung2",[5.6,12.5,9.1],[10.4,13,9.5],"ladder"),
  box("Rung3",[5.6,14.5,9.1],[10.4,15,9.5],"ladder"),
])
# Church: a gold cross standing on the lid, a book on a little lectern and two candles.
church=shape(["sides","planks","gold","book","page","wax"], base+[
  box("CrossPost",[7.25,9,3],[8.75,19,4.5],"gold"), box("CrossArm",[4.5,15,3],[11.5,16.5,4.5],"gold"),
  box("Lectern",[6.5,9,9],[9.5,12,12],"planks"), box("Book",[5,12,8.5],[11,12.8,12.5],"book"),
  box("Pages",[5.3,12.8,8.8],[10.7,13,12.2],"page"),
  box("CandleL",[2,9,9],[3,12,10],"wax"), box("CandleR",[13,9,9],[14,12,10],"wax"),
])
# Healer's Station: no crate, as it is not a hut; a small table with a red cross cloth, a rolled bandage and a jar of salve.
station_healer=shape(["planks","cross","roll","jar","cloth"], [
  box("Top",[1,10,1],[15,11.5,15],"planks"),
  box("Leg1",[2,0,2],[4,10,4],"planks"), box("Leg2",[12,0,2],[14,10,4],"planks"),
  box("Leg3",[2,0,12],[4,10,14],"planks"), box("Leg4",[12,0,12],[14,10,14],"planks"),
  box("Cloth",[2,11.5,2],[9,11.7,9],"cloth"),
  box("CrossA",[3,11.7,4.75],[8,11.9,6.25],"cross"), box("CrossB",[4.75,11.7,3],[6.25,11.9,8],"cross"),
  box("Roll",[9.5,11.5,3],[13.5,14.5,6],"roll"),
  box("Jar",[9.5,11.5,9.5],[12.5,14.5,12.5],"jar"), box("JarLid",[9.2,14.5,9.2],[12.8,15.1,12.8],"planks"),
])

if __name__ == "__main__":
    import os
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "../../assets/createacolony/shapes/block")
    for name, (tex, el) in models.items():
        json.dump(shape(tex, base + el), open(f"{out}/{name}.json", "w"), indent=1)
    for name, model in {"hut-smelter": smelter, "hut-blacksmith": smith, "hut-sawmill": saw, "hut-barracks": barracks, "hut-explorer": explorer, "hut-gatherer": gatherer, "hut-hunter": hunter, "hut-craftsman": craftsman, "hut-tailor": tailor, "hut-jeweller": jeweller, "hut-healer": healer, "hut-armourer": armourer, "hut-weaponsmith": weaponsmith, "hut-mint": mint, "hut-archive": archive, "hut-church": church, "workstation-healer": station_healer, "hut-mineshaftentrance": entrance, "supplycamp": camp, "fieldmarker": crow}.items():
        json.dump(model, open(f"{out}/{name}.json", "w"), indent=1)
