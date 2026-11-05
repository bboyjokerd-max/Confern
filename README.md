# Confern

**Hold Alt, drag a file, drop it on the format you want.** No main window — a wheel appears next to your cursor and shows what that file can become.

[**⬇ Download for Windows**](https://github.com/bboyjokerd-max/Confern/releases/latest) · [繁體中文](README.zh-Hant.md) · [Full details](docs/README.technical.md) · [Report a bug](https://github.com/bboyjokerd-max/Confern/issues)

![Drag a file onto the wheel to convert it](docs/demo/confern-demo.gif)

<sub>Real interaction, recorded 2026-11-03 ([higher quality mp4](docs/demo/confern-demo.mp4)).</sub>

---

## The wheel has two layers

First layer: what the file can become. Second layer: what to do with it.

<table>
<tr>
<td align="center"><img src="docs/images/wheel-paid.png" width="320"><br><sub><b>1st layer</b> — drop an image, the slices are its target formats</sub></td>
<td align="center"><img src="docs/images/wheel-tools-paid.png" width="320"><br><sub><b>2nd layer</b> — convert, crop, merge, <b>CUTOUT</b></sub></td>
</tr>
<tr>
<td align="center"><img src="docs/images/wheel-video.png" width="320"><br><sub>Drop a video and the slices change with it</sub></td>
<td align="center"><img src="docs/images/wheel-tools-free.png" width="320"><br><sub>2nd layer in the <b>free</b> build — no CUTOUT slice</sub></td>
</tr>
</table>

The two second-layer images are genuinely different files (sha256 `42F6A236…` vs `23750A7D…`): that is what "one tool short" looks like.

<table>
<tr>
<td align="center"><img src="docs/images/wheel-paid-dark.png" width="320"><br><sub>Same wheel over a dark background</sub></td>
<td align="center"><img src="docs/images/settings.png" width="320"><br><sub>Settings — right-click the tray icon</sub></td>
</tr>
</table>

![Crop window](docs/images/crop.png)

<sub>Crop: drag to decide what to keep. Every image is the product's own offscreen render, not a screenshot of a desktop.</sub>

## What it does

- **Images** — PNG, JPG, BMP, TIF, PDF, WebP (transparency kept)
- **Video** — common formats both ways, plus audio extraction
- **Batch** — drag several files at once; results land next to the originals
- **Watch a folder** — drop files in, they convert themselves
- **CUTOUT** — drop a photo, get the subject on a transparent background *(paid edition)*

## Two editions, one program

The free, open-source edition simply does not have the CUTOUT tool — nothing is locked, there is one fewer slice on the wheel.

| | Free | Paid |
|---|---|---|
| Everything except CUTOUT | yes | yes |
| CUTOUT (AI background removal) | — | 10 free uses, then a licence key |
| Cost | free | licence key |

**Requirements:** Windows 10/11 x64, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
**Install:** run the installer — no administrator rights, installs per user. Uninstall asks before removing your settings.

<sub>Licence: [MIT](LICENSE) — use it, modify it, sell it. The paid edition is an honour system: anyone can build a cutout with no limit, and we would rather say so than pretend otherwise.</sub>