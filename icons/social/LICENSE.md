# Social icons — attribution

Icons saved here as source-of-truth for the brand glyphs rendered in the
KusPus About tab footer. The actual rendering inlines the path data into
MainWindow.xaml (via `<Path>` elements with `Fill="{DynamicResource MutedText}"`)
so they theme-tint cleanly with dark/light mode — these `.svg` files exist
for licensing attribution and as the canonical source if the inline path
data ever needs to be re-synced.

| File         | Source                                                         | License |
|--------------|----------------------------------------------------------------|---------|
| linkedin.svg | https://simpleicons.org/?q=linkedin (simple-icons npm package) | CC0-1.0 |
| x.svg        | https://simpleicons.org/?q=x        (simple-icons npm package) | CC0-1.0 |
| github.svg   | https://simpleicons.org/?q=github   (simple-icons npm package) | CC0-1.0 |
| globe.svg    | https://lucide.dev/icons/globe      (lucide-icons)             | ISC     |

All four icons use a 24×24 SVG viewBox. LinkedIn / X / GitHub are filled
paths (single `<path>` element). Globe is stroke-based (circle + 2 paths,
2 px stroke, round caps/joins) per Lucide's house style.
