# COMMUNITY-GUIDELINES.md — AAEmu Upstream PR Compliance Layer

Compliance layer for every PR we open against upstream **AAEmu/AAEmu**.
Sits on top of `WORKFLOW.md` (Tai's fork playbook). Source of truth for
this doc: `CONTRIBUTING.md`, `AGENTS.md`, `.github/workflows/*` in the
upstream repo, plus live observations on PR #1494 and issue #1340
(verified 2026-08-03).

If a rule here conflicts with WORKFLOW.md, this doc wins for **upstream
PRs**; WORKFLOW.md wins for fork-internal work.

---

## 1. Hard requirements from CONTRIBUTING.md

| # | Requirement | Detail |
|---|-------------|--------|
| 1 | Fork + remotes | `origin` = joshhmann/AAEmu, `upstream` = AAEmu/AAEmu. Keep `upstream` added and fetch it regularly. |
| 2 | Sync before PR | Pull upstream changes into your branch before opening/updating a PR — stale forks get rejected on merge. |
| 3 | Branch from `develop` | Always. `develop` exists upstream, so never branch from `master`. |
| 4 | One branch per fix | `fix/<kebab-slug>` (de facto upstream convention — see §6). Never commit to `develop`. |
| 5 | Comment code | Match surrounding comment style; explain non-obvious logic. |
| 6 | Follow code style | Indentation + conventions of the repo (see §3). |
| 7 | Run tests | `dotnet test` before pushing. Write/adapt tests with every behavior change. |
| 8 | Docs as needed | Update `Docs/wiki/` only when user-facing setup/config/behavior changes. No unsolicited markdown elsewhere. |
| 9 | **Single squashed commit** | Interactive-rebase everything into ONE commit per PR. No merge commits, no fixup trails. |
| 10 | Present-tense message | Message describes what the commit does to the code, not what you did. "Fix X" / "Add Y", never "Fixed X" as a changelog of your actions. |
| 11 | PR targets `develop` | Open the PR against upstream `AAEmu/AAEmu:develop` — never `master`. |
| 12 | Clean up after merge | Pull upstream, delete the branch. |

## 2. AGENTS.md — the 7-step workflow (mandatory order)

1. **Scope** — subsystem (packet/manager/GameData/model/config); read 2–3
   representative files + the `I*` interface + existing tests.
2. **Implement** — smallest change that solves the task; match naming,
   placement, logging, error-handling of neighbors.
3. **Wire-up** — new manager/service: `Program.cs` DI registration (concrete
   + interface); new game packet: offsets + `RegisterPacket` in the correct
   `*Network`; new login handler: packet + handler + DI extension.
4. **SQL** — schema change ⇒ `SQL/updates/YYYY-MM-DD_aaemu_{login|game}_*.sql`
   **and** patch base `SQL/aaemu_*.sql`.
5. **Test** — add/extend `AAEmu.UnitTests`; naming
   `MethodName_Scenario_ExpectedResult`.
6. **Verify** — `dotnet build` AND `dotnet test` green before done.
7. **Document** — only user-facing changes touch `Docs/wiki/`.

### Avoid-list (absolute)

- Drive-by refactors / unrelated file reformatting / broad style cleanup
- New frameworks or mass `Singleton<T>` migration (not asked ⇒ not done)
- Renaming domain terms away from wiki vocabulary (Expedition ≠ Guild, etc.)
- Inventing opcodes — match client 1.2 tables in `*Offsets.cs`
- Committing client packs, launcher binaries, `compact.sqlite3`, secrets,
  `Config.Local.json`

## 3. Code style (from `.editorconfig` + AGENTS.md)

- 4-space indent, CRLF, UTF-8 BOM, file-scoped namespaces matching folder paths
- `#nullable enable` at file top where the area uses it
- `var` preferred; block bodies for methods; expression bodies for properties
- `_camelCase` instance fields, `s_camelCase` statics, `PascalCase` types
- No `this.` qualification; `System.*` usings first
- NLog: `LogManager.GetCurrentClassLogger()`
- DI: constructor injection for new deps; `Lazy<T>` to break manager cycles
  (the orchestrator ignores `Lazy<T>` edges); never hardcode `Singleton<T>.Instance` in new code
- Run `dotnet build` to surface analyzer violations before pushing

## 4. CI gates on upstream PRs (what must pass)

| Workflow | Runs on PRs? | What it does |
|----------|-------------|--------------|
| **Build & Unit Test** (`dotnetcore.yml`) | ✅ all branches | restore → `dotnet build -c Release` → **Scripts Compile** (`compiler-check`) → `AAEmu.UnitTests` w/ coverage → Coveralls → `AAEmu.Login.IntegrationTests` |
| **CodeQL** (`codeql-analysis.yml`) | ✅ all branches (+ push develop/master, weekly Tue) | C# security analysis (build + analyze) |
| **Greptile Review** (external app) | ✅ every PR | AI review + confidence score (see §5) |
| **Copilot** (dynamic reviewer) | ✅ active | Standard GitHub AI review |
| **Sonar** (`sonar.yml`) | ❌ push to develop/master only | SonarCloud static analysis — not a PR gate, but a red sonar on develop can stall merges |
| **Stale** (`stale.yml`) | ❌ scheduled | 90d stale → 60d close; **draft PRs exempt** — keep PRs moving or they get closed |
| **Publish Wiki** (`wiki-sync.yml`) | ❌ push to develop | Only when `Docs/wiki/**` changes |

### ⚠️ Observed pitfall (live, PR #1494)

On fork PRs, the .NET build and CodeQL runs can sit in
**`action_required`** (fork-PR approval policy) — on #1494 the only check
that actually completed was **Greptile Review (5/5)**; Build & Unit Test
and CodeQL never ran. `mergeable_state` = `blocked` until a maintainer
reviews/approves.

**Consequence:** upstream CI cannot be trusted to catch our breakage.
`dotnet build` + `dotnet test` locally (openclaw) is the real gate.
Never open a PR on an unverified build assuming CI will test it.

## 5. Greptile review — what it checks (from #1494)

- Summary accuracy: does the PR do what it claims, and only that?
- **Dependency consistency**: native lib ↔ runtime image (glibc vs musl),
  package manager consistency (apk vs apt), health-check tooling preserved
- Scope discipline: "important files changed" should be exactly the files
  the fix needs — #1494 was 1 file (Dockerfile)
- Confidence score (5/5 = safe to merge)

**How to pre-empt its feedback:**
- Structured PR body: Problem → Root cause (with evidence — `ldd` output,
  logs, backtrace) → Fix → Verification (before/after) → Notes
- One focused diff, minimal files, no noise
- Explain why the change is safe and what depends on it (health checks,
  build stages, other consumers)
- State explicitly when tests don't apply and why (e.g. Dockerfile-only)

## 6. Conventional Commits — adopt on our fork (recommended)

Issue **#1340** ("Adopt Conventional Commits") is still **OPEN** upstream
(2026-01-12, `enhancement` label, no commitlint/CI enforcement added).
BUT the de facto upstream style is already CC-shaped:

- 14 of 15 most recent merged PRs use `fix(scope):` lowercase titles
  (`fix(sql):`, `fix(skills):`, `fix(world):`, `fix(spawn):`, `fix(housing):`…)
  and `fix/<slug>` branches.
- CC titles satisfy CONTRIBUTING.md's present-tense rule ("fix: use glibc
  runtime image" is present tense).

**Verdict: adopt CC on our fork now.**
- Format: `type(scope): imperative lowercase summary` — types
  `feat` `fix` `refactor` `chore` `docs` `style` `perf` `test` `build`
  `ci` `revert`; scope = subsystem (`sql`, `skills`, `world`, `spawn`,
  `housing`, `docker`, `packets`, …)
- Breaking changes: `!` marker + footer, only when genuinely breaking
- Body/footer for longer explanation, issue references, co-authors
- **Grandfather** existing fork history — no rewrites
- Do NOT add commitlint CI to the fork (extra gate, upstream hasn't asked);
  discipline is manual — it's in our checklist, not in their repo
- PR title = commit title (upstream merges squash-style; the title is what
  lands on `develop`)

## 7. PR-READY CHECKLIST (every upstream PR)

**Before pushing:**
- [ ] Branch is `fix/<slug>` cut from a fresh `develop` (synced with upstream)
- [ ] Diff touches ONLY the files the fix needs (no reformatting, no
      drive-by changes, no fork-local files: SCORECARD.md, graphify-out/,
      WORKFLOW.md, docker-compose.yaml unless the PR is about them)
- [ ] `dotnet build` green (no new analyzer warnings)
- [ ] `dotnet test` green — AAEmu.UnitTests primary; behavior change ⇒ new
      test `MethodName_Scenario_ExpectedResult`
- [ ] `compiler-check` scripts compile step passes
- [ ] Schema change ⇒ `SQL/updates/` file AND base SQL file updated
- [ ] Commits squashed to exactly one; message
      `type(scope): imperative present-tense summary`
- [ ] No secrets, client packs, `compact.sqlite3`, `Config.Local.json` in diff

**In the PR:**
- [ ] Base = `AAEmu/AAEmu:develop` (never master)
- [ ] Body: Problem → Root cause (evidence) → Fix → Verification → Notes
- [ ] No tests apply ⇒ say so and why
- [ ] Title matches the commit title

**After opening:**
- [ ] Greptile review arrives automatically — read it; if it flags
      something real, fix and push (squashed amendment, still one commit)
- [ ] Expect Build/CodeQL in `action_required` — do NOT rely on them;
      your local build/test is the gate
- [ ] Reply to maintainer comments promptly — stale.yml closes idle PRs
      after ~90d stale + 60d grace (drafts exempt, but don't draft-hide)
- [ ] After merge: fetch upstream, rebase/merge into fork develop, delete
      branch, update WORKFLOW.md/graph (`graphify update .`) if code moved

## 8. Fork-internal vs upstream differences

| Concern | Fork-internal (joshhmann/AAEmu) | Upstream PR |
|---------|-------------------------------|-------------|
| Branch discipline | WORKFLOW.md playbook, `fix/<slug>` from `develop` | Same, plus sync with upstream before push |
| Deploy | push fork develop → docker compose on CT 133 | Never — infra is ours, not theirs |
| Local dev | graphify graph, openclaw CT 124, .NET 10 | Same stack, but evidence must stand alone (no graphify refs in PRs) |
| BUG-001 glibc fix | Must never regress (prod SIGSEGV) | **In PR #1494 — already upstream-facing**; keep aligned with upstream's Dockerfile if they change it |
| SQL | Base files seed prod DBs (touch carefully) | Same rule, stricter: both files mandatory |
| Docs | WORKFLOW.md, SCORECARD.md, COMMUNITY-GUIDELINES.md are fork-local | Never include fork-local docs in upstream PRs |
| Commit style | CC adopted (this doc) | De facto CC — title must match |
| CI | Local dotnet build/test (no CI on fork) | Greptile auto; .NET/CodeQL often `action_required` |

---

*Maintained by Nei (quartermaster) — verify live state before trusting:
`git -C /root/aaemu-dev fetch upstream && git diff HEAD..upstream/develop -- CONTRIBUTING.md AGENTS.md .github/workflows`*
