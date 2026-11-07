# TwitchMod

A [MelonLoader](https://melonwiki.xyz) mod for *Drova - Forsaken Kin* that lets Twitch chat play along.

- **Channel point rewards:** viewers spend points on a random item, gold, healing or focus potions, a monster,
  a bandit ambush, an elite foe or a random status effect. Rewards wait while the streamer is in a dialogue,
  cutscene or loading screen, and are refunded if they cannot be applied. A toast shows who redeemed what.
- **Chat only:** for channels without Affiliate or Partner, everything runs over chat. Viewers type
  `!randomitem`, `!gold`, `!healingpotion`, `!focuspotion`, `!monster`, `!bandits`, `!elite` or `!statuseffect`,
  vote on dialogue polls with the answer's number and on boss predictions with `!yes` or `!no`. Each viewer has a
  cooldown, set in the options, and is told once when they try too early.
  `!drova` lists the commands, and the mod posts that list once when it starts.
- **Raids:** a raid sends a bandit group at the streamer, bigger with more raiders, and the bandits carry gold.
- **Dialogue polls:** press the poll key (`.` or left stick press) while answers are shown, and chat votes on
  what to say, in a Twitch poll or in chat.
- **Boss predictions:** a "Will I beat the boss?" prediction opens near a boss and resolves when the fight ends.
- **Co-op:** works with the Drova co-op mod; spawns run on the host.

Channel points, Twitch polls and predictions need an Affiliate or Partner channel; chat only works on any channel.
The Channel type switch in the options picks one, Affiliate / Partner by default. A channel Twitch reports
without Affiliate or Partner always uses chat, and the switch is hidden.

## Install

1. Install MelonLoader and the [Drova Modding API](https://github.com/Drova-Modding/Drova-Modding-API) 0.8.1 or later.
2. Extract `TwitchMod.zip` into the game folder, or install it with Vortex.
3. In game, open Options, Modding, Twitch and log in. The login opens in your browser.

Reward costs, gold amount and poll duration are set in the same menu.

## License

MIT, see [LICENSE](LICENSE). The Twitch SDK is not covered by it, see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
