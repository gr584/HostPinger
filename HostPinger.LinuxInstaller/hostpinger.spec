# The payload is managed assemblies plus a small native launcher, so there is no useful debuginfo
# to split out; leaving the default on makes the build fail on the bundled native SQLite library.
%global debug_package %{nil}

# /usr/lib rather than /opt: the application is a distribution-managed, architecture-specific
# private directory, which is what %%{_prefix}/lib is for.
%global appdir %{_prefix}/lib/%{name}

# The policy module is named for the package and built against the targeted policy, which is the
# one Fedora ships and enables. On a machine running something else the subpackage's files are
# installed and no module is loaded: the scriptlet macros compare SELINUXTYPE before acting.
%global selinuxtype targeted
%global modulename %{name}
%global modulepath %{_datadir}/selinux/packages/%{selinuxtype}/%{modulename}.pp.bz2

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

# Recommends rather than Requires: the module is what makes the service work on a machine running
# SELinux in enforcing mode, so it should arrive by default, but a machine with SELinux disabled
# has no use for it and should be able to remove it without taking the application with it.
Recommends:     %{name}-selinux = %{version}-%{release}

# The tarball holds a linux-x64 publish, so this package is not portable to other architectures
# even though most of its content is IL.
ExclusiveArch:  x86_64

%description
HostPinger pings a configured list of hosts on a schedule, records every attempt in a local
SQLite database, and serves a web UI showing current state, response times and downtime history.

It runs as a systemd service under its own unprivileged account, holding only CAP_NET_RAW so that
it can send ICMP echo requests.

%package selinux
Summary:        SELinux policy module for %{name}
BuildArch:      noarch
Requires:       %{name} = %{version}-%{release}
# The policy floor the module is built against, and the tools its scriptlets call. The _min
# variant deliberately: the fuller %%{selinux_requires} adds policycoreutils-python-utils for
# semanage, and nothing here runs semanage — a file context shipped in a module needs neither the
# local customisation database nor the Python stack that edits it.
%{?selinux_requires_min}

%description selinux
The SELinux policy module for HostPinger.

It labels the service launcher the way an ordinary service binary is labelled, so that systemd
starts the unit in a service domain rather than leaving the process in systemd's own. That domain
is granted neither the raw ICMP socket the service opens nor the ping binary .NET falls back to,
so on an enforcing machine this module is what makes the service work.

%prep
%autosetup

%build
# Nothing to do for the application: build-rpm.sh runs dotnet publish and the tarball contains its
# output. Building here instead would mean a .NET SDK dependency for every rebuild of the package.

# The policy module is the one thing built here. The path in the file context is written in from
# %%{appdir} rather than spelled out a second time, because a rule naming a directory nothing is
# installed to loads without complaint and then silently does nothing.
sed -i 's|@APPDIR@|%{appdir}|' %{modulename}.fc
make -f %{_datadir}/selinux/devel/Makefile %{modulename}.pp
bzip2 -9 %{modulename}.pp

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

install -Dpm 0644 %{modulename}.pp.bz2 %{buildroot}%{modulepath}

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

%pre selinux
# Takes a copy of the file contexts as they stand before this module changes any of them, which is
# what the relabel below compares against to decide what needs restoring. Every macro here has to
# be called on one line: a continuation swallows the argument that follows it, and the scriptlet
# that results loads no module while still exiting successfully.
%selinux_relabel_pre -s %{selinuxtype}

%post selinux
%selinux_modules_install -s %{selinuxtype} %{modulepath}

%postun selinux
# Guarded inside the macro: it removes the module when the last copy is being erased and does
# nothing on the way through an upgrade.
%selinux_modules_uninstall -s %{selinuxtype} %{modulename}

%posttrans selinux
# Relabelling can only follow the module being loaded — until then the context it hands out does
# not exist for a file to be restored to — and %%post is too early for that on a fresh install.
%selinux_relabel_post -s %{selinuxtype}

%files
%license LICENSE.txt
%dir %{appdir}
%{appdir}/*
%{_unitdir}/%{name}.service
%config(noreplace) %{_sysconfdir}/sysconfig/%{name}

# Deliberately not listed: /var/lib/hostpinger. systemd owns its lifetime through StateDirectory=,
# and leaving it out of the package means uninstalling never deletes the recorded history.

%files selinux
%license LICENSE.txt
%{modulepath}
# Owned but not shipped: semodule writes this when the module is loaded and removes it when the
# module goes, so the package claims the path without carrying its contents and rpm -V does not
# report a file it was never going to install.
%ghost %verify(not md5 size mode mtime) %{_sharedstatedir}/selinux/%{selinuxtype}/active/modules/200/%{modulename}

%changelog
