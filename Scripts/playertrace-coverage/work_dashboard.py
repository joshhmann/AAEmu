"""Operational delivery cards and work records, not canonical evidence or agent launch.

The public API is WorkDashboard.snapshot/mutate/reference/brief and WorkError.
GETs never create a store. Mutations reload under a process/thread lock, check the
revision, validate the complete candidate board, then atomically replace JSON.
"""

from contextlib import contextmanager
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import tempfile
import threading
from urllib.parse import urlsplit
import uuid
try:
    import fcntl
except ImportError:
    fcntl = None

POSIX_WORK_AVAILABLE = (
    os.name == "posix" and fcntl is not None
    and all(hasattr(os, flag) for flag in ("O_NOFOLLOW", "O_DIRECTORY", "O_NONBLOCK"))
    and os.open in getattr(os, "supports_dir_fd", ())
)


class WorkError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status
        self.message = message


AUTHORITY = (
    "Operational cards/session metadata only. PROJECT-CONTROL/ROADMAP own scope; "
    "CURRENT/DECISIONS/EVIDENCE own canonical objective/decisions/proof. "
    "UI assertions do not verify outcomes. Work records do not launch OMP agents."
)
LIMITS = {"primary": 1, "secondary": 1}
MAX_STORE_BYTES = 8 * 1024 * 1024
MAX_SOURCE_BYTES = 8 * 1024 * 1024
MAX_PREVIEW_BYTES = 64 * 1024
MAX_HISTORY = 100
SLUG = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
SHA = re.compile(r"[0-9a-fA-F]{40}\Z")
TEXT_SUFFIXES = {
    ".md", ".txt", ".cs", ".py", ".js", ".ts", ".json", ".yaml",
    ".yml", ".sql", ".csv", ".rst", ".csproj", ".sln", ".slnx", ".props",
    ".targets", ".sh", ".xml", ".html", ".css", ".toml",
}
CANONICAL_PATHS = (
    "PROJECT-CONTROL.md", "ROADMAP.md", ".omp/state/CURRENT.md",
    ".omp/state/DECISIONS.md", ".omp/state/EVIDENCE.md",
)
REPO_REFERENCE_ROOTS = {
    "Docs", "docs", "scorecard-explorations", "Scripts", "scripts", "Tools", "tools",
    "SQL", ".kanban-templates", "AAEmu.Game", "AAEmu.Login", "AAEmu.Commons",
    "AAEmu.UnitTests", "AAEmu.IntegrationTests", "AAEmu.Login.IntegrationTests",
    "AAEmu.ArchaeologyMcp", "AAEmu.BotControl", "AAEmu.BotControlMcp", "AAEmu.Aspire.AppHost",
}
# Historical runtime folders are not a general browsing surface. Expand this
# receipt allowlist only when another sanitized report/spec has been reviewed.
REPO_ARTIFACT_REFERENCES = {
    ".omp/state/artifacts/autonomous-farm-2026-10-09/README.md",
    ".omp/state/artifacts/autonomy-priorities-2026-10-09/live-seven-gates/attempt-summary.json",
    ".omp/state/artifacts/dashboard-work-orchestration/contract.json",
    ".omp/state/artifacts/playerbot-variation-comparison/comparison.json",
    ".omp/state/artifacts/playerbot-variation-roadmap/catalogue-inventory.json",
}
REPO_PROJECT_REFERENCES = {"AAEmu.slnx", "Directory.Build.props", "Directory.Packages.props"}
CARD_TEXT = (
    "title", "parent", "outcome", "owner", "verifier", "baseline_sha",
    "next_action", "blocker", "acceptance_note",
)
CARD_LISTS = (
    "scope", "resources", "non_goals", "acceptance", "reference_ids",
    "dependencies", "unknowns", "evidence_refs",
)
CARD_ENUMS = {
    "mode": ("Research", "Implement", "Validate", "Review"),
    "slot": ("primary", "secondary"),
    "status": ("Backlog", "Needs evidence", "Ready", "Implementing", "Verifying", "Accepted", "Blocked"),
    "priority": ("Critical", "High", "Normal", "Low"),
    "evidence_layer": ("control", "unit", "rig", "live", "human"),
}
CONTRACT_FIELDS = (
    "id", "feature_id", "title", "parent", "outcome", "mode", "slot", "priority",
    "owner", "verifier", "scope", "resources", "non_goals", "acceptance",
    "evidence_layer", "reference_ids", "dependencies", "baseline_sha",
)
_LOCKS = {}
_LOCKS_GUARD = threading.Lock()


def _text(value, label, maximum=4000, required=False):
    if not isinstance(value, str) or len(value) > maximum or "\x00" in value:
        raise WorkError(400, f"{label} must be text of at most {maximum} characters")
    try:
        value.encode("utf-8")
    except UnicodeError as exc:
        raise WorkError(400, f"{label} must be valid UTF-8 text") from exc
    if required and not value.strip():
        raise WorkError(400, f"{label} is required")
    return value


def _slug(value, label):
    if not isinstance(value, str) or len(value) > 100 or not SLUG.fullmatch(value):
        raise WorkError(400, f"{label} must be a lowercase slug")
    return value


def _strings(value, label, maximum=80, item_max=4000):
    if not isinstance(value, list) or len(value) > maximum:
        raise WorkError(400, f"{label} must be a list of at most {maximum} entries")
    result = [_text(item, label, item_max, required=True) for item in value]
    if len(set(result)) != len(result):
        raise WorkError(400, f"{label} has duplicate entries")
    return result


def _relative(value, label):
    _text(value, label, 1000, required=True)
    if "\\" in value or any(ord(c) < 32 for c in value):
        raise WorkError(400, f"{label} is not a safe relative path")
    parts = value.split("/")
    if value.startswith("/") or any(p in ("", ".", "..") for p in parts):
        raise WorkError(400, f"{label} must be a normalized relative path without traversal")
    if ":" in value:
        raise WorkError(400, f"{label} cannot contain a URI or drive prefix")
    return str(PurePosixPath(value))


def _source_path(value):
    path = _relative(value, "reference path")
    parts = [part.lower() for part in path.split("/")]
    name = parts[-1]
    forbidden = re.compile(r"(?:^|[._-])(config(?:uration)?|appsettings|secrets?|credentials?|passwords?|passwd|tokens?|private|auth)(?:$|[._-])")
    if any(p in {".git", ".ssh", ".gnupg", "node_modules", "bin", "obj", "secrets", "private", "config", "configs", "configuration"}
           or p.startswith(".env") or forbidden.search(p) for p in parts):
        raise WorkError(400, "Configuration, authentication and secret paths cannot be previewed")
    suffix = PurePosixPath(name).suffix
    if suffix not in TEXT_SUFFIXES or any(marker in name for marker in (".sqlite", ".db", ".pem", ".key", ".pfx")):
        raise WorkError(400, "Only approved textual sources may be previewed; databases and keys are forbidden")
    if suffix in {".json", ".jsonl", ".csv", ".log", ".txt"} and any(
        re.search(r"(?:^|[._-])(raw|captures?|traces?|network|packets?|traffic|login|auth)(?:$|[._-])", p)
        for p in parts
    ):
        raise WorkError(400, "Raw authentication/network captures cannot be previewed")
    return path


def _repo_source_path(value):
    path = _source_path(value)
    parts = path.split("/")
    denied_dirs = {
        ".omp", ".agents", ".hermes", ".client_files", ".server_files", "agents", "agent",
        "sessions", "session", "cache", "caches", "runtime", "logs", "traces", "trace",
        "captures", "capture", "raw", "data",
    }
    if path in CANONICAL_PATHS or path == ".omp/state/README.md" or path in REPO_ARTIFACT_REFERENCES:
        return path
    if len(parts) == 1:
        if not parts[0].startswith(".") and (path.endswith(".md") or path in REPO_PROJECT_REFERENCES):
            return path
    elif parts[0] in REPO_REFERENCE_ROOTS:
        private_dir = re.compile(r"^(?:agents?|sessions?|caches?|runtime|logs?|traces?|captures?|raw)(?:$|[._-])", re.I)
        if any(part.lower() == "gamedata" for part in parts[1:-1]) and not (
                parts[:2] == ["AAEmu.Game", "GameData"] and path.endswith(".cs")):
            raise WorkError(400, "Only C# loader source is allowed under GameData")
        if (not any(part.startswith(".") for part in parts[1:])
                and not any(part.lower() in denied_dirs or private_dir.search(part) for part in parts[1:-1])):
            return path
    raise WorkError(400, "Reference is outside approved public documentation/source/report surfaces")


def _object(value, label, fields):
    if not isinstance(value, dict):
        raise WorkError(400, f"{label} must be an object")
    if any(not isinstance(key, str) for key in value):
        raise WorkError(400, f"{label} field names must be strings")
    extra = set(value) - set(fields)
    if extra:
        raise WorkError(400, f"Unknown {label} fields: {', '.join(sorted(extra))}")


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="microseconds")


class WorkDashboard:
    @staticmethod
    def _require_platform():
        if not POSIX_WORK_AVAILABLE:
            raise WorkError(503, "Work-board routes require POSIX flock and safe directory-descriptor reads; legacy dashboard routes remain available")

    def __init__(self, repo_root, data_file=None, research_root=None):
        self.repo_root = Path(repo_root).resolve()
        self.data_file = Path(data_file) if data_file is not None else self.repo_root / "playertrace-coverage/work_board.json"
        self.data_file = self.data_file.absolute()
        self.catalog_file = self.repo_root / "Scripts/playertrace-coverage/work_catalog.json"
        research = research_root if research_root is not None else os.environ.get("AAEMU_BOT_RESEARCH_ROOT")
        self.research_root = Path(research).resolve() if research else None
        with _LOCKS_GUARD:
            self._lock = _LOCKS.setdefault(str(self.data_file), threading.RLock())

    @contextmanager
    def _mutation_lock(self):
        with self._lock:
            lock_path = self.data_file.with_name(self.data_file.name + ".lock")
            try:
                self.data_file.parent.mkdir(parents=True, exist_ok=True)
                fd = os.open(lock_path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
                with os.fdopen(fd, "a") as stream:
                    if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                        raise WorkError(500, "Work-store lock is not a regular file")
                    fcntl.flock(stream.fileno(), fcntl.LOCK_EX)
                    try:
                        yield
                    finally:
                        fcntl.flock(stream.fileno(), fcntl.LOCK_UN)
            except OSError as exc:
                raise WorkError(500, "Cannot lock the work store") from exc

    def _read_json(self, path):
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            with os.fdopen(fd, "rb") as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                    raise WorkError(500, "Work store/catalog must be a regular file")
                raw = stream.read(MAX_STORE_BYTES + 1)
            if len(raw) > MAX_STORE_BYTES:
                raise WorkError(500, "Work store/catalog exceeds its size limit")
            def unique_pairs(pairs):
                result = {}
                for key, value in pairs:
                    if key in result:
                        raise ValueError("Duplicate JSON key")
                    result[key] = value
                return result
            return json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs)
        except FileNotFoundError:
            raise
        except WorkError:
            raise
        except (OSError, ValueError, UnicodeError, RecursionError) as exc:
            raise WorkError(500, "Work store/catalog is unreadable or corrupt; it was not replaced") from exc

    def _load(self):
        try:
            board = self._read_json(self.data_file)
        except FileNotFoundError:
            try:
                seed = self._read_json(self.catalog_file)
            except FileNotFoundError as exc:
                raise WorkError(500, "Work catalog is unavailable; no board was fabricated") from exc
            try:
                _object(seed, "catalog", ("features", "cards"))
                board = {"schema_version": 1, "revision": 0,
                         "features": [self._feature(f) for f in seed["features"]],
                         "cards": [self._card(c) for c in seed["cards"]], "sessions": []}
                self._validate(board)
            except (KeyError, TypeError, WorkError, RecursionError) as exc:
                raise WorkError(500, "Work catalog is invalid; no board was fabricated") from exc
            return board
        try:
            self._validate(board, stored=True)
        except (KeyError, TypeError, WorkError, RecursionError) as exc:
            raise WorkError(500, "Work store is corrupt or inconsistent; it was not replaced") from exc
        return board

    def _persist(self, board):
        raw = (json.dumps(board, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")
        if len(raw) > MAX_STORE_BYTES:
            raise WorkError(409, "Work store is full; preserve/archive it before starting another board")
        temporary, directory_fd, committed, warning = None, None, False, ""
        try:
            if self.data_file.is_symlink():
                raise WorkError(500, "Refusing a symlink work store")
            directory_fd = os.open(self.data_file.parent, os.O_RDONLY | os.O_DIRECTORY)
            fd, temporary = tempfile.mkstemp(prefix=self.data_file.name + ".", suffix=".tmp", dir=self.data_file.parent)
            with os.fdopen(fd, "wb") as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.data_file)
            temporary, committed = None, True
            os.fsync(directory_fd)
        except OSError as exc:
            if not committed:
                raise WorkError(500, "Cannot atomically persist the work store") from exc
            warning = f"Revision {board['revision']} is committed, but directory durability is UNKNOWN after a storage error. Review the saved state; do not retry as an unchanged write."
        finally:
            if temporary is not None:
                os.unlink(temporary)
            if directory_fd is not None:
                try:
                    os.close(directory_fd)
                except OSError as exc:
                    if not committed:
                        raise WorkError(500, "Cannot close the pre-commit storage handle") from exc
                    warning = f"Revision {board['revision']} is committed, but storage-handle cleanup failed. Review the saved state; do not retry as an unchanged write."
        return warning

    def _reference_schema(self, value):
        fields = ("id", "label", "kind", "source", "path", "url", "revision", "layer", "confidence", "limits", "anchor", "line_start", "line_end")
        _object(value, "reference", fields)
        result = {"id": _slug(value.get("id"), "reference id")}
        for field in ("label", "revision", "layer", "confidence", "limits"):
            result[field] = _text(value.get(field, "UNKNOWN"), field, required=True)
        for field, choices in (("kind", ("implementation", "evidence", "research", "corpus", "policy")),
                               ("source", ("repo", "research", "external"))):
            if value.get(field) not in choices:
                raise WorkError(400, f"Invalid reference {field}")
            result[field] = value[field]
        result["path"] = _text(value.get("path", ""), "reference path", 1000)
        result["url"] = _text(value.get("url", ""), "reference URL", 2000)
        if result["source"] == "external":
            try:
                url = urlsplit(result["url"])
                if url.scheme != "https" or not url.hostname or url.username or url.password or any(ord(c) < 33 for c in result["url"]):
                    raise ValueError()
                url.port
            except ValueError as exc:
                raise WorkError(400, "External references must be HTTPS links without credentials") from exc
            if result["path"]:
                raise WorkError(400, "External references cannot have a local path")
        else:
            guard = _repo_source_path if result["source"] == "repo" else _source_path
            result["path"] = guard(result["path"])
            if result["url"]:
                raise WorkError(400, "Local references cannot have an external URL")
        if "anchor" in value:
            result["anchor"] = _text(value["anchor"], "reference anchor", 200, required=True).lstrip("#")
            if not result["anchor"] or any(c.isspace() for c in result["anchor"]):
                raise WorkError(400, "Reference anchor must be a Markdown heading slug")
            if "line_start" in value or "line_end" in value:
                raise WorkError(400, "Use an anchor or line bounds, not both")
        if "line_start" in value or "line_end" in value:
            start, end = value.get("line_start"), value.get("line_end", value.get("line_start"))
            if type(start) is not int or type(end) is not int or not 1 <= start <= end <= 200000 or end - start >= 2000:
                raise WorkError(400, "Reference line bounds must select at most 2000 positive lines")
            result.update(line_start=start, line_end=end)
        if result["source"] == "external" and any(k in result for k in ("anchor", "line_start")):
            raise WorkError(400, "External links do not support local preview selectors")
        return result

    def _feature(self, value):
        _object(value, "feature", ("id", "title", "track", "summary", "gaps", "references"))
        result = {"id": _slug(value.get("id"), "feature id")}
        for field in ("title", "track", "summary"):
            result[field] = _text(value.get(field, ""), field, required=True)
        result["gaps"] = _strings(value.get("gaps", []), "feature gaps")
        references = value.get("references", [])
        if not isinstance(references, list) or len(references) > 100:
            raise WorkError(400, "Feature references must be a list of at most 100 entries")
        result["references"] = [self._reference_schema(r) for r in references]
        self._unique(result["references"], "reference")
        return result

    def _card(self, value):
        _object(value, "card", ("id", "feature_id") + CARD_TEXT + CARD_LISTS + tuple(CARD_ENUMS) + ("readiness",))
        result = {"id": _slug(value.get("id"), "card id"), "feature_id": _slug(value.get("feature_id"), "feature id")}
        for field in CARD_TEXT:
            result[field] = _text(value.get(field, ""), field)
        if not result["title"].strip():
            raise WorkError(400, "Card title is required")
        for field in CARD_LISTS:
            result[field] = _strings(value.get(field, []), field, item_max=1000 if field in {"scope", "resources"} else 4000)
        result["scope"] = [self._scope_path(path) for path in result["scope"]]
        if len(set(result["scope"])) != len(result["scope"]):
            raise WorkError(400, "Owned scope has duplicate normalized paths")
        for field in ("reference_ids", "dependencies"):
            for item in result[field]:
                _slug(item, field)
        for field, choices in CARD_ENUMS.items():
            result[field] = value.get(field, choices[0])
            if result[field] not in choices:
                raise WorkError(400, f"Invalid card {field}")
        if result["baseline_sha"] and not SHA.fullmatch(result["baseline_sha"]):
            raise WorkError(400, "baseline_sha must be a complete 40-hex checkout SHA")
        result["baseline_sha"] = result["baseline_sha"].lower()
        if result["status"] == "Blocked" and not result["blocker"].strip():
            raise WorkError(400, "Blocked cards need a named blocker")
        return result

    def _scope_path(self, value):
        path = _relative(value.rstrip("/"), "owned scope")
        current = self.repo_root
        for part in path.split("/"):
            current = current / part
            if current.is_symlink():
                raise WorkError(400, "Owned scope cannot contain symlinks or escape the repository")
        return path

    @staticmethod
    def _unique(items, label):
        ids = [item["id"] for item in items]
        if len(ids) != len(set(ids)):
            raise WorkError(400, f"Duplicate {label} ids")

    @staticmethod
    def _get(items, item_id, label):
        for item in items:
            if item["id"] == item_id:
                return item
        raise WorkError(404, f"Unknown {label}: {item_id}")

    def _readiness(self, card, cards):
        reasons = []
        for field in ("owner", "verifier", "scope", "outcome", "parent", "non_goals", "acceptance", "baseline_sha", "next_action", "reference_ids"):
            if not card[field] or isinstance(card[field], str) and not card[field].strip():
                reasons.append(f"Missing {field}")
        by_id = {c["id"]: c for c in cards}
        for dep in card["dependencies"]:
            if dep not in by_id or by_id[dep]["status"] != "Accepted":
                reasons.append(f"Dependency {dep} is not Accepted")
        return {"ready": not reasons, "reasons": reasons}

    def _contract(self, card, features):
        feature = self._get(features, card["feature_id"], "feature")
        result = {key: deepcopy(card[key]) for key in CONTRACT_FIELDS}
        result["feature"] = {key: deepcopy(feature[key]) for key in ("id", "title", "track", "summary", "gaps")}
        result["references"] = [deepcopy(self._get(feature["references"], rid, "reference")) for rid in card["reference_ids"]]
        return result

    def _checkpoint(self, value, status):
        _object(value, "checkpoint", ("confirmed", "unknowns", "next_action", "evidence_refs", "blocker"))
        result = {field: _strings(value.get(field, []), field) for field in ("confirmed", "unknowns", "evidence_refs")}
        result["next_action"] = _text(value.get("next_action", ""), "checkpoint next_action", required=status != "closed")
        result["blocker"] = _text(value.get("blocker", ""), "checkpoint blocker")
        return result

    def _validate_context(self, context):
        _object(context, "context", ("source_sha", "branch", "dirty_state", "current_excerpt", "current_sha256", "canonical_links"))
        if set(context) != {"source_sha", "branch", "dirty_state", "current_excerpt", "current_sha256", "canonical_links"}:
            raise WorkError(400, "Reviewed context must be the complete displayed context")
        for key in ("source_sha", "branch", "dirty_state", "current_excerpt", "current_sha256"):
            _text(context[key], key, maximum=MAX_PREVIEW_BYTES)
        if context["source_sha"] != "UNKNOWN" and not SHA.fullmatch(context["source_sha"]):
            raise WorkError(400, "Invalid context source SHA")
        if context["current_sha256"] != "UNKNOWN" and not re.fullmatch(r"[0-9a-f]{64}", context["current_sha256"]):
            raise WorkError(400, "Invalid CURRENT context digest")
        links = context["canonical_links"]
        if not isinstance(links, list) or len(links) != len(CANONICAL_PATHS):
            raise WorkError(400, "Invalid canonical context links")
        for link, path in zip(links, CANONICAL_PATHS):
            _object(link, "canonical link", ("label", "path", "sha256", "unavailable"))
            if set(link) != {"label", "path", "sha256", "unavailable"}:
                raise WorkError(400, "Reviewed context has an incomplete canonical link")
            if link["path"] != path or type(link["unavailable"]) is not bool:
                raise WorkError(400, "Invalid canonical context pointer")
            _text(link["label"], "canonical label", required=True)
            digest = link["sha256"]
            if digest != "UNKNOWN" and (not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest)):
                raise WorkError(400, "Invalid canonical context digest")

    def _validate(self, board, stored=False):
        _object(board, "board", ("schema_version", "revision", "features", "cards", "sessions"))
        if type(board.get("schema_version")) is not int or board["schema_version"] != 1 or type(board.get("revision")) is not int or board["revision"] < 0:
            raise WorkError(400, "Invalid board schema or revision")
        for name, maximum in (("features", 200), ("cards", 500), ("sessions", 500)):
            if not isinstance(board.get(name), list) or len(board[name]) > maximum:
                raise WorkError(400, f"Too many or invalid {name}")
            self._unique(board[name], name)
        features = {f["id"]: f for f in board["features"]}
        cards = {c["id"]: c for c in board["cards"]}
        for feature in board["features"]:
            if self._feature(feature) != feature:
                raise WorkError(400, "Feature store is not normalized")
        for card in board["cards"]:
            if self._card(card) != card:
                raise WorkError(400, "Card store is not normalized")
            if card["feature_id"] not in features:
                raise WorkError(400, "Card refers to an unknown feature")
            refs = {r["id"] for r in features[card["feature_id"]]["references"]}
            if not set(card["reference_ids"]).issubset(refs):
                raise WorkError(400, "Card selects an unregistered feature reference")
            for dep in card["dependencies"]:
                if dep == card["id"] or dep not in cards:
                    raise WorkError(400, "Dependencies must be existing, different cards")
            if card["status"] in {"Ready", "Implementing", "Verifying", "Accepted"}:
                ready = self._readiness(card, board["cards"])
                if not ready["ready"]:
                    raise WorkError(400, "; ".join(ready["reasons"]))
            if card["status"] == "Accepted" and (not card["acceptance_note"].strip() or not card["evidence_refs"]):
                raise WorkError(400, "Accepted requires a scenario/limitations note and recorded evidence references; this is not evidence verification")
        visiting, visited = set(), set()
        def visit(cid):
            if cid in visiting:
                raise WorkError(400, "Card dependency cycle")
            if cid in visited:
                return
            visiting.add(cid)
            for dep in cards[cid]["dependencies"]:
                visit(dep)
            visiting.remove(cid)
            visited.add(cid)
        for cid in cards:
            visit(cid)
        records = {s["id"]: s for s in board["sessions"]}
        record_order = {s["id"]: index for index, s in enumerate(board["sessions"])}
        for session in board["sessions"]:
            self._validate_session(session, cards)
            if "predecessor_id" in session:
                predecessor = records.get(session["predecessor_id"])
                if (session["strategy"] != "fresh" or predecessor is None
                        or predecessor["card_id"] != session["card_id"]
                        or predecessor["status"] != "closed"
                        or record_order[predecessor["id"]] >= record_order[session["id"]]):
                    raise WorkError(400, "Invalid fresh-record predecessor boundary")
            for event in session["history"]:
                if "successor_id" in event:
                    successor = records.get(event["successor_id"])
                    if successor is None or successor.get("predecessor_id") != session["id"]:
                        raise WorkError(400, "Invalid archived-record successor boundary")
            if session["status"] == "active":
                card = cards[session["card_id"]]
                if card["status"] not in {"Implementing", "Verifying"}:
                    raise WorkError(409, "An active work record requires an Implementing or Verifying card")
                if self._readiness(card, board["cards"])["reasons"]:
                    raise WorkError(409, "Active work dependencies/readiness cannot be removed")
                # Feature/reference edits deliberately remain visible as drift.
                if any(session["contract"][key] != card[key] for key in CONTRACT_FIELDS):
                    raise WorkError(409, "Session-owned card contract cannot change while active")
                self._ownership(board, card, exclude=session["id"])
        if stored and board["revision"] == 0 and board["sessions"]:
            raise WorkError(400, "Unpersisted revision has work records")

    def _validate_session(self, session, cards):
        fields = ("id", "card_id", "name", "strategy", "reason", "status", "contract", "context", "checkpoint", "history", "predecessor_id")
        _object(session, "work record", fields)
        _slug(session["id"], "work record id")
        if session["card_id"] not in cards:
            raise WorkError(400, "Work record refers to an unknown card")
        _text(session["name"], "work record name", 200, required=True)
        _text(session["reason"], "fresh reason", required=session["strategy"] == "fresh")
        if session["strategy"] not in {"continue", "fresh"} or session["status"] not in {"active", "paused", "closed"}:
            raise WorkError(400, "Invalid work record strategy/status")
        if "predecessor_id" in session:
            _slug(session["predecessor_id"], "predecessor id")
        self._validate_context(session["context"])
        if self._checkpoint(session["checkpoint"], session["status"]) != session["checkpoint"]:
            raise WorkError(400, "Invalid saved checkpoint")
        contract = session["contract"]
        _object(contract, "saved contract", CONTRACT_FIELDS + ("feature", "references"))
        if set(contract) != set(CONTRACT_FIELDS + ("feature", "references")) or contract["id"] != session["card_id"]:
            raise WorkError(400, "Invalid saved contract identity/fields")
        historical_card = {key: contract[key] for key in CONTRACT_FIELDS}
        historical_card["status"] = "Backlog"
        normalized_card = self._card(historical_card)
        if any(normalized_card[key] != contract[key] for key in CONTRACT_FIELDS):
            raise WorkError(400, "Saved card contract is not normalized")
        for key in ("owner", "verifier", "scope", "outcome", "parent", "non_goals", "acceptance", "baseline_sha", "reference_ids"):
            if not contract[key]:
                raise WorkError(400, "Saved contract is not a runnable bounded contract")
        feature = dict(contract["feature"], references=contract["references"])
        if self._feature(feature) != feature:
            raise WorkError(400, "Saved feature contract is not normalized")
        if feature["id"] != contract["feature_id"] or [r["id"] for r in contract["references"]] != contract["reference_ids"]:
            raise WorkError(400, "Invalid saved feature/reference contract")
        history = session["history"]
        if not isinstance(history, list) or not 1 <= len(history) <= MAX_HISTORY:
            raise WorkError(400, "Work record history is missing or exceeds its bound")
        previous = None
        for event in history:
            _object(event, "history event", ("action", "status", "checkpoint", "context", "reviewed_context", "at", "successor_id"))
            if event["action"] not in {"start", "checkpoint", "resume", "fresh_restart"} or event["status"] not in {"active", "paused", "closed"}:
                raise WorkError(400, "Invalid history event")
            _text(event["at"], "checkpoint time", 100, required=True)
            if type(event["reviewed_context"]) is not bool:
                raise WorkError(400, "Invalid reviewed context history")
            self._validate_context(event["context"])
            if self._checkpoint(event["checkpoint"], event["status"]) != event["checkpoint"]:
                raise WorkError(400, "Invalid history checkpoint")
            if "successor_id" in event:
                _slug(event["successor_id"], "successor id")
            if previous is None:
                if event["action"] != "start" or event["status"] != "active" or event["reviewed_context"]:
                    raise WorkError(400, "History must start with the original active context")
            else:
                if previous["status"] == "closed" or event["action"] == "start":
                    raise WorkError(400, "Closed history cannot be rewritten or restarted")
                if event["action"] == "resume" and (previous["status"] != "paused" or event["status"] != "active"):
                    raise WorkError(400, "Invalid resume history boundary")
                if event["action"] == "fresh_restart" and (previous["status"] != "paused" or event["status"] != "closed" or "successor_id" not in event):
                    raise WorkError(400, "Invalid fresh history boundary")
                if event["action"] == "checkpoint" and previous["status"] == "paused" and event["status"] == "active":
                    raise WorkError(400, "Checkpoint history bypasses resume ownership")
                if event["context"] != previous["context"] and not event["reviewed_context"]:
                    raise WorkError(400, "Changed saved context requires a recorded explicit review")
            if "successor_id" in event and event["action"] != "fresh_restart":
                raise WorkError(400, "Successor links require an explicit fresh restart event")
            previous = event
        last = history[-1]
        if any(last[key] != session[key] for key in ("status", "checkpoint", "context")):
            raise WorkError(400, "Work record and last history event disagree")

    def _git(self, *arguments):
        try:
            result = subprocess.run(["git", "-C", str(self.repo_root), *arguments],
                                    capture_output=True, timeout=5, check=False)
            if result.returncode == 0:
                return result.stdout.decode("utf-8", "replace").strip()
        except (OSError, subprocess.TimeoutExpired):
            pass
        return None

    def _open_source(self, root, path):
        # Walk open directory descriptors: neither a final nor an intermediate
        # symlink can redirect a registered source, even during a rename race.
        fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            parts = path.split("/")
            for part in parts[:-1]:
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = child
            result = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
            info = os.fstat(result)
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                os.close(result)
                raise WorkError(400, "Only single-link regular source files may be previewed")
            return os.fdopen(result, "rb")
        finally:
            os.close(fd)

    def _source_bytes(self, root, path):
        try:
            with self._open_source(root, path) as stream:
                raw = stream.read(MAX_SOURCE_BYTES + 1)
            if len(raw) > MAX_SOURCE_BYTES:
                return None, "Source exceeds the bounded reader limit"
            if b"\x00" in raw:
                return None, "Binary source is not previewable"
            raw.decode("utf-8")
            return raw, ""
        except FileNotFoundError:
            return None, "Registered source is unavailable"
        except UnicodeError:
            return None, "Source is not UTF-8 text"
        except OSError as exc:
            if exc.errno in (40, 20):
                raise WorkError(400, "Registered source contains a forbidden symlink or non-directory") from exc
            return None, "Registered source is unreadable"

    def _context(self):
        source = self._git("rev-parse", "HEAD")
        source = source.lower() if source and SHA.fullmatch(source) else "UNKNOWN"
        branch = self._git("symbolic-ref", "--short", "HEAD") or ("DETACHED" if source != "UNKNOWN" else "UNKNOWN")
        dirty = self._git("status", "--porcelain=v1", "--untracked-files=no")
        dirty_state = "UNKNOWN" if dirty is None else "clean (tracked files)" if not dirty else (
            f"{len(dirty.splitlines())} tracked dirty paths; status SHA256 {hashlib.sha256(dirty.encode()).hexdigest()}"
        )
        links, current, current_digest = [], "UNKNOWN: CURRENT is unavailable", "UNKNOWN"
        for path in CANONICAL_PATHS:
            try:
                raw, _ = self._source_bytes(self.repo_root, path)
            except WorkError:
                raw = None
            digest = hashlib.sha256(raw).hexdigest() if raw is not None else "UNKNOWN"
            links.append({"label": Path(path).name, "path": path, "sha256": digest, "unavailable": raw is None})
            if path.endswith("CURRENT.md") and raw is not None:
                current_digest = digest
                # The leading active objective is authoritative; old appended
                # allocations are not substituted for it.
                active = re.split(r"(?m)^## (?:Preserved|Historical|Previous)\b", raw.decode("utf-8"), maxsplit=1)[0]
                current = active.encode("utf-8")[:16000].decode("utf-8", "ignore")
        return {"source_sha": source, "branch": branch, "dirty_state": dirty_state,
                "current_excerpt": current, "current_sha256": current_digest, "canonical_links": links}

    @staticmethod
    def _context_drift(saved, current):
        reasons = []
        for field, label in (("source_sha", "Source HEAD"), ("branch", "Source branch"),
                             ("dirty_state", "Tracked dirty baseline"), ("current_sha256", "Canonical CURRENT")):
            if saved[field] != current[field]:
                reasons.append(f"{label} changed since the saved context")
        previous = {link["path"]: link["sha256"] for link in saved["canonical_links"]}
        for link in current["canonical_links"]:
            if not link["path"].endswith("CURRENT.md") and previous.get(link["path"]) != link["sha256"]:
                reasons.append(f"Canonical {link['label']} changed since the saved context")
        return reasons

    def _drift(self, session, board, context):
        reasons = self._context_drift(session["context"], context)
        card = self._get(board["cards"], session["card_id"], "card")
        if session["contract"] != self._contract(card, board["features"]):
            reasons.append("Card or selected feature/reference contract changed; a fresh record is required")
        return reasons

    def _snapshot(self, board, context=None):
        result = deepcopy(board)
        result.pop("schema_version")
        result["context"] = context if context is not None else self._context()
        result["limits"] = dict(LIMITS)
        result["authority"] = AUTHORITY
        for card in result["cards"]:
            card["readiness"] = self._readiness(card, result["cards"])
        for session in result["sessions"]:
            session["drift"] = self._drift(session, board, result["context"])
        return result

    def snapshot(self):
        self._require_platform()
        with self._lock:
            return self._snapshot(self._load())

    def _ownership(self, board, card, exclude=None):
        active = [s for s in board["sessions"] if s["status"] == "active" and s["id"] != exclude]
        for other in active:
            contract = other["contract"]
            if other["card_id"] == card["id"]:
                raise WorkError(409, "This card already has an active work record")
            if contract["owner"].strip().casefold() == card["owner"].strip().casefold():
                raise WorkError(409, f"Owner {card['owner']} already has active work")
            if contract["slot"] == card["slot"]:
                raise WorkError(409, f"{card['slot']} WIP limit is {LIMITS[card['slot']]}")
            if {r.strip().casefold() for r in contract["resources"]} & {r.strip().casefold() for r in card["resources"]}:
                raise WorkError(409, "Exclusive resource is owned by another active work record")
            for left in contract["scope"]:
                for right in card["scope"]:
                    if left == right or left.startswith(right + "/") or right.startswith(left + "/"):
                        raise WorkError(409, f"Owned scope overlaps: {left} / {right}")

    def _acknowledge(self, payload, session, context, required=False):
        drift = self._context_drift(session["context"], context)
        acknowledgement = payload.get("reviewed_context")
        if acknowledgement is None:
            if drift and required:
                raise WorkError(409, "Review current drift and explicitly acknowledge the current context before continuing")
            return False
        if not isinstance(acknowledgement, dict):
            raise WorkError(400, "reviewed_context must be the explicitly reviewed context object")
        self._validate_context(acknowledgement)
        if acknowledgement != context:
            raise WorkError(409, "Reviewed context is stale; refresh and review it again")
        session["context"] = deepcopy(context)
        return True

    def _event(self, session, action, reviewed=False, successor_id=None):
        if len(session["history"]) >= MAX_HISTORY or session["status"] != "closed" and len(session["history"]) >= MAX_HISTORY - 1:
            raise WorkError(409, "Checkpoint history is full; close this record and start a fresh record without discarding history")
        event = {"action": action, "status": session["status"], "checkpoint": deepcopy(session["checkpoint"]),
                 "context": deepcopy(session["context"]), "reviewed_context": reviewed, "at": _now()}
        if successor_id is not None:
            event["successor_id"] = successor_id
        session["history"].append(event)

    def mutate(self, payload):
        self._require_platform()
        _object(payload, "mutation", ("revision", "action", "card", "feature", "card_id", "name", "strategy", "reason", "session_id", "checkpoint", "status", "reviewed_context"))
        if type(payload.get("revision")) is not int or payload["revision"] < 0:
            raise WorkError(400, "A nonnegative integer revision is required")
        action = _text(payload.get("action"), "work action", 40, required=True)
        with self._mutation_lock():
            board = self._load()
            if payload["revision"] != board["revision"]:
                raise WorkError(409, "Stale board revision; refresh before saving")
            context = self._context()
            if action == "feature_save":
                feature = self._feature(payload.get("feature"))
                self._upsert(board["features"], feature)
            elif action == "card_save":
                card = self._card(payload.get("card"))
                for session in board["sessions"]:
                    if session["card_id"] == card["id"] and session["status"] == "active":
                        if any(session["contract"][key] != card[key] for key in CONTRACT_FIELDS):
                            raise WorkError(409, "Pause/close before changing a session-owned card contract")
                        if card["status"] not in {"Implementing", "Verifying"}:
                            raise WorkError(409, "Pause/close before accepting or changing the active card status")
                self._upsert(board["cards"], card)
            elif action == "session_start":
                self._start(board, payload, context)
            elif action in {"session_checkpoint", "session_resume"}:
                session = self._get(board["sessions"], payload.get("session_id"), "work record")
                card = self._get(board["cards"], session["card_id"], "card")
                if action == "session_resume":
                    if session["status"] != "paused":
                        raise WorkError(409, "Only a paused work record can be resumed")
                    if card["status"] not in {"Implementing", "Verifying"}:
                        raise WorkError(409, "The card is not resumable")
                    if session["contract"] != self._contract(card, board["features"]):
                        raise WorkError(409, "Contract changed; preserve this record and start fresh with a reason")
                    self._require_ready(card, board)
                    self._ownership(board, card, exclude=session["id"])
                    reviewed = self._acknowledge(payload, session, context, required=True)
                    session["status"] = "active"
                    self._event(session, "resume", reviewed)
                else:
                    status = _text(payload.get("status"), "checkpoint status", 20, required=True)
                    if status not in {"active", "paused", "closed"}:
                        raise WorkError(400, "Checkpoint status must be active, paused or closed")
                    if session["status"] == "closed":
                        raise WorkError(409, "Closed work records preserve immutable history")
                    if session["status"] == "paused" and status == "active":
                        raise WorkError(409, "Use session_resume to reacquire paused ownership")
                    if status == "active" and session["contract"] != self._contract(card, board["features"]):
                        raise WorkError(409, "Contract changed; pause/close and start fresh")
                    reviewed = self._acknowledge(payload, session, context, required=status == "active")
                    session["checkpoint"] = self._checkpoint(payload.get("checkpoint"), status)
                    session["status"] = status
                    self._event(session, "checkpoint", reviewed)
            else:
                raise WorkError(400, "Unknown work action")
            self._validate(board)
            board["revision"] += 1
            warning = self._persist(board)
            result = self._snapshot(board, context)
            result["storage_warning"] = warning
            return result

    @staticmethod
    def _upsert(items, value):
        for index, item in enumerate(items):
            if item["id"] == value["id"]:
                items[index] = value
                return
        items.append(value)

    def _require_ready(self, card, board):
        readiness = self._readiness(card, board["cards"])
        if not readiness["ready"]:
            raise WorkError(409, "; ".join(readiness["reasons"]))

    def _start(self, board, payload, context):
        card = self._get(board["cards"], payload.get("card_id"), "card")
        if card["status"] not in {"Ready", "Implementing", "Verifying"}:
            raise WorkError(409, "Start requires a Ready or resumable Implementing/Verifying card")
        self._require_ready(card, board)
        if context["source_sha"] == "UNKNOWN" or card["baseline_sha"] != context["source_sha"]:
            raise WorkError(409, "Card baseline must exactly match the current known source HEAD before a new record starts")
        strategy = _text(payload.get("strategy"), "work strategy", 20, required=True)
        if strategy not in {"continue", "fresh"}:
            raise WorkError(400, "Choose continue or fresh explicitly")
        reason = _text(payload.get("reason", ""), "fresh restart reason", required=strategy == "fresh")
        name = _text(payload.get("name", ""), "work record name", 200, required=True)
        previous = [s for s in board["sessions"] if s["card_id"] == card["id"]]
        if any(s["status"] == "active" for s in previous):
            raise WorkError(409, "Pause/close the predecessor before creating a fresh work record")
        if previous and strategy == "continue":
            raise WorkError(409, "Resume the paused record, or choose fresh with an explicit restart reason")
        self._ownership(board, card)
        session = {"id": "work-" + uuid.uuid4().hex, "card_id": card["id"], "name": name,
                   "strategy": strategy, "reason": reason, "status": "active",
                   "contract": self._contract(card, board["features"]), "context": deepcopy(context),
                   "checkpoint": self._checkpoint({"confirmed": [], "unknowns": card["unknowns"],
                       "next_action": card["next_action"], "evidence_refs": card["evidence_refs"],
                       "blocker": card["blocker"]}, "active"), "history": []}
        if previous:
            predecessor = previous[-1]
            session["predecessor_id"] = predecessor["id"]
            if predecessor["contract"] == session["contract"] and predecessor["checkpoint"]["next_action"]:
                session["checkpoint"]["next_action"] = predecessor["checkpoint"]["next_action"]
            if predecessor["status"] == "paused":
                predecessor["status"] = "closed"
                self._event(predecessor, "fresh_restart", successor_id=session["id"])
        self._event(session, "start")
        board["sessions"].append(session)
        if card["status"] == "Ready":
            card["status"] = "Implementing"

    def reference(self, feature_id, reference_id):
        self._require_platform()
        board = self._load()
        feature = self._get(board["features"], feature_id, "feature")
        metadata = deepcopy(self._get(feature["references"], reference_id, "reference"))
        result = dict(metadata, text="", sha256=None, truncated=False, unavailable=False,
                      line_start=None, line_end=None, selector_hint="Historical locator hint; preview is current source text")
        result["registered_selector"] = {key: metadata[key] for key in ("anchor", "line_start", "line_end") if key in metadata}
        if metadata["source"] == "external":
            result["note"] = "External HTTPS bibliography link only; never fetched"
            return result
        root = self.repo_root if metadata["source"] == "repo" else self.research_root
        if root is None:
            result.update(unavailable=True, note="Research source root is not configured")
            return result
        guard = _repo_source_path if metadata["source"] == "repo" else _source_path
        raw, note = self._source_bytes(root, guard(metadata["path"]))
        if raw is None:
            result.update(unavailable=True, note=note)
            return result
        result["sha256"] = hashlib.sha256(raw).hexdigest()
        lines = raw.decode("utf-8").splitlines(keepends=True)
        start, end = 1, len(lines)
        if "anchor" in metadata:
            used = {}
            found = False
            level = None
            in_fence = False
            for index, line in enumerate(lines):
                if line.lstrip().startswith(("```", "~~~")):
                    in_fence = not in_fence
                heading = None if in_fence else re.match(r"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$", line)
                if heading is None:
                    continue
                slug = re.sub(r"[^\w\- ]", "", heading[2].strip().lower()).replace(" ", "-")
                count = used.get(slug, 0)
                used[slug] = count + 1
                slug = slug + (f"-{count}" if count else "")
                if found and len(heading[1]) <= level:
                    end = index
                    break
                if slug == metadata["anchor"]:
                    start, level, found = index + 1, len(heading[1]), True
            if not found:
                result.update(unavailable=True, note="Registered historical heading anchor is absent in the current source")
                return result
        elif "line_start" in metadata:
            start, end = metadata["line_start"], min(metadata["line_end"], len(lines))
            if start > len(lines):
                result.update(unavailable=True, note="Registered historical line bounds are absent in the current source")
                return result
        selected = "".join(lines[start - 1:end]).encode("utf-8")
        preview = selected[:MAX_PREVIEW_BYTES].decode("utf-8", "ignore")
        result.update(text=preview, truncated=len(selected) > MAX_PREVIEW_BYTES,
                      line_start=start if lines else None,
                      line_end=start + len(preview.splitlines()) - 1 if preview else None,
                      note="SHA256 identifies the full current source, not an acceptance verdict")
        return result

    def brief(self, session_id):
        self._require_platform()
        board = self._load()
        session = self._get(board["sessions"], session_id, "work record")
        context = self._context()
        drift = self._drift(session, board, context)
        contract, checkpoint = session["contract"], session["checkpoint"]
        lines = [f"Work record: {session['id']} ({session['name']})", "Not an automatic OMP session or execution authorization.",
                 f"Strategy: {session['strategy']}; status: {session['status']}", f"Restart reason: {session['reason'] or 'none'}",
                 f"Card: {contract['id']} — {contract['title']}", f"Feature: {contract['feature']['title']}",
                 f"Parent: {contract['parent']}", f"Outcome: {contract['outcome']}",
                 f"Mode: {contract['mode']}; slot: {contract['slot']}; evidence layer: {contract['evidence_layer']}",
                 f"Owner: {contract['owner']}; verifier: {contract['verifier']}",
                 f"Starting card baseline: {contract['baseline_sha']}",
                 f"Saved context HEAD: {session['context']['source_sha']}; current HEAD: {context['source_sha']}",
                 f"Current branch: {context['branch']}; dirty state: {context['dirty_state']}"]
        def section(title, values):
            lines.extend(["", title + ":"] + ["- " + value for value in values or ["None recorded"]])
        for field, title in (("scope", "Exclusive owned scope"), ("resources", "Exclusive resources"),
                             ("non_goals", "Non-goals"), ("acceptance", "Observable acceptance criteria"),
                             ("dependencies", "Dependencies")):
            section(title, contract[field])
        references = []
        for ref in contract["references"]:
            locator = ref["url"] if ref["source"] == "external" else f"{ref['source']}:{ref['path']}"
            if "anchor" in ref:
                locator += "#" + ref["anchor"]
            elif "line_start" in ref:
                locator += f":{ref['line_start']}-{ref['line_end']}"
            references.append(f"{ref['id']} — {ref['label']}; {locator}; revision {ref['revision']}; "
                              f"{ref['kind']}/{ref['layer']}/{ref['confidence']}; limits: {ref['limits']}")
        section("Saved relevant bibliography (historical pointers, not fresh proof)", references)
        section("Confirmed checkpoint (operator-reported)", checkpoint["confirmed"])
        section("Unknowns", checkpoint["unknowns"])
        section("Reported evidence citations (not machine-verified)", checkpoint["evidence_refs"])
        lines.extend(["", "Blocker: " + (checkpoint["blocker"] or "None recorded"), "Next action: " + checkpoint["next_action"]])
        predecessor = None
        if "predecessor_id" in session:
            previous = self._get(board["sessions"], session["predecessor_id"], "predecessor")
            predecessor = {
                "id": previous["id"],
                "contract_compatible": previous["contract"] == contract,
                "source_sha": previous["context"]["source_sha"],
                "checkpoint": deepcopy(previous["checkpoint"]),
            }
            lines.extend(["", "Prior checkpoint — historical operator report, not current verification:",
                          json.dumps(predecessor, indent=2),
                          "Review the prior contract/source before adopting any facts. Its evidence is not promoted."])
        section("Drift — review explicitly before continuing", drift or ["No detected context/contract mismatch"])
        section("Current canonical authority pointers", [f"{link['path']} — SHA256 {link['sha256']}" for link in context["canonical_links"]])
        lines.extend(["", "Current canonical CURRENT excerpt (not the saved card contract):", context["current_excerpt"], "", AUTHORITY])
        return {"session_id": session_id, "text": "\n".join(lines), "drift": drift, "context": context,
                "predecessor": predecessor}
