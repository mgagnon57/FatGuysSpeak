# Changelog

All notable changes to FatGuysSpeak are documented here. This project adheres to
[Semantic Versioning](https://semver.org/) and the [Keep a Changelog](https://keepachangelog.com/) format.

## [Unreleased]
### Added
- Soundboard: upload short .wav clips per server and fire them into your current
  voice channel with a tap; right-click to delete. Clips are decoded and streamed
  through the existing voice pipeline (no extra dependencies, works headless).
- PorkChop games: mention @PorkChop with "trivia", "wyr"/"would you rather",
  "settle this", or "roast battle @a @b" to kick off a game in the channel.
- PorkChop voice talk-back: when you @-mention PorkChop while sitting in a voice
  channel — including by saying it aloud (transcribed) — it now also speaks its
  reply into the channel, not just posts text.
- Custom status: set a free-text "what are you up to" status that shows on your
  profile and as a subtitle under your name in the connected list.
- Move members between channels: admins can right-click anyone in the connected list (or drag
  an occupant) and drop them into another channel; the moved user's client follows automatically.
- Cross-channel presence: the sidebar shows who is in every channel — including channels you are
  not currently in — and updates live as people move around.
- Message Log moderation console: content/author/channel/server/date search, full-history
  paging, multi-select and criteria-based delete, restore, and CSV export.
- Remote desktop control during a screen share (request -> approve -> drive), with a
  server-gated single controller and an instant Stop / panic-key (Ctrl+Alt+Break).
- App versioning: single source-of-truth version with a git/date build stamp, surfaced via
  GET /api/version, the dashboard footer, client Settings, and the landing page; CHANGELOG
  and a local release.ps1.
- Update notifications: the server polls GitHub Releases and shows a dashboard banner when
  behind; the client shows a dismissible "update available" banner via GET /api/update-status.
- PorkChop, an AI sidekick: mention @PorkChop for an answer or advice. It reads the recent
  conversation — and any shared image, via vision — for context, stays on topic across a
  back-and-forth, and posts under the @PorkChop name so it reads as distinct from members.
- Daily recaps: PorkChop summarizes each completed day per channel, with text chat and voice
  transcripts summarized as separate streams. Days are collapsible with a drill-in to the
  original messages, and recaps pre-generate nightly so they open instantly.
- Load earlier messages: page back through older channel history on demand.
- Weekly digest: once a week PorkChop posts a cross-channel "what happened this week" recap
  into the server's main channel.
- Catch me up: one tap for a personal recap of everything you missed since you were last
  online, scoped to the chat source you're viewing.
- In-channel polls: drop a poll with up to ten options; vote, switch, or retract, with live
  result bars, counts, and percentages for the whole channel.
- Desktop notifications: native Windows toast pop-ups for @mentions and DMs when the app is
  minimized or in the background.
- PorkChop roasts (private, opt-in fun): it welcomes people as they join and calls out voice
  channels that have gone silent, with personalized ribbing learned from each person's own
  text and voice chat, what others say about them, and the nicknames they actually go by. It
  can speak the roast aloud in the voice channel via ElevenLabs text-to-speech, rotating
  through multiple voices. Tone and the join/idle behaviours are configurable.

### Changed
- Roles simplified to a flat Member/Admin model — the Moderator role was removed (a finer-grained
  per-channel permission model is planned). A startup migration demotes existing moderators to
  Member and rewrites any Moderator-restricted channel permission to Admin.
- The per-server default (Lobby) channel can be renamed but not deleted; the dashboard
  "Kick Voice" action now authoritatively removes the user from voice and bumps them to Lobby.
- The Voice tab is hidden in the client; voice is still captured and transcribed in the
  background and feeds PorkChop's recaps and roasts.
- Buttons and icon controls now highlight on mouse-over.
- Message rendering now displays Markdown lists and headings (previously dropped silently).
- Release builds produce a fully self-contained server installer with the .NET runtime bundled
  (like the client), so target machines need no .NET install.

### Fixed
- New channels could collide on a recycled id and surface a deleted channel's messages; channel
  ids are now allocated from a monotonic counter that never reuses a value.
- Moving a member between channels intermittently did nothing on the first attempt — the target's
  channel switch now runs on the UI thread, so it lands every time.
- PorkChop's spoken voice no longer stutters — it buffers a short lead on the client instead of
  pacing each frame with an imprecise timer.
- Expanding a past day's chat no longer jumps the view to the bottom.

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
