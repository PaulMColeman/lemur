# Voiceprint service

A small, stateless HTTP service that turns short speech clips into speaker embeddings
("voiceprints") with a [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) speaker model on
ONNX Runtime. CPU is enough. It speaks the shape of OpenAI's embeddings API, so it can sit behind
LiteLLM as an embeddings model and use its keys, per-key model limits and usage logs. The recorder
cuts clips per speaker, sends them here and compares the vectors on the PC. The server keeps
nothing: no audio or vectors on disk, no request bodies in its log.

## API

`POST /v1/embeddings`:

```json
{"model": "speaker-embed-local", "input": ["data:audio/wav;base64,UklGR..."], "encoding_format": "float"}
```

- `input` is one string or an array. Each string is a `data:audio/wav;base64,` URL (`audio/x-wav`
  and `audio/wave` work too) holding a **16 kHz mono 16-bit PCM** WAV of 0.5–15 s. That is our
  convention: OpenAI's API takes text in this field. Anything else gets a 400 that names the input
  index and the reason. The service doesn't resample.
- `model` is ignored, because there is one model per service and proxies rename models.
- `encoding_format` is `float` (the default) or `base64` (little-endian float32). The OpenAI Python
  SDK, which LiteLLM uses, asks for `base64` by default.
- The answer has one L2-normalised vector per clip, in input order. Usage "tokens" are the total
  audio seconds, rounded up.

```json
{"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.012, ...]}],
 "model":"wespeaker_en_voxceleb_resnet34_LM","usage":{"prompt_tokens":5,"total_tokens":5}}
```

Errors use OpenAI's shape, `{"error":{"message":"...","type":"invalid_request_error",...}}`: 400 for
a bad request, 401 for a bad key, 413 when the body is too large.

`GET /v1/models` lists the one model. `GET /healthz` returns `{"ok":true,"model":"<id>","dim":256}`
and needs no key.

## Configuration

| Variable | Default | |
|---|---|---|
| `VOICEPRINT_MODEL_PATH` | (required; `/models/model.onnx` in Docker) | the `.onnx` speaker model |
| `VOICEPRINT_MODEL_ID` | model file name without extension | the id reported in responses |
| `VOICEPRINT_API_KEY` | unset (no auth) | when set, `/v1/*` needs `Authorization: Bearer <key>` |
| `VOICEPRINT_THREADS` | `2` | ONNX Runtime threads; clips are embedded one at a time |
| `VOICEPRINT_PROVIDER` | `cpu` | ONNX Runtime provider; the NuGet natives are CPU builds |
| `VOICEPRINT_MAX_CLIPS` | `64` | clips per request |
| `VOICEPRINT_MIN_CLIP_SECONDS` / `VOICEPRINT_MAX_CLIP_SECONDS` | `0.5` / `15` | length of each clip |
| `VOICEPRINT_MAX_BODY_BYTES` | `8388608` (8 MB) | request body limit (Kestrel's limit is set to this as well) |
| `ASPNETCORE_URLS` | `http://+:8090` in Docker | listen address |

## Models

Download one from sherpa-onnx's
[speaker-recognition models release](https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models).
The recorder's benchmark picks the final model and its thresholds. Until then,
`wespeaker_en_voxceleb_resnet34_LM.onnx` is a good start: 26.5 MB, 256 dimensions, and about 0.1 s
per 5 s clip on 2 CPU threads. These also load and work: `3dspeaker_speech_campplus_sv_en_voxceleb_16k`
(512 dimensions), `nemo_en_titanet_large` (192) and `wespeaker_en_voxceleb_resnet293_LM` (256, about
5 times slower). Check each model's licence before you redistribute it.

Vectors from different models can't be compared. The recorder detects a model change by
fingerprinting the model's output, not by reading its id.

## Run locally

PowerShell:

```powershell
$env:VOICEPRINT_MODEL_PATH = "C:\models\wespeaker_en_voxceleb_resnet34_LM.onnx"
$env:ASPNETCORE_URLS = "http://localhost:8090"
dotnet run --project server/voiceprint/Voiceprint.Service -c Release
```

bash:

```bash
VOICEPRINT_MODEL_PATH=~/models/wespeaker_en_voxceleb_resnet34_LM.onnx ASPNETCORE_URLS=http://localhost:8090 \
  dotnet run --project server/voiceprint/Voiceprint.Service -c Release
```

Check it with `curl http://localhost:8090/healthz`.

## Run in Docker

Build from this folder. The image uses `mcr.microsoft.com/dotnet/aspnet:10.0` and runs as a non-root
user. The model isn't baked in; mount it at `/models`.

```bash
mkdir -p models && cp /path/to/wespeaker_en_voxceleb_resnet34_LM.onnx models/
echo "VOICEPRINT_API_KEY=$(openssl rand -hex 32)" > .env
docker compose up -d --build
```

`compose.yaml` reads the key from `.env` (git ignores it) and mounts `./models` read-only. It also
runs the container with a read-only root filesystem. To use a different model, set
`VOICEPRINT_MODEL=<file name without .onnx>`. On arm64 hosts, add `--build-arg RID=linux-arm64`.
The image has no HEALTHCHECK because it contains neither curl nor wget. Probe `GET /healthz` from
outside instead.

## LiteLLM

Register the service as an embeddings model. The id after `openai/` is passed through as `model`,
and the service ignores it:

```yaml
model_list:
  - model_name: speaker-embed-local
    litellm_params:
      model: openai/wespeaker_en_voxceleb_resnet34_LM
      api_base: http://<voiceprint-host>:8090/v1
      api_key: os.environ/VOICEPRINT_API_KEY
    model_info:
      mode: embedding
```

The recorder sends at most about 1 MB of clips per request, which stays under common proxy upload
limits. The service's 8 MB limit is a ceiling, not a target.

## Privacy

Clips are recordings of people's voices, so treat them like the transcripts. The service is
stateless, but **the gateway must not keep them either.** Check these LiteLLM settings:

- **Spend logs.** `general_settings: store_prompts_in_spend_logs` stores request and response bodies
  in the spend-logs table. By default LiteLLM doesn't store them, so leave it unset or `false`. The
  same switch is under *Logs → Settings* in the admin UI, and the UI value overrides the config, so
  check it there too. To keep no per-request rows at all, set `general_settings:
  disable_spend_logs: true` (you lose per-request usage rows).
  [docs.litellm.ai/docs/proxy/ui_logs](https://docs.litellm.ai/docs/proxy/ui_logs)
- **Logging callbacks** (Langfuse, OpenTelemetry and others). Set `litellm_settings:
  turn_off_message_logging: true` to keep messages and responses, here the audio and the vectors,
  out of callbacks. Request metadata such as spend is still logged. For OpenTelemetry, also set
  `callback_settings: otel: message_logging: False`. These settings apply to the whole proxy; the
  docs don't describe a per-model switch. A client can also ask for redaction per request with the
  header `x-litellm-enable-message-redaction: true` (beta). Don't give the recorder's keys
  `allow_client_message_redaction_opt_out`.
  [docs.litellm.ai/docs/proxy/logging](https://docs.litellm.ai/docs/proxy/logging)
- **Error logs.** `general_settings: disable_error_logs: true` keeps failed requests out of the
  database as well. The service's own error messages never quote audio.
  [docs.litellm.ai/docs/proxy/config_settings](https://docs.litellm.ai/docs/proxy/config_settings)

The docs don't say whether message redaction covers an embeddings `input`. Before you rely on it,
send a test clip and look at the stored spend-log row and at your callback.

## Tests

```bash
dotnet test server/voiceprint/Voiceprint.Service.Tests
```

The tests need no model. To also run the real-model test, set `VOICEPRINT_TEST_MODEL` to an `.onnx`
file.
