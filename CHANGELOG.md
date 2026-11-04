# Changelog

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
