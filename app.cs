using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

const string Owner = "umbraco";
const string Repo = "Umbraco-CMS";
const string DefaultCachePath = "data/prs.json";
const string DefaultReportPath = "report.html";

const string SearchQuery = """
query($q: String!, $cursor: String) {
  rateLimit { cost remaining resetAt }
  search(query: $q, type: ISSUE, first: 100, after: $cursor) {
    pageInfo { hasNextPage endCursor }
    nodes {
      ... on PullRequest {
        number
        title
        body
        mergedAt
        additions
        deletions
        labels(first: 50) { nodes { name } }
        closingIssuesReferences(first: 20) {
          nodes {
            number
            labels(first: 50) { nodes { name } }
          }
        }
      }
    }
  }
}
""";

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
};

return await Dispatch(args);

async Task<int> Dispatch(string[] argv)
{
    if (argv.Length == 0) { PrintUsage(); return 1; }

    return argv[0] switch
    {
        "fetch"  => await RunFetch(argv.Skip(1).ToArray()),
        "report" => RunReport(argv.Skip(1).ToArray()),
        "-h" or "--help" => (PrintUsage(), 0).Item2,
        _ => (PrintUsage(), 1).Item2,
    };
}

static int PrintUsage()
{
    Console.WriteLine("""
        Umbraco-CMS release stats tool

        Usage:
          dotnet run app.cs -- fetch  [--from YYYY-MM-DD] [--to YYYY-MM-DD] [--force] [--cache PATH]
          dotnet run app.cs -- report [--in PATH] [--out PATH] [--include-mentions]

        fetch:
          --from        Start of window (default: today - 3 years, or last cache date - 7 days if cache exists).
          --to          End of window (default: today).
          --force       Ignore cache and refetch the full window.
          --cache       Cache file path (default: data/prs.json).

        report:
          --in                 Cache path (default: data/prs.json).
          --out                Output HTML path (default: report.html).
          --include-mentions   Fall back to plain "#N" mentions in PR bodies when neither the PR nor
                               its closing issues carry a release label. Noisier — stale dependabot
                               links can mis-bucket PRs (off by default).

        Set the GITHUB_TOKEN environment variable to a fine-grained PAT with:
          - Metadata: Read
          - Pull requests: Read
          - Issues: Read
        """);
    return 0;
}

// ============================================================================
// Fetch
// ============================================================================

async Task<int> RunFetch(string[] argv)
{
    var (flags, _) = ParseFlags(argv);
    string cachePath = flags.GetValueOrDefault("cache", DefaultCachePath);
    bool force = flags.ContainsKey("force");

    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("ERROR: GITHUB_TOKEN environment variable is not set.");
        Console.Error.WriteLine("In PowerShell:  $env:GITHUB_TOKEN = \"ghp_...\"");
        Console.Error.WriteLine("Note: the umbraco org blocks fine-grained PATs; use a classic PAT.");
        return 2;
    }

    DateTimeOffset to = flags.TryGetValue("to", out var toStr)
        ? ParseDate(toStr)
        : DateTimeOffset.UtcNow;

    DateTimeOffset defaultFrom = to.AddYears(-3);

    CacheFile? existing = null;
    if (!force && File.Exists(cachePath))
    {
        try { existing = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath), jsonOpts); }
        catch (Exception ex) { Console.Error.WriteLine($"WARN: failed to read cache ({ex.Message}); refetching."); }
    }

    DateTimeOffset from;
    if (flags.TryGetValue("from", out var fromStr))
    {
        from = ParseDate(fromStr);
    }
    else if (existing is { Prs.Count: > 0 })
    {
        var max = existing.Prs.Max(p => p.MergedAt);
        from = max.AddDays(-7);
        Console.WriteLine($"Incremental: fetching from {from:yyyy-MM-dd} (max mergedAt {max:yyyy-MM-dd} - 7d).");
    }
    else
    {
        from = defaultFrom;
    }

    if (from > to) { Console.Error.WriteLine($"ERROR: --from ({from:yyyy-MM-dd}) is after --to ({to:yyyy-MM-dd})."); return 2; }

    Console.WriteLine($"Fetching PRs merged {from:yyyy-MM-dd} .. {to:yyyy-MM-dd}");

    using var http = MakeHttp(token);
    var fetched = await FetchAllAsync(http, from, to);
    Console.WriteLine($"Fetched {fetched.Count} PR records (first pass).");

    await ResolveMentionedIssuesAsync(http, fetched);

    var merged = MergeWithCache(existing?.Prs, fetched);
    Console.WriteLine($"Cache now contains {merged.Count} PRs.");

    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
    var cache = new CacheFile(
        DateTimeOffset.UtcNow,
        existing is null ? from : Min(existing.WindowFrom, from),
        Max(existing?.WindowTo ?? to, to),
        merged.OrderBy(p => p.MergedAt).ToList()
    );
    File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, jsonOpts));
    Console.WriteLine($"Wrote {cachePath}");
    return 0;
}

HttpClient MakeHttp(string token)
{
    var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("UmbracoStatsTool/1.0");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    http.Timeout = TimeSpan.FromSeconds(60);
    return http;
}

async Task<List<PrRecord>> FetchAllAsync(HttpClient http, DateTimeOffset from, DateTimeOffset to)
{
    // Slice into calendar months. GitHub search caps at 1000 results per query.
    var all = new List<PrRecord>();
    var seen = new HashSet<int>();
    var windows = MonthWindows(from, to).ToList();
    int wi = 0;
    foreach (var (wFrom, wTo) in windows)
    {
        wi++;
        Console.WriteLine($"[{wi}/{windows.Count}] window {wFrom:yyyy-MM-dd}..{wTo:yyyy-MM-dd}");
        var pageCount = await FetchWindowAsync(http, wFrom, wTo, all, seen);
        if (pageCount == 1000)
            Console.WriteLine($"  WARN: window hit 1000-result cap; consider narrowing slicing.");
    }
    return all;
}

static IEnumerable<(DateTimeOffset From, DateTimeOffset To)> MonthWindows(DateTimeOffset from, DateTimeOffset to)
{
    var cur = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero);
    while (cur <= to)
    {
        var monthStart = cur > from ? cur : from;
        var nextMonth = cur.AddMonths(1);
        var monthEnd = (nextMonth.AddDays(-1)) < to ? nextMonth.AddDays(-1) : to;
        yield return (monthStart, monthEnd);
        cur = nextMonth;
    }
}

async Task<int> FetchWindowAsync(HttpClient http, DateTimeOffset from, DateTimeOffset to, List<PrRecord> sink, HashSet<int> seen)
{
    var q = $"repo:{Owner}/{Repo} is:pr is:merged merged:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
    string? cursor = null;
    int total = 0;
    while (true)
    {
        var resp = await GraphQLAsync(http, SearchQuery, new Dictionary<string, object?> {
            ["q"] = q,
            ["cursor"] = cursor,
        });

        var search = resp.RootElement.GetProperty("data").GetProperty("search");
        var nodes = search.GetProperty("nodes");
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("number", out _)) continue;
            var pr = ParsePr(n);
            if (seen.Add(pr.Number)) sink.Add(pr);
            total++;
        }
        var pageInfo = search.GetProperty("pageInfo");
        if (!pageInfo.GetProperty("hasNextPage").GetBoolean()) break;
        cursor = pageInfo.GetProperty("endCursor").GetString();
        LogRateLimit(resp);
    }
    return total;
}

PrRecord ParsePr(JsonElement n)
{
    int number = n.GetProperty("number").GetInt32();
    string title = n.GetProperty("title").GetString() ?? "";
    string body = n.GetProperty("body").GetString() ?? "";
    var mergedAt = n.GetProperty("mergedAt").GetDateTimeOffset();
    int additions = n.GetProperty("additions").GetInt32();
    int deletions = n.GetProperty("deletions").GetInt32();

    var labels = ExtractLabels(n.GetProperty("labels"));
    var closing = new List<IssueRef>();
    foreach (var c in n.GetProperty("closingIssuesReferences").GetProperty("nodes").EnumerateArray())
    {
        closing.Add(new IssueRef(
            c.GetProperty("number").GetInt32(),
            ExtractLabels(c.GetProperty("labels"))
        ));
    }
    return new PrRecord(number, title, body, mergedAt, additions, deletions, labels, closing, new());
}

static List<string> ExtractLabels(JsonElement labels)
{
    var list = new List<string>();
    foreach (var l in labels.GetProperty("nodes").EnumerateArray())
        list.Add(l.GetProperty("name").GetString() ?? "");
    return list;
}

// ============================================================================
// Plain #N second pass
// ============================================================================

async Task ResolveMentionedIssuesAsync(HttpClient http, List<PrRecord> prs)
{
    // Collect (prIndex, issueNumber) mentions excluding closing references.
    var perPrMentions = new Dictionary<int, HashSet<int>>(); // pr.Number -> set of issue numbers
    var allNumbers = new HashSet<int>();

    foreach (var pr in prs)
    {
        var closingNums = pr.ClosingIssues.Select(c => c.Number).ToHashSet();
        var found = new HashSet<int>();
        foreach (Match m in Rx.Mention.Matches(pr.Body))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n != pr.Number && !closingNums.Contains(n))
                found.Add(n);
        }
        if (found.Count > 0)
        {
            perPrMentions[pr.Number] = found;
            foreach (var n in found) allNumbers.Add(n);
        }
    }

    if (allNumbers.Count == 0)
    {
        Console.WriteLine("No plain #N mentions to resolve.");
        return;
    }

    Console.WriteLine($"Resolving labels for {allNumbers.Count} mentioned issues...");
    var labelMap = await BatchFetchIssueLabelsAsync(http, allNumbers.ToList());

    // Attach back to PRs.
    var byNumber = prs.ToDictionary(p => p.Number);
    foreach (var (prNumber, issueNums) in perPrMentions)
    {
        var pr = byNumber[prNumber];
        foreach (var n in issueNums)
        {
            if (labelMap.TryGetValue(n, out var labels))
                pr.MentionedIssues.Add(new IssueRef(n, labels));
            // If labelMap doesn't contain n, it was likely a PR (not an issue) or non-existent — skip silently.
        }
    }
}

async Task<Dictionary<int, List<string>>> BatchFetchIssueLabelsAsync(HttpClient http, List<int> numbers)
{
    var result = new Dictionary<int, List<string>>();
    const int BatchSize = 50;
    int batches = (numbers.Count + BatchSize - 1) / BatchSize;
    for (int bi = 0; bi < batches; bi++)
    {
        var slice = numbers.Skip(bi * BatchSize).Take(BatchSize).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("query {");
        sb.AppendLine("  rateLimit { cost remaining resetAt }");
        sb.AppendLine($"  repository(owner: \"{Owner}\", name: \"{Repo}\") {{");
        foreach (var n in slice)
            sb.AppendLine($"    i{n}: issue(number: {n}) {{ number labels(first: 50) {{ nodes {{ name }} }} }}");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        var resp = await GraphQLAsync(http, sb.ToString(), null);
        var repoEl = resp.RootElement.GetProperty("data").GetProperty("repository");
        foreach (var prop in repoEl.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) continue;
            int num = prop.Value.GetProperty("number").GetInt32();
            result[num] = ExtractLabels(prop.Value.GetProperty("labels"));
        }
        Console.WriteLine($"  resolved batch {bi + 1}/{batches} ({slice.Count} issues)");
        LogRateLimit(resp);
    }
    return result;
}

// ============================================================================
// GraphQL transport
// ============================================================================

async Task<JsonDocument> GraphQLAsync(HttpClient http, string query, Dictionary<string, object?>? variables)
{
    var payload = new Dictionary<string, object?> { ["query"] = query };
    if (variables is not null) payload["variables"] = variables;

    int attempt = 0;
    while (true)
    {
        attempt++;
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql");
        req.Content = JsonContent.Create(payload, options: jsonOpts);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req); }
        catch (HttpRequestException ex) when (attempt < 4)
        {
            Console.Error.WriteLine($"  HTTP error {ex.Message}; retry {attempt}");
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            continue;
        }

        var bodyText = await resp.Content.ReadAsStringAsync();

        if ((int)resp.StatusCode == 502 || (int)resp.StatusCode == 503)
        {
            if (attempt >= 4) throw new InvalidOperationException($"GitHub returned {resp.StatusCode} after retries: {bodyText}");
            Console.Error.WriteLine($"  {resp.StatusCode}; retry {attempt}");
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            continue;
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
        {
            // Secondary rate limit. Honor Retry-After if present, else wait 60s.
            int wait = resp.Headers.RetryAfter?.Delta?.Seconds ?? 60;
            Console.Error.WriteLine($"  Secondary rate limit; sleeping {wait}s");
            await Task.Delay(TimeSpan.FromSeconds(wait));
            if (attempt < 5) continue;
        }
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GraphQL HTTP {(int)resp.StatusCode}: {bodyText}");

        var doc = JsonDocument.Parse(bodyText);
        if (doc.RootElement.TryGetProperty("errors", out var errs))
        {
            var msg = errs.GetRawText();
            // Some "errors" are warnings (e.g. deprecation) — only fail if no data.
            if (!doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException($"GraphQL errors: {msg}");
            else
                Console.Error.WriteLine($"  GraphQL warnings: {msg}");
        }

        // Soft rate-limit watch.
        try
        {
            var rl = doc.RootElement.GetProperty("data").GetProperty("rateLimit");
            int remaining = rl.GetProperty("remaining").GetInt32();
            if (remaining < 200)
            {
                var resetAt = rl.GetProperty("resetAt").GetDateTimeOffset();
                var sleep = resetAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                if (sleep > TimeSpan.Zero)
                {
                    Console.Error.WriteLine($"  Rate-limit low ({remaining}); sleeping until {resetAt:HH:mm:ss} UTC");
                    await Task.Delay(sleep);
                }
            }
        }
        catch { /* rateLimit may be absent on errors; ignore */ }

        return doc;
    }
}

void LogRateLimit(JsonDocument doc)
{
    try
    {
        var rl = doc.RootElement.GetProperty("data").GetProperty("rateLimit");
        Console.WriteLine($"  rateLimit: cost={rl.GetProperty("cost").GetInt32()} remaining={rl.GetProperty("remaining").GetInt32()}");
    }
    catch { }
}

// ============================================================================
// Cache merge
// ============================================================================

static List<PrRecord> MergeWithCache(List<PrRecord>? existing, List<PrRecord> fresh)
{
    if (existing is null || existing.Count == 0) return fresh;
    var byNumber = existing.ToDictionary(p => p.Number);
    foreach (var p in fresh) byNumber[p.Number] = p; // fresh wins
    return byNumber.Values.ToList();
}

// ============================================================================
// Report
// ============================================================================

int RunReport(string[] argv)
{
    var (flags, _) = ParseFlags(argv);
    string cachePath = flags.GetValueOrDefault("in", DefaultCachePath);
    string outPath = flags.GetValueOrDefault("out", DefaultReportPath);
    bool includeMentions = flags.ContainsKey("include-mentions");

    if (!File.Exists(cachePath))
    {
        Console.Error.WriteLine($"ERROR: cache not found at {cachePath}. Run `dotnet run app.cs -- fetch` first.");
        return 2;
    }

    var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath), jsonOpts)!;
    var (buckets, skippedCount, skippedNumbers) = BuildBuckets(cache.Prs, includeMentions);

    Console.WriteLine($"Buckets: {buckets.Count}; PRs charted: {buckets.Sum(b => b.PrCount)}; skipped (no release label): {skippedCount}");

    var html = RenderHtml(cache, buckets, skippedCount, skippedNumbers, includeMentions);
    File.WriteAllText(outPath, html);
    Console.WriteLine($"Wrote {outPath}");
    return 0;
}

static (List<Bucket> Buckets, int Skipped, List<int> SkippedSample) BuildBuckets(List<PrRecord> prs, bool includeMentions)
{
    var agg = new Dictionary<(int Major, int Minor), (int Count, long Churn, List<int> Nums)>();
    int skipped = 0;
    var skippedSample = new List<int>();

    foreach (var pr in prs)
    {
        // Tier A: PR's own labels.  Tier B: closing-issue labels.  Tier C: mentioned-issue labels (opt-in fallback).
        // Use the lowest non-empty tier — prevents stale plain-#N mentions overriding real PR/closing labels.
        var tierA = ParseReleaseTuples(pr.Labels);
        var tierB = ParseReleaseTuples(pr.ClosingIssues.SelectMany(c => c.Labels));
        var tierC = includeMentions ? ParseReleaseTuples(pr.MentionedIssues.SelectMany(m => m.Labels)) : new List<(int, int)>();

        var pool = tierA.Count > 0 ? tierA : tierB.Count > 0 ? tierB : tierC;

        (int Major, int Minor)? earliest = null;
        foreach (var t in pool)
            if (earliest is null || t.CompareTo((earliest.Value.Major, earliest.Value.Minor)) < 0)
                earliest = t;

        if (earliest is null)
        {
            skipped++;
            if (skippedSample.Count < 50) skippedSample.Add(pr.Number);
            continue;
        }

        var key = earliest.Value;
        if (!agg.TryGetValue(key, out var v)) v = (0, 0, new List<int>());
        v.Count++;
        v.Churn += pr.Additions + pr.Deletions;
        v.Nums.Add(pr.Number);
        agg[key] = v;
    }

    var buckets = agg
        .OrderBy(kv => kv.Key.Major).ThenBy(kv => kv.Key.Minor)
        .Select(kv => new Bucket(
            $"{kv.Key.Major}.{kv.Key.Minor}",
            kv.Key.Major, kv.Key.Minor,
            kv.Value.Count, kv.Value.Churn,
            kv.Value.Nums.OrderBy(n => n).ToList()))
        .ToList();
    return (buckets, skipped, skippedSample);
}

static List<(int Major, int Minor)> ParseReleaseTuples(IEnumerable<string> labels)
{
    var list = new List<(int, int)>();
    foreach (var lbl in labels)
    {
        var m = Rx.ReleaseLabel.Match(lbl);
        if (m.Success)
            list.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
    }
    return list;
}

string RenderHtml(CacheFile cache, List<Bucket> buckets, int skipped, List<int> skippedSample, bool includeMentions)
{
    var data = new
    {
        labels = buckets.Select(b => b.Minor).ToArray(),
        prCount = buckets.Select(b => b.PrCount).ToArray(),
        churn = buckets.Select(b => b.TotalChurn).ToArray(),
        prsByBucket = buckets.ToDictionary(b => b.Minor, b => b.PrNumbers),
    };
    string dataJson = JsonSerializer.Serialize(data, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    });

    string fetched = cache.FetchedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    string window = $"{cache.WindowFrom:yyyy-MM-dd} → {cache.WindowTo:yyyy-MM-dd}";
    int charted = buckets.Sum(b => b.PrCount);
    string skipNote = skipped == 0
        ? "0 PRs without any release label."
        : $"{skipped} PRs had no release label and are not charted." + (skippedSample.Count > 0 ? $" Sample: {string.Join(", ", skippedSample.Take(20).Select(n => $"#{n}"))}" : "");

    return $$""""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Umbraco-CMS release stats</title>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4"></script>
<style>
  body { font-family: -apple-system, Segoe UI, Roboto, Helvetica, Arial, sans-serif; margin: 2rem; max-width: 1100px; color: #222; }
  h1 { margin-bottom: .25rem; }
  .meta { color: #666; font-size: .9rem; margin-bottom: 1.5rem; }
  .meta a { color: #0366d6; }
  .chart-wrap { margin: 2rem 0; }
  details { margin: .5rem 0; padding: .5rem .8rem; background: #f6f8fa; border: 1px solid #e1e4e8; border-radius: 6px; }
  summary { cursor: pointer; font-weight: 600; }
  .pr-list { columns: 4; column-gap: 1.5rem; margin-top: .75rem; font-size: .9rem; }
  .pr-list a { color: #0366d6; text-decoration: none; }
  .pr-list a:hover { text-decoration: underline; }
  .note { color: #888; font-size: .85rem; margin: 1rem 0; padding: .75rem; background: #fff8e1; border-left: 3px solid #ffb300; }
</style>
</head>
<body>
<h1>Umbraco-CMS release stats</h1>
<div class="meta">
  Repo: <a href="https://github.com/{{Owner}}/{{Repo}}">{{Owner}}/{{Repo}}</a> ·
  Window: {{window}} ·
  Generated: {{fetched}} ·
  PRs charted: {{charted}} across {{buckets.Count}} minor releases.
</div>

<div class="note">
  Bucketing rules: each PR is bucketed into the <strong>earliest</strong> <code>release/X.Y.Z</code>
  label found, looking first at the PR's own labels, then at issues it closes via
  <code>Fixes/Closes/Resolves #N</code>{{(includeMentions ? ", then at issues it merely mentions via #N (opt-in via --include-mentions; noisier — stale dependabot links can mis-bucket)" : "")}}.
  Lines-of-code is <strong>additions + deletions</strong> (churn). {{skipNote}}
</div>

<div class="chart-wrap">
  <h2>PRs per minor release</h2>
  <canvas id="prChart" height="120"></canvas>
</div>

<div class="chart-wrap">
  <h2>Churn (lines added + deleted) per minor release</h2>
  <canvas id="locChart" height="120"></canvas>
</div>

<h2>PRs in each bucket</h2>
<div id="bucketList"></div>

<script id="data" type="application/json">{{dataJson}}</script>
<script>
  const data = JSON.parse(document.getElementById('data').textContent);
  const ghUrl = (n) => `https://github.com/{{Owner}}/{{Repo}}/pull/${n}`;

  new Chart(document.getElementById('prChart'), {
    type: 'bar',
    data: {
      labels: data.labels,
      datasets: [{ label: 'PRs', data: data.prCount, backgroundColor: '#0366d6' }]
    },
    options: { responsive: true, scales: { y: { beginAtZero: true } } }
  });

  new Chart(document.getElementById('locChart'), {
    type: 'bar',
    data: {
      labels: data.labels,
      datasets: [{ label: 'Lines of code (added + deleted)', data: data.churn, backgroundColor: '#28a745' }]
    },
    options: { responsive: true, scales: { y: { beginAtZero: true } } }
  });

  const list = document.getElementById('bucketList');
  for (const minor of data.labels) {
    const nums = data.prsByBucket[minor] || [];
    const det = document.createElement('details');
    const sum = document.createElement('summary');
    sum.textContent = `${minor}  —  ${nums.length} PR(s)`;
    det.appendChild(sum);
    const ul = document.createElement('div');
    ul.className = 'pr-list';
    ul.innerHTML = nums.map(n => `<a href="${ghUrl(n)}" target="_blank">#${n}</a>`).join('<br>');
    det.appendChild(ul);
    list.appendChild(det);
  }
</script>
</body>
</html>
"""";
}

// ============================================================================
// Helpers
// ============================================================================

static (Dictionary<string, string> Flags, List<string> Positional) ParseFlags(string[] argv)
{
    var flags = new Dictionary<string, string>();
    var positional = new List<string>();
    for (int i = 0; i < argv.Length; i++)
    {
        var a = argv[i];
        if (a.StartsWith("--"))
        {
            var key = a[2..];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--"))
            {
                flags[key] = argv[++i];
            }
            else
            {
                flags[key] = "true";
            }
        }
        else { positional.Add(a); }
    }
    return (flags, positional);
}

static DateTimeOffset ParseDate(string s) =>
    DateTimeOffset.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

// ============================================================================
// Types (must follow all top-level statements)
// ============================================================================

static class Rx
{
    public static readonly Regex Mention = new(@"(?<![\w/])#(\d+)\b", RegexOptions.Compiled);
    public static readonly Regex ReleaseLabel = new(@"^release/(\d+)\.(\d+)\.\d+(?:[-+].*)?$", RegexOptions.Compiled);
}

record IssueRef(int Number, List<string> Labels);

record PrRecord(
    int Number,
    string Title,
    string Body,
    DateTimeOffset MergedAt,
    int Additions,
    int Deletions,
    List<string> Labels,
    List<IssueRef> ClosingIssues,
    List<IssueRef> MentionedIssues
);

record CacheFile(
    DateTimeOffset FetchedAt,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    List<PrRecord> Prs
);

record Bucket(string Minor, int Major, int MinorNum, int PrCount, long TotalChurn, List<int> PrNumbers);
