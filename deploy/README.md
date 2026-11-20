# deploy

- `Dockerfile` builds the `Ignixa.Federation.Api` host into a non-root ASP.NET image listening on port 8080.
  The build context is the repository root.
- `compose.yaml` runs the federation on `localhost:5201` with two configured shards, `shard-a` and `shard-b`.

Build the image:

```sh
docker build -f deploy/Dockerfile -t ignixa-federation .
```

Run the compose stack:

```sh
docker compose -f deploy/compose.yaml up --build
```

The two shards in `compose.yaml` are placeholders: busybox containers, not FHIR servers. They exist so the shard
registry settings have something to point at. Replace them with real FHIR R4 servers once a backend is wired in.
