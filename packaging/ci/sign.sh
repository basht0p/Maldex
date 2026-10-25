#!/usr/bin/env bash
# Sign the release files in <dist-dir>: rpmsign --addsign on the .rpm, then SHA256SUMS, SHA256SUMS.asc (detached, armored)
# and maldex-signing-key.asc (the public key). Needs RPM_GPG_PRIVATE_KEY (armored) and RPM_GPG_PASSPHRASE in the environment,
# plus rpmsign and gpg. The key lives only in a mktemp -d GNUPGHOME, removed on exit. Nothing secret is ever printed.
set -euo pipefail
set +x

DIST="${1:?usage: sign.sh <dist-dir>}"
: "${RPM_GPG_PRIVATE_KEY:?RPM_GPG_PRIVATE_KEY is not set}"
: "${RPM_GPG_PASSPHRASE:?RPM_GPG_PASSPHRASE is not set}"
DIST="$(cd "$DIST" && pwd)"

GNUPGHOME=""
MACROS_BACKUP=""
cleanup() {
  if [ -n "$GNUPGHOME" ]; then gpgconf --kill gpg-agent >/dev/null 2>&1 || true; fi
  if [ -n "$MACROS_BACKUP" ] && [ -f "$MACROS_BACKUP" ]; then cp "$MACROS_BACKUP" "$HOME/.rpmmacros"; else rm -f "$HOME/.rpmmacros"; fi
  if [ -n "$GNUPGHOME" ]; then rm -rf "$GNUPGHOME"; fi
}
trap cleanup EXIT
GNUPGHOME="$(mktemp -d -t maldex-gnupg.XXXXXX)"
export GNUPGHOME
chmod 700 "$GNUPGHOME"
if [ -f "$HOME/.rpmmacros" ]; then MACROS_BACKUP="$GNUPGHOME/rpmmacros.orig"; cp "$HOME/.rpmmacros" "$MACROS_BACKUP"; fi

PASSFILE="$GNUPGHOME/pass"
( umask 077; printf '%s' "$RPM_GPG_PASSPHRASE" > "$PASSFILE" )
GPG=(gpg --batch --yes --pinentry-mode loopback --passphrase-file "$PASSFILE")

set +x
printf '%s\n' "$RPM_GPG_PRIVATE_KEY" | "${GPG[@]}" --import >/dev/null 2>&1 || { echo "sign.sh: could not import the signing key" >&2; exit 1; }
KEYID="$(gpg --batch --list-secret-keys --with-colons | awk -F: '/^fpr:/ {print $10; exit}')"
[ -n "$KEYID" ] || { echo "sign.sh: no secret key after import" >&2; exit 1; }
echo "signing with key ${KEYID: -16}"

cat > "$HOME/.rpmmacros" <<EOF
%_gpg_name $KEYID
%__gpg $(command -v gpg)
%__gpg_sign_cmd %{__gpg} gpg --batch --no-verbose --no-armor --pinentry-mode loopback --passphrase-file $PASSFILE --no-secmem-warning -u "%{_gpg_name}" -sbo %{__signature_filename} --digest-algo sha256 %{__plaintext_filename}
EOF

shopt -s nullglob
rpms=("$DIST"/maldex-*.rpm)
debs=("$DIST"/maldex_*.deb)
if [ "${#rpms[@]}" -ne 1 ] || [ "${#debs[@]}" -ne 1 ]; then echo "sign.sh: expected exactly one .deb and one .rpm in $DIST" >&2; exit 1; fi

rpmsign --addsign "${rpms[0]}" >/dev/null

( cd "$DIST" && sha256sum "$(basename "${debs[0]}")" "$(basename "${rpms[0]}")" > SHA256SUMS )
rm -f "$DIST/SHA256SUMS.asc"
"${GPG[@]}" --detach-sign --armor --local-user "$KEYID" --output "$DIST/SHA256SUMS.asc" "$DIST/SHA256SUMS"
gpg --batch --yes --armor --export "$KEYID" > "$DIST/maldex-signing-key.asc"

# verify what we just made, against the exported public key only
VERIFY="$(mktemp -d)"
chmod 700 "$VERIFY"
GNUPGHOME="$VERIFY" gpg --batch --import "$DIST/maldex-signing-key.asc" >/dev/null 2>&1
GNUPGHOME="$VERIFY" gpg --batch --verify "$DIST/SHA256SUMS.asc" "$DIST/SHA256SUMS" >/dev/null 2>&1 || { echo "sign.sh: SHA256SUMS.asc does not verify" >&2; GNUPGHOME="$VERIFY" gpgconf --kill gpg-agent || true; rm -rf "$VERIFY"; exit 1; }
GNUPGHOME="$VERIFY" gpgconf --kill gpg-agent >/dev/null 2>&1 || true
rm -rf "$VERIFY"
echo "signed: $(basename "${rpms[0]}"), SHA256SUMS.asc, maldex-signing-key.asc"
