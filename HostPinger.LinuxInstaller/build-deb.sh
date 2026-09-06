#!/usr/bin/env bash
#
# Builds the HostPinger .deb for Debian 13. Run it on a Debian machine with the .NET SDK,
# debhelper and dpkg-dev installed:
#
#   ./build-deb.sh                 version taken from the git history via GitVersion
#   VERSION=1.4.0 ./build-deb.sh   version supplied explicitly
#
# The second form is what to use where the git history is not available — a shallow CI checkout,
# or an unpacked source archive — since GitVersion needs the full history to compute a version.
#
# The package lands in build-deb/. There is no counterpart to the RPM's SELinux policy module:
# Debian confines services with AppArmor, which leaves a service having no profile unconfined, so
# nothing extra is needed for the service to run on a stock machine.

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/.." && pwd)
project="$repo_root/HostPinger/HostPinger.csproj"

# Deliberately not build/, which build-rpm.sh empties on every run: the two scripts are run one
# after the other often enough that sharing a directory would mean each quietly deleting the
# other's output.
build_dir="$script_dir/build-deb"
publish_dir="$build_dir/publish"

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

# The same translation build-rpm.sh makes, and for the same reason: Debian reads '~' as "sorts
# before", which is what a SemVer prerelease suffix means. 1.2.0~3 precedes 1.2.0, whereas the
# '-' spelling would start a Debian revision on a package that has none.
deb_version=${VERSION//-/\~}

echo "==> version $VERSION (deb: $deb_version)"

rm -rf "$build_dir"
mkdir -p "$publish_dir"

# Framework-dependent against Microsoft's aspnetcore-runtime package, and RID-specific so the
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
# 1.8MB of a 22MB package and would only draw lintian complaints about a non-development package.
rm -f "$publish_dir/libe_sqlite3.a"

echo "==> staging sources"
stage="$build_dir/$name-$deb_version"
mkdir -p "$stage"
cp -a "$publish_dir" "$stage/publish"
cp -a "$script_dir/debian" "$stage/debian"
cp "$repo_root/LICENSE.txt" "$stage/"

# The unit and the environment file have one canonical copy each, kept in the form the RPM
# installs, and the two Debian differences are made here rather than by keeping a second pair of
# files that would drift. Both edits are checked below: a sed whose pattern no longer matches
# changes nothing and says nothing, and would ship a package whose service reads a file Debian
# never installs.
sed 's|^EnvironmentFile=-/etc/sysconfig/hostpinger$|EnvironmentFile=-/etc/default/hostpinger|' \
    "$script_dir/$name.service" > "$stage/debian/$name.service"

sed -e 's|^# firewalld is a separate step the package deliberately does not take:$|# a firewall is a separate step the package deliberately does not take, for example:|' \
    -e 's|^#   firewall-cmd --permanent --add-port=8080/tcp && firewall-cmd --reload$|#   ufw allow 8080/tcp|' \
    "$script_dir/$name.sysconfig" > "$stage/$name.default"

if grep -q '/etc/sysconfig' "$stage/debian/$name.service"; then
    echo "the unit still points at /etc/sysconfig; the EnvironmentFile edit did not match" >&2
    exit 1
fi
if grep -q 'firewall-cmd' "$stage/$name.default"; then
    echo "the environment file still names firewall-cmd; the firewall note edit did not match" >&2
    exit 1
fi

# Written rather than committed: the changelog is what tells dpkg-buildpackage the version, and
# that comes from the git history on every build. The releases themselves carry the notes, so
# there is nothing here for a hand-maintained entry to say that the tag does not.
cat > "$stage/debian/changelog" <<EOF
$name ($deb_version) trixie; urgency=medium

  * HostPinger $VERSION. Release notes:
    https://github.com/gr584/HostPinger/releases/tag/v$VERSION

 -- Gleb Romanov <gleb.romanov@outlook.com>  $(date -R)
EOF

# Unsigned deliberately, and not only because there is no key on a developer machine: apt
# verifies the repository's signed InRelease rather than individual packages, so the signature
# that matters is applied when the repository index is built, not here.
echo "==> building deb"
(cd "$stage" && dpkg-buildpackage -b -us -uc)

echo
echo "built:"
find "$build_dir" -maxdepth 1 -name '*.deb'
