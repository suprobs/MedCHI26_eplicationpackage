#!/usr/bin/env python3
"""
Competency-question validator for the conceptual device model.

Loads an EFSM + device_spec JSON instance and checks that each of the 13
competency questions (CQ1-CQ13) can be answered over it. A CQ that returns no
answer where one is expected flags a modelling gap; a CQ that is only partly
answerable (e.g. the effect of a signal is not encoded) is reported as PARTIAL,
which is itself a useful finding about the model.

This is an OFFLINE model-validation tool: run it over your device instances to
produce the evidence, e.g.:

    python validate_conceptual_model.py PrinterJSON.json
    python validate_conceptual_model.py PrinterJSON.json Coffee.json
    python validate_conceptual_model.py -v PrinterJSON.json      # verbose

Standard library only. Field access is defensive: schema variations degrade to
PARTIAL/GAP instead of crashing.
"""
import json, sys, re
from collections import OrderedDict

# ----------------------------------------------------------------- helpers ---
def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)

def machine_component(m):
    """Component id a machine belongs to (explicit field or 'machine_<id>')."""
    cid = m.get("component_id")
    if cid:
        return cid
    mid = m.get("id", "")
    return mid[len("machine_"):] if mid.startswith("machine_") else None

def build_index(d):
    idx = {}
    idx["components"]   = {c.get("id"): c for c in d.get("components_catalog", [])}
    idx["machines"]     = {m.get("id"): m for m in d.get("machines", [])}
    idx["m2c"]          = {m.get("id"): machine_component(m) for m in d.get("machines", [])}
    t2m = {}
    for m in d.get("machines", []):
        for t in m.get("transitions", []):
            t2m[t.get("id")] = (m.get("id"), t)
    idx["transitions"]  = t2m
    return idx

def state_label(s):
    return (s.get("id") or s.get("name")
            or (f"{s.get('dimension')}={s.get('value')}" if s.get("dimension") else None))

# --------------------------------------------------------- competency Qs -----
# Each returns (status, summary, detail). status in ANSWERED / PARTIAL / GAP.

def cq1(d, idx):  # components: interactive vs static vs extension
    inter = [c.get("id") for c in d.get("components_catalog", [])]
    static = [s.get("id") for s in d.get("scene_props", [])] + \
             [e.get("id") for e in d.get("excluded_components", [])]
    ext = [n.get("id", n.get("name")) for n in d.get("new_elements", [])]
    status = "ANSWERED" if inter else "GAP"
    return status, f"{len(inter)} interactive, {len(static)} static, {len(ext)} extension", \
           {"interactive": inter, "static": static, "extension": ext}

def cq2(d, idx):  # 3D representation of each component
    rows = []
    for c in d.get("components_catalog", []):
        lh = c.get("layout_hint") or {}
        rows.append({"component": c.get("id"),
                     "anchor": lh.get("anchor"), "rel": lh.get("rel"),
                     "size": lh.get("size"), "hingeEdge": lh.get("hingeEdge")})
    with_geom = [r for r in rows if any(r[k] is not None for k in ("anchor", "rel", "size"))]
    if not rows:                       status = "GAP"
    elif len(with_geom) == len(rows):  status = "ANSWERED"
    else:                              status = "PARTIAL"
    return status, f"{len(with_geom)}/{len(rows)} components carry a layout_hint", rows

def cq3(d, idx):  # states + initial per machine
    rows = []
    for m in d.get("machines", []):
        states = [state_label(s) for s in m.get("states", [])]
        init = m.get("initial_state") or next(
            (state_label(s) for s in m.get("states", []) if s.get("initial") is not None), None)
        rows.append({"machine": m.get("id"), "component": idx["m2c"].get(m.get("id")),
                     "states": states, "initial": init})
    missing = [r["machine"] for r in rows if not r["states"] or r["initial"] is None]
    status = "ANSWERED" if rows and not missing else ("PARTIAL" if rows else "GAP")
    return status, f"{len(rows)} machines; initial state missing in {len(missing)}", rows

def cq4(d, idx):  # transitions from states + guard
    rows = []
    for m in d.get("machines", []):
        for t in m.get("transitions", []):
            rows.append({"machine": m.get("id"), "from": t.get("from"),
                         "to": t.get("to"), "guard": t.get("guard"), "id": t.get("id")})
    ok = [r for r in rows if r["from"] is not None and r["to"] is not None]
    status = "ANSWERED" if rows and len(ok) == len(rows) else ("PARTIAL" if rows else "GAP")
    return status, f"{len(rows)} transitions ({sum(1 for r in rows if r['guard'])} guarded)", rows

def cq5(d, idx):  # action + movement of each transition
    rows = [{"id": t.get("id"), "action": t.get("action"), "movement": t.get("movement")}
            for (_, t) in idx["transitions"].values()]
    ok = [r for r in rows if r["action"]]
    status = "ANSWERED" if rows and len(ok) == len(rows) else ("PARTIAL" if rows else "GAP")
    return status, f"{len(ok)}/{len(rows)} transitions name an action", rows

def cq6(d, idx):  # cross-component coupling (guards) + signals emitted
    cross = []
    for (mid, t) in idx["transitions"].values():
        g = t.get("guard")
        if not g: continue
        others = sorted({ref for ref in re.findall(r"(machine_[A-Za-z0-9_]+)\.", str(g)) if ref != mid})
        if others:
            cross.append({"transition": t.get("id"), "machine": mid, "depends_on": others})
    emitters = []
    for (mid, t) in idx["transitions"].values():
        sig = t.get("emits") or (t.get("signals_emitted") or [None])[0]
        if sig: emitters.append({"transition": t.get("id"), "emits": sig})
    if not cross and not emitters:
        return "N/A", "no cross-component guards and no signals", []
    return "ANSWERED", (f"{len(cross)} transitions guarded on another component; "
                        f"{len(emitters)} transitions emit a signal"), {"cross":cross,"signals":emitters}

def cq7(d, idx):  # procedures of the device
    procs = [p.get("id") or p.get("name") for p in d.get("procedures", [])]
    return ("ANSWERED" if procs else "GAP"), f"{len(procs)} procedure(s)", procs

def cq8(d, idx):  # ordered steps + transition each performs
    rows = []
    for p in d.get("procedures", []):
        steps = sorted(p.get("steps", []), key=lambda s: s.get("order", 0))
        seq = [{"order": s.get("order"),
                "transition_ref": s.get("transition_ref")
                                  or (s.get("accepted_transition_refs") or [None])[0],
                "external": bool(s.get("external_step")) or s.get("step_type") == "external"}
               for s in steps]
        rows.append({"procedure": p.get("id"), "steps": seq})
    all_steps = [st for r in rows for st in r["steps"]]
    # A step is answered if it references an existing transition, or if it is
    # explicitly declared external (by design it has no transition in the device).
    with_ref = [st for st in all_steps if st["transition_ref"] in idx["transitions"]]
    external = [st for st in all_steps if not st["transition_ref"] and st["external"]]
    broken   = [st for st in all_steps if st["transition_ref"] and st["transition_ref"] not in idx["transitions"]]
    ok = len(with_ref) + len(external)
    status = "ANSWERED" if all_steps and ok == len(all_steps) else ("PARTIAL" if all_steps else "GAP")
    return status, (f"{len(with_ref)}/{len(all_steps)} steps reference a transition, "
                    f"{len(external)} declared external, {len(broken)} broken reference(s)"), rows

def cq9(d, idx):  # pre/postconditions
    rows = []
    for p in d.get("procedures", []):
        rows.append({"procedure": p.get("id"),
                     "preconditions": p.get("preconditions", []),
                     "postconditions": p.get("postconditions", [])})
    has_pre = any(r["preconditions"] for r in rows)
    has_post = any(r["postconditions"] for r in rows)
    if not rows:                 status = "GAP"
    elif has_pre and has_post:   status = "ANSWERED"
    else:                        status = "PARTIAL"
    return status, f"pre-conditions:{'yes' if has_pre else 'no'} post-conditions:{'yes' if has_post else 'no'}", rows

def cq10(d, idx):  # /involves : components of a procedure (DERIVED)
    rows = []
    for p in d.get("procedures", []):
        comps = set()
        for ics in p.get("initial_component_states", []):
            if ics.get("component_id"): comps.add(ics["component_id"])
        for cond in list(p.get("preconditions", [])) + list(p.get("postconditions", [])):
            comps.add(idx["m2c"].get(cond.get("machine_id")))
        for s in p.get("steps", []):
            ref = s.get("transition_ref") or (s.get("accepted_transition_refs") or [None])[0]
            if ref in idx["transitions"]:
                comps.add(idx["m2c"].get(idx["transitions"][ref][0]))
        comps.discard(None)
        rows.append({"procedure": p.get("id"), "involves": sorted(comps)})
    ok = [r for r in rows if r["involves"]]
    status = "ANSWERED" if rows and len(ok) == len(rows) else ("PARTIAL" if rows else "GAP")
    return status, f"derived involves-set non-empty for {len(ok)}/{len(rows)} procedures", rows

def cq11(d, idx):  # rules governing a procedure
    top_rules = d.get("rules", []) or []
    rows = []
    for p in d.get("procedures", []):
        pid = p.get("id")
        inline = p.get("rules", []) or []
        linked = [r for r in top_rules if r.get("procedure_id") in (pid, None)]
        rows.append({"procedure": pid, "rules": len(inline) + len(linked)})
    total = len(top_rules) + sum(len(p.get("rules", []) or []) for p in d.get("procedures", []))
    status = "ANSWERED" if total else "N/A"
    return status, f"{total} rule(s) in this instance", rows

def cq12(d, idx):  # inter-procedure dependencies
    rows = []
    for p in d.get("procedures", []):
        deps = {"requires": p.get("requires", []) or [],
                "unlocks": p.get("unlocks", []) or [],
                "resolver": [pc.get("resolver_procedure_id")
                             for pc in p.get("preconditions", []) if pc.get("resolver_procedure_id")],
                "goto": [b.get("goto_procedure")
                         for s in p.get("steps", []) for b in (s.get("branches") or [])
                         if b.get("goto_procedure")]}
        rows.append({"procedure": p.get("id"), **deps})
    any_dep = any(r["requires"] or r["unlocks"] or r["resolver"] or r["goto"] for r in rows)
    # the RELATION is representable even if this instance has no dependency
    known_ids = {p.get("id") for p in d.get("procedures", [])}
    bad_targets = sorted(set((d.get("device_meta") or {}).get("target_procedures") or []) - known_ids)
    if not any_dep:
        if bad_targets:
            return "PARTIAL", ("no dependencies declared, but device_meta.target_procedures names "
                               f"{', '.join(bad_targets)}, not defined in this instance"), rows
        return "N/A", "relation representable; none in this instance", rows
    # referential integrity: every referenced procedure must exist in the instance
    known = {p.get("id") for p in d.get("procedures", [])}
    refs = {x for r in rows for k in ("requires", "unlocks", "resolver", "goto") for x in r[k]}
    refs |= set((d.get("device_meta") or {}).get("target_procedures") or [])
    dangling = sorted(refs - known)
    if dangling:
        return "PARTIAL", ("dependencies declared but unresolved: "
                           f"{', '.join(dangling)} not defined in this instance"), rows
    return "ANSWERED", f"{len(refs)} dependency target(s), all resolved", rows

def cq13(d, idx):  # vocabulary terms used
    e = d.get("enums", {})
    facets = {k: len(v) for k, v in e.items() if isinstance(v, list)}
    status = "ANSWERED" if facets else "GAP"
    return status, ", ".join(f"{k}:{n}" for k, n in facets.items()), e

def cq14(d, idx):  # materials the device processes
    mats = [c.get("id") for c in d.get("components_catalog", [])
            if str(c.get("category","")).strip().lower().startswith(("material","external"))]
    if not mats:
        return "N/A", "device processes no external materials", []
    return "ANSWERED", f"{len(mats)} material(s): {', '.join(mats)}", mats

CQS = OrderedDict([
    ("CQ1  components: interactive/static/extension", cq1),
    ("CQ2  3D representation of components",           cq2),
    ("CQ3  states + initial state",                    cq3),
    ("CQ4  transitions from a state + guard",          cq4),
    ("CQ5  action + movement of a transition",         cq5),
    ("CQ6  cross-component guards + signals emitted",  cq6),
    ("CQ7  procedures of the device",                  cq7),
    ("CQ8  ordered steps + transition performed",      cq8),
    ("CQ9  pre/postconditions",                        cq9),
    ("CQ10 components a procedure involves (derived)", cq10),
    ("CQ11 rules governing a procedure",               cq11),
    ("CQ12 inter-procedure dependencies",              cq12),
    ("CQ13 vocabulary terms used",                     cq13),
    ("CQ14 materials the device processes",            cq14),
])

# --------------------------------------------------------------- runner ------
def run(path, verbose=False):
    d = load(path)
    idx = build_index(d)
    name = d.get("device_meta", {}).get("device_name") or path
    print(f"\n=== Competency-question validation: {name} ===")
    tally = {"ANSWERED": 0, "PARTIAL": 0, "GAP": 0, "N/A": 0, "ERROR": 0}
    for label, fn in CQS.items():
        try:
            status, summary, detail = fn(d, idx)
        except Exception as ex:
            status, summary, detail = "ERROR", f"{type(ex).__name__}: {ex}", None
        tally[status] = tally.get(status, 0) + 1
        print(f"  [{status:8}] {label}\n             -> {summary}")
        if verbose and detail is not None:
            print("             " + json.dumps(detail, ensure_ascii=False)[:800])
    print(f"  ------------------------------------------------------------")
    print(f"  answered {tally['ANSWERED']}  partial {tally['PARTIAL']}  "
          f"gap {tally['GAP']}  n/a {tally['N/A']}  error {tally['ERROR']}  (of 14)")
    return tally

if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    verbose = "-v" in sys.argv or "--verbose" in sys.argv
    if not args:
        print(__doc__); sys.exit(1)
    for p in args:
        run(p, verbose)