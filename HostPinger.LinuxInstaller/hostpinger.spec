# The payload is managed assemblies plus a small native launcher, so there is no useful debuginfo
# to split out; leaving the default on makes the build fail on the bundled native SQLite library.
%global debug_package %{nil}

# /usr/lib rather than /opt: the application is a distribution-managed, architecture-specific
# private directory, which is what %%{_prefix}/lib is for.
%global appdir %{_prefix}/lib/%{name}

# The bundled SQLite library is private to this application. Without this the package advertises
# libe_sqlite3.so to the whole distribution as though it were a system library.
%global __provides_exclude ^libe_sqlite3\\.so.*$

# The version comes from the git history on every build, so there is no hand-maintained changelog
# for RPM to take a build timestamp from.
%global source_date_epoch_from_changelog 0

Name:           hostpinger
# Supplied by build-rpm.sh, which translates the SemVer that GitVersion produces into the RPM
# convention — a prerelease "1.2.0-3" has to become "1.2.0~3" so that it sorts before the 1.2.0
# release rather than after it.
Version:        %{_hpversion}
Release:        1%{?dist}
Summary:        Pings configured hosts on a schedule and serves the HostPinger web UI
License:        MIT

Source0:        %{name}-%{version}.tar.gz

# Provides %%{_unitdir} and the scriptlet macros below.
BuildRequires:  systemd-rpm-macros

# The application is published framework-dependent, so the runtime comes from the distribution
# and is patched with it. ASP.NET Core, not just dotnet-runtime: this is a web application.
Requires:       aspnetcore-runtime-10.0

# The tarball holds a linux-x64 publish, so this package is not portable to other architectures
# even though most of its content is IL.
ExclusiveArch:  x86_64

%description
HostPinger pings a configured list of hosts on a schedule, records every attempt in a local
SQLite database, and serves a web UI showing current state, response times and downtime history.

It runs as a systemd service under its own unprivileged account, holding only CAP_NET_RAW so that
it can send ICMP echo requests.

%prep
%autosetup

%build
# Nothing to do: build-rpm.sh runs dotnet publish and the tarball contains its output. Building
# here instead would mean a .NET SDK dependency for every rebuild of the package.

%install
mkdir -p %{buildroot}%{appdir}
cp -a publish/. %{buildroot}%{appdir}/

# Normalise modes rather than inheriting whatever the checkout carried: a working tree on a
# Windows filesystem presents every file as 0777, which would otherwise put the executable bit on
# several hundred stylesheets. Only the launcher and the native libraries need it.
find %{buildroot}%{appdir} -type d -exec chmod 0755 {} +
find %{buildroot}%{appdir} -type f -exec chmod 0644 {} +
find %{buildroot}%{appdir} -name '*.so' -exec chmod 0755 {} +
chmod 0755 %{buildroot}%{appdir}/HostPinger

install -Dpm 0644 hostpinger.service %{buildroot}%{_unitdir}/%{name}.service
install -Dpm 0644 hostpinger.sysconfig %{buildroot}%{_sysconfdir}/sysconfig/%{name}

%pre
# The account owns nothing but /var/lib/hostpinger, which systemd creates from StateDirectory= on
# first start. Created here rather than through sysusers.d only to keep the package's moving
# parts to a minimum.
getent group %{name} >/dev/null || groupadd -r %{name}
getent passwd %{name} >/dev/null || \
    useradd -r -g %{name} -d /var/lib/%{name} -s /sbin/nologin \
            -c "HostPinger service" %{name}
exit 0

%post
%systemd_post %{name}.service

%preun
%systemd_preun %{name}.service

%postun
%systemd_postun_with_restart %{name}.service

%files
%license LICENSE.txt
%dir %{appdir}
%{appdir}/*
%{_unitdir}/%{name}.service
%config(noreplace) %{_sysconfdir}/sysconfig/%{name}

# Deliberately not listed: /var/lib/hostpinger. systemd owns its lifetime through StateDirectory=,
# and leaving it out of the package means uninstalling never deletes the recorded history.

%changelog
