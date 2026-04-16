%define _binaries_in_noarch_packages_terminate_build 0
%define debug_package %{nil}

Name:           dmart
Version:        %{?version}%{!?version:0.1.0}
Release:        1%{?dist}
Summary:        dmart — Unified Data Platform (REST API server + CLI)
License:        AGPL-3.0
URL:            https://github.com/edraj/csdmart
Source0:        %{name}-%{version}.tar.gz

# No auto-dependency detection — the AOT binary is self-contained
AutoReqProv:    no

BuildRequires:  dotnet-sdk-10.0
BuildRequires:  clang
BuildRequires:  zlib-devel

Requires:       postgresql-server
Requires(pre):  shadow-utils

%description
dmart is a unified data platform providing a REST API backed by PostgreSQL.
This package includes the native AOT server binary, the CLI client tool,
built-in plugin configurations, and a systemd service unit.

%prep
%setup -q

%build
# Build from source if dmart.csproj is present (SRPM rebuild).
# Binary RPM path already has the pre-built binary — skip.
if [ -f dmart.csproj ]; then
    dotnet publish dmart.csproj -r linux-x64 \
        -p:PublishAot=true \
        -p:StripSymbols=true \
        -c Release \
        -o %{_builddir}/%{name}-%{version}/out
fi

%install
# Binary — from out/ (SRPM build) or root (binary RPM)
# SRPM builds put binary in out/, binary RPM has it in root
if [ -f out/dmart ]; then
    install -D -m 0755 out/dmart %{buildroot}/usr/bin/dmart
else
    install -D -m 0755 dmart %{buildroot}/usr/bin/dmart
fi

# Plugin configs
for dir in plugins/*/; do
    name=$(basename "$dir")
    install -D -m 0644 "$dir/config.json" \
        "%{buildroot}/usr/lib/dmart/plugins/$name/config.json"
done

# Config sample
install -D -m 0644 config.env.sample %{buildroot}/usr/share/dmart/config.env.sample

# Systemd unit
install -D -m 0644 dmart.service %{buildroot}/usr/lib/systemd/system/dmart.service

# Shell completions
install -D -m 0644 dmart.bash %{buildroot}/etc/bash_completion.d/dmart
install -D -m 0644 dmart.fish %{buildroot}/usr/share/fish/vendor_completions.d/dmart.fish

# Runtime directories
install -d -m 0755 %{buildroot}/etc/dmart
install -d -m 0755 %{buildroot}/var/lib/dmart
install -d -m 0755 %{buildroot}/var/lib/dmart/spaces
install -d -m 0755 %{buildroot}/var/lib/dmart/custom_plugins

%pre
# Create dmart system user if it doesn't exist
getent group dmart >/dev/null || groupadd -r dmart
getent passwd dmart >/dev/null || \
    useradd -r -g dmart -d /var/lib/dmart -s /sbin/nologin \
    -c "dmart data platform" dmart
exit 0

%post
# Install default config.env if missing
if [ ! -f /etc/dmart/config.env ]; then
    cp /usr/share/dmart/config.env.sample /etc/dmart/config.env
    chmod 0640 /etc/dmart/config.env
    chown root:dmart /etc/dmart/config.env
    echo "Installed default config: /etc/dmart/config.env"
    echo "Edit it with your database credentials and JWT secret, then run:"
    echo "  systemctl enable --now dmart"
fi
# Reload systemd
systemctl daemon-reload >/dev/null 2>&1 || true

%preun
# Stop service on uninstall (not upgrade)
if [ "$1" = "0" ]; then
    systemctl stop dmart >/dev/null 2>&1 || true
    systemctl disable dmart >/dev/null 2>&1 || true
fi

%postun
systemctl daemon-reload >/dev/null 2>&1 || true
# Remove user only on full uninstall
if [ "$1" = "0" ]; then
    userdel dmart >/dev/null 2>&1 || true
    groupdel dmart >/dev/null 2>&1 || true
fi

%files
%attr(0755, root, root) /usr/bin/dmart
/usr/lib/dmart/plugins/
/usr/share/dmart/config.env.sample
/usr/lib/systemd/system/dmart.service
/etc/bash_completion.d/dmart
/usr/share/fish/vendor_completions.d/dmart.fish
%dir %attr(0750, root, dmart) /etc/dmart
%dir %attr(0755, dmart, dmart) /var/lib/dmart
%dir %attr(0755, dmart, dmart) /var/lib/dmart/spaces
%dir %attr(0755, dmart, dmart) /var/lib/dmart/custom_plugins

%changelog
* Wed Apr 15 2026 dmart <admin@dmart.cc> - 0.1.0-1
- Initial RPM package with AOT server binary, CLI client, plugin configs
