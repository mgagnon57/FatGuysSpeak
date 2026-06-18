# Changelog

All notable changes to FatGuysSpeak are documented here. This project adheres to
[Semantic Versioning](https://semver.org/) and the [Keep a Changelog](https://keepachangelog.com/) format.

## [Unreleased]

## [1.0.0] - 2026-06-17
### Added
- Real-time text chat with reactions, replies, threads, attachments, and Group DMs.
- Push-to-talk voice (Opus, 48 kHz), screen share and webcam streaming, Whisper STT.
- Remote desktop control during a screen share (request -> approve -> drive), with a
  server-gated single controller and an instant Stop / panic-key.
- Admin dashboard: live metrics, user management, channel permissions, word filter, audit log.
- Message Log moderation console: content/author/channel/date search, full-history paging,
  multi-select and criteria-based delete, restore, and CSV export.
- Per-server default (Lobby) channel that can be renamed but not deleted.
- Sign in with Google (server-side validation + Windows client loopback OAuth).
- Security hardening: SSRF-safe previews/webhooks, per-IP rate limiting, signature-checked
  uploads, strict dashboard CSP, BCrypt timing-safe auth, JWT session blacklisting.
