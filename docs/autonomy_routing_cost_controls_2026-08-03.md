# Autonomy Routing and Cost Controls

Date: 2026-08-03
Status: Accepted and implemented

## Incident evidence

On 2026-08-03 the bot made 527 model calls and consumed approximately $7.15 to $7.90 in one UTC day.
World autonomy accounted for about 97 percent of estimated spend. The audience judge evaluated 177 ambient
episodes, but Shadow mode still launched the full xhigh agent after predictions of silence or reaction.

The expensive behavior was not uniformly worthless. Ten autonomy messages received recorded positive reactions,
and several earlier structural episodes produced well-received persistent Discord changes. The evidence therefore
does not justify blindly enforcing every silence prediction or removing world autonomy.

The routing defect was using the same executive agent for two different jobs:

- deciding and executing persistent Discord actions;
- producing one ambient Robotnik line.

## Decisions

### 1. Four host-owned routes

Ambient admission now maps independent judge scores to four routes:

- `FullAutonomy`: structural opportunity; xhigh agent plus deferred Steward catalog.
- `Conversation`: one xhigh call, no tools, one concise Robotnik line.
- `Reaction`: lightweight emoji path.
- `Silence`: no creative model call after the judge.

Direct mentions, replies, and commands bypass ambient admission and retain full autonomy, subject to their own
route budget and the global provider guard.

### 2. Conversation is not administrative autonomy

The conversation route receives Robotnik's stable character contract, bounded room context, media evidence, and
mood. It receives no Steward tools, request IDs, mutation instructions, or function loop. It may not claim server
state changes. Delivery still uses the registered Sky transport, sent-message registry, transcript sink, reaction
attribution, and post-speech cadence state.

This preserves the model quality used for Robotnik's voice while removing the executive prompt and multi-call tool
loop from ordinary ambient commentary.

### 3. Canary enforcement replaces unlimited Shadow

Canary enforces the four-way route. Ten percent exploration remains, but exploration uses the cheapest useful
challenger:

- predicted silence or reaction explores the conversation route;
- predicted conversation explores full autonomy;
- predicted structural action remains full autonomy.

Shadow remains available for offline diagnostics, but it is no longer the production default. A production
experiment may not run an unlimited expensive baseline.

### 4. Structural action remains fail-open within a hard budget

During Canary, the action threshold is temporarily lower than the final target. This protects unusual structural
opportunities while evidence accumulates. Full ambient autonomy is capped separately per hour and per UTC day.
Once real structural opportunities have been observed and reviewed, the action threshold may be raised without
changing the conversation route.

### 5. Recent speech cannot suppress a strong new line

The old recent-speech threshold inflation could turn a high conversation score into silence. Post-speech cadence
may now hold only an episode already below conversation, reaction, and action thresholds. New material or a strong
conversation/action score escapes the cadence hold.

### 6. Route budgets are persistent and separate

The host persists fixed UTC-hour and UTC-day counters on the PVC for:

- ambient full autonomy;
- ambient conversation;
- direct full autonomy;
- direct conversation fallback.

When ambient full autonomy is exhausted, the route degrades to conversation. When ambient conversation is also
exhausted, the episode is silent. When direct full autonomy is exhausted, the bot attempts direct conversation.
If that route or the provider is unavailable, a deterministic no-model treasury notice replies to the petitioner.

Restarting the pod does not reset healthy budget state. Corrupt route or provider spend state is logged, emitted
as telemetry, replaced with an exhausted sentinel, and held through the current UTC day rather than reset to zero.

### 7. One shared provider guard

All active-provider chat workloads and OpenAI image generation use one singleton guard. It provides:

- immediate quota/auth circuit opening;
- one half-open probe after the configured interval;
- conservative per-call reservations;
- persistent estimated hourly and daily dollar totals;
- pre-provider blocking when either dollar limit is exhausted;
- durable guard telemetry.

World autonomy retains its outer run-level probe ownership, while its individual provider calls participate in the
same dollar accounting without acquiring a second probe lease.

Cost estimates use model-specific input, cached-input, cache-write, and output rates. When GPT-5.6 does not expose
cache-write usage, all uncached input is conservatively priced at the cache-write rate. Unknown model names use the
expensive rate and reservation rather than being treated as mini models.

Reservations cap concurrent exposure before exact token usage exists: Sol and unknown chat models reserve $0.75,
Luna and mini models reserve $0.02, and GPT Image reserves the maximum configured per-image estimate of $0.21.
Abandoned streaming enumerations release their reservation without recording a successful call.

A local state-write failure after a successful provider response is fail-soft: it is logged but never discards the
response or causes an implicit model retry.

### 8. Terminal delivery is enabled before explicit caching

Terminal speech and successful visual delivery stop the function loop after delivery, provided no write is
unsettled and the delivery is the sole function call. This removes the deterministic post-speech acknowledgment
call.

Explicit prompt caching remains off until a prompt-quality review and paid cache canary are possible. Transport
support is implemented and tested, but cost pressure is not a reason to change prompt ordering without quality
evidence.

### 9. Cold-open generation is disabled while credits are scarce

Cold-open polling itself is free, but Shadow composition still calls the model. Cold opens are disabled during the
credit-constrained rollout. Stable target IDs and Shadow mode remain configured for later review.

## Production limits

Initial limits are intentionally conservative:

| Limit | Value |
|---|---:|
| Ambient full autonomy | 4/hour, 16/day |
| Ambient conversation | 12/hour, 60/day |
| Direct full autonomy | 8/hour, 40/day |
| Direct conversation | 16/hour, 80/day |
| All guarded OpenAI work | $1/hour, $3/day estimated upper bound |
| Canary exploration | 10 percent |

Route limits protect scarce executive attention. The global dollar ceiling is the final backstop across creative,
utility, memory, cold-open, and image workloads. These values are configuration, not permanent product policy.

## Rejected alternatives

### Enforce every silence prediction

Rejected. Recorded reactions demonstrate false-negative silence predictions, including high-confidence misses.
The cheap judge cannot reliably predict every line a stronger model can invent.

### Keep full autonomy but lower reasoning effort

Rejected as the first response. It preserves the wrong routing architecture and risks the structural quality that
users valued. Selection and call count dominated spend.

### Remove world autonomy

Rejected. Persistent server theater is differentiated and has produced strongly received outcomes. The fix is to
reserve it for structural opportunities and direct petitions.

### Run a 100 percent Shadow baseline again

Rejected. Exploration must be sampled and bounded. Observation does not justify unlimited production spend.

### Enable explicit caching immediately

Rejected for this rollout. The transport is proven, but prompt reordering still requires quality review and cache
read/write evidence with funded API access.

## Rollout and rollback

1. Deploy with Canary routing, terminal delivery enabled, explicit caching off, and cold opens disabled.
2. Verify configuration, health, both Steward children, route-budget state, and provider-guard telemetry.
3. Add credits only after the guard is live.
4. Observe one active-room window before adjusting thresholds or limits.
5. Review conversation, reaction, silence exploration, structural actions, cost per human message, and direct
   delivery.

Rollback levers are independent:

- set ambient mode to `Shadow` to restore the old baseline behavior;
- disable terminal delivery without changing routing;
- disable the provider guard only for emergency diagnosis;
- increase a route budget without changing judge thresholds;
- keep explicit caching off;
- keep cold opens disabled.

No rollout should change more than one quality-sensitive lever after this initial containment release.
