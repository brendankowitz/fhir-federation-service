# Redis continuation cache: provider validation

`RedisContinuationCache` implements `IContinuationCache` natively over a borrowed StackExchange.Redis
`IDatabase`. This note records the Redis behaviour the adapter relies on, how it is validated, and its limits.
Statements from Redis source are marked as such; statements observed against a real server name the run.

## Requirements

- Redis 7.0 or later. The host must verify this itself: the adapter does not check the version. On an older server
  reads and shortens fail as unavailable (`PEXPIRETIME` is unknown), but writes (`SET ... PXAT`, Redis 6.2+) and
  removals can still succeed.
- A standalone primary. Every command is sent with `CommandFlags.DemandMaster`.
- RESP2 or RESP3. Replies are checked by their exact wire type, so the client's RESP3-to-RESP2 coercions (Boolean
  to Integer, Verbatim to BulkString) are not accepted as confirmations or values.

## Expiry precision

Redis stores key expiry as whole Unix milliseconds. From the Redis 7.4 source:

- `db.c` `keyIsExpired` reports a key expired only when `now > when`. At exact equality the key is still present
  through native expiry alone.
- `server.c` `commandTimeSnapshot` freezes the expiry clock for an execution unit, including a whole script.
- `server.c` `call()` refreshes the cached time only at the top execution level, so `TIME` inside a script reports
  that frozen snapshot and `floor(TIME in ms)` equals the expiry clock. If the cached time were ever refreshed during
  a script, `TIME` could only move ahead, making the guard below earlier than native expiry, never later.
- `expire.c` `checkAlreadyExpired` deletes the key when `when <= now` for `PEXPIREAT`. `SET ... PXAT` does not apply
  that check: a value written at the execution's own millisecond is stored and present.

The `SET ... PXAT` behaviour was also observed on Redis 7.4.12, by the coordinator (one script: `SET PXAT floor(TIME)`
returns `OK`, `PEXPIRETIME - now = 0`, `EXISTS = 1`) and by
`StagingAtTheScriptsOwnMillisecond_LeavesTheKeyPresent`.

Native expiry alone would therefore expose an entry at its expiry millisecond. Every operation runs as one Lua
script that reads `TIME` and applies its own equality guard:

| Operation | Rule (D = `expiry.ToUnixTimeMilliseconds()`, now = `floor(TIME)` in ms) |
| --- | --- |
| Set | Refused before `SET` when `D <= now`. Otherwise `SET key value PXAT D`. |
| Get | Miss when the key is absent or `PEXPIRETIME <= now`. Otherwise the value if `STRLEN` is within the limit. |
| TryShorten | False when the key is absent or `PEXPIRETIME <= now`. Unavailable if `STRLEN` is over the limit. Otherwise `PEXPIREAT` to `now + floor(limit ms)` only if that is earlier. |
| Remove | `DEL key`; the reply must be the integer 0 or 1. |

Consequences:

- An entry is never readable at or after its expiry millisecond. For an expiry with a sub-millisecond fraction, `D`
  rounds down, so the entry ends up to that fraction early, never late.
- An expiry within the current Redis millisecond is refused on write (`ContinuationCacheEntryExpiredException`)
  rather than stored. A remaining TTL of 0 is never treated as live.
- A shorten limit under 1 ms targets `now`, and `PEXPIREAT` at `now` deletes the key. A limit of 1 ms or more never
  targets the equality millisecond. Shortening never extends the expiry, creates a key, or rewrites the value.
- Expiry is computed on the Redis clock. A wall-clock step on the Redis host moves every entry's effective lifetime
  with it.

## Failure semantics

- Redis errors, timeouts, connection failures and replies of any other wire type or value become a payload-free
  `ContinuationCacheUnavailableException`.
- Persistent keys, non-string keys and values over the limit are unavailable to read and shorten, and are left
  untouched, including their expiry. The length is checked server-side with `STRLEN`, so an oversized value is
  never transferred.
- Cancellation is honoured before sending and while awaiting. A command that was already sent may still be
  applied, so cancellation does not prove that a mutation was abandoned.

## Tests

- `RedisContinuationCacheTests` uses a scripted `IDatabase` double, not a Redis server or a RESP fake. It is
  deterministic, needs no process, and runs in the regular solution test run. It checks argument validation,
  cancellation, error translation, the accepted and rejected reply shapes (including RESP3 Boolean, Verbatim and
  null shapes), and that every call targets the caller's key on the primary. It does not execute the Lua scripts;
  what it shows is how the adapter reads replies, not how Redis behaves.
- `RedisContinuationCacheInteropTests` (16 facts) runs against a real Redis process and is opt-in. It covers:
  - verified identity
  - every member over both RESP2 and RESP3, including empty value versus miss and `DEL` counts
  - the length limit at the boundary for read and shorten, refusing an oversized value without sending it, and
    leaving its value and expiry unchanged
  - out-of-policy keys
  - millisecond truncation on write, and write refusal at or before Redis now
  - the exact expiry millisecond for read, shorten and write, each with a one-millisecond-earlier control
  - shorten clamping, including a sub-millisecond limit and a missing key
  - competing clamps from two connections
  - pre-canceled calls

  Exact-millisecond tests use `ScriptPrefixingDatabase`, a test-only wrapper that runs a staging prefix and the
  adapter's own script in one execution. The prefix stages the key, or replaces the requested expiry, at
  `floor(TIME)`, the frozen clock of that very execution, so the test observes the guard at equality rather than
  inferring it from timing. `StagingAtTheScriptsOwnMillisecond_LeavesTheKeyPresent` shows the staged key is
  present, so the equality tests cannot pass on native expiry alone.
- Failure messages in the interop tests carry no client details or stored contents: direct setup and inspection
  commands report only the command name, and values are compared without printing them.

## Running the interop tests

The interop tests read two variables from the test process environment: `FEDERATION_TEST_REDIS_PORT` and
`FEDERATION_TEST_REDIS_PASSWORD`. They connect only to `127.0.0.1`.

- Neither variable set: every interop test is reported as skipped with "No real Redis behaviour was validated".
  A skipped test is not evidence.
- Only one set, or a port that is not a TCP port: every interop test fails.
- Both set: the fixture authenticates, then refuses to run unless the server is a standalone primary, version
  7.0 or later, on the given port, with an empty keyspace, and the RESP2 and RESP3 connections negotiated their
  protocols. Each run therefore needs a freshly launched, disposable process.

A real validation run reports 16 interop tests succeeded and 0 skipped. Never commit or log the variable values.

## Topology limits

- Validated only against a single standalone primary. Cluster, Sentinel and replicas are not covered.
- No `WAIT`. With asynchronous replication, a failover can lose acknowledged writes and shortens, so a
  shortened entry could reappear with its original expiry.
- Reconnect, backlog and retry behaviour depends on how the host configures the multiplexer.

## Evidence status

- The deterministic tests pass.
- Disposable implementer run, through the coordinator's Redis 7.4.12 fixture: 16 interop tests succeeded,
  0 skipped. With the equality guards weakened from `<=` to `<`, 4 failed: the three exact-millisecond tests and
  write refusal at Redis now. The committed source has the guards.
- The coordinator's exact-head run is the authoritative record of real interop for this change.
