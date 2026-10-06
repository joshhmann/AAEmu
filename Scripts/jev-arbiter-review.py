"""Offline REVIEW of frozen arbiter decisions. Never a replay, never a verdict.

Reads a captured ``jev-shadow/v2`` dataset and reports, per wake: the incumbent
the game took, the legal alternatives it outranked, the hard-precondition
rejections, and the request-vs-world boundary.

Three rules, each enforced in code:

1. It never re-decides. Priorities are the frozen copies the shipped selector
   saw. Nothing here re-runs ``BotDecisionSelector`` — this is a review of a
   record, not a re-execution of it.
2. Request completion is NOT world consequence. ``request.state`` is the actor
   request's terminal state and ``decide_detail`` is the actor's own
   self-report string (BotRoamStepExecutor.cs:1566). ``completed_quest_ids`` is
   a set-difference over ``ActiveQuestIds`` (QuestBehavior.cs:959), i.e. "this
   quest left the active set" — ``DropQuest`` removes from that same set on the
   client abandon packet and on GM commands, so it does not prove completion.
3. It refuses a dataset it cannot read faithfully: a non-v2 or mixed-schema
   file, or a row whose ``candidate_count`` contradicts its own arrays. Those
   are errors with a non-zero exit, not a report.

No model caller: no credentials, no network, no model. It produces the artifact
a reviewer reads; it does not produce the verdict.
"""

import argparse
import hashlib
import json
import sys

# The live schema. v3 adds `target_id` to every candidate row, which is what
# makes a legal loser joinable to the objective it named.
SCHEMA = "jev-shadow/v3"

# v2 is HISTORIC. It has no target_id, and its datasets are never rewritten or
# relabelled. It is readable only under the explicit --historic flag, which
# makes the missing identity visible in every report instead of silently
# reading as "same quest". v1 is NOT readable: its candidate_count meant
# rejections+1 and cannot see a legal loser at all.
HISTORIC_SCHEMA = "jev-shadow/v2"

# The rank keys of a candidate row: everything the SELECTOR ordered on. They
# describe a score, not an identity — two different proposals can share all
# four — so this tuple is reported for inspection and is never used to decide
# whether two candidates are the same candidate. That decision is made on the
# reference a reviewer named (see compare()).
CANDIDATE_KEYS = ("goal", "action", "priority", "weight")


def check_row(row, where, require_target):
    """Validate one row's shape, raising ValueError naming the first defect.

    Only the types this review actually reads are enforced. Anything it would
    otherwise subscript blind — the row itself, the two candidate arrays, and
    the four rank keys of a candidate — is checked here so a malformed row is
    reported as unreadable rather than crashing mid-render.
    """
    if not isinstance(row, dict):
        raise ValueError(f"{where}: expected a JSON object, got {type(row).__name__}")
    if not isinstance(row.get("schema"), str):
        raise ValueError(f"{where}: 'schema' must be a string")

    def check_candidate(candidate, label):
        if not isinstance(candidate, dict):
            raise ValueError(
                f"{label} must be an object, got {type(candidate).__name__}")
        for key in CANDIDATE_KEYS:
            if key not in candidate:
                raise ValueError(f"{label} is missing '{key}'")
        # v3 carries the proposal's own identity on EVERY candidate kind. It is
        # required, not optional: a candidate row without it is a row the
        # recorder did not write, and silently reading it as 0 would invent an
        # "unnamed target" the capture never asserted.
        if require_target:
            target = candidate.get("target_id")
            if not isinstance(target, int) or isinstance(target, bool) or target < 0:
                raise ValueError(
                    f"{label}: 'target_id' must be a non-negative integer in a "
                    f"{SCHEMA} row, got {candidate.get('target_id')!r}")

    for field in ("candidates", "legal_alternatives"):
        value = row.get(field)
        # legal_alternatives is null when the lane did not collect it; every
        # other value must be a list, and that list must hold candidate rows.
        if value is not None and not isinstance(value, list):
            raise ValueError(
                f"{where}: '{field}' must be a list or null, got {type(value).__name__}")
        for position, candidate in enumerate(value or []):
            check_candidate(candidate, f"{where}: {field}[{position}]")

    selected = row.get("selected_candidate")
    if selected is not None:
        check_candidate(selected, f"{where}: 'selected_candidate'")

    # Required, not optional: load() subscripts this field to reconcile it
    # against the row's own arrays, so an absent or non-integer count must be
    # refused here rather than surfacing as a KeyError during reconciliation.
    if "candidate_count" not in row:
        raise ValueError(f"{where}: 'candidate_count' is required")
    count = row["candidate_count"]
    if not isinstance(count, int) or isinstance(count, bool):
        raise ValueError(
            f"{where}: 'candidate_count' must be an integer, got {type(count).__name__}")

    # Required, not optional: render() joins the dataset's scenario names, so a
    # null would fail there rather than being reviewable content.
    if not isinstance(row.get("scenario"), str):
        raise ValueError(
            f"{where}: 'scenario' must be a string, got "
            f"{type(row.get('scenario')).__name__}")

    request = row.get("request")
    if request is not None and not isinstance(request, dict):
        raise ValueError(
            f"{where}: 'request' must be an object or null, got {type(request).__name__}")


def load(path, historic=False):
    """Return the rows, or raise ValueError naming why the file is unreadable.

    Default reads only the live schema. `--historic` additionally accepts the
    v2 shape, which has no `target_id`: those rows are read for their rank and
    legality content, and every report says the identity field was absent
    rather than letting it read as zero. v1 is never readable — its
    candidate_count meant rejections+1 and cannot see a legal loser.

    A row whose candidate_count cannot be produced by its own arrays is
    internally inconsistent, so trusting it would launder a recorder defect
    into a review finding. That is an error here, not a reported observation.
    """
    allowed = {SCHEMA} | ({HISTORIC_SCHEMA} if historic else set())
    rows, schemas = [], set()
    with open(path, encoding="utf-8") as stream:
        for number, line in enumerate(stream, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"line {number}: malformed JSON ({error.msg})") from error
            # Validate the shape BEFORE reading any field off the row: a row
            # that is not an object has no .get at all, and evaluating the
            # require_target argument first would raise AttributeError instead
            # of the named refusal check_row produces.
            if not isinstance(row, dict):
                raise ValueError(
                    f"line {number}: expected a JSON object, got {type(row).__name__}")
            # v3 requires the identity field; v2 never had one.
            check_row(row, f"line {number}", require_target=row.get("schema") == SCHEMA)
            schemas.add(row["schema"])
            rows.append(row)

    if not rows:
        raise ValueError("dataset has no rows")
    if not schemas <= allowed:
        found = ", ".join(sorted(schemas))
        raise ValueError(
            f"refusing a dataset outside {', '.join(sorted(allowed))} (found: {found}). "
            "v1's candidate_count means something different; a v2 dataset needs "
            "--historic and has no target_id. Re-harvest instead of relabelling."
        )

    for number, row in enumerate(rows, start=1):
        # candidate_count is selector truth: hard rejections + every legal
        # proposal, the winner included. Same meaning in v2 and v3.
        expected = len(row.get("candidates") or []) \
            + len(row["legal_alternatives"] or []) \
            + (1 if row.get("selected_candidate") else 0)
        if row["candidate_count"] != expected:
            raise ValueError(
                f"line {number}: candidate_count {row['candidate_count']} "
                f"contradicts its arrays (they imply {expected})"
            )
    return rows

# A claim is an assertion ABOUT a wake, supplied from outside this capture.
# It is never inferred: the reviewer states it, states what it rests on, and
# states how strongly it joins to the frozen row. `join` records the
# REVIEWER'S DECLARED join strength — this tool never opens or reads the file
# named in `source`, so it cannot check that the artifact carries the cycle id
# or says what the claim asserts:
#   "exact-cycle"        -> the reviewer declares the artifact carries the row's
#                           own cycle_id; a DIRECT join, but unverified here
#   "identity-sequence"  -> matches on bot/quest/run only; correlation, not proof
#   "none"               -> an unbound assertion
# What the tool DOES check is the binding it controls: the envelope's
# dataset_sha256 must match this capture and the cycle_id must name exactly one
# wake. Claim TRUTH remains a human reviewer's judgement.
JOIN_STRENGTHS = ("exact-cycle", "identity-sequence", "none")
CLAIM_CLASSES = ("authoritative-state", "engine-log", "actor-self-report", "reviewer-opinion")

# A reviewer may disagree with the incumbent, but nothing in this dataset or its
# lane reports what the other candidate would have produced. The comparison
# therefore states the disagreement and stops there.
UNKNOWN_BENEFIT = (
    "UNKNOWN — no artifact observes what the other candidate would have done, "
    "so a DISAGREES verdict is a difference of opinion, not an improvement.")


# Identity tokens that mark a critique as a PLACEHOLDER rather than a real
# review. The shipped example uses one, and without this a reader could run it
# and get a confident-looking DISAGREES/AGREES pair attributed to nobody. A
# placeholder is a documentation artifact, not a review, so it is refused rather
# than reported: emitting a verdict for it would be fabricated evidence.
ILLUSTRATIVE_REVIEWER_MARKERS = (
    "example-reviewer",
    "illustrative",
    "placeholder",
    "replace with a real identity",
    "todo",
    "changeme",
)


def is_illustrative_reviewer(name):
    """True when a reviewer identity is an obvious placeholder."""
    lowered = name.strip().lower()
    return any(marker in lowered for marker in ILLUSTRATIVE_REVIEWER_MARKERS)



def load_outcomes(path, sha256, by_cycle):
    """Read externally OBSERVED outcomes. Every failure raises, none is a note.

    An outcome claim asserts a fact about the world, so it must be bound as
    strictly as a critique: it names the exact dataset sha256 it was gathered
    against and a cycle this capture actually contains. A claim that cannot be
    bound is refused rather than reported, because an unbound fact silently
    attached to the wrong wake is worse than no claim at all.
    """
    with open(path, encoding="utf-8") as stream:
        envelope = json.load(stream)
    if not isinstance(envelope, dict):
        raise ValueError("outcome envelope must be a JSON object")
    for field in ("dataset_sha256", "claims"):
        if field not in envelope:
            raise ValueError(f"outcome envelope needs '{field}'")
    if str(envelope["dataset_sha256"]).lower() != sha256:
        raise ValueError(
            f"outcome claims target dataset sha256 {envelope['dataset_sha256']} but "
            f"this capture is {sha256}; refusing claims about different rows")
    claims = envelope["claims"]
    if not isinstance(claims, list):
        raise ValueError("outcome envelope 'claims' must be a list")

    resolved = []
    for position, raw in enumerate(claims):
        label = f"outcome claim {position}"
        if not isinstance(raw, dict):
            raise ValueError(f"{label}: not an object")
        for field in ("cycle_id", "claim", "basis", "source", "join", "class"):
            if not isinstance(raw.get(field), str) or not raw[field].strip():
                raise ValueError(f"{label}: missing or non-string '{field}'")
        if raw["join"] not in JOIN_STRENGTHS:
            raise ValueError(
                f"{label}: join must be one of {', '.join(JOIN_STRENGTHS)}, "
                f"got '{raw['join']}'")
        if raw["class"] not in CLAIM_CLASSES:
            raise ValueError(
                f"{label}: class must be one of {', '.join(CLAIM_CLASSES)}")
        if raw["class"] == "reviewer-opinion":
            raise ValueError(
                f"{label}: an outcome must be observed; a reviewer-opinion "
                "belongs in a critique envelope")
        if raw["cycle_id"] not in by_cycle:
            raise ValueError(
                f"{label}: cycle '{raw['cycle_id']}' is not in this capture; "
                "refusing an unbound claim")
        resolved.append(raw)
    return resolved


def load_critique(path, sha256, by_cycle):
    """Read a reviewer critique envelope and bind each pick to its frozen wake.

    An envelope must name its reviewer and the exact dataset sha256 it was
    written against; a hash that does not match this capture is REFUSED, because
    a critique judged against different rows says nothing about these. Each pick
    must index a LEGAL candidate — `selected_candidate` or
    `legal_alternatives[n]` — and never a hard rejection in `candidates[]`,
    which the engine already refused as illegal and which therefore cannot be a
    challenger.
    Every failure raises: a critique that cannot be bound to a wake must not be
    silently downgraded to a note. An ILLUSTRATIVE reviewer identity is refused
    outright (see is_illustrative_reviewer), so a documentation template can
    never be rendered into an AGREES/DISAGREES verdict.
    """
    with open(path, encoding="utf-8") as stream:
        envelope = json.load(stream)
    if not isinstance(envelope, dict):
        raise ValueError("critique envelope must be a JSON object")
    for field in ("reviewer", "dataset_sha256"):
        if not isinstance(envelope.get(field), str) or not envelope[field].strip():
            raise ValueError(f"critique envelope needs a non-empty '{field}'")
    reviewer = envelope["reviewer"]
    if is_illustrative_reviewer(reviewer):
        raise ValueError(
            f"critique reviewer {reviewer!r} is an ILLUSTRATIVE placeholder, not a "
            "real reviewer; refusing to emit an AGREES/DISAGREES verdict from "
            "filler. Replace 'reviewer' with a real identity (or drop --critique) "
            "to record genuine external review. The shipped example file is a "
            "format template only.")
    if envelope["dataset_sha256"].lower() != sha256:
        raise ValueError(
            f"critique targets dataset sha256 {envelope['dataset_sha256']} but "
            f"this capture is {sha256}; refusing a critique of different rows")
    picks = envelope.get("critique")
    if not isinstance(picks, list) or not picks:
        raise ValueError("critique envelope needs a non-empty 'critique' list")

    resolved = []
    for position, raw in enumerate(picks):
        label = f"critique {position}"
        if not isinstance(raw, dict):
            raise ValueError(f"{label}: not an object")
        for field in ("cycle_id", "chosen", "claim", "basis"):
            if not isinstance(raw.get(field), str) or not raw[field].strip():
                raise ValueError(f"{label}: missing or non-string '{field}'")
        cycle = raw["cycle_id"]
        wake = by_cycle.get(cycle)
        if wake is None:
            raise ValueError(
                f"{label}: cycle '{cycle}' is not in this capture "
                f"({len(by_cycle)} cycles); refusing an unbound critique")
        resolved.append((wake, raw, resolve_choice(label, wake, raw["chosen"])))
    return envelope["reviewer"], resolved


def resolve_choice(label, wake, chosen):
    """Map a challenger's `chosen` reference onto the frozen candidate it names.

    Only the legal set is addressable. `selected_candidate` and
    `legal_alternatives[n]` resolve; anything addressing `candidates[]` — a hard
    precondition rejection, which the engine refused as illegal — is rejected,
    because a refused proposal cannot be proposed again.
    """
    legal = wake["legal_alternatives"]
    if chosen == "selected_candidate":
        if wake["incumbent"] is None:
            raise ValueError(f"{label}: 'selected_candidate' but this wake selected nothing")
        return wake["incumbent"]
    if chosen.startswith("legal_alternatives["):
        try:
            position = int(chosen[len("legal_alternatives["):-1])
        except ValueError:
            raise ValueError(f"{label}: cannot parse '{chosen}'") from None
        if legal is None:
            raise ValueError(
                f"{label}: this wake did not collect legal_alternatives (null), "
                "so no alternative can be chosen")
        if not 0 <= position < len(legal):
            raise ValueError(
                f"{label}: '{chosen}' is out of range (0..{len(legal) - 1})")
        return legal[position]
    raise ValueError(
        f"{label}: 'chosen' must be 'selected_candidate' or 'legal_alternatives[n]', "
        f"got '{chosen}'; a hard rejection in candidates[] is illegal and cannot be chosen")


def compare(wake, raw, chosen):
    """Compare the challenger's pick against the frozen incumbent, by REFERENCE.

    The verdict is decided on WHICH frozen candidate the reviewer named, not on
    its rank tuple. Rank equality is not identity: two different proposals can
    score identically (a band tie, or two quests at the same offered level), and
    comparing on rank would report AGREES for a challenger that picked a
    different objective than the game did. `resolve_choice` already resolved the
    reference, so the answer is structural — `selected_candidate` IS the
    incumbent by construction, and any `legal_alternatives[n]` is by definition
    NOT the one the game took.

    The candidate data is still carried, separately and verbatim, so a reader
    can see what was picked and what was taken; `targets_differ` names the case
    that a rank-based comparison would have silently missed.

    BENEFIT is always UNKNOWN — no artifact in this dataset or its lane reports
    what the other candidate would have produced, so the tool refuses to imply
    that a disagreement is an improvement.
    """
    incumbent = wake["incumbent"]
    ref = raw["chosen"]
    same = ref == "selected_candidate"
    chosen_target = chosen.get("target_id")
    incumbent_target = incumbent.get("target_id") if incumbent else None
    return {
        "reviewer_claim": raw["claim"],
        "basis": raw["basis"],
        "chosen_ref": ref,
        "chosen": chosen,
        "incumbent": incumbent,
        "verdict": "AGREES" if same else "DISAGREES",
        "targets_differ": (chosen_target is not None and incumbent_target is not None
                           and chosen_target != incumbent_target),
        # True only when the two picks are indistinguishable by score AND differ
        # by identity — the only case a rank-based comparison would misreport.
        "rank_collision": (incumbent is not None
                           and tuple(incumbent.get(key) for key in CANDIDATE_KEYS)
                           == tuple(chosen.get(key) for key in CANDIDATE_KEYS)),
        "chosen_target": chosen_target,
        "incumbent_target": incumbent_target,
        "targets_differ": (chosen_target is not None and incumbent_target is not None
                           and chosen_target != incumbent_target),
        "benefit_of_disagreement": UNKNOWN_BENEFIT,
    }


def review_wake(row, index):
    return {
        "wake": index,
        "cycle_id": row.get("cycle_id"),
        "incumbent": row.get("selected_candidate"),
        "legal_alternatives": row.get("legal_alternatives"),
        "hard_rejections": row.get("candidates") or [],
        # The three outcome layers, kept apart on purpose.
        "request_state": (row.get("request") or {}).get("state"),
        "actor_self_report": row.get("decide_detail"),
        "left_active_quest_set": row.get("completed_quest_ids") or [],
    }


def review(path, outcome_path=None, critique_path=None, historic=False):
    rows = load(path, historic)
    with open(path, "rb") as stream:
        digest = hashlib.sha256(stream.read()).hexdigest()
    schemas = sorted({row["schema"] for row in rows})

    wakes = [review_wake(row, index) for index, row in enumerate(rows)]
    # cycle_id is the ONLY join key an outcome or critique envelope is bound
    # against. A duplicate would silently collapse two wakes into one and let a
    # claim attach to the wrong decision; an empty or missing one cannot be
    # bound at all. Both are refused before any claim is read.
    by_cycle = {}
    for wake in wakes:
        cycle = wake["cycle_id"]
        if not isinstance(cycle, str) or not cycle.strip():
            raise ValueError(
                f"line {wake['wake'] + 1}: 'cycle_id' must be a non-empty string; "
                "it is the only key a supplied claim can be bound to")
        if cycle in by_cycle:
            raise ValueError(
                f"line {wake['wake'] + 1}: duplicate cycle_id '{cycle}'; a supplied "
                "claim could not be resolved to exactly one wake")
        by_cycle[cycle] = wake

    for wake in wakes:
        wake["external_outcome_claims"] = []
        wake["reviewer_comparisons"] = []
    if outcome_path:
        for claim in load_outcomes(outcome_path, digest, by_cycle):
            by_cycle[claim["cycle_id"]]["external_outcome_claims"].append(claim)
    reviewer = None
    if critique_path:
        reviewer, picks = load_critique(critique_path, digest, by_cycle)
        for wake, raw, chosen in picks:
            wake["reviewer_comparisons"].append(compare(wake, raw, chosen))
    # Per-WAKE, not per dataset: under --historic a file may legitimately mix v3
    # and v2 rows, and a dataset-wide flag would claim v3 identity for a v2 wake
    # that never had it. Each row's own schema decides.
    for wake, row in zip(wakes, rows):
        wake["target_identity"] = "present" if row["schema"] == SCHEMA else "ABSENT (v2)"
    return {
        "schema": "/".join(schemas),
        "target_identity": ("all present" if schemas == [SCHEMA]
                            else "mixed" if len(schemas) > 1 else "ABSENT (historic v2)"),
        "dataset": path,
        "sha256": digest,
        "wake_count": len(rows),
        "scenario": sorted({row["scenario"] for row in rows}),
        "wakes": wakes,
        "outcome_boundary": (
            "IN THIS DATASET ALONE, nothing is independently verified: "
            "request_state is the actor request's terminal state, "
            "actor_self_report is the actor's own detail string, and "
            "left_active_quest_set is a set-difference over ActiveQuestIds "
            "(QuestBehavior.cs:959) that DropQuest also mutates on the client "
            "abandon packet and on GM commands. An external artifact (a server "
            "log, a lane test report, a DB read) MAY corroborate a specific "
            "wake — that is what the external claims below record, each with "
            "its own join strength."
        ),
        "comparative_quality": UNKNOWN_BENEFIT,
        "critic_reviewer": reviewer,
    }


def render(report):
    out = [
        "OFFLINE REVIEW — frozen capture read as-is; no wake was re-decided",
        f"schema  : {report['schema']}   wakes: {report['wake_count']}   "
        f"scenario: {', '.join(report['scenario'])}",
        f"targets : {report['target_identity']}",
        "",
    ]
    for wake in report["wakes"]:
        incumbent = wake["incumbent"]
        out.append(f"[{wake['wake']}] {wake['cycle_id']}")
        # Per ROW, not per dataset: a --historic read may legitimately mix v3
        # and v2 rows, and only the v3 ones carry target_id. Reading the flag
        # from the dataset would subscript a v2 row that never had the field.
        target = (lambda c: f" target={c['target_id']}"
                  if c.get("target_id") is not None else " target=ABSENT")
        if incumbent:
            out.append(f"    took    : {incumbent['action']} p{incumbent['priority']} "
                       f"w{incumbent['weight']}{target(incumbent)}")
        else:
            out.append("    took    : (nothing selected)")
        for alt in wake["legal_alternatives"] or []:
            out.append(f"    outranked: {alt['action']} p{alt['priority']} "
                       f"w{alt['weight']}{target(alt)}")
        if wake["legal_alternatives"] is None:
            out.append("    legal alts: NOT COLLECTED (null) — not 'none existed'")
        out.append(f"    request : {wake['request_state'] or '-'}")
        out.append(f"    self-rpt: {(wake['actor_self_report'] or '-')[:88]}")
        out.append(f"    left set: {wake['left_active_quest_set']}  <- left ActiveQuestIds; NOT proof of completion")
        for claim in wake["external_outcome_claims"]:
            out.append(f"    EXTERNAL  [{claim['join']}/{claim['class']}] {claim['claim']}")
            out.append(f"              basis: {claim['basis']}")
            out.append(f"              source: {claim['source']}")
        for entry in wake["reviewer_comparisons"]:
            out.append(f"    COMPARE   {entry['verdict']}  (reviewer: {report['critic_reviewer']})")
            out.append(f"              chose {entry['chosen_ref']} -> "
                       f"{entry['chosen']['action']} p{entry['chosen']['priority']} "
                       f"w{entry['chosen']['weight']}")
            out.append("              incumbent -> " + (
                f"{entry['incumbent']['action']} p{entry['incumbent']['priority']} "
                f"w{entry['incumbent']['weight']}{target(entry['incumbent'])}"
                if entry["incumbent"] else "(none)"))
            out.append(f"              basis: {entry['basis']}")
            # The collision case a score-based comparison would have MISSED is
            # specifically "same rank, different target". Different ranks are
            # already visibly different, so naming them a collision would be
            # misleading; report only the genuinely indistinguishable pair.
            if entry["targets_differ"] and entry["rank_collision"]:
                out.append(f"              NOTE: same rank, DIFFERENT target "
                           f"({entry['chosen_target']} vs {entry['incumbent_target']}) "
                           f"- a rank-based comparison would have called this agreement")
            elif entry["targets_differ"]:
                out.append(f"              note: different targets "
                           f"({entry['chosen_target']} vs {entry['incumbent_target']}), "
                           f"and different ranks")
            out.append(f"              benefit: {entry['benefit_of_disagreement']}")
        out.append("")
    out.append(f"BOUNDARY: {report['outcome_boundary']}")
    out.append(f"COMPARATIVE QUALITY: {report['comparative_quality']}")
    return "\n".join(out)

def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Offline review of frozen jev-shadow arbiter decisions.")
    parser.add_argument("dataset", help="path to a captured jev-shadow JSONL dataset")
    parser.add_argument("--out", help="write the JSON report here")
    parser.add_argument(
        "--historic",
        action="store_true",
        help=f"also read the historical {HISTORIC_SCHEMA} shape, which has no "
             "target_id. Off by default, so a v2 file is never mistaken for a "
             "live one; reports state the identity field is absent.")
    parser.add_argument(
        "--outcome-claims",
        help="JSON envelope of externally OBSERVED outcomes. Must carry the "
             "dataset sha256 it was gathered against; every claim must name a "
             "cycle this capture contains. Bound strictly or refused.")
    parser.add_argument(
        "--critique",
        help="JSON envelope of reviewer picks. Must carry a reviewer identity "
             "and the dataset sha256; each pick must index selected_candidate or "
             "legal_alternatives[n] (never a hard rejection).")
    args = parser.parse_args(argv)

    try:
        report = review(args.dataset, args.outcome_claims, args.critique, args.historic)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"REFUSED  {args.dataset}\n  - {error}", file=sys.stderr)
        return 1

    print(render(report))
    if args.out:
        try:
            with open(args.out, "w", encoding="utf-8") as stream:
                json.dump(report, stream, indent=2)
                stream.write("\n")
        except OSError as error:
            print(f"CANNOT WRITE REPORT  {args.out}\n  - {error}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())