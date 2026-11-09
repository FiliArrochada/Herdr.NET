# CLAUDE.md

Guidance for Claude Code in this repo. The workspace `CLAUDE.md` one level up still applies.

## What this is

`HerdrNet`: a hand-maintained .NET client for herdr-core's protocol (README has the overview).
One package, `net10.0`, UI-free, MIT. Layout:

| Path | What |
|---|---|
| `src/HerdrNet/Framing/` | Frame codec (`[channel u32][kind u8][len u32][payload]`) and `ChannelEndpoint` (per-channel pipe, credit, close) |
| `src/HerdrNet/HerdrConnection.cs` | Handshake (plain → framed), request/response correlation, reader loop, teardown |
| `src/HerdrNet/Clients/` (one file per client, plus `HerdrMethods`), `TerminalStream.cs`, `EventSubscription.cs` | Public typed API |
| `src/HerdrNet/TerminalMirror.cs`, `ConnectionSupervisor.cs`, `HerdrServiceCollectionExtensions.cs` | Live state from events; reconnect with backoff and transport fallback; `AddHerdr()` |
| `src/HerdrNet/Model/`, `Serialization/` | DTOs (`Models`, `MethodModels`; `EventData` from the schema README, not checkable by the contract test) and the source-generated JSON context (snake_case policy) |
| `src/HerdrNet/Transport/` | Endpoint resolution, socket/pipe transport, `hc connect` transport, `HcLocator` |
| `src/HerdrNet.Test/` | xUnit v3; `FakeCore` is the scripted in-memory core every protocol test uses |
| `samples/HerdrNet.Probe/` | End-to-end probe against a real core |
| `api/` | Vendored OpenRPC documents, one per release, Apache-2.0 (`api/NOTICE.md`) |

## Rules

- **The protocol comes from herdr-core's documents, never from guesses.** Sources of truth: the
  vendored OpenRPC file and herdr-core's `schema/README.md` (framing, attach, events). Anything not
  in them is marked as unverified in code comments and README Notes until a real core confirms it.
- **Hand-written models, checked by `SchemaContractTests`.** New API release: add its document to
  `api/` (never overwrite), bump `SchemaFile`, fix the failures, bump `<Version>`.
- **Open word sets stay strings** (`state`, `reason`, status words, error kinds, event types) with
  constants alongside: the core adds values without a new generation.
- **The reader loop never waits on a consumer.** Stream bytes go into a pipe with no pause
  threshold; the core's credit bounds them. A channel is registered on the reader loop when its
  reply is parsed, because the core sends the first stream frame right after the reply.
- **Never stop or restart a core, never start one someone stopped, and never kill a terminal the
  code did not start.** `hc connect` starts a stopped core, so the supervisor falls back to it only
  before its first connection or on `RetryNow(startCore: true)`; a connection that only
  `hc connect` or a `HerdrOptions.Command` makes waits in `Disconnected` after a loss for that
  same call (one attempt per call). A command transport's `Description` names its program only:
  arguments can hold key paths or passwords and the description reaches logs. The client offers no
  `server.stop`/`server.restart`; tests use `FakeCore`; live runs use a scratch
  `HC_SESSION` (the probe's default is `herdrnet-probe`).
- **Never log terminal bytes or call parameters**: terminal output and documents may hold secrets.
  Log methods, ids, sizes and timings only.
- Run tests through the workspace's `dt.sh`. A hung test usually means a `ReadAsync` waiting on
  data that never comes: give every await in a test a `WaitAsync(Timeout)`.
