# Redis continuation cache: provider validation

`RedisContinuationCache` implements `IContinuationCache` natively over a borrowed StackExchange.Redis
`IDatabase`. This note records the Redis behaviour the adapter relies on, how it is validated, and its limits.

## Requirements

- Redis 7.0 or later (`PEXPIRETIME`). Older servers answer `ERR`, which the adapter reports as unavailable.
- A standalone primary. Every command is sent with `CommandFlags.DemandMaster`.

## Expiry precision

Redis stores key expiry as whole Unix milliseconds. It decides whether a key has expired against a per-command
time snapshot. Behaviour the design depends on, from the Redis 7.4 source:

- `db.c` `keyIsExpired` reports a key expired only when `now > when`. At exact equality the key is still readable
  through native expiry alone.
- `server.c` `commandTimeSnapshot` freezes `now` at entry to an execution unit, including scripts.
- `expire.c` `checkAlreadyExpired` treats `when <= now` as already expired for `PEXPIREAT` and `SET ... PXAT`.
- `TIME` is derived from `server.ustime`, so `floor(TIME in ms)` is at or after the frozen snapshot.

Native expiry alone would therefore expose an entry at its expiry millisecond. Every operation runs as one Lua
script that reads `TIME` and applies its own equality guard:

| Operation | Rule (D = `expiry.ToUnixTimeMilliseconds()`, now = `TIME` in ms) |
| --- | --- |
| Set | Refused before `SET` when `D <= now`. Otherwise `SET key value PXAT D`. |
| Get | Miss when the key is absent or `PEXPIRETIME <= now`. Otherwise the value, if no longer than the limit. |
| TryShorten | False when the key is absent or `PEXPIRETIME <= now`. Otherwise `PEXPIREAT` to `now + floor(limit ms)` only if that is earlier. |

Consequences:

- An entry is never readable at or after its expiry instant. For an expiry with a sub-millisecond fraction, `D`
  rounds down, so the entry ends up to that fraction early, never late.
- An expiry within the current Redis millisecond is refused on write (`ContinuationCacheEntryExpiredException`)
  rather than stored. A remaining TTL of 0 is never treated as live.
- A shorten limit under 1 ms ends the entry. Shortening never extends the expiry, creates a key, or rewrites the
  value.
- `TIME` can advance during a script while the expiry snapshot stays frozen, so the guard can only be earlier than
  native expiry, never later.
- Expiry is computed on the Redis clock. A wall-clock step on the Redis host moves every entry's effective lifetime
  with it.

## Failure semantics

- Redis errors, timeouts, connection failures and nonconforming replies become a payload-free
  `ContinuationCacheUnavailableException`.
- Persistent keys, non-string keys and values over the limit are unavailable and left untouched. The length limit
  is checked server-side with `STRLEN`, so an oversized value is never transferred.
- Cancellation is honoured before sending and while awaiting. A command that was already sent may still be
  applied, so cancellation does not prove that a mutation was abandoned.

## Tests

- `RedisContinuationCacheTests` uses a scripted `IDatabase` double, not a Redis server or a RESP fake. It is
  deterministic, needs no process, and runs in the regular solution test run. It checks argument validation,
  cancellation, reply and error translation, and that every call targets the caller's key on the primary. It does
  not execute the Lua scripts; their semantics are validated only by the interop tests.
- `RedisContinuationCacheInteropTests` (14 facts) runs against a real Redis process and is opt-in. It covers:
  - verified identity
  - empty value versus miss
  - the length limit at the boundary, and refusing an oversized value without sending it
  - out-of-policy keys
  - millisecond truncation on write
  - write refusal at or before Redis now
  - the expiry-millisecond boundary
  - shorten clamping, including a sub-millisecond limit and a missing key
  - competing clamps from two connections
  - pre-canceled calls

## Running the interop tests

The interop tests read two variables from the test process environment: `FEDERATION_TEST_REDIS_PORT` and
`FEDERATION_TEST_REDIS_PASSWORD`. They connect only to `127.0.0.1`.

- Neither variable set: every interop test is reported as skipped with "No real Redis behaviour was validated".
  A skipped test is not evidence.
- Only one set, or a port that is not a TCP port: every interop test fails.
- Both set: the fixture authenticates, then refuses to run unless the server is a standalone primary, version
  7.0 or later, on the given port, with an empty keyspace. Each run therefore needs a freshly launched,
  disposable process.

A real validation run reports 14 interop tests succeeded and 0 skipped. Never commit or log the variable values.

## Topology limits

- Validated only against a single standalone primary. Cluster, Sentinel and replicas are not covered.
- No `WAIT`. With asynchronous replication, a failover can lose acknowledged writes and shortens, so a
  shortened entry could reappear with its original expiry.
- Reconnect, backlog and retry behaviour depends on how the host configures the multiplexer.

## Evidence status

- The deterministic tests pass.
- The interop tests have not yet been run against a real Redis process in this change. Until a run shows 14
  succeeded and 0 skipped, no real Redis behaviour has been validated. The coordinator intends to run them
  against Redis 7.4.12.
