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
`FEDERATION_TEST_REDIS_PASSWORD`. Every fixture connection dials only the numerical IPv4 `127.0.0.1` endpoint on
that port; see "Endpoint containment".

- Neither variable set: every interop test is reported as skipped with "No real Redis behaviour was validated".
  A skipped test is not evidence.
- Only one set, or a port that is not a TCP port: every interop test fails.
- Both set: the fixture authenticates, then refuses to run unless the server is a standalone primary, version
  7.2 or later, on the given port, with an empty keyspace, and the RESP2 and RESP3 connections negotiated their
  protocols. The fixture needs Redis 7.2's cached `TIME` so the staging prefix and adapter script observe the
  same millisecond. Redis 7.0 samples wall time for each call. This is a test prerequisite, not a change to the
  adapter's Redis 7.0 minimum. Use a freshly launched, disposable process for each run.

A real validation run reports 16 interop tests succeeded and 0 skipped. Never commit or log the variable values.

## Endpoint containment

The client's seed list is not an allowlist. StackExchange.Redis 3.4.35 can learn further endpoints from server
topology or redirects while connecting, before the fixture's identity checks run, and it reconnects on its own.
Every fixture connection, inspector and adapter, RESP2 and RESP3, therefore carries a `Tunnel` guard.
`PhysicalConnection.BeginConnectAsync` asks the tunnel which endpoint to dial before it creates any socket. The
guard returns the fixture's own `127.0.0.1:port` only for an `IPEndPoint` that is IPv4 `127.0.0.1` on exactly that
port. For anything else it returns no endpoint, so no socket is created. That includes other ports,
other loopback addresses, IPv6 and IPv4-mapped aliases, and unresolved host names. The connection then fails in
`BeforeAuthenticateAsync`, inside the client's own failure handling, with a fixed message that names no endpoint.
The identity, version, empty-keyspace and protocol checks still run after connecting.

`RedisInteropFixtureTests` runs in the regular test run and needs no Redis process:

- Tested offline: for every combination of protocol and admin mode, the fixture's options dial only the allowed
  endpoint and refuse the aliases above. The authentication hook fails a refused endpoint and passes an allowed
  socket through.
- Tested live, against listeners the test owns: one test uses an IPv4 loopback decoy, and one uses an IPv6 loopback
  decoy when the host can bind it. Each listener is bound to its own ephemeral port, held exclusively on Windows,
  never answers, and stays open until after the multiplexer is disposed.
  - The live options name only the exact numerical endpoints of the allowed listener and the decoy: no host names,
    unbound addresses or ports, mapped forms, or remote addresses. So even without the guard, no dial can reach a
    target the test does not own. The aliases listed above are checked offline only.
  - The client's own counter shows it attempted the decoy at least twice, its first attempt and a retry, and the
    decoy accepted no connection.
  - After the test closes the allowed listener's connections, the client dials it again.
  - No connection ever completes a handshake, so a repeated dial is a connect retry, not an authenticated
    reconnect, and no topology is learned.
  - If IPv6 loopback cannot be bound, the IPv6 test is reported as skipped. Any other bind failure fails the test.
- Inferred from the pinned client source, not tested: when a bridge reconnects after an authenticated connection
  drops, it creates a new `PhysicalConnection` and calls `BeginConnectAsync`, which asks the tunnel for the socket
  endpoint before creating the socket. The tests show that the decoy is attempted and never dialed, but not how the
  client reports the refusal.

## Topology limits

- Validated only against a single standalone primary. Cluster, Sentinel and replicas are not covered.
- No `WAIT`. With asynchronous replication, a failover can lose acknowledged writes and shortens, so a
  shortened entry could reappear with its original expiry.
- Reconnect, backlog and retry behaviour depends on how the host configures the multiplexer.

## Evidence status

The following historical evidence bullets were recorded with StackExchange.Redis 2.13.23:

- The deterministic tests pass.
- Disposable implementer run, through the coordinator's Redis 7.4.12 fixture: 16 interop tests succeeded,
  0 skipped. With the equality guards weakened from `<=` to `<`, 4 failed: the three exact-millisecond tests and
  write refusal at Redis now. The committed source has the guards.
- The coordinator's exact-head run is the authoritative record of real interop for this change.
- With the endpoint guard (R53), `RedisInteropFixtureTests` failed 3 of 3 without the guard and passed 3 of 3 with
  it, including 5 consecutive repeat runs. A disposable implementer run through the same Redis 7.4.12 fixture still
  reported 16 interop tests succeeded and 0 skipped.
- R53 fix round 1 supersedes that control, whose live seeds included host names and unbound endpoints. The live
  tests now name only owned listener endpoints. With a compiled guard-absent control,
  `RedisInteropFixtureTests` failed 4 of 4: the two offline tests because the options had no guard, and both live
  tests because the decoy was dialed, over IPv4 and over IPv6. With the guard, it passed 4 of 4 in 6 consecutive
  runs, with the IPv6 decoy active and not skipped.
- With StackExchange.Redis 3.4.35, a coordinator run against the same disposable, authenticated Redis 7.4.12
  fixture passed the whole solution: 3,344 tests passed and 0 were skipped, including the live interop tests.
