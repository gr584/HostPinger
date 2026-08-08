#!/usr/bin/env bash
#
# Builds the HostPinger RPMs — the application and its SELinux policy module. Run it on a Linux
# machine with the .NET SDK, rpm-build and selinux-policy-devel installed:
#
#   ./build-rpm.sh                 version taken from the git history via GitVersion
#   VERSION=1.4.0 ./build-rpm.sh   version supplied explicitly
#
# The second form is what to use where the git history is not available — a shallow CI checkout,
# or an unpacked source archive — since GitVersion needs the full history to compute a version.
#
# They land in build/rpmbuild/RPMS/x86_64/ and build/rpmbuild/RPMS/noarch/, the policy module
# being the same file whatever the architecture.

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/.." && pwd)
project="$repo_root/HostPinger/HostPinger.csproj"

build_dir="$script_dir/build"
publish_dir="$build_dir/publish"
topdir="$build_dir/rpmbuild"

name=hostpinger

# GitVersion assigns $(Version) from a target, so the property has to be read after that target
# runs: asking for it off a bare evaluation returns the SDK's 1.0.0 placeholder instead.
#
# -restore is required rather than tidy. GitVersion arrives as a NuGet package, so on a clean
# checkout its targets are not imported until the project has been restored, and -t:GetVersion
# fails with "the target does not exist".
if [[ -z "${VERSION:-}" ]]; then
    VERSION=$(dotnet msbuild "$project" -restore -t:GetVersion -getProperty:Version -p:Configuration=Release \
              | tail -n 1 | tr -d '[:space:]')
fi

if [[ -z "$VERSION" ]]; then
    echo "could not determine a version; set VERSION explicitly" >&2
    exit 1
fi

# RPM forbids '-' in a version and reads '~' as "sorts before", which is what a SemVer prerelease
# suffix means: 1.2.0~3 precedes 1.2.0, whereas 1.2.0.3 would follow it.
rpm_version=${VERSION//-/\~}

echo "==> version $VERSION (rpm: $rpm_version)"

rm -rf "$build_dir"
mkdir -p "$publish_dir" "$topdir"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}

# Framework-dependent against the distribution's aspnetcore-runtime, and RID-specific so the
# publish carries a Linux launcher and only the linux-x64 native libraries.
echo "==> publishing"
dotnet publish "$project" \
    --configuration Release \
    --runtime linux-x64 \
    --no-self-contained \
    --output "$publish_dir" \
    -p:Version="$VERSION"

# The development settings file is an artefact of running from a checkout and has no meaning on an
# installed machine — the Windows installer excludes it for the same reason.
rm -f "$publish_dir/appsettings.Development.json"

# The static archive beside libe_sqlite3.so is for linking against, never loaded at runtime; it is
# 1.8MB of a 22MB package and would only draw rpmlint complaints about a non-devel package.
rm -f "$publish_dir/libe_sqlite3.a"

echo "==> staging sources"
stage="$build_dir/$name-$rpm_version"
mkdir -p "$stage"
cp -a "$publish_dir" "$stage/publish"
cp "$script_dir/hostpinger.service" "$script_dir/hostpinger.sysconfig" \
   "$script_dir/hostpinger.te" "$script_dir/hostpinger.fc" "$stage/"
cp "$repo_root/LICENSE.txt" "$stage/"

tar -czf "$topdir/SOURCES/$name-$rpm_version.tar.gz" -C "$build_dir" "$name-$rpm_version"

echo "==> building rpm"
rpmbuild -bb \
    --define "_topdir $topdir" \
    --define "_hpversion $rpm_version" \
    "$script_dir/$name.spec"

echo
echo "built:"
find "$topdir/RPMS" -name '*.rpm'
