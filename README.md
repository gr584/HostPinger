# HostPinger

[![build](https://github.com/gr584/HostPinger/actions/workflows/build.yml/badge.svg)](https://github.com/gr584/HostPinger/actions/workflows/build.yml)

<!--
  The section between the two markers below is also the application's own About page: the file is
  embedded in the assembly and that section is rendered there, so this is the only copy of it.

  Two things to keep in mind when editing it. It has to stand on its own — a link to another
  heading in this README has nothing to point at once the section is rendered by itself — and the
  inline <span class="badge …"> elements are what draw the real status badges on that page. GitHub
  drops the class and shows the text, so both readings work.
-->
<!-- BEGIN: about -->

Pings a configured list of hosts on a schedule, records every attempt in a local SQLite database,
and serves a web UI over the history it builds up: what is reachable now, how fast it is
answering, and how long it was last unreachable.

It runs as a background service on both Windows and Linux from a single codebase, packaged as an
MSI and an RPM respectively.

## The web UI

Every page carries a theme picker in the top right: **Auto** takes the light or dark setting from
the browser and follows it as it changes, **Light** and **Dark** pin it. The choice is remembered
by the browser that made it rather than by the service, so each machine viewing the same
HostPinger can be set differently.

### Hosts — `/`

The list of everything being monitored, one row per host, refreshed every five seconds. Clicking a
row opens that host's ping graph.

- **Status** — <span class="badge text-bg-success">**Up**</span> when the last ping was answered, <span class="badge text-bg-danger">**Down**</span> when it was not, <span class="badge bg-body-secondary text-body-secondary">**Waiting…**</span> until the first round covers a newly added host, and <span class="badge text-bg-secondary">**Paused**</span> for a host that is not being pinged at all.
- **Last ping** — the round trip in milliseconds, or *no reply*, with how long ago the attempt was
  made.
- **Last downtime** — when the host was last seen up before it stopped answering, and how long it
  stayed unanswered. An ongoing outage keeps counting.
- **Add, edit and delete** — a host is a name and an address, either an IP address or a name to
  resolve. Addresses are unique, and a delete asks for confirmation because it takes the host's
  recorded history with it.
- **Pause and resume** — the toggle stops a host being pinged without deleting it, so its history
  survives planned maintenance.

### Graph — `/graph`

Round-trip time plotted against time, for up to eight hosts at once. It reloads every five seconds
and resizes with the window.

- **Time windows** — 15 minutes, 1 hour, 6 hours, 24 hours or 7 days. Long windows are averaged
  down to at most 600 points per host, so a week of history draws as quickly as a quarter of an
  hour of it.
- **Host picker** — searchable by name or address. Where the search leaves a single match, Enter
  selects it and closes the picker.
- **Shareable selection** — the hosts on the chart are part of the URL, so a particular comparison
  can be bookmarked or sent to someone else.
- **Hover readout** — a crosshair with every plotted host's value at that moment, an unanswered
  ping reading as *down*.
- **Legend** — the latest value per host, and a click hides or shows a series, which also rescales
  the axis to what is left.
- **Gaps are drawn as gaps** — the line breaks at an unanswered ping and marks it with a cross
  along the baseline, rather than bridging the outage. It breaks the same way across a stretch
  where nothing was recorded at all, so a stopped service does not read as a flat, healthy line.
- **Stable colours** — each host keeps its colour as the selection changes, from a palette chosen
  to stay distinguishable with colour vision deficiency.

### Configuration — `/configuration`

The settings that can be changed while the service is running. Saving writes them to an overlay
file beside the database and they take effect from the next ping round — no restart, and nothing
to edit on disk.

- **Ping interval** — how often every enabled host is pinged. Default 30 seconds.
- **Timeout** — how long to wait for a reply before recording the host as down. Default 5 seconds.
- **Resolve timeout** — how long to wait for a host name to become an address. Default 3 seconds.
  Separate from the reply timeout, which does not cover resolution, so a name that hangs cannot
  set the pace of the round on its own. A name that does not resolve in time is skipped for that
  round rather than recorded as down.
- **Maximum database size** — the oldest attempts are pruned once the file grows past this.
  Default 100 MB; 0 disables pruning and lets the file grow without bound.
- **Capacity estimate** — the current file size, how fast it is growing at the present host count
  and interval, and roughly how much history fits inside the limit at that rate.

## How the monitoring works

- Every enabled host is pinged once per interval, all of them in parallel, and each attempt is
  stored with its timestamp and either a round-trip time or nothing at all.
- Each address is resolved before it is pinged, under its own timeout, and the ping goes to the
  resolved address so nothing is looked up twice.
- A ping that reaches a known address but cannot be completed — a silent host, or a socket the
  operating system refuses — is recorded as unanswered rather than abandoning the round, so one
  bad host cannot cost every other host its data point.
- A name that will not resolve is different, and records nothing at all: nothing was asked of the
  host, so there is nothing to store about it, and storing a missed ping would show up later as an
  outage invented out of a name that does not resolve. The host is tried again the next round.
  Either failure is logged at most once every 15 minutes per address.
- Downtime is measured between the answered pings on either side of an outage, from the last
  moment the host was known reachable to the moment it answered again. That is deliberately the
  widest window the recorded attempts support: an outage that began while the service itself was
  stopped is reported from when the host was last seen up, rather than from when monitoring
  resumed.
- Sending an ICMP echo is privileged, and a refused socket looks exactly like an unreachable host
  once the ping has failed. The service therefore pings loopback at startup and reports in the log
  whether ICMP is usable at all, so a permissions problem is not mistaken for every host going
  down at once.

## Storage

- One SQLite file holds the hosts and every ping attempt. Its schema is migrated automatically at
  startup, so an upgrade needs no separate step.
- Pruning deletes the oldest attempts in batches until the file is back under the limit, and the
  file is vacuumed incrementally as it goes so the space is actually returned rather than left as
  free pages.
- Everything is local. Nothing is sent anywhere, and there is no external dependency beyond the
  hosts being pinged.

## Running as a service

- The same build runs as a Windows service named `HostPinger` and as the systemd unit
  `hostpinger`, and starts with the machine either way.
- The database, the settings overlay and the data protection keys live under
  `%ProgramData%\HostPinger` on Windows and `/var/lib/hostpinger` on Linux. Both survive an
  upgrade or an uninstall.
- `/health` answers for liveness probes. It deliberately touches nothing, so a pruning pass
  holding the database busy is not read as an unhealthy service.
- Timestamps are rendered on the server, so the time zone the service runs in is what everyone
  viewing the UI sees.

<!-- END: about -->

## Installing on Fedora

### From the package repository

Every release is indexed as a dnf repository, which is what makes `dnf upgrade` work afterwards —
a release attached to a tag is a file to fetch, not something dnf can poll.

```bash
sudo dnf config-manager addrepo --from-repofile=https://gr584.github.io/HostPinger/hostpinger.repo
sudo dnf install hostpinger
sudo systemctl enable --now hostpinger
```

`hostpinger-selinux` comes along with it. It carries a policy module that labels the service
launcher so that systemd starts it in a service domain, and on an enforcing machine it is what
makes the service work at all — see [SELinux and the service domain](#selinux-and-the-service-domain).
It is a weak dependency rather than a hard one, so a machine with SELinux disabled can remove it
without taking the application with it.

On dnf4 the first line is `sudo dnf config-manager --add-repo <same url>`. From then on the package
moves with the rest of the system:

```bash
sudo dnf upgrade hostpinger
```

The packages are not signed, so the repository sets `gpgcheck=0` and what protects the download is
Pages being HTTPS. x86_64 only — the spec sets `ExclusiveArch`, since the tarball holds a linux-x64
publish.

Then carry on from [Install and run](#install-and-run) for the parts the package deliberately
leaves to you, the firewall among them.

### Build the package

Needs the .NET SDK, the RPM build tooling and the SELinux policy sources. On the build machine:

```bash
sudo dnf install -y dotnet-sdk-10.0 rpm-build systemd-rpm-macros selinux-policy-devel systemd
bash HostPinger.LinuxInstaller/build-rpm.sh
```

`selinux-policy-devel` compiles the policy module and brings make, m4 and checkpolicy with it.
`systemd` is there only to satisfy a build dependency the SELinux macros declare — the pkgconfig
file they ask for ships in that package rather than in `systemd-devel`, which is not what the name
suggests.

The version comes from the git history via GitVersion, so the build needs a full clone rather than
a shallow one. Where that is not available — an unpacked archive, or a CI checkout that has not
fetched the history — supply it directly:

```bash
VERSION=1.4.0 bash HostPinger.LinuxInstaller/build-rpm.sh
```

Two packages come out of it: the application in
`HostPinger.LinuxInstaller/build/rpmbuild/RPMS/x86_64/`, and the policy module beside it in
`RPMS/noarch/`, the module being the same file whatever the architecture.

### Install and run

```bash
sudo dnf install ./HostPinger.LinuxInstaller/build/rpmbuild/RPMS/*/hostpinger*.rpm
sudo systemctl enable --now hostpinger
```

The UI is then on port 8080, on every interface. Check that it came up cleanly:

```bash
systemctl status hostpinger
journalctl -u hostpinger -f
```

The lines worth looking for are `ICMP is available` and `Now listening on`. If ICMP is *not*
available the service says so explicitly and explains what to grant — see
[How ICMP is permitted](#how-icmp-is-permitted).

Fedora does not open the port for you:

```bash
sudo firewall-cmd --permanent --add-port=8080/tcp && sudo firewall-cmd --reload
```

### Where things live

| Path | Contents |
| --- | --- |
| `/usr/lib/hostpinger/` | The application. Replaced wholesale on upgrade. |
| `/var/lib/hostpinger/` | Database, saved settings, data protection keys. Created by systemd; uninstalling never removes it. |
| `/etc/sysconfig/hostpinger` | Port, time zone, runtime diagnostics switch. Survives upgrades. |
| `/usr/lib/systemd/system/hostpinger.service` | The unit. |
| `/usr/share/selinux/packages/targeted/hostpinger.pp.bz2` | The policy module, from `hostpinger-selinux`. Loaded into the policy store on install. |

The package requires `aspnetcore-runtime-10.0` from the Fedora repositories, so the runtime is
patched with the distribution rather than bundled.

## Installing on Windows

Build the MSI, which publishes the application and packages it in one step:

```powershell
dotnet build HostPinger.WindowsInstaller\HostPinger.WindowsInstaller.wixproj -c Release
```

The target machine needs the [ASP.NET Core Runtime 10.0 (x64)](https://dotnet.microsoft.com/download/dotnet/10.0),
for the same reason the RPM depends on `aspnetcore-runtime-10.0`: the application is published
framework-dependent, so the runtime is serviced by the machine rather than frozen into the
installer. The MSI checks for it and stops with a message naming it rather than failing later on,
when it would otherwise show up only as the service refusing to start.

Installing it registers a `HostPinger` service that starts automatically and runs as LocalSystem.
Its database and settings live under `%ProgramData%\HostPinger`.

The UI is then on port 5000, on every interface, as the Linux package puts it on 8080. The
installer sets that through the service's environment rather than leaving Kestrel on its own
default, which would be localhost only — see [Environment configuration](#environment-configuration)
for changing it.

The installer also opens the port in Windows Firewall, in the domain and private profiles, for this
executable only. It leaves the public profile alone: the UI has no authentication, so anyone who
reaches it can add and delete monitored hosts along with their history, and an untrusted network is
not somewhere to answer on. Where that is wanted anyway:

```powershell
Set-NetFirewallRule -DisplayName 'HostPinger web UI' -Profile Domain,Private,Public
```

Adding the rule is the one part of the install allowed to fail quietly, since a machine behind a
third-party firewall, or with the Windows Firewall service disabled, is not a reason to fail the
whole install. If the UI answers locally but not from another machine, that is the thing to check:

```powershell
Get-NetFirewallRule -DisplayName 'HostPinger web UI' | Format-Table DisplayName, Profile, Enabled
New-NetFirewallRule -DisplayName 'HostPinger web UI' -Direction Inbound -Protocol TCP -LocalPort 5000 -Action Allow -Profile Domain,Private
```

## Environment configuration

Ping interval, the two timeouts and the database size limit belong to the running service and are
edited on the Configuration page, as described above. Everything else is environment configuration:

| Variable | Purpose |
| --- | --- |
| `ASPNETCORE_HTTP_PORTS` | Listening port, on every interface. 8080 on Linux, 5000 on Windows. |
| `TZ` | Time zone for displayed timestamps. They are rendered server-side, so this decides what users see. Linux only — Windows takes the system time zone. |
| `Pinger__DatabasePath` | Database location. Defaults to `/var/lib/hostpinger/hostpinger.db` on Linux and `%ProgramData%\HostPinger\hostpinger.db` on Windows. |
| `Pinger__UserSettingsPath` | Settings overlay location. Defaults to sitting beside the database. |
| `DOTNET_EnableDiagnostics` | `0` in the shipped Linux sysconfig, which keeps the runtime from opening a debugger transport and a diagnostics socket a service does not need. See below. |

On Linux they live in `/etc/sysconfig/hostpinger` and take effect on `systemctl restart
hostpinger`. On Windows they live in the service's `Environment` value, which the service control
manager passes to the process, and take effect on `Restart-Service HostPinger`. Setting it replaces
the whole set, so include the port the installer put there:

```powershell
Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\HostPinger -Name Environment `
  -Value @('ASPNETCORE_HTTP_PORTS=5000', 'Pinger__DatabasePath=D:\HostPinger\hostpinger.db')
```

Unlike the sysconfig file, which the RPM leaves alone on upgrade, reinstalling or upgrading the MSI
writes that value back to the default.

Changing the port on either platform leaves the firewall behind: the RPM never opened one, and the
rule the MSI adds names port 5000. `Set-NetFirewallRule -DisplayName 'HostPinger web UI' -LocalPort
<new>` moves it, until the next upgrade puts 5000 back.

## How ICMP is permitted

Sending an ICMP echo is privileged, and the service deliberately does not run as root. The systemd
unit grants it exactly one capability, `CAP_NET_RAW`, and nothing else.

This matters more than it might appear. A refused ICMP socket is indistinguishable from an
unreachable host once the ping has failed — both are recorded as no reply — so a permissions
problem would otherwise look like every monitored host going down at once. The service therefore
pings loopback at startup and reports the result, at error level if it cannot.

Some distributions, Fedora among them, already permit unprivileged ICMP through
`net.ipv4.ping_group_range`. That does not make the capability redundant, because the sysctl governs
the *datagram* ICMP socket and .NET's `Ping` asks for a *raw* one, which is `CAP_NET_RAW` or
nothing. Where the raw socket is refused, .NET falls back to running `/usr/bin/ping` as a subprocess
and reading back what it prints — so on Fedora the pings usually still work, at the cost of a
process per host per round. On RHEL and Debian, which do not open the sysctl, the capability is what
makes the service work at all.

### SELinux and the service domain

Both halves of that — the raw socket and the fallback — depend on which domain the service runs in,
and left alone it is the wrong one. `/usr/lib/hostpinger/HostPinger` is labelled `lib_t`, as
everything under `/usr/lib` is, and systemd transitions into a service domain only for a binary
labelled the way an ordinary service binary is. An unlabelled launcher leaves the unit in `init_t`,
systemd's own domain, which is granted neither the raw socket nor the exec of `/usr/bin/ping`.

That is what `hostpinger-selinux` is for. The module it carries holds no rules at all — its entire
content is one file context:

```
/usr/lib/hostpinger/HostPinger    --    system_u:object_r:bin_t:s0
```

The transition follows from the label, and both denials go with it. On a stock policy the service
then runs as `unconfined_service_t` and is confined by the unit's own hardening rather than by
policy, which is where a third-party service without a policy of its own normally sits. Only the
launcher is labelled: the assemblies and web assets beside it are read and never executed, and
`lib_t` is right for those.

Where the module is missing, the journal says so:

```
SELinux is preventing HostPinger from execute_no_trans access on the file /usr/bin/ping.
```

`execute_no_trans` is permission to run a binary while staying in the domain you are already in.
Fedora's policy expects `/usr/bin/ping` to be entered by transitioning into the `ping_t` domain, and
that transition is not available to a unit running under `NoNewPrivileges=yes`, so the exec is
refused outright rather than redirected. The flag does not stop systemd starting the unit in a
service domain in the first place — plenty of confined services set it — it governs what the service
may transition into afterwards.

The denial is not cosmetic. A refused exec is reported by .NET as a failure to start a process
rather than as a failed ping, which is not the shape of failure the ping path expects, so the effect
is a service that will not stay up rather than one recording hosts as down. It also means the raw
socket was refused first, since a service that gets one never reaches for the binary.

What to check, in the order that answers it quickest:

```bash
rpm -q hostpinger-selinux                    # installed at all?
semodule -l | grep hostpinger                # loaded into the policy store?
matchpathcon /usr/lib/hostpinger/HostPinger  # bin_t, or still lib_t?
ps -eo label,comm | grep HostPinger          # the domain the service is actually in
```

`init_t` in the last of those is the whole problem, and installing the subpackage is the fix:

```bash
sudo dnf install hostpinger-selinux
sudo systemctl restart hostpinger
```

The restart is not redundant. Relabelling runs at the end of the transaction, by which point an
upgrade has already restarted the service — so the process running immediately afterwards is still
the one that started in the old domain. `journalctl -u hostpinger | grep ICMP` then says whether the
startup probe is satisfied, which is the point of the exercise: with the raw socket available there
is no subprocess left to deny.

A machine relabelled by hand before this subpackage existed carries a local rule saying the same
thing. It does no harm, and can be dropped once the module is in place:

```bash
sudo semanage fcontext -d /usr/lib/hostpinger/HostPinger
```

Failing all of that, allow the access as it stands — which is what the denial message itself
suggests:

```bash
sudo dnf install -y policycoreutils-devel
sudo ausearch -c HostPinger --raw | audit2allow -M hostpinger-local
sudo semodule -X 300 -i hostpinger-local.pp
```

Read the generated `hostpinger-local.te` before installing it. audit2allow writes a rule for every
denial it is handed, so an audit log holding unrelated ones produces a broader module than intended.

## SELinux and the .NET debug pipe

The same domain produces a second denial, unrelated to pinging and stranger-looking than it is:

```
SELinux is preventing .NET DebugPipe from read access on the fifo_file clr-debug-pipe-217546-26891892-in.
```

`.NET DebugPipe` is a thread inside the runtime rather than anything HostPinger runs, and the number
in the pipe's name is the pid of the process being denied. Every .NET process on Linux creates a
pair of FIFOs and a diagnostics socket as it starts, then waits on one of the FIFOs for a debugger
to attach. `init_t` is granted nothing on a `fifo_file`, so the runtime is refused the pipe it
created a moment earlier — on the unit's own tmpfs, which is what `PrivateTmp=yes` provides.

Unlike the exec denial this one is cosmetic: the thread is a background listener, and the service
starts, pings and serves without it. Its value is as evidence — it means the service is running in
`init_t`, so `hostpinger-selinux` is missing or was never loaded, and it recurs on every restart
until that is dealt with.

The service has no use for the transport in any case, so `/etc/sysconfig/hostpinger` ships with:

```
DOTNET_EnableDiagnostics=0
```

which stops the debugger transport, the diagnostics socket and the profiler being created at all.
Nothing is given up by it — `PrivateTmp=yes` already puts the socket beyond the reach of
`dotnet-counters` and `dotnet-dump` unless they are run inside the service's namespace.

The RPM never overwrites that file, so an installation predating this setting keeps its own copy and
goes on logging the denial. The new default arrives beside it as `/etc/sysconfig/hostpinger.rpmnew`;
adding the line by hand and restarting comes to the same thing.

## Development

```bash
dotnet run --project HostPinger        # http://localhost:5041, database under HostPinger/Data
dotnet test HostPinger.Test
```

The development profile keeps its database and settings inside the working tree, so a local run
never touches an installed service's data.

CI builds and tests on every push. The RPM job runs inside a `fedora:44` container using the .NET
SDK that Fedora packages, rather than on a generic runner with the SDK from Microsoft: the two are
different feature bands and do not accept identical source. Building on the target distribution is
what catches that.

The three build jobs run independently so that a push reports every kind of failure at once.
Pushing a `v*` tag additionally runs a release job, which requires all three to have passed and
attaches the RPM and MSI to a GitHub release — a tag carrying a prerelease suffix is marked as
such rather than becoming the current release.

## Repository layout

| Project | Purpose |
| --- | --- |
| `HostPinger/` | Blazor Server web application and service host. |
| `HostPinger.Core/` | Pinging, storage, pruning, settings. |
| `HostPinger.Test/` | NUnit tests. |
| `HostPinger.WindowsInstaller/` | WiX project producing the MSI. |
| `HostPinger.LinuxInstaller/` | Spec file, systemd unit, SELinux policy module and build script producing the RPMs. |

Verified against Fedora 44 and Windows 11.
