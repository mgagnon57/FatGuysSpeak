# Changelog

All notable changes to FatGuysSpeak are documented here. This project adheres to
[Semantic Versioning](https://semver.org/) and the [Keep a Changelog](https://keepachangelog.com/) format.

## [Unreleased]
### Added
- Message Log moderation console: content/author/channel/server/date search, full-history
  paging, multi-select and criteria-based delete, restore, and CSV export.
- Remote desktop control during a screen share (request -> approve -> drive), with a
  server-gated single controller and an instant Stop / panic-key (Ctrl+Alt+Break).
- App versioning: single source-of-truth version with a git/date build stamp, surfaced via
  GET /api/version, the dashboard footer, client Settings, and the landing page; CHANGELOG
  and a local release.ps1.
- Update notifications: the server polls GitHub Releases and shows a dashboard banner when
  behind; the client shows a dismissible "update available" banner via GET /api/update-status.

### Changed
- The per-server default (Lobby) channel can be renamed but not deleted; the dashboard
  "Kick Voice" action now authoritatively removes the user from voice and bumps them to Lobby.

## [1.1.0] - 2026-06-16
### Added
- Security hardening, channel-id reuse fix, and landing-page update.

## [1.0.0] - 2026-06-14
### Added
- Real-time text chat with reactions, replies, threads, attachments, and Group DMs.
- Push-to-talk voice (Opus, 48 kHz), screen share and webcam streaming, Whisper STT.
- Admin dashboard: live metrics, user management, channel permissions, word filter, audit log.
- Sign in with Google (server-side validation + Windows client loopback OAuth).
- Security hardening: SSRF-safe previews/webhooks, per-IP rate limiting, signature-checked
  uploads, strict dashboard CSP, BCrypt timing-safe auth, JWT session blacklisting.
