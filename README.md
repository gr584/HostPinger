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

Beside it, once a password has been set, is whether this browser is **Locked** or **Unlocked**, and
clicking it opens the overlay that changes that — see *Locking* below. Everything described here
can be read either way; the password only decides who can change something.

### Hosts — `/`

The list of everything being monitored, one row per host, in name order until a heading is clicked,
refreshed every five seconds. Clicking a row opens that host's ping graph.

- **Status** — <span class="badge text-bg-success">**Up**</span> when the last ping was answered, <span class="badge text-bg-warning">**Retrying**</span> once pings start going unanswered, <span class="badge text-bg-danger">**Down**</span> once it has used up the *Retry attempts* it is allowed, <span class="badge bg-body-secondary text-body-secondary">**Waiting…**</span> until the first round covers a newly added host, and <span class="badge text-bg-secondary">**Paused**</span> for a host that is not being pinged at all. Hovering **Retrying** says how many pings it has missed so far, and how many make it down.
- **Last ping** — the round trip in milliseconds, or *no reply*, with how long ago the attempt was
  made.
- **Last downtime** — when the host was last seen up before it stopped answering, and how long it
  stayed unanswered. An ongoing outage keeps counting. A run of missed pings the host recovered
  from inside its *Retry attempts* is not an outage and is not reported, so a host that is
  retrying goes on showing whatever outage it last had.
- **Sorting** — clicking a column heading orders the table by it, and clicking that heading again
  turns the order round. **Name** and **Address** read A to Z first; the three columns that say how
  a host is doing lead with the worst of what they show — down before up, no reply before a slow
  reply, the most recent outage first. Hosts with nothing in the column being sorted on stay at the
  end whichever way it is read, and hosts that tie fall back to their names. The order is kept in
  the address rather than in the browser, so it survives opening a host's graph and coming back,
  and a table sorted to show what is wrong can be sent to someone as a link.
- **Add, edit and delete** — a host is a name and an address, either an IP address or a name to
  resolve. Addresses are unique, and a delete asks for confirmation because it takes the host's
  recorded history with it. Offered only while the browser is unlocked, as below.
- **Pause and resume** — the toggle stops a host being pinged without deleting it, so its history
  survives planned maintenance.

### Graph — `/graph`

Round-trip time plotted against time, for up to eight hosts at once. It reloads every five seconds
and resizes with the window.

- **Time windows** — 15 minutes, 1 hour, 6 hours, 24 hours or 7 days. Long windows are averaged
  down to one point per pixel of plot width, so a week of history draws as quickly as a quarter of
  an hour of it, at whatever detail the display can actually show. Each point covers a fixed
  stretch of the clock rather than one measured back from now, so a refresh only fills in the point
  at the leading edge and drops the one that has fallen off the far end: the rest of the line keeps
  the shape it had and slides along.
- **Zoom to a period** — dragging across the plot redraws the chart over just the stretch of time
  the drag covered, and dragging back to where it started abandons it. Where the drag ends decides
  what it means: run it into the right-hand edge of a live chart and it picks a narrower window
  that still ends at *now*, so the chart goes on following the clock; stop short of the edge, or
  zoom in further on a period already picked out, and it is a fixed period out of the past.
- **Live and paused** — a bar above the chart gives the exact range on screen and says which of
  those two it is. A fixed period reads as <span class="badge text-bg-warning">**Paused**</span>,
  and the five-second refresh stops for as long as one is on screen, so it stays where it was put
  rather than being dragged back to the present. **Resume live** puts back the window that was on
  screen before the zoom and starts it following again, however many times it was zoomed in the
  meantime; picking any of the time windows follows the clock over that window instead.
- **Host picker** — every host by name, searchable by name or address. Where the search leaves a
  single match, Enter selects it and closes the picker.
- **Shareable selection** — the hosts on the chart are part of the URL, so a particular comparison
  can be bookmarked or sent to someone else.
- **Hover readout** — a crosshair with every plotted host's value at that moment, an unanswered
  ping reading as *down*.
- **Legend** — the latest value per host, and a click hides or shows a series, which also rescales
  the axis to what is left.
- **Gaps are drawn as gaps** — the line breaks at an unanswered ping and marks it with a cross
  along the baseline, rather than bridging the outage. It breaks the same way across a stretch
  where nothing was recorded at all, so a stopped service does not read as a flat, healthy line.
- **Stable colours** — each host keeps its colour as the selection changes, and as the picker is
  searched or another host is added, from a palette chosen to stay distinguishable with colour
  vision deficiency. A colour follows the host rather than its place in the list, which is why the
  picker is read in name order but coloured in the order the hosts were added.

### Resolver errors — `/resolver-errors`

The addresses that could not be turned into an IP address, one row each, refreshed every five
seconds. A round that cannot resolve a host never asks it anything, so the failure is recorded
here rather than as a missed ping — a name that does not resolve is not the same as a host that is
down, and this is where the difference can be read without going through the log.

- **One row per address**, the most recently failed at the top, however many rounds it has failed
  in. Every failure is stored; the row is what they add up to.
- **Reason** — <span class="badge text-bg-secondary">**Timed out**</span> when the lookup did not
  come back inside the *Resolve timeout*, which usually says more about the resolver than about
  the name, <span class="badge text-bg-warning">**No addresses**</span> when it came back with
  nothing in it, and <span class="badge text-bg-warning">**Lookup failed**</span> when it failed
  outright — an unknown name, or no resolver able to answer. What is shown is the most recent
  failure's reason, and hovering it explains what it means.
- **Last 24 h**, **Last 7 d** and **Last 30 d** — how many failures fall inside each window,
  counted back from now. They are what separate an address failing every round from one that
  failed once last week; an address whose failures have aged out of the narrower windows is still
  listed, with a dash in each of them.
- **Kept for 30 days** — failures older than that are deleted on the next ping round, so the
  widest column is the whole of what is stored and an address stops being listed once its last
  failure has aged out. This happens whatever the maximum database size is set to, including the
  0 that turns size pruning off: it is about there being nothing left to read rather than about
  disk space.
- **The address rather than the host** — the rows belong to the address that was looked up, so
  they survive a host being re-pointed or deleted. The host's name is shown beside it for as long
  as one still carries that address.

### Configuration — `/configuration`

The settings that can be changed while the service is running. Saving writes them to an overlay
file beside the database and they take effect from the next ping round — no restart, and nothing
to edit on disk. They can be read whether or not the browser is unlocked, and saved only when it
is.

- **Ping interval** — how often every enabled host is pinged. Default 30 seconds.
- **Timeout** — how long to wait for a reply before recording the host as down. Default 5 seconds.
- **Resolve timeout** — how long to wait for a host name to become an address. Default 3 seconds.
  Separate from the reply timeout, which does not cover resolution, so a name that hangs cannot
  set the pace of the round on its own. A name that does not resolve in time is skipped for that
  round rather than recorded as down, and the failure is listed on the *Resolver errors* page.
- **Retry attempts** — how many times a host that misses a ping is retried before it counts as
  down. Default 3, and never less than 0. A host reads as *Retrying* rather than *Down* while it
  has attempts left, and a run of misses it recovers from inside them is not reported as downtime
  at all, so a dropped packet or a moment of congestion does not read as an outage; 0 allows no
  retries and makes the first missed ping count, as it did before this setting existed. It
  changes what is reported rather than what is recorded — every attempt is stored either way, and
  changing it re-reads the whole history rather than only applying from here on.
- **Maximum database size** — the oldest recorded history is pruned once the file grows past this.
  Default 100 MB; 0 disables pruning and lets the file grow without bound, apart from the resolver
  errors, which are dropped at 30 days either way.
- **Capacity estimate** — the current file size, how fast it is growing at the present host count
  and interval, and roughly how much history fits inside the limit at that rate.
- **Security** — whether a password is set, and the way to `/password` to set, change or remove
  one.

### Locking

A single password, and no accounts. It covers everything that changes something: adding, editing,
deleting, pausing and resuming hosts, and saving any setting. Everything else — the host list, the
graph, the settings as they stand, this page, `/health` — is readable by anyone who can reach the
service, locked or not.

- **Unlocking happens in an overlay**, over whatever page is open, the way adding or editing a host
  does. The **Locked** button in the top right opens it, as does the *Unlock* offered wherever a
  page has had to disable something; **Unlocked** opens the same overlay to lock again. Escape or a
  click outside closes it without doing anything.
- **No password is set by default**, and nothing is locked until one is. A HostPinger that nobody
  has given a password behaves exactly as it did before the feature existed, and says so on the
  Configuration page.
- **Unlocking is per browser and lasts until that browser is closed.** It is not remembered any
  longer than that, and nothing is remembered about who unlocked it — there is nobody to remember.
  Locking applies from the next page each open window loads, so a window left sitting on the host
  list keeps what it had until it is used again.
- **Changing the password locks every browser again**, including the one that changed it, which is
  signed straight back in under the new one. Removing it unlocks everything for everyone.
- **Wrong passwords are made to wait.** The first three cost nothing, so mistyping a password from
  memory is free. Each one after that doubles the wait before another guess from the same address
  is looked at — five seconds, then ten, then twenty, and so on up to an hour, where it stops. That
  turns an unattended run through a word list into a couple of dozen guesses a day. The right
  password is turned away along with the wrong ones while a wait stands; getting it right once the
  wait is over clears the tally, as does leaving the password alone for an hour afterwards. The
  change and removal forms count against the same tally, so neither is a way round it, and an empty
  box is never counted as a guess. Nothing is written to disk, so restarting the service forgets
  every tally.
- **The overlay counts the wait down**, second by second, and holds its **Unlock** button back until
  it has run out — so a wait is something to watch rather than something to keep trying at. Closing
  the overlay and opening it again during one picks the countdown up where it is. The `/unlock` page
  reached directly gives the same wait as a number, but does not count it down: it has no live
  connection to the service, which is what lets it write the cookie an unlock is made of.
- **The wait is per address**, so that somebody guessing cannot shut everybody else out of their own
  monitoring. Behind a reverse proxy every visitor arrives from the same address, and there it is in
  practice one wait for the whole service. A whole IPv6 /64 counts as one address, since a single
  machine there usually has billions to guess from.
- **Every attempt is logged.** A wrong password — on the unlock page, or as the current password
  when changing or removing one — is a warning naming the address it came from, and unlocking,
  setting, changing and removing are recorded beside them, so a run of failures can be read
  against whether any of them worked. A run long enough to earn a wait says so on its own line,
  once per wait rather than once per attempt, so nobody can fill the journal by guessing. The
  address is the one the connection was opened from, which behind a reverse proxy is the proxy: no
  forwarded headers are trusted, because a tally kept against a header anyone can set is no tally
  at all. Passwords themselves are never written to the log.
- **The password is stored hashed** — PBKDF2 over a random salt — in the settings overlay beside
  the database, and only ever compared against. There is nothing to read back out of it, which
  also means a forgotten one cannot be recovered: delete the `Security` section from
  `usersettings.json` and the service unlocks itself within seconds, without a restart.
- **It travels in the clear over plain HTTP**, which is how the service is normally reached. On a
  network where that matters, put something that terminates TLS in front of it. The password stops
  a passer-by changing the monitoring; it is not a defence against someone watching the wire.

## How the monitoring works

- Every enabled host is pinged once per interval, all of them in parallel, and each attempt is
  stored with its timestamp and either a round-trip time or nothing at all.
- Each address is resolved before it is pinged, under its own timeout, and the ping goes to the
  resolved address so nothing is looked up twice.
- A ping that reaches a known address but cannot be completed — a silent host, or a socket the
  operating system refuses — is recorded as unanswered rather than abandoning the round, so one
  bad host cannot cost every other host its data point.
- A name that will not resolve is different, and records no ping attempt at all: nothing was asked
  of the host, so there is nothing to store about its reachability, and storing a missed ping
  would show up later as an outage invented out of a name that does not resolve. What is recorded
  instead is the failed lookup itself — the address, the moment, and which way it failed — against
  the address rather than the host, which is what the *Resolver errors* page reads. The host is
  tried again the next round. Either failure is logged at most once every 15 minutes per address.
- Downtime is measured between the answered pings on either side of an outage, from the last
  moment the host was known reachable to the moment it answered again. That is deliberately the
  widest window the recorded attempts support: an outage that began while the service itself was
  stopped is reported from when the host was last seen up, rather than from when monitoring
  resumed.
- An outage is a run of missed pings that outlasted the *Retry attempts* allowed; shorter runs are
  passed over, back to the last run that did. The retries are inside the outage once one is
  reported, because they are time the host was not answering — the setting decides whether an
  outage is reported, not when it started.
- Sending an ICMP echo is privileged, and a refused socket looks exactly like an unreachable host
  once the ping has failed. The service therefore pings loopback at startup and reports in the log
  whether ICMP is usable at all, so a permissions problem is not mistaken for every host going
  down at once.

## Storage

- One SQLite file holds the hosts, every ping attempt and every failed lookup. Its schema is
  migrated automatically at startup, so an upgrade needs no separate step.
- The settings edited on the Configuration page, and the hashed password if one is set, live in a
  small JSON file beside the database rather than inside it. It holds only what has been changed
  from the defaults, and can be read or repaired with a text editor.
- Pruning deletes the oldest recorded history in batches until the file is back under the limit,
  and the file is vacuumed incrementally as it goes so the space is actually returned rather than
  left as free pages. Ping attempts and resolver errors are trimmed alike, so whichever of them is
  filling the file, the limit holds — an address that never resolves records a failure every round
  for as long as it stays configured.
- Resolver errors are kept for 30 days on top of that, and dropped past it on the next ping round
  whatever the size limit allows. Ping attempts have no such window: they are kept for as long as
  the file has room for them, since the graph and the downtime figures read the whole history.
- Everything is local. Nothing is sent anywhere, and there is no external dependency beyond the
  hosts being pinged.

## Running as a service

- The same build runs as a Windows service named `HostPinger` and as the systemd unit
  `hostpinger`, and starts with the machine either way.
- The database, the settings overlay and the data protection keys live under
  `%ProgramData%\HostPinger` on Windows and `/var/lib/hostpinger` on Linux. Both survive an
  upgrade or an uninstall. The keys are what let an unlocked browser stay unlocked across a
  restart of the service, as well as reconnecting a page that was open when it went down.
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
dotnet test HostPinger.Test            # the library and the service logic
dotnet test HostPinger.UITest          # the web UI, some of it through a real browser
```

`HostPinger.UITest` drives Chrome or Chromium against an instance of the application it starts
itself, on a spare port and with a database in a temporary directory, so a run touches neither the
development data nor an installed service's. It uses whichever browser is already installed rather
than downloading one; where there is none, those tests report why and are ignored instead of
failing.

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
| `HostPinger.Test/` | NUnit tests over the library and the service logic. |
| `HostPinger.UITest/` | NUnit tests over the web UI, driving a browser through Playwright. |
| `HostPinger.WindowsInstaller/` | WiX project producing the MSI. |
| `HostPinger.LinuxInstaller/` | Spec file, systemd unit, SELinux policy module and build script producing the RPMs. |

Verified against Fedora 44 and Windows 11.
