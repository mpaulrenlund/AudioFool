PS1 Light and PS2 Dark, side by side: light-<name>.png and dark-<name>.png.

Rendered with ThemeLab (tools/themelab) at 100% on 2026-10-15, at the end of the
PS2 Dark build (app session 53). They show the real windows with the theme
resources, drawn off-screen; the main window has ThemeLab's sample library, the
dialogs the real one. Each is drawn over its theme's window colour (#BAB7B3 /
#111216), which shows only around the menus and the tooltip.

  main            the main window, a song playing     (no --window)
  statistics      Library Statistics                  --window stats --w 900
  tags-album      Edit Album Tags                     --window tags --album "Saturn Return" --w 460
  tags-song       Edit Track Tags                     --window tags --track "Slinky" --w 460
  analyze         Analyze                             --window analysis --file <a FLAC>
  lastfm          Last.fm, connected                  --window lastfm --state connected --w 480
  prompt          the New Playlist prompt             --window playlists --w 1720 --h 1080 (its -prompt.png)
  menu-logo       the logo menu's drop-down           --menushot <png>
  menu-context    a context menu, one item hovered    --popupshot <png>
  tooltip         a tooltip                           --popupshot <png> (its -tip.png)

Each with --theme light or --theme dark, and --bg "#BAB7B3" or "#111216".
Not here: the art viewer and art search, which are mostly album covers.
theme-spec.md (and ../theme-spec.md for Light) remains the source of truth for
exact values; design/progress.md has what each session verified.
