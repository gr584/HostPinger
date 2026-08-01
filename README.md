# HostPinger

Pings a configured list of hosts on a schedule, records every attempt in a local SQLite database,
and serves a web UI showing current state, response times and downtime history.

It runs as a background service on both Windows and Linux from a single codebase, packaged as an
MSI and an RPM respectively.

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

Build the MSI, which publishes the application self-contained and packages it in one step:

```powershell
dotnet build HostPinger.WindowsInstaller\HostPinger.WindowsInstaller.wixproj -c Release
```

Installing it registers a `HostPinger` service that starts automatically and runs as LocalSystem.
Its database and settings live under `%ProgramData%\HostPinger`.

Unlike the Linux package, the Windows service listens on `http://127.0.0.1:5000` — Kestrel's
default — so the UI is reachable only from the machine itself unless `ASPNETCORE_URLS` is set for
the service.

## Configuration

Ping interval, timeout and the database size limit are edited on the **Configuration** page in the
UI. They are written to an overlay file in the data directory and take effect without a restart,
so they are not part of either package.

Everything else is environment configuration — on Linux in `/etc/sysconfig/hostpinger`, applied on
`systemctl restart hostpinger`:

| Variable | Purpose |
| --- | --- |
| `ASPNETCORE_HTTP_PORTS` | Listening port. 8080 on Linux. |
| `TZ` | Time zone for displayed timestamps. They are rendered server-side, so this decides what users see. |
| `Pinger__DatabasePath` | Database location. Defaults to `/var/lib/hostpinger/hostpinger.db` on Linux and `%ProgramData%\HostPinger\hostpinger.db` on Windows. |
| `Pinger__UserSettingsPath` | Settings overlay location. Defaults to sitting beside the database. |

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
