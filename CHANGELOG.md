# Changelog

## [1.4.1] - 2026-11-11
### Fixed
- In co-op, an elite's strength sent by a teammate's game is kept within 150 to 450%.

## [1.4.0] - 2026-11-11
### Changed
- An elite foe gets 150 to 450% health and damage, up from 400%.
- An elite foe pays more experience, in proportion to how strong it was made: 14% more at 150%, up to double at 450%. In co-op the whole party gets it.
- Random effects, monsters and items no longer repeat until every other one has come up once.
- Strong random items, those worth 600 gold or more, are rare: exactly one in 25.

## [1.3.0] - 2026-11-07
### Added
- Dialogue polls and boss predictions work in chat, for channels without Affiliate or Partner.
  Viewers vote with the answer's number (`1` or `!1`) or with `!yes` / `!no`, and a viewer's latest vote counts.
  The prediction result is posted in chat. Chat polls take any number of answers, Twitch polls at most five.

### Changed
- One Channel type switch, Affiliate / Partner or Chat only, replaces the "Rewards through" switch and covers rewards, polls and predictions.
  A channel Twitch reports without Affiliate or Partner always uses chat, and the switch is hidden for it.
- The chat command cooldown is per viewer. The monster, bandit, elite and effect rewards keep their own short cooldown for the whole chat.
- Every viewer who uses a command too early is told once in chat when it is ready for them, instead of one reply per 5 s for the whole chat.
- Ambush and raid bandits keep chasing the streamer much further before they give up.

### Fixed
- Logging out or switching the rewards no longer logs an "Unhandled exception: terminating stream" warning.

## [1.2.0] - 2026-11-04
### Added
- A toast in the top right shows which viewer redeemed which reward.
- Chat commands, for channels without channel points. A switch under Options, Modding, Twitch picks channel points or chat commands, never both.
  Viewers type the reward's name, such as `!gold` or `!bandits`. Commands are free but have a cooldown, 120 s by default and adjustable.
  A command on cooldown gets a chat reply saying when it is ready again.
- `!drova` lists the chat commands. The mod also posts the list once when it starts reading chat.
- Raids send a bandit ambush at the streamer, one bandit more per 10 raiders (up to 8), and a toast names the raider.
  The bandits carry 25 gold per raider, up to 5000 in all. This works with channel points and with chat commands.

### Changed
- The login asks for one more permission, to post in chat. Log in again once after updating.
- In co-op, all players need this version, because the spawn message between players changed.
- Needs the Drova Modding API 0.8.1 or later.

### Fixed
- Ambush bandits no longer stand at their spawn point running in place.
- In co-op, teammates see the ambush bandits with their real gear instead of naked.
- A second Drova on the same PC no longer makes Twitch refuse the channel point rewards (429 Too Many Requests). Only the first game runs them.

## [1.1.0] - 2026-11-03
### Changed
- The monster reward picks from non-boss creatures of the player's level tier and the tier below, instead of a fixed few.
- Bandits of one ambush fight as one aggressive group.
- An elite foe chases the player at once.
- While a food buff is active, the random effect reward replaces it with a new food only half as often.
- Needs the Drova Modding API 0.8.0 or later.

### Fixed
- A reward's cooldown holds even when Twitch lets a redemption through during it. That redemption is refunded.

## [1.0.0] - 2026-10-27
- First release.
