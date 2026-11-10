# Vendored API documents

Each `herdr-core-<version>.openrpc.json` here is the OpenRPC document of that herdr-core release,
copied unchanged from `schema/released.openrpc.json` in
[herdrdev/herdr-core](https://github.com/herdrdev/herdr-core) (also printed by `hc schema` and
returned by the `rpc.discover` method).

These files are licensed under the **Apache License 2.0** by the herdr-core authors, not under this
repository's MIT licence. Full text: <https://www.apache.org/licenses/LICENSE-2.0>.

One file per release, never overwritten: add the new release's document beside the old ones, point
`SchemaContractTests.SchemaFile` at it, and fix what the contract tests report.

| File | Source commit | Copied |
|---|---|---|
| `herdr-core-0.1.18.openrpc.json` | `cd12ddd` (after `v0.1.18`) | 2026-11-07 |
| `herdr-core-0.1.19.openrpc.json` | `06793ed` (`v0.1.19`; methods and schemas as 0.1.18) | 2026-11-09 |
| `herdr-core-0.1.20.openrpc.json` | `de97ea5` (`v0.1.20`) | 2026-11-10 |
