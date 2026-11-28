"""Build the three True 1.65 Traveler's Inn dye merchants from the active catalog."""
import argparse
import json
import math
from pathlib import Path
import sqlite3
import struct
import sys
import uuid

ROOT = Path(__file__).resolve().parents[3]
NOW = "2026-11-17 00:00:00"
NS = uuid.UUID("6b1d5c2e-1650-4a6e-9a1e-165165165165")
CONFIG = (
    ("alb", 1, 1, 9, "Maerwynn Dyeford", 75, "Godfrey", [(25, 58, 79), (26, 57, 40)]),
    ("mid", 2, 100, 102, "Yrsa Pigmenthand", 216, "Ragnhild", [(22, 248, 0), (23, 249, 0), (25, 245, 53), (27, 246, 0), (28, 247, 0), (26, 57, 49)]),
    ("hib", 3, 200, 202, "Síofra Dyemere", 317, "Eithne", [(25, 97, 67), (26, 96, 71)]),
)


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def z_at(point, triangles, reference):
    x, y = point
    hits = []
    for (ax, ay, az), (bx, by, bz), (cx, cy, cz) in triangles:
        if not (min(ax,bx,cx)-0.001 <= x <= max(ax,bx,cx)+0.001 and min(ay,by,cy)-0.001 <= y <= max(ay,by,cy)+0.001):
            continue
        det = (bx-ax)*(cy-ay)-(by-ay)*(cx-ax)
        if abs(det) < 0.001:
            continue
        u = ((x-ax)*(cy-ay)-(y-ay)*(cx-ax))/det
        v = ((bx-ax)*(y-ay)-(by-ay)*(x-ax))/det
        if u >= -0.00001 and v >= -0.00001 and u+v <= 1.00001:
            hits.append(az+u*(bz-az)+v*(cz-az))
    return min(hits, key=lambda z: abs(z-reference), default=None)


def route(db, layout, zone, region, source, npcs, patrols):
    from navdiff import tiles, world_bounds
    ax, ay, az = (source[k] for k in ("X", "Y", "Z"))
    _, tile_data, _ = tiles(ROOT / "runtime/server/navmesh-165" / f"zone{zone:03}.nav")
    triangles = []
    for _, raw in tile_data.values():
        x0,y0,x1,y1 = world_bounds(raw)
        if x0 > ax+950 or x1 < ax-950 or y0 > ay+950 or y1 < ay-950:
            continue
        h = struct.unpack_from("<5iI9i3f3f3ff",raw)
        pc,vc = h[6:8]
        verts = struct.unpack_from(f"<{vc*3}f",raw,100)
        for i in range(pc):
            poly = struct.unpack_from("<I6H6HHBB",raw,100+vc*12+i*32)
            if poly[15] >> 6 or poly[13] == 0 or poly[14] < 3:
                continue
            vs = [(verts[j*3]*32,verts[j*3+2]*32,verts[j*3+1]*32) for j in poly[1:1+poly[14]]]
            if all(abs(z-az) <= 45 for _,_,z in vs):
                triangles.extend((vs[0],vs[j],vs[j+1]) for j in range(1,len(vs)-1))

    avoid = [(n["world"]["x"],n["world"]["y"],n["radius_units"]+55)
             for n in layout["npc_spots"]["trader_roam"]["no_go"]]
    avoid += [(n["X"],n["Y"],max(140,(n["RoamingRange"] or 0)+90)) for n in npcs if n["Region"]==region]
    # Friendly realm guards have nonzero aggro against enemies. They are handled above as foot traffic.
    hazards = list(db.execute("SELECT Name,X,Y,AggroRange FROM Mob WHERE Region=? AND Realm<>? AND AggroLevel>0 AND abs(X-?)<4500 AND abs(Y-?)<4500",
                              (region,source["Realm"],ax,ay)))
    avoid += [(n["X"],n["Y"],max(n["AggroRange"] or 0,650)+500) for n in hazards]
    lanes = [(n["X"],n["Y"]) for n in patrols if abs(n["X"]-ax)<1000 and abs(n["Y"]-ay)<1000]
    candidates = sorted(((x,y) for x in range(ax-800,ax+801,25) for y in range(ay-800,ay+801,25)),
                        key=lambda pt: math.dist(pt,(ax,ay)))
    for x,y in candidates:
        square = [(x-70,y-70),(x+70,y-70),(x+70,y+70),(x-70,y+70)]
        samples = [(u+(v-u)*i/10,w+(q-w)*i/10)
                   for (u,w),(v,q) in zip(square,square[1:]+square[:1]) for i in range(10)]
        if any(math.dist((sx,sy),(u,v))<gap for sx,sy in samples for u,v,gap in avoid):
            continue
        if any(math.dist(pt,lane)<155 for pt in samples for lane in lanes):
            continue
        zs = [z_at(pt,triangles,az) for pt in samples]
        if any(z is None for z in zs) or max(zs)-min(zs) > 15:
            continue
        # Leave walkable space alongside the merchant for buyers.
        if any(z_at((sx+dx,sy+dy),triangles,az) is None for sx,sy in samples[::2]
               for dx,dy in ((42,0),(-42,0),(0,42),(0,-42))):
            continue
        points = [(sx,sy,round(zs[i*10])) for i,(sx,sy) in enumerate(square)]
        print(f"zone {zone}: route {points}, checked {len(samples)} perimeter points; hostile spawns checked {len(hazards)}")
        return points
    raise ValueError(f"No clear route near {source['Name']}")


def merchant_stock(db, prefix, realm):
    # Use the existing vendor catalog plus the realm's craftable colors.
    # Keep one entry per material/color and prefer the existing vendor SKU.
    colors = {}
    for row in db.execute("""
        SELECT i.Model, i.Color, i.Id_nb
        FROM MerchantItem m JOIN ItemTemplate i ON i.Id_nb=m.ItemTemplateID COLLATE NOCASE
        WHERE m.ItemListID='ClothLeatherEnamelDye2' AND i.Item_Type=40 AND i.Color>0
        ORDER BY m.PageNumber,m.SlotPosition
    """):
        realm_item = db.execute("""
            SELECT Id_nb FROM ItemTemplate
            WHERE Model=? AND Color=? AND Item_Type=40 AND Realm=?
              AND Name=(SELECT Name FROM ItemTemplate WHERE Id_nb=?) COLLATE NOCASE
            ORDER BY Id_nb LIMIT 1
        """, (row["Model"], row["Color"], realm, row["Id_nb"])).fetchone()
        colors.setdefault((row["Model"], row["Color"]), realm_item["Id_nb"] if realm_item else row["Id_nb"])

    for row in db.execute("""
        SELECT i.Model,i.Color,i.Id_nb FROM ItemTemplate i
        WHERE i.Realm=? AND i.Item_Type=40 AND i.Model IN (229,494,495) AND i.Color>0
          AND EXISTS (SELECT 1 FROM CraftedItem c WHERE c.Id_nb=i.Id_nb AND c.CraftingSkillType=4)
        ORDER BY i.Id_nb
    """, (realm,)):
        colors.setdefault((row["Model"], row["Color"]), row["Id_nb"])

    stock = []
    for model, remover in ((229, "cloth_bleach"), (494, "mild_acid_wash"), (495, "mild_acid_enamel_remover")):
        stock.extend(item for (kind, _), item in colors.items() if kind == model)
        stock.append(remover)
    assert len(stock) <= 150 and all(db.execute(
        "SELECT Realm FROM ItemTemplate WHERE Id_nb=?", (item,)).fetchone()["Realm"] in (0, realm)
        for item in stock)
    return [dict(MerchantItem_ID=f"{prefix}-{index // 30}-{index % 30}",
                 ItemListID=prefix, ItemTemplateID=item, PageNumber=index // 30,
                 SlotPosition=index % 30, LastTimeRowUpdated=NOW)
            for index, item in enumerate(stock)]


def build(output):
    sys.path.insert(0,str(ROOT / "development-source/server/tools/navmesh_splice"))
    db = sqlite3.connect((ROOT / "runtime/data/opendaoc-165.sqlite3.db").as_uri()+"?mode=ro",uri=True)
    db.row_factory = sqlite3.Row
    npcs = read(ROOT/"runtime/true165/add-inns165.json")["rows"]
    patrols = read(ROOT/"runtime/true165/add-pathpoints165.json")["rows"]
    tables = {t:(key,[]) for t,key in (("Mob","Mob_ID"),("Path","Path_ID"),("PathPoints","PathPoints_ID"),
                                       ("NPCEquipment","NPCEquipment_ID"),("MerchantItem","MerchantItem_ID"))}
    for code,realm,region,zone,name,model,anchor,outfit in CONFIG:
        vendor_id = str(uuid.uuid5(NS,f"{code}/dye_trader"))
        assert not db.execute("SELECT 1 FROM Mob WHERE Name=? COLLATE NOCASE AND Mob_ID<>?",(name,vendor_id)).fetchone()
        layout = read(ROOT.parent/"1.65 MODE CLIENT (local only)/layouts"/f"inn_{code}.json")
        source = next(n for n in npcs if n["Name"]==anchor and n["Region"]==region)
        points = route(db,layout,zone,region,source,npcs,patrols)
        prefix = f"true165_inn_{code}_dye"
        mob = dict(source)
        x,y,z = points[0]
        mob.update(Name=name,Guild="Dye Merchant",ClassType="DOL.GS.GameMerchant",X=x,Y=y,Z=z,
                   Model=model,Gender=2,Size=50,Heading=3072,Speed=70,Realm=realm,Race={1:3,2:5,3:9}[realm],Flags=0,
                   AggroLevel=0,AggroRange=0,FactionID=0,Brain=None,NPCTemplateID=-1,VisibleWeaponSlots=255,
                   PathID=prefix,RoamingRange=0,EquipmentTemplateID=prefix,ItemsListTemplateID=prefix,
                   LastTimeRowUpdated=NOW,Mob_ID=vendor_id)
        tables["Mob"][1].append(mob)
        tables["Path"][1].append(dict(Path_ID=prefix+"-path",PathID=prefix,PathType=3,RegionID=region,LastTimeRowUpdated=NOW))
        for step,(x,y,z) in enumerate(points,1):
            tables["PathPoints"][1].append(dict(PathPoints_ID=f"{prefix}-{step}",PathID=prefix,Step=step,X=x,Y=y,Z=z,MaxSpeed=70,WaitTime=45,LastTimeRowUpdated=NOW))
        for slot,item,color in outfit:
            tables["NPCEquipment"][1].append(dict(NPCEquipment_ID=f"{prefix}-{slot}",TemplateID=prefix,Slot=slot,Model=item,Color=color,Effect=0,Extension=0,Emblem=0,LastTimeRowUpdated=NOW))
        tables["MerchantItem"][1].extend(merchant_stock(db, prefix, realm))
    db.close()
    output.mkdir(parents=True,exist_ok=True)
    for table,(key,rows) in tables.items():
        (output/f"add-inn-dye-{table.lower()}165.json").write_text(
            json.dumps(dict(table=table,key=key,reason="Traveler's Inn dye merchants",rows=rows),indent=1,ensure_ascii=False)+"\n",encoding="utf-8")
        print(table,len(rows))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output",type=Path,required=True)
    build(parser.parse_args().output)
