<#
.SYNOPSIS
  Regenerates the installer's two WixUI bitmaps from the source artwork.

.DESCRIPTION
  dialog.bmp and banner.bmp are committed because WiX needs them at build time
  and CI has no image tooling. This script is how they were made, so they can be
  remade rather than hand-edited.

  WixUIDialogBmp is 493x312, but WixUI_Minimal covers all but the leftmost ~165px
  of it with the licence control. A wide composition scaled into that would show
  only its left edge, so the visible strip is cropped around the shield, which is
  the centrepiece of the artwork. The hidden remainder is filled with the
  artwork's own background colour, so nothing seams if a dialog ever shows more.

  WixUIBannerBmp is 493x58 and WixUI draws the dialog title over its left side in
  dark text, so the background stays light and only the icon goes on the right,
  which is where the WiX default kept its disc glyph.

  Both are written as 24-bit BMP. WixUI Bitmap controls do not accept PNG.

  Requires ImageMagick 7 (magick). ASCII only, per the repo's PowerShell rule.

.EXAMPLE
  .\RunAsHelper.Installer\New-InstallerArt.ps1
#>
[CmdletBinding()]
param(
    [string]$Magick = 'magick'
)
$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repo         = Split-Path $installerDir -Parent
$art          = Join-Path $repo 'RunAsHelper.Shared\security-image.png'
$icon         = Join-Path $repo 'power.png'

foreach ($f in @($art, $icon)) {
    if (-not (Test-Path $f)) { throw "Source image not found: $f" }
}

# Background of the source artwork, used to fill the part of the dialog bitmap
# that WixUI never shows.
$background = 'srgb(16,1,32)'

$strip = Join-Path $env:TEMP 'runashelper-dialog-strip.png'
$flame = Join-Path $env:TEMP 'runashelper-banner-icon.png'

# Dialog bitmap. The shield sits around x=384 of the 769-wide source, so take a
# portrait slice centred there and fit it to the strip WixUI actually shows.
& $Magick $art -crop '230x459+269+0' +repage -resize '165x312^' `
    -gravity center -extent '165x312' $strip
if ($LASTEXITCODE -ne 0) { throw "Dialog crop failed (exit $LASTEXITCODE)." }

& $Magick -size '493x312' "xc:$background" $strip -geometry +0+0 -composite `
    -alpha remove -alpha off "BMP3:$(Join-Path $installerDir 'dialog.bmp')"
if ($LASTEXITCODE -ne 0) { throw "Dialog compose failed (exit $LASTEXITCODE)." }

# Banner bitmap: light background, product icon on the right.
& $Magick $icon -resize '42x42' -background white -alpha remove -alpha off $flame
if ($LASTEXITCODE -ne 0) { throw "Icon resize failed (exit $LASTEXITCODE)." }

& $Magick -size '493x58' 'xc:white' $flame -gravity east -geometry +14+0 -composite `
    -alpha remove -alpha off "BMP3:$(Join-Path $installerDir 'banner.bmp')"
if ($LASTEXITCODE -ne 0) { throw "Banner compose failed (exit $LASTEXITCODE)." }

Remove-Item $strip, $flame -ErrorAction SilentlyContinue

foreach ($name in @('dialog.bmp', 'banner.bmp')) {
    $path = Join-Path $installerDir $name
    Write-Host "$name : $(& $Magick identify -format '%wx%h %z-bit' $path)"
}