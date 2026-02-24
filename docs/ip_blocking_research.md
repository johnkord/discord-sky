# IP Blocking from AKS Clusters — Research & Alternatives

## Problem Statement

When running the Discord Sky bot from an AKS cluster (Azure datacenter IPs), websites like Reddit return HTTP 403 for requests they suspect are automated. The current browser User-Agent fix works for now, but datacenter IP ranges are well-known and many sites block or throttle them regardless of UA string.

This document evaluates alternatives for robust web content unfurling from cloud infrastructure.

---

## 1. Reddit `.json` API (Public Endpoints)

**How it works:** Reddit provides JSON representations of almost any page by appending `.json` to the URL path. For example:
- `https://www.reddit.com/r/technology/top.json?limit=1`
- `https://www.reddit.com/r/AskReddit/comments/{id}/{slug}/.json`

**Verified behavior:**
- Returns HTTP 200 even with a bot User-Agent (`DiscordSkyBot/1.0`) — confirmed via curl testing
- Returns structured JSON with post title, selftext, author, score, num_comments, and top comments
- Rate-limited to ~100 requests per 10 minutes (unauthenticated), visible via `x-ratelimit-*` headers
- No OAuth registration required for read-only access

**Response structure:**
```json
[
  { "kind": "Listing", "data": { "children": [
    { "kind": "t3", "data": { "title": "...", "selftext": "...", "author": "...", "url": "...", "score": 1234, "num_comments": 56 }}
  ]}},
  { "kind": "Listing", "data": { "children": [
    { "kind": "t1", "data": { "body": "...", "author": "...", "score": 42 }}
  ]}}
]
```

**Pros:**
- Free, no registration, no API keys
- Bypasses IP/UA blocking entirely — JSON API is meant for programmatic access
- Returns structured data (title, selftext, top comments) — richer than HTML scraping
- Low latency since there's no HTML to parse
- Already works from datacenter IPs with bot UAs

**Cons:**
- Reddit-specific — doesn't help with other blocked sites
- Rate limit of ~100 req/10 min (sufficient for Discord bot use case)
- URL mapping required: must convert shared Reddit URL → `.json` URL
- Doesn't return rendered markdown (selftext is raw markdown, which is fine for LLMs)

**Implementation effort:** Small — add a `RedditUnfurler` following the existing `TweetUnfurler` pattern. Add reddit.com domains to `SkippedDomains` in `WebContentUnfurler`.

**Recommendation: ★★★★★ — Best approach for Reddit specifically. Implement as a dedicated `ILinkUnfurler`.**

---

## 2. Reddit OAuth2 API

**How it works:** Register an application at `reddit.com/prefs/apps/`, get client_id/secret, use OAuth2 client credentials flow to get bearer token, then access `oauth.reddit.com` endpoints.

**Key details:**
- All API clients must authenticate with OAuth2 (Reddit's official requirement)
- OAuth rate limit: 60 requests/minute per client (documented)
- Uses `oauth.reddit.com` instead of `www.reddit.com`
- Requires a unique, descriptive User-Agent (e.g., `linux:com.discordsky:v1.0 (by /u/username)`)

**Pros:**
- Official, sanctioned access — won't be blocked
- Higher rate limits than unauthenticated `.json` (60 req/min vs ~10 req/min)
- Access to more endpoints if needed in the future
- Respects Reddit's API terms

**Cons:**
- Requires Reddit account registration and API app creation
- Needs secret management (client_id + client_secret in K8s secrets)
- Slightly more complex implementation (OAuth2 token refresh flow)
- Reddit-specific — doesn't solve general problem

**Implementation effort:** Medium — OAuth2 client credentials flow, token caching, dedicated Reddit unfurler.

**Recommendation: ★★★★☆ — Good if you need higher rate limits or want to follow Reddit's official path. Overkill for the current use case since the `.json` endpoint works without auth.**

---

## 3. Jina Reader API (`r.jina.ai`)

**How it works:** Prepend `https://r.jina.ai/` to any URL to get an LLM-optimized markdown version of the page content. Jina handles the fetching, JavaScript rendering, and content extraction on their infrastructure (residential/diverse IPs).

**Example:**
```bash
curl "https://r.jina.ai/https://www.reddit.com/r/programming/comments/xyz/post_title/"
```

**Pricing & Rate Limits:**
| Tier | Rate Limit | Cost |
|------|-----------|------|
| No API key | 20 RPM | Free |
| Free API key (10M tokens) | 500 RPM | Free |
| Prototype (1B tokens) | 500 RPM | $50 |
| Production (11B tokens) | 5000 RPM | $500 |

**Features:**
- Handles JavaScript-rendered pages
- Built-in proxy support (`X-Proxy` header for country-specific proxying)
- Custom User-Agent forwarding (`X-Set-Cookie`, `X-Proxy-Url`)
- Content format options: markdown, HTML, text, screenshot
- CSS selector targeting (`X-Target-Selector`, `X-Remove-Selector`)
- Caching with configurable tolerance
- PDF and image support

**Pros:**
- Universal — works for ANY website, not just Reddit
- Handles IP blocking, JavaScript rendering, anti-bot measures
- LLM-optimized output (clean markdown)
- Free tier (20 RPM without key, 500 RPM with free key)
- Simple API — one HTTP GET per URL
- Eliminates need for AngleSharp, HTML parsing, boilerplate removal
- Built-in proxy infrastructure

**Cons:**
- External service dependency — single point of failure
- Latency: ~7.9s average per request (vs ~1-3s direct fetch)
- Token-based billing could add up at scale
- Privacy: URLs and content pass through Jina's servers
- Free tier rate limits may be hit in busy servers

**Implementation effort:** Small — replace/supplement `WebContentUnfurler.FetchAndParseAsync` with a Jina Reader call for sites that return 403.

**Recommendation: ★★★★☆ — Excellent as a fallback for sites that block direct access. Could be used as fallback-on-403 strategy rather than primary unfurler (to avoid latency and dependency for sites that work fine directly).**

---

## 4. Azure NAT Gateway (Static Outbound IP)

**How it works:** Configure a NAT Gateway on the AKS cluster's subnet to give all outbound traffic a predictable, static public IP address.

**Configuration:**
```bash
az aks create \
    --resource-group $RG \
    --name $CLUSTER \
    --outbound-type managedNATGateway \
    --nat-gateway-managed-outbound-ip-count 2 \
    --nat-gateway-idle-timeout 4
```

**Pros:**
- All outbound traffic gets a stable, known IP
- Can rotate IPs by adding/removing public IP associations
- Supports up to 16 public IPs per NAT Gateway
- Native Azure service — no external dependency

**Cons:**
- **Doesn't solve the core problem** — the IP is still in Azure's published datacenter range
- Sites block entire IP ranges (e.g., Azure's ASN), not specific IPs
- Additional Azure cost (~$30-45/month per NAT Gateway + IP)
- Can't change the fact that it's a datacenter IP

**Implementation effort:** Infrastructure only — no code changes needed, but doesn't help with blocking.

**Recommendation: ★★☆☆☆ — Useful for other reasons (stable egress IP for allowlisting) but does NOT solve website IP blocking since the IPs are still in Azure's datacenter range.**

---

## 5. Residential/Rotating Proxy Services

**How it works:** Route HTTP requests through residential proxy networks (Bright Data, Oxylabs, SmartProxy, etc.) that use real ISP IP addresses instead of datacenter IPs.

**Typical pricing:**
| Provider | Plan | Price |
|----------|------|-------|
| Bright Data | Residential pay-as-you-go | ~$8.40/GB |
| SmartProxy | Residential starter | ~$7/GB |
| Oxylabs | Residential | ~$10/GB |

**Pros:**
- Residential IPs are almost never blocked
- Works for any website
- Geographic targeting available
- High reliability

**Cons:**
- Expensive — per-GB pricing adds up quickly
- Privacy concern — traffic passes through third-party residential networks
- Ethical concerns — some residential proxy networks use questionable consent
- Overkill for a Discord bot that unfurls a few links per hour
- Additional secret management (proxy credentials)
- Latency overhead from proxy routing

**Implementation effort:** Small — configure `HttpClient` with `WebProxy` in DI.

**Recommendation: ★★☆☆☆ — Too expensive and ethically questionable for this use case. Only justified for heavy/commercial scraping workloads.**

---

## 6. Site-Specific API Adapters (Existing Pattern)

**How it works:** Follow the existing `TweetUnfurler` pattern — create dedicated `ILinkUnfurler` implementations for commonly-shared sites that use their native APIs or JSON endpoints.

**Candidates for specialized unfurlers:**
| Site | Approach | Effort |
|------|----------|--------|
| Reddit | `.json` endpoint (Option 1) | Small |
| Hacker News | `https://hacker-news.firebaseio.com/v0/item/{id}.json` — free, no auth | Small |
| Wikipedia | `https://en.wikipedia.org/api/rest_v1/page/summary/{title}` — free, no auth | Small |
| GitHub | REST API (unauthenticated 60 req/hr, or PAT) | Small-Medium |
| Stack Overflow | API v2.3 (free tier, 300 req/day) | Small |

**Pros:**
- Highest quality data — structured, clean, no scraping noise
- Fastest — direct API calls, no HTML parsing
- Most reliable — APIs are designed for programmatic access
- Follows existing architecture pattern
- No external proxy/service dependency
- Free for all listed sites

**Cons:**
- Only covers known/popular sites — long tail still needs `WebContentUnfurler`
- Each adapter requires development and testing
- API-specific quirks to handle

**Implementation effort:** Small per adapter (following established `ILinkUnfurler` pattern + `CompositeUnfurler` registration).

**Recommendation: ★★★★★ — Best long-term strategy. Cover the 80% case (most-shared domains) with dedicated adapters, and use `WebContentUnfurler` + browser UA for the long tail.**

---

## 7. Headless Browser (Playwright/Puppeteer)

**How it works:** Run a headless browser in the container to fetch pages as a real browser would, complete with JavaScript execution, cookies, and realistic TLS fingerprints.

**Pros:**
- Handles JavaScript-rendered content
- Most realistic browser fingerprint
- Works for complex SPAs

**Cons:**
- Heavy resource usage (~200-500MB RAM per browser instance)
- Significantly increases container image size
- Slow (2-10s per page)
- Complex deployment in K8s (Chrome needs specific security contexts)
- Still uses datacenter IPs — doesn't solve IP-level blocking
- Requires .NET interop (Playwright.NET is available but adds ~250MB to container)

**Implementation effort:** Large — container changes, K8s security context, resource allocation.

**Recommendation: ★☆☆☆☆ — Not recommended. Doesn't solve IP blocking (still datacenter IPs), adds significant complexity and resource cost. The current AngleSharp approach is much lighter and sufficient.**

---

## 8. Fallback Strategy (403 → Jina Reader)

**How it works:** Keep the current `WebContentUnfurler` as primary fetcher with browser UA. When a site returns 403/429, automatically fall back to Jina Reader API.

**Implementation sketch:**
```csharp
internal async Task<UnfurledLink?> FetchAndParseAsync(Uri uri, ...)
{
    // Try direct fetch first (fast, free, no dependency)
    var result = await TryDirectFetchAsync(uri, ...);
    if (result != null) return result;
    
    // Fallback to Jina Reader for blocked sites
    return await TryJinaReaderFetchAsync(uri, ...);
}
```

**Pros:**
- Best of both worlds — fast direct access when possible, reliable fallback when blocked
- Minimizes Jina API usage (most sites work fine directly)
- No wasted tokens/calls for sites that don't need proxying
- Graceful degradation

**Cons:**
- Added latency for blocked sites (failed fetch attempt + Jina call)
- Still depends on external service for some sites

**Recommendation: ★★★★☆ — Excellent complement to site-specific adapters. Handles the long tail of sites that block datacenter IPs.**

---

## Recommended Strategy (Layered Approach)

Implement a three-tier unfurling strategy:

### Tier 1: Site-Specific Adapters (Highest Priority)
- `TweetUnfurler` — already implemented ✅
- `RedditUnfurler` — new, uses `.json` endpoint
- `HackerNewsUnfurler` — new, uses Firebase API
- `WikipediaUnfurler` — new, uses REST API

### Tier 2: Direct HTML Fetch (Current)
- `WebContentUnfurler` with browser UA — works for most sites ✅

### Tier 3: Fallback (Optional, for robustness)
- If direct fetch returns 403/429, retry via Jina Reader API
- Requires Jina API key (free tier gives 10M tokens)

### Implementation Priority

| Phase | What | Effort | Impact |
|-------|------|--------|--------|
| **Phase A** | `RedditUnfurler` with `.json` API | 1-2 hours | Fixes immediate Reddit 403 problem properly |
| **Phase B** | `HackerNewsUnfurler` | 30 min | Covers another commonly-shared domain |
| **Phase C** | Jina Reader fallback in `WebContentUnfurler` | 1 hour | Handles all remaining blocked sites |
| **Phase D** | Additional adapters as needed | 30 min each | Incremental coverage improvement |

### Why NOT the Other Options

| Option | Why Skip |
|--------|----------|
| Azure NAT Gateway | Still datacenter IP — doesn't help with blocking |
| Residential proxies | Too expensive and ethically questionable for this scale |
| Headless browser | Heavy, complex, still datacenter IP |
| Reddit OAuth2 | Unnecessary complexity — `.json` endpoint works without auth |

---

## Key Finding

**Reddit's public `.json` API works from datacenter IPs with any User-Agent.** This was verified via curl testing — returns HTTP 200 with structured JSON data while the same IP gets HTTP 403 on the HTML page. This is the simplest, most reliable fix for the Reddit blocking problem.

The general pattern of "use site-specific APIs when available, fall back to HTML scraping" is the industry standard approach and maps perfectly to the existing `ILinkUnfurler` / `CompositeUnfurler` architecture.

---

## Implementation Status

All Phase A–B items are complete. Phase C (Jina fallback) deferred.

| Component | Status | Details |
|-----------|--------|---------|
| `RedditUnfurler` | ✅ Implemented | Uses `.json` API; extracts title, selftext, metadata, top 3 comments; handles `redd.it` short links |
| `HackerNewsUnfurler` | ✅ Implemented | Uses Firebase API (`hacker-news.firebaseio.com/v0/item/{id}.json`); extracts stories + comments; strips HTML from text |
| `WikipediaUnfurler` | ✅ Implemented | Uses Wikimedia REST API (`/api/rest_v1/page/summary/{title}`); supports all language editions; extracts thumbnail |
| `WebContentUnfurler` SkippedDomains | ✅ Updated | Added reddit.com variants, redd.it, news.ycombinator.com, *.wikipedia.org |
| `Program.cs` DI | ✅ Updated | All unfurlers registered in priority order: Tweet → Reddit → HN → Wikipedia → WebContent |
| Tests | ✅ 321 passing | RedditUnfurlerTests, HackerNewsUnfurlerTests, WikipediaUnfurlerTests added |
| Jina Reader fallback | ⬜ Deferred | Phase C — would add resilience for arbitrary 403s on unknown sites |
