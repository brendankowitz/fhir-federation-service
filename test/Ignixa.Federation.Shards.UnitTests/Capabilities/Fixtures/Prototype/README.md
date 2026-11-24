# Pinned prototype metadata controls

These are reduced parser inputs from D85's `v85-pinned-captures`, not full
CapabilityStatements or a release certificate. The extracts keep the reported
software, R4 version and statement kind; the server-mode Patient `read` and
`search-type` interactions; and the `_id`, `given`, `name`, and `family` declarations.
The advertised Observation `read`, `search-type`, and `_id` are deliberate
negative controls for the Patient-only ceiling. Other fields, runtime URLs and
unrelated interactions are omitted. The captures and extracts contain no patient
records; no request headers or credentials were copied into the fixtures.

| Extract | Pinned source commit | Observed host DLL SHA-256 | Original uncompressed `/metadata` SHA-256 |
|---|---|---|---|
| `east-metadata.json` | [Ignixa `2f07ac88d655b3e8e87d41c39f6c2b4039b5a8d7`](https://github.com/brendankowitz/ignixa-fhir/commit/2f07ac88d655b3e8e87d41c39f6c2b4039b5a8d7) | `934affc55d4a7b1ad5c1fd59dfc5b98f333d20232044931a713834ab19e92af6` | `b9a4c2a575bef49aee1cf834661649579729ad83daf08627551733edca1932e4` |
| `west-metadata.json` | [Microsoft FHIR Server `2a67597a015b957066822b0d54f2ccc46971c14e`](https://github.com/microsoft/fhir-server/commit/2a67597a015b957066822b0d54f2ccc46971c14e) | `f73114669d4fff04fa69c0f86ae9462e89f0f630af1a985215b574ee54ad30f8` | `7c7d83411a7d42b6c0e89f0bc9c08d9dd7153f7af7559dd014ddff5d944557db` |

Digests come from `measurements.json`; the metadata digests were also recomputed
after decompressing the original captures. They identify those observations,
not the reduced fixtures and not the binary deployed at a remote URL. In
particular, the Microsoft capture's `1.0.0` is an advertised version, not a
source-build identifier. Opting in by naming an exact catalog profile is the
operator's attestation about the backend they deployed; the live handshake
checks the advertised identity and behavior, not a local DLL hash.

D85 measured positive and negative `_id` and `given` searches, including ID
case/prefix misses, case-insensitive given-name prefixes, and intersecting
`_id` with `given`. It also measured `_count=1` replay with GET continuation
and owner-qualified reads that returned 200 and 404 for synthetic IDs. The
catalog is limited to GET-started searches: D85 also observed form POST,
but that method is not admitted. No stable ordering, exact totals, modifiers,
repetition, alternatives, includes, or cross-resource paths are certified.
