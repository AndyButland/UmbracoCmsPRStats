# Umbraco-CMS release stats

A .NET 10 file-based app that fetches merged PRs from `umbraco/Umbraco-CMS` and renders a self-contained HTML report charting PR count and lines-of-code churn per minor release.

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

For each merged PR, the tool looks for `release/X.Y.Z` labels in this order, using the **first non-empty tier**:

1. The PR's own labels.
2. Labels on every issue the PR closes (`Fixes #N`, `Closes #N`, `Resolves #N`, or a Development-sidebar link).
3. *(opt-in via `--include-mentions`)* Labels on issues the PR's body merely mentions via plain `#N`.

Within the chosen tier, if multiple `release/X.Y.Z` labels exist, the PR is bucketed into the **earliest** `(major, minor)` — interpreted as "the first release this work shipped in." PRs with no release label in any active tier are noted in the report header but not charted.

The tier hierarchy avoids a common false positive: dependabot-generated PR bodies often plain-mention very old issues (e.g. an `8.1.4` lodash issue), which would otherwise drag a modern PR into an ancient release bucket. By falling through to mentions only when the PR and its closing issues say nothing, we trust authoritative labels first.

## Files

- `app.cs` — the whole tool (entry point, fetch, report).
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
  --in   PATH         Cache path (default: data/prs.json).
  --out  PATH         Output HTML (default: report.html).
```

## Known limitations

- Plain `#N` matches on the body are a heuristic — false positives possible (e.g. "this is *not* like #1234").
- Cross-repo issue references are skipped.
- An LTS backport labeled `release/13.5.2` and `release/15.2.0` is counted only in `13.5` (earliest-wins rule).
- GitHub's `search` API caps at 1000 results per query; the tool slices by calendar month so this rarely bites, but watch for warnings if a single month exceeds 1000 merges.
