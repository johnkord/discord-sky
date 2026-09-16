# GPT Image 2.5 In Discord Sky

Updated 2026-09-16 UTC. This is the current image pipeline and operating guide.

## Discord Use

Ask Robotnik for an image normally, or use an explicit command:

```text
!sky(image) an imperial postage stamp --format webp --size 1536x864
!sky(image) a mechanical crown sticker --transparent --quality high
!sky(image) a panoramic workshop --size 3840x2160 --quality low --format jpeg
```

Reply to an image with `change the background to green`, `remove the hat`, or `make it transparent`.
For explicit control, reply with:

```text
!sky(image) change only the background to green --edit --model sunburst
!sky(image) make the background transparent --edit --format png
```

Attach up to four PNG, JPEG, or WebP references to the request, or reply to a message containing them.
Sky downloads the actual image pixels. A reply's attachments come first, followed by the new request's
attachments. `--generate` deliberately ignores references and creates a fresh image. `--edit` requires a source;
`--action auto` uses references when present and otherwise generates.

For a masked edit, attach a file named `mask.png`. It must have an alpha channel and the same dimensions as
the first source image, which must also be PNG. Transparent mask regions identify the area to change. The
mask guides the model; it is not a guarantee of exact pixel preservation. A reply's first image is the first
source when both the reply and the new message have images.

Commands and autonomous `create_visual` requests use one Discord progress message. Preview attachments replace
one another; the final caption and image replace the last preview. A failed autonomous render removes its
progress message on a best-effort basis before returning a refusal to Robotnik. Previews do not count as final
registered deliveries. Ordinary `generate_image` and ambient renders attach only the final result and do not
request paid previews.

All paths retain Robotnik's existing cartoon art direction, voice, guild policy, and shared spend protection.
Disabled guilds remain disabled. References are limited to Discord attachments on the triggering message and
its same-channel reply, not arbitrary URLs, other channels, provider file IDs, or hidden conversation state.
Replying to the delivered image carries edits across restarts without a process-local conversation cache.

## Controls

Natural-language requests can select these options through the rewriter or image tool. Explicit command flags
take precedence over the rewriter. Place `--` before any literal prompt text that starts with option-like words.

| Flag | Values | Production Default |
| --- | --- | --- |
| `--model` | `flare`, `sunburst`, or approved full model ID | Flare for generation, Sunburst for edits |
| `--action` | `auto`, `generate`, `edit`; also `--generate` and `--edit` | auto |
| `--quality` | `low`, `medium`, `high`, `xhigh`, `max`, `auto` | medium |
| `--size` | `auto` or `WIDTHxHEIGHT` | 1024x1024 |
| `--format` | `png`, `jpeg`, `jpg`, `webp` | jpeg |
| `--background` | `opaque`, `transparent`, `auto`; also `--transparent` | opaque |
| `--compression` | 0 to 100, JPEG/WebP only | 85 |
| `--previews` | 0 to 3 | 1 on progress-capable paths |
| `--moderation` | `auto`, `low` | auto |

Transparency automatically selects PNG when no format was explicitly requested and the configured format is
JPEG. Explicit transparent JPEG requests are rejected. PNG never receives a compression parameter.
Both dimensions must be multiples of 16, no edge may exceed 3840, the aspect ratio must be between 1:3 and 3:1,
and the pixel count must be between 655360 and 8294400. Sizes above 2560x1440 remain experimental upstream.
`xhigh` and `max` require GPT Image 2.5 or newer. Image quality is separate from Sol's reasoning effort.

The existing one-image-per-request contract remains. There are no batch requests, background retries,
Responses API image sessions, additional image-planning model calls, or automatic fallback to older models.
The Image API itself handles generation and edits; Sol and its xhigh reasoning profile are unchanged.

## Production Settings And Limits

`Image:Model=gpt-image-2.5-flare`, `Image:EditModel=gpt-image-2.5-sunburst`, and `Image:EditingEnabled=true`.
`Image:AllowHighQuality=true` enables explicitly requested higher qualities without promoting ordinary requests.
Setting it false caps high, xhigh, max, and auto at medium. Older and mini models remain prohibited below the
existing GPT Image 2 non-mini quality floor.

The production pod allows one image operation at a time, four reference images, 8 MiB per reference, 16 MiB
total reference bytes including a mask, and 8 MiB per delivered image. Input images are limited to 16 megapixels.
Downloads validate the exact Discord CDN host and source channel, reject redirects, enforce byte limits while
reading, and inspect actual image format and dimensions with ImageSharp. Nonessential metadata is skipped.
The render pipeline has a five-minute deadline. These host limits are intentionally below the API's limits to
fit the 512 MiB combined Sky/Steward pod and ordinary Discord uploads.

Useful configuration keys are `Image:PartialImages`, `OutputCompression`, `Background`, `MaxConcurrent`,
`MaxReferenceImages`, `MaxReferenceBytes`, `MaxTotalReferenceBytes`, and `RequestTimeoutMinutes`.
The existing daily, per-user, and monthly image gates also remain active.

## Accounting

All image calls share the live `LlmProviderGuard` with chat: $1/hour and $3/day in production. Admission uses a
request-specific estimate, not a flat image charge. At square size, reservations start at $0.10 for low, $0.21
for medium, $0.42 for high, $0.65 for xhigh, and $0.90 for max/auto. These are host estimates, not a provider
pricing table. Larger pixel counts scale the estimate; each reference and mask adds $0.05, each requested
preview adds $0.003, and prompt length adds an input allowance. `size=auto` reserves for the maximum canvas.

Consequently, a valid 4K/high-quality or auto-size request can exceed the current hourly budget before any
provider call. Choose a smaller canvas or lower quality, or make an explicit owner budget change. A budget hold
does not mean the model lacks the requested feature. Reservations are not absolute billing guarantees.

Successful GPT Image 2.5 calls replace the reservation with an estimate from returned token usage. Both models
currently cost $5/M text-input tokens, $8/M image-input tokens, and $30/M output tokens. Cached text/image input
rates are $1.25/M and $2/M when the response supplies the corresponding breakdown. Unknown input modalities are
charged at the higher input rate; absent cache detail gets no speculative discount. Returned output usage
includes preview work and is not charged a second time. Missing usage or interrupted dispatched requests retain
the reservation estimate. Charged errors and cancellations count toward the monthly image budget as well.

Image requests have SDK automatic retries disabled. User errors, moderation blocks, and quota failures are not
resent. A lost stream or timeout can still have incurred provider charges. Retry only deliberately after reading
the failure reason. Quota/auth errors open the shared provider circuit.

## Evidence And Operations

The private image JSONL adds action, reference message IDs/count, mask presence, format/background/compression,
preview count, token usage, cost basis, provider request ID/status/code, and moderation stage/categories.
It retains the existing bounded final prompt. Do not publish this log. Image bytes, masks, and signed reference
URLs are not retained in Sky's durable log. Provider raw error text is not echoed into application logs.

For an image failure, correlate the trigger/opportunity with its image record and provider-guard event. Check
`provider_error_code`, `http_status`, `request_id`, `cost_basis`, and `moderation_stage` before retrying. Model-list
access proves entitlement, not billing availability or successful generation. `/healthz` still measures Discord
and Steward readiness, not image quality or OpenAI funds.

The repaired live smoke tool accepts `--direct` to skip the chat rewrite, `--reference`, `--mask`, `--background`,
`--previews`, and the existing model/quality/size/format arguments. It writes local images and previews and uses
a separate persistent $0.75/hour/day guard. Its spend is not written into the production ledger. Supply the key
through `OPENAI_API_KEY`, never a command-line argument, and keep outputs outside the repository:

```bash
dotnet run --project tools/DiscordSky.ImageSmoke -- --direct --model gpt-image-2.5-flare \
  --quality low --format png --background transparent --previews 1 \
  --request "a red launch button on a metal pedestal" --out /tmp/flare.png
dotnet run --project tools/DiscordSky.ImageSmoke -- --direct --model gpt-image-2.5-sunburst \
  --reference /tmp/flare.png --quality medium --format png --background transparent --previews 1 \
  --request "change only the red button cap to blue" --out /tmp/sunburst.png
```

Live pre-release evidence on 2026-09-16: Flare generated a 1024x1024 transparent PNG in 11.3 seconds with no
partial frame; Sunburst edited that reference in 15.4 seconds and emitted one partial frame. Pixel inspection
confirmed real alpha transparency in both finals and the preview. The requested red-to-blue edit was visible
with the pedestal composition retained. Combined token-based estimate: $0.030532. These checks did not send
Discord messages. Mask constraints, HTTP/SSE errors, and delivery ownership are also covered
by focused tests; actual Discord delivery remains a post-deployment user acceptance check.

The final matched stack uses OpenAI .NET 2.13.0 and Microsoft.Extensions.AI/OpenAI 10.10.0, with the standard .NET
SSE parser reading an unbuffered SDK protocol response. OpenAI 2.14.0 was rejected because it removes an
experimental MCP type still required by the current Microsoft adapter. The hosted tool-search test now exposes
such pre-send exceptions and has a deadline, instead of swallowing them and waiting forever. ImageSharp 3.1.12
handles reference inspection. The guard and final-delivery behavior are shared with existing callers.

On that final stack, Sol/xhigh rewrite plus a medium-quality Sunburst edit completed in 23.6 seconds and emitted
one preview. All three image checks plus the rewrite totaled $0.069430 in the separate local smoke ledger.
The complete regression suite passed 1,354 tests, and the Release build and Kubernetes client dry-run passed.

Reference: [OpenAI Image Generation Guide](https://developers.openai.com/api/docs/guides/image-generation).