# Umbraco-CMS release stats

A .NET 10 file-based app that fetches merged PRs from `umbraco/Umbraco-CMS` and renders a self-contained HTML report charting PR count and lines-of-code churn per minor release. Each bar is stacked into **HQ** (PRs raised from a branch inside the main repo) and **Community** (PRs raised from a fork).

## Prerequisites

- .NET 10 SDK (preview is fine).
- A GitHub personal access token. **Note**: the `umbraco` organisation blocks fine-grained PATs ("forbids access via a personal access token with fine-grained permissions"), so you need a **classic PAT** instead:
  - Create at https://github.com/settings/tokens (classic).
  - For a public repo, no scope is strictly required, but `public_repo` is fine.
  - With auth, you get the full 5000-points/hour GraphQL budget.

## Usage

Set the token (PowerShell):

```powershell
$env:GITHUB_TOKEN = "github_pat_..."
```

Fetch (default: last 3 years, incremental on subsequent runs):

```powershell
dotnet run app.cs -- fetch
```

A first full sweep takes a few minutes; subsequent runs only fetch since the last cached `mergedAt` (minus a 7-day overlap to catch late-applied labels). Use `--force` to refetch everything.

Generate the HTML report:

```powershell
dotnet run app.cs -- report
```

Open `report.html` in a browser. The file uses Chart.js via CDN — once loaded, the browser caches the script for offline viewing.

## How PRs are bucketed by release

For each merged PR, the tool tries four tiers in order and uses the **first one that yields a `release/X.Y.Z` signal**:

1. **PR's own labels.**
2. **Closing-issue labels.** From two sources, treated equivalently:
   - GitHub's parsed `closingIssuesReferences` (`Fixes #N`, `Closes #N`, `Resolves #N`, or a Development-sidebar link).
   - The PR body, scanned for `Fix(ed|es)?/Close(d|s)?/Resolve(d|s)?` followed by `#N` *or* a `https://github.com/umbraco/Umbraco-CMS/{issues|pull}/N` URL. This catches PRs that linked the issue as a URL, or that GitHub didn't auto-link (e.g. merges into non-default branches).
3. *(opt-in via `--include-mentions`)* **Bare mentions in the body** — `#N` or issue URLs with no closing verb. Noisier; dependabot PR bodies often plain-mention very old issues.
4. **Inferred from base branch + merge date** (see below). Only used when tiers 1–3 found nothing.

Within the chosen tier, if multiple `release/X.Y.Z` labels exist, the PR is bucketed into the **earliest** `(major, minor)` — "the first release this work shipped in."

### Tier 4: date-based fallback

When no release label can be found, the tool falls back to the PR's **base branch** and **merge date**, cross-referenced against `release-dates.csv`. Patterns:

| Base branch | Major | Minor |
|---|---|---|
| `release/X.Y[.Z]` | `X` | `Y` |
| `vN/...` (`vN/dev`, `vN/feature/...`, etc.) | `N` | first stable `N.Y.0` released after merge, else `latestMinor(N) + 1` |
| `main` | first stable `M.0.0` released after merge, else `latestMajor + 1` | as above |
| `contrib` | the major **currently released** at merge time | as above |

Pre-releases (`-rc`, `-beta`) are ignored when computing release boundaries. PRs on feature branches with no version prefix (e.g. `feature/acceptance-tests-no-docker`) remain unbucketed.

### HQ vs Community

PRs raised from a fork (GraphQL's `isCrossRepository: true`) are tagged **Community**; PRs raised from a branch inside `umbraco/Umbraco-CMS` (push access required) are **HQ**. Each bar in the report is stacked by this split. The classification is heuristic — an HQ engineer who happens to PR from their own fork is counted as community.

## Files

- `app.cs` — the whole tool (entry point, fetch, report).
- `release-dates.csv` — `version,released` (ISO date), used by tier 4 inference. Sourced from the NuGet listing for `Umbraco.Cms`. Refresh manually when new releases ship.
- `data/prs.json` — local cache (gitignored). Refetch with `dotnet run app.cs -- fetch --force` to rebuild from scratch.
- `report.html` — generated output (gitignored).

## Flags

```
fetch:
  --from YYYY-MM-DD   Start date (default: today - 3y or last cache + 7d overlap).
  --to   YYYY-MM-DD   End date   (default: today).
  --force             Ignore cache; refetch full window.
  --cache PATH        Cache file (default: data/prs.json).

report:
  --in        PATH    Cache path (default: data/prs.json).
  --out       PATH    Output HTML (default: report.html).
  --releases  PATH    Release-dates CSV (default: release-dates.csv). Tier 4 is
                      disabled if this file is missing.
  --include-mentions  Activate tier 3 (bare #N / URL mentions). Noisier.
```

## Cache schema changes & backwards compatibility

The cache JSON has gained fields over time: `isFromFork`, `baseRefName`, and `bodyClosingIssues`. Records pre-dating each field deserialize cleanly (nullable for `isFromFork`, empty string/list otherwise) but the new tiers can't classify them. Run `dotnet run app.cs -- fetch --force` once after pulling these changes to backfill — the report will warn if any cached PRs still lack fork status.

## Known limitations

- Bare `#N` / URL mentions on the body are a heuristic — false positives possible (e.g. "this is *not* like #1234").
- Cross-repo issue references are skipped.
- An LTS backport labeled `release/13.5.2` and `release/15.2.0` is counted only in `13.5` (earliest-wins rule).
- Tier 4 attribution for `main` and `contrib` assumes a single development line at any time; LTS branches that share `main` would be miscounted.
- A PR merged to `vN/dev` past the last known `N.Y.0` is allocated to a synthetic `N.(Y+1)` bucket — fine while the bucket is in development, but the projection may drift if `release-dates.csv` is stale.
- GitHub's `search` API caps at 1000 results per query; the tool slices by calendar month so this rarely bites, but watch for warnings if a single month exceeds 1000 merges.
