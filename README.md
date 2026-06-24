# SfGarnet — Microsoft Garnet on Service Fabric

`SfGarnet` is a Service Fabric application that hosts **[Microsoft Garnet](https://github.com/microsoft/garnet)**
(a high-performance, RESP/Redis-compatible cache-store) as a first-class Service Fabric
workload. A **single application type** serves two topologies, selected at deploy time:

| Topology       | Service Fabric cluster        | Garnet layout                                     | Use case                     |
| -------------- | ----------------------------- | ------------------------------------------------- | ---------------------------- |
| **Standalone** | 1 node (on-premise)           | One Garnet node on `:6379`                        | Dev box, edge, small on-prem |
| **Cluster**    | N nodes (on-premise or cloud) | One Garnet node per SF node, sharded + replicated | HA / scale-out cache         |

Clients talk plain RESP — any Redis client library works.

* Root namespace: `SfGarnet`
* Application name: `fabric:/SfGarnet`
* Target framework: `net8.0` (win-x64)

## Architecture

Garnet ships an embeddable server (`GarnetServer`) and a **passive** cluster mode: it
implements the Redis Cluster wire protocol (slots, `MOVED`/`ASK`, gossip, replication)
but it does **not** elect leaders, assign slots, or trigger failover on its own. Those
decisions need an external control plane — and Service Fabric is exactly that fabric.

The application therefore has **two stateless services**:

```
                         fabric:/SfGarnet
        ┌───────────────────────────┴────────────────────────────┐
        │                                                        │
  GarnetService (data plane)                        ClusterManager (control plane)
  InstanceCount = -1  (one per SF node)             InstanceCount = 1 (singleton)
  ─ embeds GarnetServer on :6379                    ─ discovers GarnetService instances
  ─ cluster bus on :16379 (=6379+10000)             ─ forms / heals the Garnet cluster
  ─ publishes "ip:port" to Naming Service           ─ talks RESP admin commands to nodes
```

* **`GarnetService`** — a stateless service that owns the lifetime of one
  in-process `GarnetServer`. One instance runs per Service Fabric node. Its communication
  listener starts Garnet in `OpenAsync` and disposes it in `CloseAsync`/`Abort`, publishing
  the node's `ip:port` so the control plane and clients can find it.

* **`ClusterManager`** — a stateless **singleton** that runs the control
  loop. In *Standalone* mode it stays idle. In *Cluster* mode it periodically reconciles
  the Garnet topology with the live set of GarnetService instances: it forms a new
  cluster, introduces newly added nodes, and forces a replica to take over from a failing
  primary. It uses a tiny built-in RESP client (no Redis library) so admin commands are
  never second-guessed by client-side cluster auto-discovery.

### Why stateless, not stateful?

Garnet/Tsavorite manage their own storage, checkpoints, AOF and replication. Layering
Service Fabric *stateful* replication on top would replicate the data twice. The services
are therefore **stateless**; Service Fabric provides placement, health, restart and
rolling upgrades, while Garnet owns the data.

## Prerequisites

| Requirement                                                     | Notes                                                                                                                        |
| --------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| Windows                                                         | Service Fabric runs on Windows (and Linux); these docs assume Windows.                                                       |
| .NET 8 SDK                                                      | Builds the two service projects.                                                                                             |
| Microsoft Service Fabric **SDK + Runtime**                      | Provides the packaging targets and the local cluster (OneBox).                                                               |
| Visual Studio 2019/2022 **Azure Service Fabric Tools** workload | Provides the `.sfproj` MSBuild targets used to package the app.                                                              |
| **Windows PowerShell 5.1**                                      | **Required for deployment.** The Service Fabric PowerShell module only loads in Windows PowerShell, *not* PowerShell 7/Core. |

> **Deployment & PowerShell:** the Service Fabric cmdlets (`Connect-ServiceFabricCluster`,
> `Publish-NewServiceFabricApplication`, …) exist only in **Windows PowerShell 5.1**. The
> deploy scripts detect when they are started from PowerShell 7 (Core) and **automatically
> relaunch themselves under Windows PowerShell 5.1**, so you can invoke them from either
> shell. If you run the SF cmdlets by hand, use a *Windows PowerShell* prompt.

A local development cluster can be started from the **Service Fabric Local Cluster Manager**
tray app (choose a 1-node cluster for Standalone, a 5-node cluster for Cluster mode).

## Build

Build just the service binaries (fast inner loop):

```powershell
dotnet build .\src\GarnetService\GarnetService.csproj
dotnet build .\src\ClusterManager\ClusterManager.csproj
```

Build **and package** the full Service Fabric application (requires the SF Tools MSBuild
targets — the deploy scripts do this for you):

```powershell
# Run from a Developer prompt / where MSBuild with the SF workload is available
msbuild .\SfGarnet.sfproj /t:Package /p:Configuration=Release /p:Platform=x64
# → produces the application package under .\pkg\Release\
```

> `dotnet build SfGarnet.sfproj` is **not** supported — `.sfproj` packaging needs full MSBuild
> from Visual Studio (the SF application targets are not part of the .NET SDK).


## Configuration reference

All knobs are **application parameters** (set per environment in
`ApplicationParameters\*.xml`, or with `-ApplicationParameter` at deploy time). They are
mapped onto each service's `Settings.xml` via `ConfigOverrides` in
`ApplicationManifest.xml`.

| Application parameter                                   | Default      | Applies to     | Meaning                                                              |
| ------------------------------------------------------- | ------------ | -------------- | ------------------------------------------------------------------- |
| `SfGarnet_Mode`                                         | `Standalone` | both           | `Standalone` or `Cluster`. Drives Garnet `--cluster` and the control loop. |
| `GarnetService_InstanceCount`                           | `1`          | GarnetService  | `1` for Standalone; `-1` (one per node) for Cluster.                |
| `GarnetService_MemorySize`                              | `1g`         | GarnetService  | Garnet main-log memory (`--memory`), e.g. `256m`, `1g`, `4g`.       |
| `GarnetService_IndexSize`                               | *(empty)*    | GarnetService  | Optional hash-index start size (`--index`), e.g. `64m`.             |
| `GarnetService_EnableAof`                               | `false`      | GarnetService  | Append-only file (`--aof`). **Set `true` in Cluster mode** (needed for replica resync). |
| `GarnetService_CleanClusterConfig`                      | `true`       | GarnetService  | Start nodes with empty cluster config (`--clean-cluster-config`) so the control plane is the single topology authority. |
| `GarnetService_ExtraArgs`                               | *(empty)*    | GarnetService  | Free-form extra Garnet args, space-separated.                       |
| `ClusterManager_ReplicaCount`                           | `1`          | ClusterManager | Replicas attached per primary. `0` = primaries only (no redundancy). |
| `ClusterManager_ReconcileIntervalSeconds`               | `15`         | ClusterManager | Control-loop period (minimum 5).                                    |
| `ClusterManager_FailoverConfirmations`                  | `2`          | ClusterManager | Consecutive cycles a *suspected* (not yet cluster-agreed) primary must stay unreachable before a failover is forced (minimum 1). Agreed `fail` acts immediately. |
| `ClusterManager_FailoverCooldownSeconds`                | `30`         | ClusterManager | Minimum interval between forced failovers of the **same** primary, so an in-flight promotion can settle (minimum 5). |
| `SfGarnet_ClusterUsername` / `SfGarnet_ClusterPassword` | *(empty)*    | both           | Optional credentials for the intra-cluster gossip/replication bus.  |

Ports are fixed: **6379** (RESP, clients) and **16379** (cluster bus = RESP + 10000),
declared in `GarnetService/PackageRoot/ServiceManifest.xml`. One Garnet node per SF node
keeps every node on the same well-known address — clean for real multi-machine clusters.

## How clustering works

On each reconcile cycle the ClusterManager discovers the **ready** GarnetService instances
(via the SF Query API) and reads each node's published `ip:port`. Then:

**Forming a new cluster** (no node reports an existing topology):

1. `CLUSTER SET-CONFIG-EPOCH <i>` — a unique epoch per node while each is still isolated.
2. `CLUSTER ADDSLOTSRANGE <start> <end>` — disjoint contiguous slot ranges to each primary.
3. `CLUSTER MEET …` — introduce every node to node[0]; Garnet's gossip propagates the rest.
4. Wait for convergence — every node's `CLUSTER NODES` lists all peers.
5. `CLUSTER MYID` on primaries, then `CLUSTER REPLICATE <primaryId>` on the replicas.
6. Wait for `cluster_state:ok`.

**Slot & role math** (`N` = live nodes, `R` = `ReplicaCount`):

* `primaryCount = R == 0 ? N : max(1, N / (R + 1))`
* The 16384 slots are split into `primaryCount` near-equal contiguous ranges (remainder
  spread across the first ranges).
* The remaining nodes become replicas, assigned round-robin:
  `primaryIndex = (j - primaryCount) % primaryCount`.

**Healing an existing cluster:**

* Topology is always read from the **most-informed reachable node** (the one whose
  `CLUSTER NODES` lists the most peers), not an arbitrary one, to avoid acting on a stale
  gossip view.
* Any newly added SF instance that isn't in `CLUSTER NODES` yet is introduced with
  `CLUSTER MEET`.
* A primary is failed over only when it still **owns slots** and is in trouble. The control
  plane distinguishes the two `CLUSTER NODES` states:
  * **`fail`** (cluster consensus) — promote immediately.
  * **`fail?`/`pfail`** (a single peer's *suspicion*) — confirmed independently first: the
    control plane must itself be unable to `PING` the primary, and that must hold for
    `FailoverConfirmations` consecutive cycles. A transient GC pause or network blip is thus
    ignored, and a primary that becomes reachable again cancels the pending failover.
* Among healthy replicas, the one with the **highest replication offset**
  (`INFO replication`) is promoted to minimise data loss, via `CLUSTER FAILOVER FORCE`.
* A per-primary **cooldown** (`FailoverCooldownSeconds`) prevents re-issuing a forced
  failover every cycle while a promotion settles; once slots have moved to the promoted
  replica the old primary no longer owns slots and is skipped automatically.

> Note: `FORCE` still requires a master-majority to grant the new config epoch. The control
> plane deliberately does **not** escalate to `CLUSTER FAILOVER TAKEOVER` (which bypasses the
> vote) — that avoids split-brain at the cost of needing a quorum to recover.

Because every step is idempotent, transient SF restarts and gossip races self-heal across
cycles. (Validated end-to-end against real embedded Garnet nodes: 3-primary formation and
1-primary/2-replica formation both converge to `cluster_state:ok` and are safe to re-run.)

## Client usage

Garnet speaks RESP, so use any Redis client.

**Standalone** — connect to the single node:

```csharp
using StackExchange.Redis;
var mux = await ConnectionMultiplexer.ConnectAsync("nodeA:6379");
var db = mux.GetDatabase();
await db.StringSetAsync("greeting", "hello");
```

**Cluster** — seed a cluster-aware client with one or more node addresses; it discovers the
topology and follows `MOVED`/`ASK`:

```csharp
var mux = await ConnectionMultiplexer.ConnectAsync("nodeA:6379,nodeB:6379,nodeC:6379");
var db = mux.GetDatabase();        // routed to the slot owner automatically
await db.StringSetAsync("user:42:name", "Ada");
```

Each node advertises its own IP (`--cluster-announce-ip`), so redirects point clients at
reachable addresses. Front the seed set with DNS or a Service Fabric reverse-proxy / DNS
service if you don't want to hard-code node IPs.

> Multi-key operations and transactions must keep keys in one slot — use Redis **hash tags**
> (e.g. `{user:42}:name`, `{user:42}:email`) to co-locate related keys.

## Operations

* **Scaling out:** add Service Fabric nodes. SF places a new GarnetService instance on each;
  the control loop `MEET`s them within a reconcile interval. (New empty primaries don't take
  slots automatically — see *Limitations* for resharding.)
* **Node loss / failover:** if a primary's node dies, SF marks the instance gone and Garnet
  flags the primary `fail`; the control loop force-promotes a healthy replica.
* **Rolling upgrades:** use Service Fabric monitored upgrades
  (`Start-ServiceFabricApplicationUpgrade`) to update binaries/config node-by-node. Bump the
  service/app versions in the manifests when you change code.
* **Memory sizing:** set `GarnetService_MemorySize` to fit the node SKU; size index with
  `GarnetService_IndexSize` for large key counts.

## Durability & trade-offs

This is a **cache** first. Defaults favour throughput and simple operations:

* **Standalone:** AOF off by default → data is in memory and lost on restart. Set
  `GarnetService_EnableAof = true` (and rely on the per-node checkpoint dir under the SF
  work directory) if you need recovery.
* **Cluster:** AOF on (in the provided cluster parameter files) so replicas can resync and a
  primary can recover its log. Redundancy comes from **replicas**, not from SF state.
* **`--clean-cluster-config`** treats nodes as cattle: a restarted node rejoins empty and the
  control plane reassigns its role. This is the right default for a cache; if you need a node
  to resume its previous identity across restarts, turn it off and persist the work directory.

## Security

* **Intra-cluster auth:** set `SfGarnet_ClusterUsername` / `SfGarnet_ClusterPassword` to require
  credentials on the gossip/replication bus (Garnet `--cluster-username/--cluster-password`).
  Both services receive the same values.
* **Network:** restrict 6379/16379 to trusted clients and the SF subnet (NSG / firewall).
  Garnet also supports TLS and ACL — pass the relevant flags via `GarnetService_ExtraArgs`.
* **Cluster connection:** for secured SF clusters, the deploy/remove scripts accept
  `-ServerCertThumbprint`/`-ClientCertThumbprint` (X509). See `PublishProfiles\Cloud.xml`
  for Azure AD / X509 examples.

## Troubleshooting

| Symptom                                             | Likely cause / fix                                                                 |
| --------------------------------------------------- | ---------------------------------------------------------------------------------- |
| `Connect-ServiceFabricCluster` is not recognized    | You're in PowerShell 7. Use **Windows PowerShell 5.1** (the deploy scripts relaunch automatically). |
| `msbuild` can't find the SF targets                 | Install the VS **Azure Service Fabric Tools** workload; run from a VS Developer prompt. |
| `CLUSTERDOWN Hash slot not served`                  | Cluster still forming. Wait a reconcile interval; check ClusterManager logs and `CLUSTER INFO`. |
| Client gets `MOVED` and can't connect to the target | Client isn't cluster-aware, or node IPs aren't routable from the client. Use a cluster client and ensure announce IPs are reachable. |
| Replicas show `replicas=0` right after forming      | Gossip lag — the primary's view updates after ~5 s. Each node knows its own role immediately. |
| Two nodes on one machine fail to bind 6379          | Fixed ports assume one Garnet per SF node. For multi-node-on-one-box dev, run separate machines/VMs or customize the endpoint ports. |

Logs: both services log to the Service Fabric **diagnostics/EventStore** and stdout; view
per-node logs in Service Fabric Explorer.

## Limitations & notes

* **`.sfproj` packaging** requires Visual Studio's Service Fabric MSBuild targets; it cannot
  be built with the bare .NET SDK. The two C# service projects build with `dotnet build`.
* **Online resharding** (moving slots with `CLUSTER SETSLOT … MIGRATING/IMPORTING` +
  `MIGRATE`) is **not** automated here — new nodes join as replicas/empty primaries but slots
  are not live-migrated. Initial sharding across the nodes present at formation is automatic.
* Fixed ports (6379/16379) assume **one Garnet node per Service Fabric node**.
* The control plane is a **singleton** (`InstanceCount = 1`); Service Fabric restarts it on
  failure. Its actions are idempotent, so a brief outage only delays reconciliation.
