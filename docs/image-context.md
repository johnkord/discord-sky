# Image Context Integration

## Objective
Enable personas to reference Discord image attachments or inline image links when invoking OpenAI Responses API without disrupting current text-only behavior.

## Collection
- Extend `ChannelMessage` with an `Images` list of `ChannelImage` records (url, filename, source, timestamp).
- In `ContextAggregator`, capture image metadata from `message.Attachments` (content type `image/` or filename extension) and inline links that resolve to Discord CDN or configured allow-list domains.
- Preserve per-message ordering by attaching images to the logged message and emitting them in the same relative position as the originating text.
- Respect `BotOptions` additions: `AllowImageContext` (default true) and `HistoryImageLimit` (default 3). Trim oldest images first; log and skip on parsing failures.

## Prompt Assembly
- Add `OpenAiResponseInputContent.FromImage(Uri url, string detail)` helper returning `{ "type": "input_image", "image_url": url, "detail": detail }`; expose `OpenAIOptions.VisionDetail` (default `auto`).
- When building the request payload, emit one `input_text` entry per history message and immediately follow it with that message's images as `input_image` entries to preserve in-chat ordering. Skip the image entries entirely when no media is associated with a message.
- Include a short JSON summary of images (id, author, age_minutes, filename, url) in the user prompt to ground the model.

## Example Payload
```bash
curl https://api.openai.com/v1/responses \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "model": "gpt-4.1",
    "input": [
      {
        "role": "user",
        "content": [
          {"type": "input_text", "text": "what is in this image?"},
          {
            "type": "input_image",
            "image_url": "https://upload.wikimedia.org/wikipedia/commons/thumb/d/dd/Gfp-wisconsin-madison-the-nature-boardwalk.jpg/2560px-Gfp-wisconsin-madison-the-nature-boardwalk.jpg"
          }
        ]
      }
    ]
  }'
```

Example response (truncated for brevity):
```json
{
  "status": "completed",
  "model": "gpt-4.1-2025-04-14",
  "output": [
    {
      "role": "assistant",
      "content": [
        {
          "type": "output_text",
          "text": "The image depicts a scenic landscape ..."
        }
      ]
    }
  ],
  "usage": {
    "input_tokens": 328,
    "output_tokens": 52,
    "total_tokens": 380
  }
}
```

## Safety & Validation
- Only forward HTTPS URLs; ignore domains outside the allow-list.
- Skip image injection if requests exceed payload limits or URLs are expired.
- Extend tests to cover attachment extraction, inline detection, and serialization of mixed `input_text`/`input_image` payloads.
