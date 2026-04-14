# Bundled Fonts

RAMWatch embeds JetBrainsMono Nerd Font for consistent display across systems.

## Setup

1. Download **JetBrainsMono** from the [nerd-fonts releases](https://github.com/ryanoasis/nerd-fonts/releases).
2. Extract and place these files in this directory:
   - `JetBrainsMonoNerdFont-Regular.ttf`
   - `JetBrainsMonoNerdFont-Bold.ttf`
3. The `.csproj` bundles all `*.ttf` files as embedded resources automatically.

If the font files are not present, the app falls back to Cascadia Mono / Segoe UI.

## License

JetBrains Mono is licensed under the SIL Open Font License 1.1, compatible with GPL-3.0.
