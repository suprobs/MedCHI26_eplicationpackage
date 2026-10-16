#!/usr/bin/env python3
"""
Validate a model instance (behavioural specification + device specification).

Three kinds of checks:
  1. Schema      - structure, against schemas/efsm.schema.json and
                   schemas/device_spec.schema.json (needs `pip install jsonschema`;
                   skipped with a notice otherwise).
  2. Integrity   - cross-references and constraints C1-C5 of Section 3.2. The
                   checks and their severities mirror those run by the authoring
                   tool (EFSM integrity check at each generation; binding check at
                   export).
  3. Competency  - answers to the fourteen competency questions of Table 1,
     questions     obtained by querying the instance. A question that cannot be
                   answered for a given instance is reported as n/a.

Usage:
  python validate_instance.py --efsm <efsm.json> [--spec <device_spec.json>]
                              [--procedure <id>] [--json report.json]

Exit code: 0 if no errors, 1 otherwise (warnings do not fail).
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMAS = os.path.join(HERE, "..", "schemas")
ATOM = re.compile(r"^([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\s+(eq|neq)\s+(.+)$")


# --------------------------------------------------------------------------- helpers
def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def s(v):
    return str(v).lower() if isinstance(v, bool) else str(v)


def guard_atoms(expr):
    """Return [(machine, dimension, op, value)] or None if the expression is malformed."""
    if not expr or not str(expr).strip():
        return []
    out = []
    for conj in re.split(r"\s+or\s+", str(expr)):
        for atom in re.split(r"\s+and\s+", conj):
            m = ATOM.match(atom.strip())
            if not m:
                return None
            out.append((m.group(1), m.group(2), m.group(3), m.group(4).strip().strip("\"'")))
    return out


def guard_eval(expr, snap):
    """Same semantics as the runtime: 'or' of 'and'-conjunctions of eq/neq atoms."""
    if not expr or not str(expr).strip():
        return True
    for conj in re.split(r"\s+or\s+", str(expr)):
        ok = True
        for atom in re.split(r"\s+and\s+", conj):
            m = ATOM.match(atom.strip())
            if not m:
                ok = False
                break
            cur = s(snap.get(m.group(1), {}).get(m.group(2)))
            v = m.group(4).strip().strip("\"'")
            if (cur == v) != (m.group(3) == "eq"):
                ok = False
                break
        if ok:
            return True
    return False


class Report:
    def __init__(self):
        self.sections = {}

    def add(self, section, level, msg):
        self.sections.setdefault(section, []).append({"level": level, "msg": msg})

    def errors(self):
        return sum(1 for v in self.sections.values() for x in v if x["level"] == "error")

    def warnings(self):
        return sum(1 for v in self.sections.values() for x in v if x["level"] == "warning")


# --------------------------------------------------------------------------- 1. schema
def check_schema(rep, data, schema_file, section):
    try:
        import jsonschema
    except ImportError:
        rep.add(section, "info", "jsonschema not installed; schema check skipped (pip install jsonschema).")
        return
    schema = load(os.path.join(SCHEMAS, schema_file))
    validator = jsonschema.Draft202012Validator(schema)
    errs = sorted(validator.iter_errors(data), key=lambda e: list(e.path))
    for e in errs[:50]:
        path = "/".join(str(p) for p in e.path) or "(root)"
        rep.add(section, "error", f"{path}: {e.message[:200]}")
    if not errs:
        rep.add(section, "ok", f"Conforms to {schema_file}.")


# --------------------------------------------------------------------------- 2. integrity
def index(m):
    machines = {mc["id"]: mc for mc in m.get("machines", [])}
    trans = {}
    for mc in m.get("machines", []):
        for t in mc.get("transitions", []):
            trans[t["id"]] = (t, mc["id"])
    comps = {c["machine_id"]: c for c in m.get("components_catalog", [])}
    dims = {mid: {d["name"]: d for d in c.get("dimensions", [])} for mid, c in comps.items()}
    procs = {p["id"]: p for p in m.get("procedures", [])}
    return machines, trans, comps, dims, procs


def seed_snapshot(m):
    snap = {}
    for c in m.get("components_catalog", []):
        snap[c["machine_id"]] = {}
        for d in c.get("dimensions", []):
            snap[c["machine_id"]][d["name"]] = d.get("initial", d.get("initial_value"))
    return snap


def check_references(rep, m):
    sec = "References"
    machines, trans, comps, dims, procs = index(m)
    en = m.get("enums", {})

    for mid, c in comps.items():
        if mid not in machines:
            rep.add(sec, "error", f"Component '{c['id']}' names machine '{mid}', which is not in machines[].")
        for d in c.get("dimensions", []):
            if "initial" not in d or "initial_value" not in d:
                rep.add(sec, "error", f"{mid}.{d['name']}: both 'initial' and 'initial_value' are required.")
            elif s(d["initial"]) != s(d["initial_value"]):
                rep.add(sec, "error", f"{mid}.{d['name']}: initial ({d['initial']}) differs from initial_value ({d['initial_value']}).")
            if s(d.get("initial")) not in [s(v) for v in d.get("values", [])]:
                rep.add(sec, "error", f"{mid}.{d['name']}: initial value '{d.get('initial')}' is not one of its values.")
            if d["name"] not in en.get("dimensions", []):
                rep.add(sec, "warning", f"Dimension '{d['name']}' is not listed in enums.dimensions.")

    seen = set()
    for mid, mc in machines.items():
        if mid not in comps:
            rep.add(sec, "error", f"Machine '{mid}' has no component in components_catalog.")
            continue
        states = {st["id"]: st for st in mc.get("states", [])}
        if mc.get("initial_state") not in states:
            rep.add(sec, "error", f"{mid}: initial_state '{mc.get('initial_state')}' is not one of its states.")
        else:
            dv = states[mc["initial_state"]].get("dimension_values", {})
            for k, v in dv.items():
                ini = dims[mid].get(k, {}).get("initial")
                if s(ini) != s(v):
                    rep.add(sec, "error", f"{mid}: initial_state sets {k}={v} but the dimension's initial is {ini}.")
        for st in mc.get("states", []):
            for k, v in st.get("dimension_values", {}).items():
                if k not in dims[mid]:
                    rep.add(sec, "error", f"{mid}.{st['id']}: dimension '{k}' is not defined for this component.")
                elif s(v) not in [s(x) for x in dims[mid][k].get("values", [])]:
                    rep.add(sec, "error", f"{mid}.{st['id']}: value '{v}' is not a value of '{k}'.")
            to = (st.get("timeout") or {}).get("to_state")
            if to and to not in states:
                rep.add(sec, "error", f"{mid}.{st['id']}: timeout goes to unknown state '{to}'.")
        for t in mc.get("transitions", []):
            if t["id"] in seen:
                rep.add(sec, "error", f"Duplicate transition id '{t['id']}'.")
            seen.add(t["id"])
            d = dims[mid].get(t.get("dimension"))
            if d is None:
                rep.add(sec, "error", f"{t['id']}: dimension '{t.get('dimension')}' is not defined for {mid}.")
            else:
                vals = [s(x) for x in d.get("values", [])]
                for k in ("from", "to"):
                    if s(t.get(k)) not in vals:
                        rep.add(sec, "error", f"{t['id']}: {k} value '{t.get(k)}' is not a value of {mid}.{t['dimension']}.")
            if t.get("action") not in en.get("actions", []):
                rep.add(sec, "warning", f"{t['id']}: action '{t.get('action')}' is not listed in enums.actions.")
            if t.get("movement") not in en.get("movements", []) + ["none"]:
                rep.add(sec, "warning", f"{t['id']}: movement '{t.get('movement')}' is not listed in enums.movements.")
            sig = t.get("signals_emitted") or []
            if (t.get("emits") or None) != (sig[0] if sig else None):
                rep.add(sec, "error", f"{t['id']}: emits ({t.get('emits')}) must equal signals_emitted[0] ({sig[0] if sig else None}).")
            for x in sig:
                if x not in en.get("signals", []):
                    rep.add(sec, "warning", f"{t['id']}: signal '{x}' is not listed in enums.signals.")
            check_guard(rep, sec, t["id"], t.get("guard"), dims)

    for r in m.get("rules", []):
        check_guard(rep, sec, "rule " + r["id"], r.get("expr"), dims)
        for a in r.get("applies_to", []):
            if a not in trans and a not in procs:
                rep.add(sec, "warning", f"rule {r['id']}: applies_to '{a}' is neither a transition nor a procedure.")

    for p in procs.values():
        for kind in ("preconditions", "postconditions"):
            key = "required_value" if kind == "preconditions" else "guaranteed_value"
            for c in p.get(kind, []):
                d = dims.get(c["machine_id"], {}).get(c["dimension"])
                if d is None:
                    rep.add(sec, "error", f"'{p['id']}' {kind[:-1]}: {c['machine_id']}.{c['dimension']} is not defined.")
                elif s(c.get(key)) not in [s(x) for x in d.get("values", [])]:
                    rep.add(sec, "error", f"'{p['id']}' {kind[:-1]}: '{c.get(key)}' is not a value of {c['machine_id']}.{c['dimension']}.")
        check_guard(rep, sec, f"procedure {p['id']}", p.get("guard"), dims)
        orders = [st["order"] for st in p.get("steps", [])]
        if len(orders) != len(set(orders)):
            rep.add(sec, "error", f"'{p['id']}': duplicate step orders.")
        for st in p.get("steps", []):
            for b in st.get("branches", []):
                if b.get("to_step") is not None and b["to_step"] not in orders:
                    rep.add(sec, "error", f"'{p['id']}' step {st['order']}: branch to_step {b['to_step']} does not exist.")

    if not any(x["level"] == "error" for x in rep.sections.get(sec, [])):
        rep.add(sec, "ok", "All references resolve.")


def check_guard(rep, sec, where, expr, dims):
    atoms = guard_atoms(expr)
    if atoms is None:
        rep.add(sec, "error", f"{where}: malformed guard '{expr}' (use 'machine.dimension eq|neq value' with and/or).")
        return
    for mid, dim, _, val in atoms:
        d = dims.get(mid, {}).get(dim)
        if d is None:
            rep.add(sec, "error", f"{where}: guard names {mid}.{dim}, which is not defined in components_catalog.")
        elif val not in [s(x) for x in d.get("values", [])]:
            rep.add(sec, "error", f"{where}: guard compares {mid}.{dim} with '{val}', which is not one of its values.")


def check_c1_c4(rep, m):
    """Mirror of the authoring tool's EFSM integrity check."""
    machines, trans, comps, dims, procs = index(m)
    targets = m.get("device_meta", {}).get("target_procedures", [])
    has = procs.__contains__

    # C1: steps perform transitions of the instance.
    sec = "C1 steps perform transitions"
    for p in procs.values():
        for st in p.get("steps", []):
            refs = [r for r in [st.get("transition_ref")] + (st.get("accepted_transition_refs") or []) if r]
            for r in refs:
                if r not in trans:
                    rep.add(sec, "error", f"'{p['id']}' step {st['order']}: transition '{r}' is not defined in machines.")
            if not refs and not st.get("external_step"):
                rep.add(sec, "warning", f"'{p['id']}' step {st['order']}: no transition and not marked external.")
            ro = (st.get("loop") or {}).get("repeat_step_orders") or []
            if ro and (any(ro[k] != ro[k - 1] + 1 for k in range(1, len(ro))) or ro[-1] != st["order"]):
                rep.add(sec, "error", f"'{p['id']}' step {st['order']}: loop {ro} must be the contiguous range {ro[0]}..{st['order']}.")
    if sec not in rep.sections:
        rep.add(sec, "ok", "Every non-external step references existing transitions.")

    # C2: required procedures exist and guarantee the preconditions.
    sec = "C2 procedure dependencies"
    for t in targets:
        if not has(t):
            rep.add(sec, "error", f"device_meta.target_procedures names '{t}', which is not in procedures[].")
    for p in procs.values():
        for k in ("requires", "unlocks"):
            for x in p.get(k, []):
                if not has(x):
                    rep.add(sec, "error", f"'{p['id']}' {k} '{x}', which is not in procedures[].")
        for x in p.get("requires", []):
            if has(x) and p["id"] not in procs[x].get("unlocks", []):
                rep.add(sec, "warning", f"'{p['id']}' requires '{x}', but '{x}' does not list '{p['id']}' in unlocks.")
        for x in p.get("unlocks", []):
            if has(x) and p["id"] not in procs[x].get("requires", []):
                rep.add(sec, "warning", f"'{p['id']}' unlocks '{x}', but '{x}' does not list '{p['id']}' in requires.")
        for pc in p.get("preconditions", []):
            r = pc.get("resolver_procedure_id")
            if not r:
                continue
            if not has(r):
                rep.add(sec, "error", f"'{p['id']}': resolver '{r}' of {pc['machine_id']}.{pc['dimension']} is not in procedures[].")
                continue
            if not any(po["machine_id"] == pc["machine_id"] and po["dimension"] == pc["dimension"]
                       and s(po["guaranteed_value"]) == s(pc["required_value"]) for po in procs[r].get("postconditions", [])):
                rep.add(sec, "warning", f"'{p['id']}': resolver '{r}' does not guarantee {pc['machine_id']}.{pc['dimension']} = {pc['required_value']}.")
        for st in p.get("steps", []):
            for b in st.get("branches", []):
                if b.get("goto_procedure") and not has(b["goto_procedure"]):
                    rep.add(sec, "error", f"'{p['id']}' step {st['order']}: goto_procedure '{b['goto_procedure']}' is not in procedures[].")
    if sec not in rep.sections:
        deps = any(p.get("requires") or p.get("unlocks") or any(pc.get("resolver_procedure_id") for pc in p.get("preconditions", []))
                   for p in procs.values())
        rep.add(sec, "ok", "All procedure dependencies resolve." if deps else "No dependencies between procedures (n/a).")

    # C3 + C4: seeded snapshot and simulated walk of the target procedures, in order.
    snap = seed_snapshot(m)
    walk = targets or (list(procs)[:1])

    def apply_timeouts():
        fired = []
        for mid, mc in machines.items():
            cur = snap.setdefault(mid, {})
            st = next((x for x in mc.get("states", []) if x.get("dimension_values")
                       and all(s(cur.get(k)) == s(v) for k, v in x["dimension_values"].items())), None)
            to = st and (st.get("timeout") or {}).get("after_ms") and (st.get("timeout") or {}).get("to_state")
            tgt = next((x for x in mc.get("states", []) if x["id"] == to), None) if to else None
            if tgt:
                cur.update(tgt.get("dimension_values", {}))
                fired.append(f"{mid}: {st['id']} -> {to} after {st['timeout']['after_ms']} ms")
        return fired

    walked = True
    for k, pid in enumerate(walk):
        p = procs.get(pid)
        if not p:
            walked = False
            break
        unmet = [pc for pc in p.get("preconditions", []) if s(snap.get(pc["machine_id"], {}).get(pc["dimension"])) != s(pc["required_value"])]
        sec = "C3 initial values" if k == 0 else "C2 procedure dependencies"
        if unmet:
            rep.add(sec, "error", f"Start of '{pid}': precondition(s) not met - " + "; ".join(
                f"{pc['machine_id']}.{pc['dimension']} is {snap.get(pc['machine_id'], {}).get(pc['dimension'])}, needs {pc['required_value']}" for pc in unmet))
            walked = False
            break
        if k == 0:
            rep.add(sec, "ok", f"Initial values satisfy the preconditions of '{pid}'.")

        sec = "C4 executability"
        by_order = {st["order"]: st for st in p.get("steps", [])}
        cur = min(p.get("steps", []), key=lambda x: x["order"]) if p.get("steps") else None
        remaining, n = {}, 0
        while cur and n < 500:
            n += 1
            if cur.get("branches"):
                rep.add(sec, "warning", f"'{pid}': walk stopped at step {cur['order']} (branches are not simulated).")
                walked = False
                break
            if not cur.get("external_step"):
                refs = [trans[r] for r in [cur.get("transition_ref")] + (cur.get("accepted_transition_refs") or []) if r and r in trans]

                def pick():
                    src = [x for x in refs if s(snap.get(x[1], {}).get(x[0]["dimension"])) == s(x[0]["from"])]
                    return next((x for x in src if guard_eval(x[0].get("guard"), snap)), src[0] if src else None)

                fire = pick()
                if not fire or not guard_eval(fire[0].get("guard"), snap):
                    tf = apply_timeouts()
                    if tf:
                        fire = pick()
                        rep.add(sec, "warning", f"'{pid}' step {cur['order']} relies on a state timeout ({'; '.join(tf)}).")
                if not fire:
                    rep.add(sec, "error", f"'{pid}' step {cur['order']}: no referenced transition can fire from the current state.")
                    walked = False
                    break
                if not guard_eval(fire[0].get("guard"), snap):
                    rep.add(sec, "error", f"'{pid}' step {cur['order']}: guard of {fire[0]['id']} is false ({fire[0]['guard']}).")
                    walked = False
                    break
                snap.setdefault(fire[1], {})[fire[0]["dimension"]] = fire[0]["to"]
            loop = cur.get("loop") or {}
            ro, nxt = loop.get("repeat_step_orders") or [], None
            if ro and (loop.get("times") or 0) > 0:
                remaining.setdefault(cur["order"], loop["times"])
                if remaining[cur["order"]] > 0:
                    remaining[cur["order"]] -= 1
                    nxt = by_order.get(ro[0])
            cur = nxt or by_order.get(cur["order"] + 1)
        if not walked:
            break
        post = [po for po in p.get("postconditions", []) if s(snap.get(po["machine_id"], {}).get(po["dimension"])) != s(po["guaranteed_value"])]
        for po in post:
            rep.add(sec, "warning", f"End of '{pid}': postcondition {po['machine_id']}.{po['dimension']} is {snap.get(po['machine_id'], {}).get(po['dimension'])}, guaranteed {po['guaranteed_value']}.")
    if walked and walk:
        rep.add("C4 executability", "ok", "Simulated walk of " + " -> ".join(walk) + " completes with every step firing from the seeded snapshot.")
    return walk


def check_c5(rep, m, spec, proc_ids):
    """Mirror of the authoring tool's export check (bindings, attach targets, step coverage)."""
    sec = "C5 steps triggered by interactions"
    machines, trans, comps, dims, procs = index(m)
    actions = {mid: [t["action"] for t in mc.get("transitions", [])] for mid, mc in machines.items()}
    ids = {c["id"] for c in spec.get("components", [])}
    for c in spec.get("components", []):
        b = c.get("efsm_binding") or {}
        if c.get("machine_id") and c["machine_id"] not in machines:
            rep.add(sec, "error", f"{c['id']}: machine_id '{c['machine_id']}' does not exist in the EFSM.")
        for k in ("action_at_max", "action_at_min", "press_action"):
            if b.get(k) and c.get("machine_id") in actions and b[k] not in actions[c["machine_id"]]:
                rep.add(sec, "error", f"{c['id']}: {k}='{b[k]}' does not exist in {c['machine_id']}.")
            if k != "action_at_min" and k in b and not b[k]:
                rep.add(sec, "error", f"{c['id']}: {k} is empty.")
        mv = (c.get("movement") or {}).get("type")
        att = c.get("attach_on_complete")
        if mv == "attach" and not att:
            rep.add(sec, "error", f"{c['id']}: movement is 'attach' but no target is set.")
        if att and att.get("target_id") not in ids:
            rep.add(sec, "error", f"{c['id']}: attach target '{att.get('target_id')}' is not in the scene.")

    norm = lambda x: re.sub(r"^machine_", "", str(x or ""))
    interactive = set()
    for c in spec.get("components", []):
        if c.get("interactive") and c.get("machine_id"):
            interactive |= {c["machine_id"], norm(c["machine_id"])}
    for pid in proc_ids:
        p = procs.get(pid)
        if not p:
            continue
        for st in p.get("steps", []):
            if st.get("external_step"):
                continue
            refs = [st["transition_ref"]] if st.get("transition_ref") else (st.get("accepted_transition_refs") or [])
            ms = {trans[r][1] for r in refs if r in trans}
            if ms and not any(x in interactive or norm(x) in interactive for x in ms):
                rep.add(sec, "error", f"'{pid}' step {st['order']}: no interactive component for machine(s) {sorted(ms)}.")

    si = spec.get("scene_integrity") or {}
    if si.get("missing_from_scene"):
        rep.add(sec, "error", "Components missing from the scene: " + ", ".join(si["missing_from_scene"]))
    fc = si.get("figure_check") or {}
    if fc.get("required") and fc.get("status") != "confirmed":
        rep.add(sec, "error", "Whole-device figure check not confirmed.")
    if sec not in rep.sections:
        rep.add(sec, "ok", "Every step is triggered by an interaction of an interactive component; bindings and attach targets resolve.")


# --------------------------------------------------------------------------- 3. competency questions
def all_component_ids(m):
    ids = []
    for key in ("components_catalog", "scene_props", "excluded_components"):
        for c in m.get(key, []):
            if c.get("id") and c["id"] not in ids:
                ids.append(c["id"])
    for c in m.get("new_elements", []):
        cid = re.sub(r"\W+", "_", c.get("name", "").strip().lower())
        if cid and cid not in ids:
            ids.append(cid)
    return ids


def answer_cqs(m, spec, proc_ids):
    machines, trans, comps, dims, procs = index(m)
    en = m.get("enums", {})
    sel = [procs[p] for p in proc_ids if p in procs]
    A = {}

    ids = all_component_ids(m)
    mats = [c for c in comps.values() if c.get("category") == "material"]
    A["CQ1"] = (f"{len(ids)} components; {len(comps)} with a StateMachine ({len(mats)} materials: "
                + ", ".join(c["name"] for c in mats) + ")")

    if spec:
        rows = []
        for c in spec.get("components", []):
            if not c.get("interactive"):
                continue
            d = [round(x * 1000) for x in c["representation"]["dimensions_m"]]
            pv = c["placement"]["pivot"]
            pvs = pv["mode"] + (f" {pv.get('local_position')}" if pv["mode"] == "explicit" else "") + (f" {pv.get('edge')}" if pv.get("edge") else "")
            rows.append(f"{c['id']}: {d[0]} x {d[1]} x {d[2]} mm, {c['representation']['kind']}, pivot {pvs}")
        A["CQ2"] = rows
    else:
        A["CQ2"] = "n/a (no device_spec given)"

    A["CQ3"] = [f"{mid}: " + "; ".join(f"{k} in {d['values']} (initial {d.get('initial')})" for k, d in dims[mid].items())
                + f" | states {[st['id'] for st in mc.get('states', [])]}"
                for mid, mc in machines.items() if mid in dims]

    cq4 = []
    for mid, mc in machines.items():
        for st in mc.get("states", []):
            out = [f"{t['action']}" + (f" [{t['guard']}]" if t.get("guard") else "") for t in mc.get("transitions", [])
                   if s(st.get("dimension_values", {}).get(t["dimension"])) == s(t["from"])]
            to = (st.get("timeout") or {})
            if to.get("after_ms") and to.get("to_state"):
                out.append(f"timeout {to['after_ms']} ms -> {to['to_state']}")
            if out:
                cq4.append(f"{st['id']}: " + ", ".join(out))
    A["CQ4"] = cq4

    if spec:
        cq5, bound = [], set()
        for c in spec.get("components", []):
            b = c.get("efsm_binding") or {}
            mv = (c.get("movement") or {}).get("type")
            for k in ("press_action", "action_at_max", "action_at_min"):
                if b.get(k):
                    cq5.append(f"{b[k]}: {c['id']} {mv} ({k})")
                    bound.add(b[k])
        timed = {t["action"] for mc in machines.values() for t in mc.get("transitions", [])
                 for st in mc.get("states", []) if (st.get("timeout") or {}).get("to_state")
                 and s(st.get("dimension_values", {}).get(t["dimension"])) == s(t["from"]) and t.get("movement") == "none"}
        for a in sorted(timed - bound):
            cq5.append(f"{a}: state timeout (no interaction)")
        A["CQ5"] = cq5
    else:
        A["CQ5"] = "n/a (no device_spec given)"

    cross = [t for t, mid in trans.values() if any(a[0] != mid for a in (guard_atoms(t.get("guard")) or []))]
    A["CQ6"] = (f"{len(cross)} of {len(trans)} transitions cross-guarded: " + ", ".join(t["id"] for t in cross)
                + " | signals: " + ", ".join(f"{t['action']} emits {t['emits']}" for t, _ in trans.values() if t.get("emits")))

    A["CQ7"] = [f"{p['id']} ({p.get('name')})" for p in m.get("procedures", [])]

    A["CQ8"] = {p["id"]: [f"{st['order']}: " + (", ".join(trans[r][0]["action"] for r in ([st["transition_ref"]] if st.get("transition_ref") else st.get("accepted_transition_refs") or []) if r in trans) or "external")
                          + (f" (loop {st['loop']['repeat_step_orders']} x{st['loop'].get('times')})" if (st.get("loop") or {}).get("repeat_step_orders") else "")
                          for st in sorted(p.get("steps", []), key=lambda x: x["order"])] for p in sel}

    A["CQ9"] = {p["id"]: {"pre": [f"{c['machine_id']}.{c['dimension']} = {c['required_value']}" for c in p.get("preconditions", [])],
                          "post": [f"{c['machine_id']}.{c['dimension']} = {c['guaranteed_value']}" for c in p.get("postconditions", [])]} for p in sel}

    cq10 = {}
    for p in sel:
        acted = set()
        for st in p.get("steps", []):
            for r in [st.get("transition_ref")] + (st.get("accepted_transition_refs") or []):
                if r in trans:
                    acted.add(trans[r][1])
        cond = {c["machine_id"] for k in ("preconditions", "postconditions") for c in p.get(k, [])}
        for st in p.get("steps", []):
            for r in [st.get("transition_ref")] + (st.get("accepted_transition_refs") or []):
                if r in trans:
                    cond |= {a[0] for a in guard_atoms(trans[r][0].get("guard")) or []}
        only = cond - acted
        cq10[p["id"]] = f"{len(acted | only)} components ({len(acted)} acted on: {sorted(acted)}; {len(only)} only in conditions: {sorted(only)})"
    A["CQ10"] = cq10

    rules = m.get("rules", [])
    A["CQ11"] = f"{len(rules)} rules, {sum(1 for r in rules if r.get('expr'))} evaluable"

    if any(p.get("requires") for p in m.get("procedures", [])):
        A["CQ12"] = {p["id"]: p.get("requires") for p in m.get("procedures", []) if p.get("requires")}
    else:
        A["CQ12"] = "n/a (no procedure requires another)"

    A["CQ13"] = (f"{len(en.get('dimensions', []))} state variables, {len(en.get('actions', []))} actions, "
                 f"{len(en.get('signals', []))} signals")

    A["CQ14"] = [f"{c['name']}: " + " x ".join(f"{d['name']} {d['values']}" for d in c.get("dimensions", [])) for c in mats] or "n/a (no materials)"
    return A


# --------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--efsm", required=True)
    ap.add_argument("--spec")
    ap.add_argument("--procedure", help="Procedure for C5 and CQ8-CQ10 (default: device_meta.target_procedures).")
    ap.add_argument("--json", help="Write the full report to this file.")
    a = ap.parse_args()

    m = load(a.efsm)
    spec = load(a.spec) if a.spec else None
    rep = Report()

    check_schema(rep, m, "efsm.schema.json", "Schema (EFSM)")
    if spec:
        check_schema(rep, spec, "device_spec.schema.json", "Schema (device_spec)")
    check_references(rep, m)
    walk = check_c1_c4(rep, m)
    procs = [a.procedure] if a.procedure else walk
    if spec:
        check_c5(rep, m, spec, procs)
    else:
        rep.add("C5 steps triggered by interactions", "info", "Not checked (no device_spec given).")
    cqs = answer_cqs(m, spec, procs)

    icon = {"ok": "OK  ", "error": "ERR ", "warning": "WARN", "info": "INFO"}
    print(f"Instance: {os.path.basename(a.efsm)}" + (f" + {os.path.basename(a.spec)}" if spec else ""))
    for sec, items in rep.sections.items():
        print(f"\n[{sec}]")
        for x in items:
            print(f"  {icon[x['level']]} {x['msg']}")
    print("\n[Competency questions]")
    for k, v in cqs.items():
        if isinstance(v, (list, dict)):
            print(f"  {k}:")
            for line in (v if isinstance(v, list) else [f"{kk}: {vv}" for kk, vv in v.items()]):
                print(f"      {line}")
        else:
            print(f"  {k}: {v}")
    answered = sum(1 for v in cqs.values() if not (isinstance(v, str) and v.startswith("n/a")))
    print(f"\nSummary: {rep.errors()} error(s), {rep.warnings()} warning(s); "
          f"competency questions answered {answered} / n.a. {len(cqs) - answered}")

    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump({"checks": rep.sections, "competency_questions": cqs,
                       "errors": rep.errors(), "warnings": rep.warnings()}, f, indent=2, ensure_ascii=False)
    sys.exit(1 if rep.errors() else 0)


if __name__ == "__main__":
    main()
