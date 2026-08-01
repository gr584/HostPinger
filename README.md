# HostPinger

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

### Hosts — `/`

The list of everything being monitored, one row per host, refreshed every five seconds. Clicking a
row opens that host's ping graph.

- **Status** — <span class="badge text-bg-success">**Up**</span> when the last ping was answered, <span class="badge text-bg-danger">**Down**</span> when it was not, <span class="badge text-bg-light text-muted">**Waiting…**</span> until the first round covers a newly added host, and <span class="badge text-bg-secondary">**Paused**</span> for a host that is not being pinged at all.
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
- **Timeout** — how long to wait for a reply before recording the host as down. Default 5000 ms.
- **Maximum database size** — the oldest attempts are pruned once the file grows past this.
  Default 100 MB; 0 disables pruning and lets the file grow without bound.
- **Capacity estimate** — the current file size, how fast it is growing at the present host count
  and interval, and roughly how much history fits inside the limit at that rate.

## How the monitoring works

- Every enabled host is pinged once per interval, all of them in parallel, and each attempt is
  stored with its timestamp and either a round-trip time or nothing at all.
- A ping that cannot be sent — an address that will not resolve, a socket the operating system
  refuses — is recorded as unanswered rather than abandoning the round, so one bad host cannot
  cost every other host its data point. The log entry is repeated at most once every 15 minutes
  per address.
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

### Build the package

Needs the .NET SDK and the RPM build tooling. On the build machine:

```bash
sudo dnf install -y dotnet-sdk-10.0 rpm-build systemd-rpm-macros
bash HostPinger.LinuxInstaller/build-rpm.sh
```

The version comes from the git history via GitVersion, so the build needs a full clone rather than
a shallow one. Where that is not available — an unpacked archive, or a CI checkout that has not
fetched the history — supply it directly:

```bash
VERSION=1.4.0 bash HostPinger.LinuxInstaller/build-rpm.sh
```

The package lands in `HostPinger.LinuxInstaller/build/rpmbuild/RPMS/x86_64/`.

### Install and run

```bash
sudo dnf install ./HostPinger.LinuxInstaller/build/rpmbuild/RPMS/x86_64/hostpinger-*.rpm
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
| `/etc/sysconfig/hostpinger` | Port and time zone. Survives upgrades. |
| `/usr/lib/systemd/system/hostpinger.service` | The unit. |

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

Ping interval, timeout and the database size limit belong to the running service and are edited on
the Configuration page, as described above. Everything else is environment configuration:

| Variable | Purpose |
| --- | --- |
| `ASPNETCORE_HTTP_PORTS` | Listening port, on every interface. 8080 on Linux, 5000 on Windows. |
| `TZ` | Time zone for displayed timestamps. They are rendered server-side, so this decides what users see. Linux only — Windows takes the system time zone. |
| `Pinger__DatabasePath` | Database location. Defaults to `/var/lib/hostpinger/hostpinger.db` on Linux and `%ProgramData%\HostPinger\hostpinger.db` on Windows. |
| `Pinger__UserSettingsPath` | Settings overlay location. Defaults to sitting beside the database. |

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
`net.ipv4.ping_group_range`, in which case the capability is redundant but harmless. Others,
including RHEL and Debian, do not, and there it is what makes the service work at all.

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
| `HostPinger.LinuxInstaller/` | Spec file, systemd unit and build script producing the RPM. |

Verified against Fedora 44 and Windows 11.
