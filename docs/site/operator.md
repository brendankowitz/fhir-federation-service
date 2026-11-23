---
title: Operate a gateway
description: Choose a starting point for deploying, configuring, verifying, or troubleshooting a federation gateway.
sidebar_position: 2
---

# Operate a gateway

**First decide what you are validating.** To prove that the image runs, use the [quickstart](quickstart.md). To serve FHIR traffic, you also need a host that supplies a current capabilities view and a shard transport. Adding shard URLs to the stock image does not supply either. The [host registration](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/src/Ignixa.Federation.Api/Search/PrimarySearchEndpoints.cs) and [cold-host tests](https://github.com/brendankowitz/fhir-federation-service/blob/REVISION/test/Ignixa.Federation.Tests.Integration/Search/PrimarySearchColdHostTests.cs) explain why the distinction matters.

| If you need to… | Read |
| --- | --- |
| Understand physical ownership, request flow and capability generations | [Architecture](architecture.md) |
| Start the image and interpret the first three probe responses | [Quickstart](quickstart.md) |
| Set ingress, roll out replicas and verify a deployment | [Deployment](deployment.md) |
| Understand what must be supplied in code rather than settings | [Host and backend binding](binding.md) |
| Register owners and distinguish placement assertions from certified behavior | [Shard registry](shards.md) |
| Configure token validation and app-role grants | [Caller authentication](authentication.md) |
| Decide whether a query can run in this checkout | [Search support matrix](status.md) |
| Operate continuation tokens and shared keys | [Server paging](server-paging.md) |
| Size a request and investigate budget exhaustion | [Limits](limits.md) |
| Interpret a status code or `OperationOutcome` | [Errors](errors.md) |
| Decide what to measure and what not to log | [Observability](observability.md) |
| Review trust boundaries before admitting traffic | [Security](security.md) |
| Diagnose an unhealthy replica or a failing search | [Troubleshooting](troubleshooting.md) |
| Look up a setting and its default | [Generated configuration reference](configuration.md) |
