"""Add the game states tests build on to the default fixture.

Each step below sets absolute values and inserts or replaces records by ID, so running
the script on its own output changes nothing:

    python tools/build_fixture.py fixtures/default.json fixtures/default.json

New records are cloned from a record of the same kind already in the save, then adjusted
the way the game's own code would. fixtures/README.md lists what each step adds and the
IDs tests can rely on. The result is checked with check_save.py before it's written.
"""

import argparse
import copy
import sys
from pathlib import Path

from check_save import check
from savefile import load, write

NEVER = "0001-01-01 00:00:00"   # DateTime.MinValue
UNSET = "1900-01-01 00:00:00"   # the game's "not set" date for idol fields

# data_girls._status
NORMAL, PRACTICE, INJURED, SCENE, GRADUATED, DEPRESSED, HIATUS, ANNOUNCED = range(8)


# ---------------------------------------------------------------- lookups

def by_id(records, key, value):
    for r in records:
        if r[key] == value:
            return r
    raise KeyError(f"no record with {key}={value}")


def upsert(records, key, record):
    for i, r in enumerate(records):
        if r[key] == record[key]:
            records[i] = record
            return
    records.append(record)


def girl(d, gid):
    return by_id(d["data_girls__Girls"], "id", gid)


def staffer(d, sid):
    return by_id(d["staff__Staff"], "id", sid)


def single(d, sid):
    return by_id(d["singles__Singles"], "id", sid)


def show(d, sid):
    return by_id(d["shows__Shows"], "id", sid)


def group(d, gid):
    return by_id(d["Groups__Groups"], "ID", gid)


def room(d, floor_id, room_type):
    floor = by_id(d["agency__Floors"], "FloorID", floor_id)
    for r in floor["Rooms"]:
        if r["Type"] == room_type:
            return r
    raise KeyError(f"no room of type {room_type} on floor {floor_id}")


def param(g, ptype):
    return by_id(g["parameters"], "type", ptype)


def relationship(d, a, b):
    for rel in d["Relationships__RelationshipsData"]:
        if set(rel["Girls"]) == {a, b}:
            return rel
    rel = {"Girls": [a, b], "Vals": ["1", "-1"], "Dating": "false", "Temp": "0"}  # Relationships.CreateRelationships
    d["Relationships__RelationshipsData"].append(rel)
    return rel


def set_status(g, status, previous=NORMAL):
    g["status"] = str(status)
    g["previous_status"] = str(previous)


def counter_at_least(d, key, value):
    d[key] = str(max(int(d[key]), value))


def start_work(s, skill):
    s["isWorking"] = "true"
    s["isResearching"] = "false"
    s["skillInUse"] = str(skill)


# ---------------------------------------------------------------- steps

def options_on(d):
    """All five new-game options on, as a new game defaults to (staticVars._playerData)."""
    for option in d["staticVars__PlayerData"]["Options"]:
        option["Val"] = "true"


def cleanup(d):
    """Idol 3 was left in scene status with no scene running; she's idle."""
    set_status(girl(d, "3"), NORMAL)


NEW_IDOLS = [
    # id, clone of, group, first, last, birthday, sexuality (0 straight, 1 bi, 2 lesbian), trait, hired,
    # portrait (body, hair, face) on a base-game body no other idol uses
    ("222", "11", "0", "春香", "森", "2000-05-14 05:50:00", "2", "33", "2024-03-11 05:50:00",
     ("0 4 2", "1 4 3", "2 4 1")),    # Amorous
    ("223", "13", "0", "美咲", "高橋", "2005-10-02 05:50:00", "0", "1", "2024-03-11 05:50:00",
     ("0 8 1", "1 8 5", "2 8 4")),    # Prodigy
    ("224", "9", "1", "優奈", "佐藤", "2007-08-08 05:50:00", "0", "34", "2024-03-18 05:50:00",
     ("0 9 3", "1 9 7", "2 9 2")),    # Loyal
    ("225", "6", "1", "結衣", "中村", "2003-02-20 05:50:00", "0", "11", "2024-03-18 05:50:00",
     ("0 10 0", "1 10 12", "2 10 5")),  # Indiscreet
]

DATING_FREE = {
    "Previous_Attempt": "0", "Success_Counter": "0", "Partner_Status": "0", "Partner_Status_Known_To_Player": "0",
    "Is_Partner_Status_Known": "false", "Is_Sexuality_Known": "false", "Is_Uninterested": "false",
    "Had_Scandal": "false", "Had_Scandal_Ever": "false", "Used_Goods": "false", "Dated_Idol": "false",
}


def new_idols(d):
    """Four newly hired idols, two per group, cloned from existing idols' stats, each with her own face."""
    for gid, source, grp, first, last, birthday, sexuality, trait, hired, portrait in NEW_IDOLS:
        g = copy.deepcopy(girl(d, source))
        g.pop("Wish_Formula", None)
        g.update({
            "id": gid, "firstName": first, "lastName": last, "birthday": birthday, "sexuality": sexuality,
            "trait": trait, "Hiring_Date": hired, "Trivia": [], "Variables": [], "RowInSenbatsu": [],
            "Wish_Type": "0", "Wish_Fulfilled": "false", "Wish_Effect_Until": UNSET, "LastDate": UNSET,
            "TransferDate": UNSET, "BullyStopped": UNSET, "LastDatingScandal": NEVER, "ScandalPoints": "0",
            "Hiatus_Coeff": "1", "HiatusEnd": NEVER, "Depression_Counter": "0", "Injury_Counter": "0",
            "Earnings_CurrentMonth": "0", "Earnings_History": [], "Rel_Friendship_Points": "0",
            "Rel_Influence_Points": "0", "Rel_Romance_Points": "0", "RelationshipsKnown": "false",
            "DatingData": dict(DATING_FREE), "Graduation_Date": UNSET, "Graduation_History": [],
            "Will_Graduate_At_18": "false", "SSK_Expected_Place": "0", "SSK_History": [], "DateHistory": [],
            "Stats": {k: "0" for k in g["Stats"]}, "StatsYearly": {k: "0" for k in g["StatsYearly"]},
            "textureAssets": [{"type": asset_id[0], "asset_id": asset_id} for asset_id in portrait],
        })
        set_status(g, NORMAL)
        param(g, "10")["_val"] = "1500"   # famePoints: a new hire's
        param(g, "19")["_val"] = "0"      # scandalPoints
        for fans in g["Fans"]:
            fans["people"] = str(max(1, int(fans["people"]) // 100))
        upsert(d["data_girls__Girls"], "id", g)
        for other in group(d, "0")["Girls"], group(d, "1")["Girls"]:
            if gid in other:
                other.remove(gid)
        group(d, grp)["Girls"].append(gid)
    counter_at_least(d, "data_girls__LastGirlID", max(int(n[0]) for n in NEW_IDOLS))
    # Relationships.CreateRelationships: a pair with every idol who hasn't graduated.
    for gid, *_ in NEW_IDOLS:
        for other in d["data_girls__Girls"]:
            if other["id"] != gid and other["status"] != str(GRADUATED):
                relationship(d, gid, other["id"])


NEW_STAFF = [
    # id, type, first, last, skills [(skill_type, primary)] as staff.GenerateSkills gives them
    ("107", "9", "恵子", "石井", [("4", "true"), ("5", "false")]),   # psychiatrist
    ("108", "7", "由美", "小林", [("7", "false"), ("6", "true")]),   # stylist (cute/pretty)
    ("109", "4", "健一", "山本", [("3", "false"), ("2", "true")]),   # production manager
]


def new_staff(d):
    """A psychiatrist, a stylist and a production manager, so every room type can be staffed."""
    for sid, stype, first, last, skills in NEW_STAFF:
        s = copy.deepcopy(staffer(d, "47"))
        primary = next(t for t, p in skills if p == "true")
        s.update({
            "id": sid, "type": stype, "firstName": first, "lastName": last, "HireDate": "2024-03-01 00:00:00",
            "skills": [{"skill_type": t, "primary": p, "exp": "50000" if p == "true" else "0",
                        "lastFrameWorking": "false"} for t, p in skills],
            "LevelledUp": "false", "PreferredStyle": "0", "PreferredProposals": "-1", "isWorking": "false",
            "isResearching": "false", "skillInUse": primary, "state": "0", "textureAssets": [],
        })
        upsert(d["staff__Staff"], "id", s)
    counter_at_least(d, "staff__LastStaffID", max(int(n[0]) for n in NEW_STAFF))


ORIGINAL_FLOORS = {"0", "1", "2", "3"}


def floors(d):
    """Three new floors: doctor/dressing/office rooms on top, a sister-group theater and a cafe below.

    Floor IDs order floors by build time (rent rises with it); the two empty build floors
    at the ends always hold the largest IDs (agency.addRoom).
    """
    template = copy.deepcopy(d["agency__Floors"][0]["Rooms"][0])

    def new_room(rtype, **fields):
        r = copy.deepcopy(template)
        r["Type"] = rtype
        r.update(fields)
        return r

    def new_floor(fid, rooms):
        return {"FirstFloor": "false", "FloorID": fid, "Rooms": rooms}

    kept = [f for f in d["agency__Floors"] if f["FloorID"] in ORIGINAL_FLOORS]
    basement_at = next(i for i, f in enumerate(kept) if f["FloorID"] == "1")  # the original theater floor
    above, below = kept[:basement_at], kept[basement_at:]
    d["agency__Floors"] = (
        [new_floor("7", [new_room("12")]),
         new_floor("4", [new_room("5", staffer="107"), new_room("4", staffer="108"),
                         new_room("1", staffer="109"), new_room("8")])]
        + above + below
        + [new_floor("5", [new_room("6", TheaterID="1")]),
           new_floor("6", [new_room("7", TheaterID="0")]),
           new_floor("8", [new_room("12")])]
    )


def idol_statuses(d):
    """One idol in each status: training, injured (in treatment), depressed, hiatus, announced graduation."""
    # Training: idol 167 practicing in the dance studio (agency._room.assign).
    g = girl(d, "167")
    set_status(g, PRACTICE)
    dance = float(param(g, "5")["_val"])
    studio = room(d, "2", "2")
    studio.update({"status": "1", "girl": "167", "startTime": "2024-03-23 00:00:00", "duration": "5400",
                   "finishTime": "2024-03-26 18:00:00", "CompletionTime": "5400",
                   "Progress": repr(round(dance - int(dance), 6)), "practicing_style": "0"})
    start_work(staffer(d, "76"), 1)  # coaching

    # Injured, being treated by the physician (agency._room.assign_treatment: 5 days).
    g = girl(d, "13")
    set_status(g, INJURED)
    g["Injury_Counter"] = "1"
    g["Hiatus_Coeff"] = "1"
    doctor = room(d, "3", "5")
    doctor.update({"status": "19", "girl": "13", "startTime": "2024-03-22 03:50:00", "duration": "0",
                   "finishTime": "2024-03-27 03:50:00", "CompletionTime": "7200", "Progress": "0"})
    start_work(staffer(d, "104"), 4)  # physical_health

    # Depressed, untreated; the psychiatrist's office upstairs is free.
    g = girl(d, "6")
    set_status(g, DEPRESSED)
    g["Depression_Counter"] = "1"
    g["Hiatus_Coeff"] = "1"

    # On hiatus until late May.
    g = girl(d, "8")
    set_status(g, HIATUS)
    g["HiatusEnd"] = "2024-05-26 03:50:00"

    # Announced graduation; graduates late May.
    g = girl(d, "73")
    set_status(g, ANNOUNCED)
    g["Graduation_Date"] = "2024-05-26 00:00:00"


def wishes(d):
    """Two active wishes, filled the way girl_wishes.GenerateWish does."""
    g = girl(d, "1")
    rows = [int(r) for r in g["RowInSenbatsu"] if r != "-1"]
    best = min(rows) if rows else 1000
    g["Wish_Type"] = "1"  # single
    g["Wish_Formula"] = "-1 -1 -1 -1" if best == 1000 else ("0 -1 -1 -1" if best == 0 else f"{best - 1} -1 -1 -1")
    g["Wish_Fulfilled"] = "false"

    g = girl(d, "9")
    g["Wish_Type"] = "2"  # show; not in a permanent cast, so any show will do
    g["Wish_Formula"] = "-1 -1 -1"
    g["Wish_Fulfilled"] = "false"


def relationships(d):
    """An idol couple, the player's girlfriend, a known boyfriend, a hostile pair, bullying, a mentor, pushes."""
    # Idols 71 (bi) and 222 (lesbian) dating each other (Relationships._relationship.StartDating).
    rel = relationship(d, "71", "222")
    rel.update({"Vals": ["1"] * 8 + ["-1", "2"], "Dating": "true", "Temp": "0"})
    for gid in ("71", "222"):
        dating = girl(d, gid)["DatingData"]
        dating.update({"Partner_Status": "3", "Dated_Idol": "true"})

    # The player is dating idol 12 (fun route, after the first public date).
    g = girl(d, "12")
    g["DatingData"] = dict(DATING_FREE)
    g["Rel_Friendship_Points"] = "200"
    g["Rel_Romance_Points"] = "180"
    d["Dating__Partners"] = [{"GirlID": "12", "Route": "0", "Progress": "1", "Status": "1"}]

    # Idol 9's outside boyfriend is known to the player.
    girl(d, "9")["DatingData"].update({"Partner_Status_Known_To_Player": "1", "Is_Partner_Status_Known": "true"})

    # Idols 3 and 13 hate each other.
    relationship(d, "3", "13").update({"Vals": ["-1"] * 9 + ["1"], "Dating": "false", "Temp": "0"})

    # The clique led by idol 11 bullies idol 224, and the player knows.
    clique = by_id(d["Relationships__Cliques"], "Leader", "11")
    clique.update({"Known": "true", "Bullied_Girls": ["224"], "KnownBulliedGirls": ["224"], "StoppedBullying": []})

    d["Girls_Mentors__Mentors"] = [{"Senpai": "11", "Kohai": "223"}]
    d["pushes__Data"] = {"girls": ["1", "12", "222"], "days": ["0", "15", "28"]}


def schedule(*days):
    return [{"Type": t, "FanType_Everyone": "true" if f is None else "false", "FanType": "3" if f is None else f}
            for t, f in days]


# Theaters._theater._schedule._type: 0 auto, 1 performance, 2 manzai, 3 day off.
# resources.fanType: 0 male, 1 female, 2 casual, 3 hardcore, 4 teen, 5 young adult, 6 adult; None = everyone.
MAIN_WEEK = schedule(("1", None), ("1", "3"), ("2", "4"), ("0", None), ("1", "1"), ("2", None), ("3", None))
SISTER_WEEK = schedule(("1", None), ("3", None), ("1", "2"), ("3", None), ("2", None), ("1", "0"), ("3", None))
SUBSCRIBERS = ["400", "300", "200", "150", "100", "80", "250", "200", "150", "90", "60", "40"]


def theaters(d):
    """The main theater runs every kind of day with subscribers and streaming; the sister group gets its own."""
    main = by_id(d["Theaters__Theaters"], "ID", "0")
    main["Schedule"] = MAIN_WEEK
    for sub, people in zip(main["Subscribers"], SUBSCRIBERS):
        sub["People"] = people
    main["Streaming_Researched"] = "true"
    main["Equipment_Purchased"] = "true"

    sister = copy.deepcopy(main)
    sister.update({"ID": "1", "Group": "1", "Schedule": SISTER_WEEK, "Doing_Now": "3", "Stats": [],
                   "Streaming_Researched": "false", "Equipment_Purchased": "false",
                   "Subscription_Price": "2000", "Ticket_Price": "6000"})
    for sub in sister["Subscribers"]:
        sub["People"] = "0"
    upsert(d["Theaters__Theaters"], "ID", sister)


# Cafes.GetDishTypeByParam: (normal, gold at 80+) per stat parameter type.
DISH_BY_PARAM = {"0": ("0", "1"), "1": ("2", "3"), "2": ("4", "5"), "3": ("6", "7"),
                 "4": ("8", "9"), "5": ("14", "15"), "6": ("10", "11"), "7": ("12", "13")}


def cafe(d):
    """A cafe for the main group, with three dishes on the menu (Cafe_Popup.GenerateDish)."""
    dishes = []
    for dish_id, gid in enumerate(("1", "3", "11")):
        stats = [p for p in girl(d, gid)["parameters"] if p["type"] in DISH_BY_PARAM]
        best = max(stats, key=lambda p: float(p["_val"]))
        normal, gold = DISH_BY_PARAM[best["type"]]
        dishes.append({"ID": str(dish_id), "Group": "0", "Girl": gid,
                       "Type": gold if float(best["_val"]) >= 80 else normal,
                       "Average_Check": best["_val"], "Novelty": "100"})
    upsert(d["Cafes__Cafes"], "ID", {
        "ID": "0", "Group": "0", "WorkingGirls": [], "WaitStaff": ["1", "3", "11", "71", "222", "223"],
        "Staff_StaminaLimit": "75", "Cafe_Prio": "1", "Staff_Prio": "2", "Dishes": dishes,
        "Menu": ["0", "1", "2", "-1", "-1", "-1", "-1"], "Stats": [],
    })


def cast(*ids):
    return list(ids) + ["-1"] * (15 - len(ids))


def singles_(d):
    """Single 32 ready to release, 33 in production in the recording studio, 34 just started."""
    s = single(d, "33")
    s.update({"status": "1", "qty": "50000", "productionCost": single(d, "32")["productionCost"],
              "girls": cast("1", "3", "11", "71", "222", "223", "12", "9", "224", "225")})
    for p, val in zip(s["parameters"], ("45", "20", "0", "0")):
        p["val"] = val
    studio = room(d, "3", "3")
    studio.update({"status": "2", "single": "33", "startTime": "2024-03-22 00:00:00", "duration": "5400",
                   "finishTime": "2024-03-25 18:00:00", "CompletionTime": "5400", "Progress": "0"})
    start_work(staffer(d, "67"), 2)  # production

    s = copy.deepcopy(single(d, "32"))
    s.update({"id": "34", "status": "0", "title": "Morning Light Parade", "marketing": [],
              "Marketing_Result": "50", "Marketing_Result_Status": "0",
              "girls": cast("3", "1", "11", "71", "222", "223")})
    for p in s["parameters"]:
        p["val"] = "0"
    upsert(d["singles__Singles"], "id", s)
    if "34" not in group(d, "0")["Singles"]:
        group(d, "0")["Singles"].append("34")
    counter_at_least(d, "singles__LastSingleID", 34)


def shows(d):
    """Shows on all three media: TV and internet airing, radio relaunching, a new one in development."""
    show(d, "2").update({"status": "2", "ToCancel": "false"})
    # Relaunching (Shows._show.OnRelaunchStart): production starts over, the old values are kept.
    s = show(d, "1")
    s.update({"status": "3", "ToCancel": "false", "Previous_Concept": "29", "Previous_Production": "6.19999980926514"})
    for p in s["parameters"]:
        p["val"] = "0"

    s = copy.deepcopy(show(d, "5"))
    s.update({"id": "6", "status": "0", "title": "Weekend Idol Hour", "cost": "10000", "episodeCount": "0",
              "LaunchDate": NEVER, "peakAudience": "0", "ToCancel": "false", "NumberOfRelaunches": "0",
              "WasRelaunched": "false", "Previous_Concept": "0", "Previous_Production": "0",
              "medium": show(d, "2")["medium"], "mc": "0"})
    for key in ("audience", "revenue", "fans", "buzz", "fatigue", "fame", "famePoints"):
        s[key] = []
    for p in s["parameters"]:
        p["val"] = "0"
    upsert(d["shows__Shows"], "id", s)
    counter_at_least(d, "shows__LastShowID", 6)


def event_status(d, etype, status):
    by_id(d["SpecialEvents_Manager__EventData"], "Type", etype)["Status"] = status


def tour(d):
    """World tour 3 planned and fully produced, waiting to be launched (SEvent_Tour.SetTour)."""
    t = copy.deepcopy(by_id(d["SEvent_Tour__Tours"], "ID", "2"))
    t.update({"ID": "3", "Status": "0", "FinishDate": "2024-03-20 00:00:00", "Revenue": "0", "NewFans": "0"})
    for country in t["SelectedCountries"]:
        country.update({"Attendance": "0", "Audience": "0", "NewFans": "0", "Revenue": "0"})
    upsert(d["SEvent_Tour__Tours"], "ID", t)
    d["SEvent_Tour__Tour"] = "3"
    counter_at_least(d, "SEvent_Tour__LastTourID", 3)
    event_status(d, "2", "1")


def concert(d):
    """Concert 8 at the stadium, fully produced, waiting to be launched (SEvent_Concerts.SetConcert)."""
    c = copy.deepcopy(by_id(d["SEvent_Concert__Concerts"], "ID", "6"))
    c.update({"ID": "8", "Status": "0", "FinishDate": "2024-03-20 00:00:00", "Venue": "3", "Hype": "60",
              "Cast_Changed": "false",
              "SetListItems": [{"IsMC": "false", "Girls": [g], "Single": s}
                               for g, s in (("1", "30"), ("3", "29"), ("11", "27"), ("12", "26"),
                                            ("9", "24"), ("71", "25"))]})
    c["ProjectedValues"].update({"Actual_Attendance": "0", "Actual_Audience": "0", "Actual_Hype": "0",
                                 "Actual_Revenue": "0", "Actual_Cost": "0"})
    upsert(d["SEvent_Concert__Concerts"], "ID", c)
    d["SEvent_Concert__Concert"] = "8"
    counter_at_least(d, "SEvent_Concert__LastConcertID", 8)
    event_status(d, "0", "1")


ELECTION_VOTERS = ["1", "3", "4", "6", "8", "9", "11", "12", "13"]  # idols active on 2023-07-12


def election(d):
    """A finished election (July 2023) on single 27 and concert 6, with results and history.

    Its election single was never made, which the game only offers for 105 days afterwards.
    """
    base = 6404 * 2 * 2 - 2084 * 2  # resources.FameLevelToPoints(2): the live-blog broadcast
    bonus = []
    for _ in range(10):  # SSK.RecalcFameBonus
        bonus.append(base)
        base = round(base * 0.75)
    votes = {gid: round(float(param(girl(d, gid), "10")["_val"]) * 0.2) for gid in ELECTION_VOTERS}
    ranked = sorted(ELECTION_VOTERS, key=lambda gid: (-votes[gid], int(gid)))
    results = []
    for place, gid in enumerate(ranked, start=1):
        results.append({"Girl": gid, "Place": str(place), "Votes": str(votes[gid]),
                        "FamePoints": str(bonus[place - 1] if place <= 10 else 0)})
        g = girl(d, gid)
        g["SSK_History"] = [str(place)]
        g["SSK_Expected_Place"] = str(place)
    upsert(d["SEvent_SSK__Elections"], "ID", {
        "ID": "1", "Status": "2", "Single": "27", "Concert": "6", "ReleaseSingle": "-1", "Count": "1",
        "FinishDate": "2023-07-12 02:00:00", "Broadcast": "0", "FameBonus": [str(b) for b in bonus],
        "parameters": [{"type": "0", "val": "85", "progress": "0"}, {"type": "1", "val": "16", "progress": "0"}],
        "Results": results,
    })
    counter_at_least(d, "SEvent_SSK__LastSSKID", 1)
    ev = by_id(d["SpecialEvents_Manager__EventData"], "Type", "1")
    ev.update({"Cooldown_Start": "2023-07-12 02:00:00", "Cooldown_Stop": "2024-01-12 02:00:00"})
    d["Stats__data"]["ssk_max_idol_votes"] = str(max(votes.values()))


def loan(d):
    """A 3-month bank loan of 10M taken at the start of March (loans._loan.Initialize)."""
    amount = 10_000_000
    total = amount + round(amount * 0.10)  # 10% interest for 3 months at the bank
    upsert(d["loans__LoanData"], "ID", {
        "ID": "1", "Active": "true", "Type": "2", "Duration": "1", "StartDate": "2024-03-04 00:00:00",
        "EndDate": "2024-06-04 00:00:00", "Amount": str(amount), "PaymentPerWeek": str(round(total / 12)),
    })
    counter_at_least(d, "loans__LastLoanID", 1)


AWAY = {str(s) for s in (INJURED, GRADUATED, DEPRESSED, HIATUS)}


def remove_away_idols(d):
    """Idols who are away leave unreleased work, as data_girls.girls.RemoveFromEverything does."""
    away = {g["id"] for g in d["data_girls__Girls"] if g["status"] in AWAY}
    for s in d["singles__Singles"]:
        if s["status"] != "2":
            s["girls"] = ["-1" if gid in away else gid for gid in s["girls"]]
    for s in d["shows__Shows"]:
        if s["status"] != "5":
            s["girls"] = ["-1" if gid in away else gid for gid in s["girls"]]
    push = d["pushes__Data"]
    for i, gid in enumerate(push["girls"]):
        if gid in away:
            push["girls"][i], push["days"][i] = "-1", "0"
    d["Girls_Mentors__Mentors"] = [m for m in d["Girls_Mentors__Mentors"]
                                   if m["Senpai"] not in away and m["Kohai"] not in away]
    for c in d["Cafes__Cafes"]:
        c["WaitStaff"] = [gid for gid in c["WaitStaff"] if gid not in away]


STEPS = [
    options_on, cleanup, new_idols, new_staff, floors, idol_statuses, wishes, relationships,
    theaters, cafe, singles_, shows, tour, concert, election, loan, remove_away_idols,
]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", type=Path, nargs="?")
    ap.add_argument("output", type=Path, nargs="?")
    ap.add_argument("--list", action="store_true", help="list the steps and exit")
    args = ap.parse_args()

    if args.list or not args.input or not args.output:
        for step in STEPS:
            print(f"{step.__name__.rstrip('_'):20} {step.__doc__.splitlines()[0]}")
        return 0 if args.list else 2

    data = load(args.input)
    for step in STEPS:
        step(data)

    errors = check(data)
    if errors:
        print("\n".join(errors))
        print(f"Not written: {len(errors)} problem(s)")
        return 1
    write(data, args.output)
    print(f"Wrote {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
