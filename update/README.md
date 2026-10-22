# BlockHelm Launcher Remote Update Manifests

This directory stores release notes and the update manifest template for
BlockHelm Launcher releases. The default branch does not store live
`latest.json` manifests.

The `update` directory is not used as the launcher's local runtime configuration.

## Channels

- `release/notes/`: stable release notes.
- `beta/notes/`: beta release notes.
- `latest.template.json`: manifest schema/template used by release workflows.

Live channel manifests are written by the release workflows to the
`update-manifests` branch in both GitHub and Gitee:

- `update/release/latest.json`: stable release channel for normal users.
- `update/beta/latest.json`: beta channel for test builds and early validation.

Update manifests are not signed. The workflows publish the exact same manifest
bytes to both providers and remove legacy `latest.json.sig` files from the
manifest branches. Historical release attachments and notes remain unchanged.

## Release Checklist

When preparing a new version, update the build metadata in
`Launcher.App/Launcher.App.csproj` and add a matching release notes file:

- Release notes:
  - `release/notes/{versionName}.md`, for example `release/notes/1.2.3.md`.
  - `beta/notes/{versionName}.md`, for example `beta/notes/1.2.3-beta.1.md`.
- `LauncherVersionCode` uses `MMmmppbb` semantics and is stored as a JSON number.
  - Release builds use `bb = 99`, for example `0.9.1` -> `90199`.
  - Beta builds use the beta revision for `bb`, for example `0.9.1-beta.1` -> `90101`.

Use `release-notes.template.md` as the starting point for every new release
note. Start with a level-one release title. The following level-two sections
are recommended when applicable, but names and ordering are not restricted:

- `新功能`
- `问题修复`
- `优化改进`
- `兼容性与迁移`
- `已知问题`
- `其他`

Beta notes may use English headings or introduce release-specific sections.
The dialog supports headings, paragraphs, ordered and unordered nested lists,
bold, italic, strikethrough, inline and fenced code, block quotes, horizontal
rules, and HTTP/HTTPS links. Raw HTML is displayed as text, image URLs are not
loaded, and tables are not enabled. Keep each notes file within 65536
characters. The release and beta workflows run
`scripts/validate-release-notes.ps1` before publishing.

The manifest continues to carry the original Markdown in the `releaseNotes`
string so older launchers remain compatible. Newer launchers render the
Markdown directly in the update dialog. Historical flat release notes remain
supported.

The release workflows parse `versionName` from the Git tag and verify
`InformationalVersion`, `LauncherBuildChannel`, and `LauncherVersionCode` from
`Launcher.App/Launcher.App.csproj`. The workflows do not use live `latest.json`
manifests to decide the version being published.

After GitHub and Gitee Releases are created successfully, the workflow writes the
final remote manifest to `update/{channel}/latest.json` on the
`update-manifests` branch in GitHub and Gitee:

- `versionName`: user-facing version, for example `1.2.3`.
- `versionCode`: numeric version used for comparisons, for example `1020399`.
- `publishedAt`: publish time in ISO 8601 format.
- `mandatory`: whether this update is mandatory.
- `minSupportedVersionCode`: local versions below this value must update.
- `releaseNotes`: Markdown copied from the matching notes file.
- `assets`: Windows x64 single-file `exe` package metadata.
- `fileName`: published executable file name.
- `size`: executable size in bytes.
- `sha256`: SHA-256 checksum used for download integrity verification.
- `urls`: Gitee and GitHub download mirrors, tried from lower `priority` to higher `priority`.

Final manifests contain only `gitee` and `github` download URLs. Gitee is the
first update manifest source and the first download mirror; GitHub is the
fallback. The client validates each manifest independently and stops at the
first structurally valid source; any network or validation failure falls
through to the next source. Initial update URLs must use the configured official
GitHub or Gitee HTTPS hosts; redirects may use any HTTPS host, remain limited to
five hops, and can never downgrade to HTTP. Executable downloads must match the
manifest's exact byte size and SHA-256 hash before installation.

Because the manifest is unsigned, its size and SHA-256 values protect against
corrupt or mismatched executable downloads but do not provide an independent
authenticity guarantee if a manifest publishing account or approved host is
compromised.

The Gitee repository is not a source-code mirror; its `update-manifests` branch
only stores `update/release/latest.json`, `update/beta/latest.json`, lightweight
tags, and Release attachments. Both providers receive the same local manifest;
the workflow reads each copy back with cache-busting URLs and fails unless both
copies are byte-identical.
