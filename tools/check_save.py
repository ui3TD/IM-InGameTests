"""Check an Idol Manager save for internal consistency before using it as a fixture.

Looks for the problems that crash or quietly break a load: IDs that point at nothing,
rooms whose job doesn't match the idol or staffer in them, floors of the wrong width,
counters below the IDs in use, values the game can't parse, and portraits whose parts
the game doesn't have or that come from different bodies.

    python tools/check_save.py fixtures/default.json

Exits 1 and lists every problem found, or prints OK.
"""

import argparse
import re
import sys
from datetime import datetime
from pathlib import Path

from savefile import in_base_game, load, portrait_part

# data_girls._status
NORMAL, PRACTICE, INJURED, SCENE, GRADUATED, DEPRESSED, HIATUS, ANNOUNCED = range(8)
UNAVAILABLE = {INJURED, GRADUATED, DEPRESSED, HIATUS}  # removed from unreleased work by RemoveFromEverything

# agency._type -> width in floor units (agency.roomSpace)
ROOM_WIDTH = {0: 1, 1: 1, 2: 2, 3: 2, 4: 1, 5: 2, 6: 5, 7: 5, 8: 1, 9: 2, 10: 3, 11: 4, 12: 5, 13: 2}
THEATRE, CAFE, BUILD_FLOOR = 6, 7, 12

# staff._type -> agency._type of the room it works in (staff.GetRoomType)
STAFF_ROOM = {0: 2, 1: 2, 2: 3, 3: 3, 4: 1, 5: 1, 6: 5, 9: 5, 7: 4, 8: 4, 10: 0, 11: 0}

# agency._room._status values that need something in the room
TRAINING, SINGLE_PROD, SHOW_PROD, CONCERT_PROD, SSK_PROD, TOUR_PROD, LOAN = 1, 2, 12, 15, 16, 17, 18
SUBSTORY_SCENE, INJURY_TREATMENT, DEPRESSION_TREATMENT = 14, 19, 20

FINISHED = 2  # SEvent_Tour.tour._status, shared by tours, concerts and elections
DATE_FORMAT = "%Y-%m-%d %H:%M:%S"
LOOKS_LIKE_DATE = re.compile(r"^\d{4}-\d{2}-\d{2}")


def as_int(value, default=-1):
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def check(data: dict) -> list:
    errors = []

    def err(msg):
        errors.append(msg)

    # Format: every scalar is a non-empty string; dates parse.
    def scan(node, path):
        if isinstance(node, dict):
            for k, v in node.items():
                scan(v, f"{path}.{k}")
        elif isinstance(node, list):
            for i, v in enumerate(node):
                scan(v, f"{path}[{i}]")
        elif not isinstance(node, str):
            err(f"{path}: value {node!r} is not a string")
        elif node == "":
            err(f"{path}: empty string (the game's saves leave these out)")
        elif LOOKS_LIKE_DATE.match(node):
            try:
                datetime.strptime(node, DATE_FORMAT)
            except ValueError:
                err(f"{path}: {node!r} is not a yyyy-MM-dd HH:mm:ss date")

    scan(data, "save")

    girls = {g["id"]: g for g in data.get("data_girls__Girls", [])}
    staff = {s["id"]: s for s in data.get("staff__Staff", [])}
    singles = {s["id"]: s for s in data.get("singles__Singles", [])}
    shows = {s["id"]: s for s in data.get("shows__Shows", [])}
    tours = {t["ID"]: t for t in data.get("SEvent_Tour__Tours", [])}
    concerts = {c["ID"]: c for c in data.get("SEvent_Concert__Concerts", [])}
    elections = {e["ID"]: e for e in data.get("SEvent_SSK__Elections", [])}
    loans = {l["ID"]: l for l in data.get("loans__LoanData", [])}
    theaters = {t["ID"]: t for t in data.get("Theaters__Theaters", [])}
    cafes = {c["ID"]: c for c in data.get("Cafes__Cafes", [])}
    groups = {g["ID"]: g for g in data.get("Groups__Groups", [])}

    def status(girl_id):
        return as_int(girls[girl_id]["status"]) if girl_id in girls else None

    def need_girl(girl_id, where, allow_none=True):
        if allow_none and girl_id == "-1":
            return
        if girl_id not in girls:
            err(f"{where}: idol {girl_id} doesn't exist")

    # Counters must stay at or above the largest ID in use.
    for counter, ids in [
        ("data_girls__LastGirlID", girls), ("staff__LastStaffID", staff), ("singles__LastSingleID", singles),
        ("shows__LastShowID", shows), ("SEvent_Tour__LastTourID", tours), ("SEvent_Concert__LastConcertID", concerts),
        ("SEvent_SSK__LastSSKID", elections), ("loans__LastLoanID", loans),
    ]:
        if ids and as_int(data.get(counter), 0) < max(as_int(i, 0) for i in ids):
            err(f"{counter} {data.get(counter)} is below the largest ID in use ({max(as_int(i, 0) for i in ids)})")

    # Idols: all 20 parameters with _val.
    for gid, girl in girls.items():
        types = set()
        for p in girl.get("parameters", []):
            types.add(as_int(p.get("type")))
            if "_val" not in p:
                err(f"idol {gid}: parameter {p.get('type')} has no _val (old 'val' format)")
        if types != set(range(20)):
            err(f"idol {gid}: parameters {sorted(types)} should be types 0-19")

    # Portraits: one body's parts, each one the game has. Parts from a mod can't be checked here.
    for gid, girl in girls.items():
        bodies = set()
        for asset in girl.get("textureAssets", []):
            part = portrait_part(asset.get("asset_id", ""))
            if part is None:
                err(f"idol {gid}: portrait part {asset.get('asset_id')!r} isn't a portrait ID")
                continue
            bodies.add(part[1])
            if not part[3] and not in_base_game(asset["asset_id"]):
                err(f"idol {gid}: portrait part {asset['asset_id']!r} isn't in the base game (drawn misaligned)")
        if len(bodies) > 1:
            err(f"idol {gid}: portrait mixes parts of bodies {sorted(bodies)} (drawn misaligned)")

    # Groups: every non-graduated idol in exactly one group.
    membership = {}
    for grp in groups.values():
        for gid in grp.get("Girls", []):
            need_girl(gid, f"group {grp['ID']}", allow_none=False)
            membership.setdefault(gid, []).append(grp["ID"])
        for sid in grp.get("Singles", []):
            if sid not in singles:
                err(f"group {grp['ID']}: single {sid} doesn't exist")
    for gid in girls:
        if status(gid) != GRADUATED and len(membership.get(gid, [])) != 1:
            err(f"idol {gid} is in groups {membership.get(gid, [])}; an active idol must be in exactly one")

    # Floors and rooms.
    floors = data.get("agency__Floors", [])
    for edge, floor in (("first", floors[0] if floors else None), ("last", floors[-1] if floors else None)):
        if floor is None or [as_int(r["Type"]) for r in floor["Rooms"]] != [BUILD_FLOOR]:
            err(f"agency__Floors: the {edge} floor must be a single empty type-12 build floor")
    rooms_by_girl = {}
    theatre_rooms, cafe_rooms = [], []
    for floor in floors:
        width = sum(ROOM_WIDTH.get(as_int(r["Type"]), 0) for r in floor["Rooms"])
        if width != 5:
            err(f"floor {floor['FloorID']}: rooms are {width} units wide, must be 5")
        for room in floor["Rooms"]:
            rtype, rstatus = as_int(room["Type"]), as_int(room["status"])
            where = f"floor {floor['FloorID']} room type {rtype}"
            for key in ("girl", "cafe_girl_1", "cafe_girl_2", "cafe_girl_3"):
                gid = room.get(key, "-1")
                need_girl(gid, f"{where} {key}")
                if gid != "-1":
                    rooms_by_girl.setdefault(gid, []).append(where)
            sid = room.get("staffer", "-1")
            if sid != "-1":
                if sid not in staff:
                    err(f"{where}: staffer {sid} doesn't exist")
                elif STAFF_ROOM.get(as_int(staff[sid]["type"])) != rtype:
                    err(f"{where}: staffer {sid} (type {staff[sid]['type']}) can't work in this room type")
            for key, table, statuses in (
                ("single", singles, {SINGLE_PROD}), ("show", shows, {SHOW_PROD}), ("tour", tours, {TOUR_PROD}),
                ("concert", concerts, {CONCERT_PROD}), ("ssk", elections, {SSK_PROD}), ("loan", loans, {LOAN}),
            ):
                ref = room.get(key, "-1")
                if ref != "-1" and ref not in table:
                    err(f"{where}: {key} {ref} doesn't exist")
                if rstatus in statuses and ref == "-1":
                    err(f"{where}: status {rstatus} needs a {key}")
            if rstatus == SUBSTORY_SCENE:
                err(f"{where}: status 14 (substory scene) isn't saved and crashes on load")
            if rstatus == TRAINING and status(room.get("girl")) != PRACTICE:
                err(f"{where}: training room, but idol {room.get('girl')} isn't in practice status")
            if rstatus in (INJURY_TREATMENT, DEPRESSION_TREATMENT):
                want = INJURED if rstatus == INJURY_TREATMENT else DEPRESSED
                if status(room.get("girl")) != want:
                    err(f"{where}: treatment room, but idol {room.get('girl')} isn't status {want}")
                if sid == "-1":
                    err(f"{where}: treatment needs a doctor")
            if rstatus == SINGLE_PROD and room.get("single") in singles and as_int(singles[room["single"]]["status"]) != 1:
                err(f"{where}: producing single {room['single']}, which isn't in working status")
            if rtype == THEATRE:
                theatre_rooms.append(room)
            if rtype == CAFE:
                cafe_rooms.append(room)
            if rtype in (THEATRE, CAFE) and len(floor["Rooms"]) != 1:
                err(f"floor {floor['FloorID']}: a theater or cafe takes the whole floor")

    for gid in girls:
        if status(gid) == PRACTICE and gid not in rooms_by_girl and not any(
                gid in c.get("WorkingGirls", []) for c in cafes.values()):
            err(f"idol {gid} is in practice status but in no room or cafe")
        if status(gid) == SCENE:
            in_scene = any(gid in r.get("sceneGirls", []) for f in floors for r in f["Rooms"])
            if not in_scene:
                err(f"idol {gid} is in scene status but in no scene room")

    for kind, rooms, table in (("theater", theatre_rooms, theaters), ("cafe", cafe_rooms, cafes)):
        linked = [r["TheaterID"] for r in rooms]
        for tid in linked:
            if tid not in table:
                err(f"a {kind} room points at {kind} {tid}, which doesn't exist")
        for tid, rec in table.items():
            if linked.count(tid) != 1:
                err(f"{kind} {tid} has {linked.count(tid)} rooms; it needs exactly one")
            if rec.get("Group") not in groups:
                err(f"{kind} {tid}: group {rec.get('Group')} doesn't exist")
        for key in ("WaitStaff", "WorkingGirls"):
            for rec in table.values():
                for gid in rec.get(key, []):
                    need_girl(gid, f"{kind} {rec['ID']} {key}", allow_none=False)

    # Singles: unreleased casts can't hold idols who are away.
    for sid, single in singles.items():
        for gid in single.get("girls", []):
            need_girl(gid, f"single {sid} cast")
            if single["status"] != "2" and gid != "-1" and status(gid) in UNAVAILABLE:
                err(f"single {sid} is unreleased but casts idol {gid} (status {status(gid)})")

    # Shows: per-episode arrays match the episode count.
    for sid, show in shows.items():
        n = as_int(show.get("episodeCount"), 0)
        for key in ("audience", "revenue", "fans", "buzz", "fatigue", "fame", "famePoints"):
            if len(show.get(key, [])) != n:
                err(f"show {sid}: {key} has {len(show.get(key, []))} entries for {n} episodes")

    # Special events.
    def need_params(kind, rec, types):
        have = {as_int(p.get("type")) for p in rec.get("parameters", [])}
        if not set(types) <= have:
            err(f"{kind} {rec['ID']}: parameters {sorted(have)} must include {sorted(types)}")

    for tour in tours.values():
        need_params("tour", tour, (0, 1))
    for concert in concerts.values():
        need_params("concert", concert, (0, 1, 2))
        for item in concert.get("SetListItems", []):
            for gid in item.get("Girls", []):
                need_girl(gid, f"concert {concert['ID']} setlist")
                if concert["Status"] != "2" and status(gid) in UNAVAILABLE:
                    err(f"concert {concert['ID']} is unfinished but its setlist has idol {gid} (status {status(gid)})")
            if item.get("IsMC") != "true" and item.get("Single") not in singles:
                err(f"concert {concert['ID']}: setlist single {item.get('Single')} doesn't exist")
    for ssk in elections.values():
        need_params("election", ssk, (0, 1))
        for key, table in (("Single", singles), ("Concert", concerts), ("ReleaseSingle", singles)):
            if ssk.get(key, "-1") != "-1" and ssk[key] not in table:
                err(f"election {ssk['ID']}: {key} {ssk[key]} doesn't exist (this throws during load)")
        for res in ssk.get("Results", []):
            need_girl(res.get("Girl", "-1"), f"election {ssk['ID']} results")

    event_status = {e["Type"]: e["Status"] for e in data.get("SpecialEvents_Manager__EventData", [])}
    for etype, pointer, table, kind in (("0", "SEvent_Concert__Concert", concerts, "concert"),
                                        ("1", "SEvent_SSK__SSK", elections, "election"),
                                        ("2", "SEvent_Tour__Tour", tours, "tour")):
        current = data.get(pointer, "-1")
        if current != "-1" and current not in table:
            err(f"{pointer} {current} doesn't exist")
        in_production = current in table and table[current]["Status"] != "2"
        if event_status.get(etype) == "1" and not in_production:
            err(f"special event {etype} is in production but {pointer} has no unfinished {kind}")

    # People references elsewhere.
    for p in data.get("Dating__Partners", []):
        need_girl(p.get("GirlID", "-1"), "Dating__Partners", allow_none=False)
    push = data.get("pushes__Data", {})
    if len(push.get("girls", [])) != 3 or len(push.get("days", [])) != 3:
        err("pushes__Data: girls and days must have exactly 3 entries each")
    for gid in push.get("girls", []):
        need_girl(gid, "pushes__Data")
    for m in data.get("Girls_Mentors__Mentors", []):
        need_girl(m.get("Senpai", "-1"), "Girls_Mentors senpai", allow_none=False)
        need_girl(m.get("Kohai", "-1"), "Girls_Mentors kohai", allow_none=False)

    pairs = set()
    for rel in data.get("Relationships__RelationshipsData", []):
        a, b = rel["Girls"]
        need_girl(a, "relationship", allow_none=False)
        need_girl(b, "relationship", allow_none=False)
        key = frozenset((a, b))
        if key in pairs:
            err(f"relationship {a}-{b} appears twice")
        pairs.add(key)
        if rel.get("Dating") == "true":
            for gid in (a, b):
                if gid in girls and girls[gid]["DatingData"].get("Partner_Status") != "3":
                    err(f"idols {a} and {b} are dating but idol {gid} isn't marked taken_idol")
    for clique in data.get("Relationships__Cliques", []):
        for gid in [clique.get("Leader", "-1")] + clique.get("Members", []) + clique.get("Bullied_Girls", []):
            need_girl(gid, "clique")

    return errors


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("save", type=Path)
    args = ap.parse_args()
    errors = check(load(args.save))
    for e in errors:
        print(e)
    if errors:
        print(f"{len(errors)} problem(s) in {args.save}")
        return 1
    print(f"OK: {args.save}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
