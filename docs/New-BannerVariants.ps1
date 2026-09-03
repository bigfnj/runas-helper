<#
.SYNOPSIS
  Regenerates the README banner's light and dark variants from the source drawing.

.DESCRIPTION
  docs/images/great-responsibility.png is the original: black line art on an
  opaque #EFEFEF background. Dropped into a README as-is it renders as a bright
  card on GitHub's dark theme, so the README uses a <picture> element with a
  prefers-color-scheme source instead, and these two derivatives are what it
  points at.

  Knocking the background out with -transparent leaves grey halos on the
  anti-aliased strokes. So the luminance is used as an alpha mask instead:
  dark pixels become opaque, light pixels transparent, and everything between
  keeps its partial coverage. The mask is then filled with a flat colour, which
  is what lets the same strokes be black in one variant and near-white in the
  other with no re-drawing.

  Requires ImageMagick 7 (magick). ASCII only, per the repo's PowerShell rule.

.EXAMPLE
  .\docs\New-BannerVariants.ps1
#>
[CmdletBinding()]
param(
    [string]$Magick = 'magick'
)
$ErrorActionPreference = 'Stop'

$images = Join-Path $PSScriptRoot 'images'
$source = Join-Path $images 'great-responsibility.png'
if (-not (Test-Path $source)) { throw "Source image not found: $source" }

# GitHub's default text colours, so the strokes match surrounding body text
# rather than fighting it.
$variants = @(
    @{ Name = 'banner-light.png'; Ink = 'srgb(31,35,40)'   },  # GitHub light  #1f2328
    @{ Name = 'banner-dark.png';  Ink = 'srgb(230,237,243)' }   # GitHub dark   #e6edf3
)

$mask = Join-Path $env:TEMP 'runashelper-banner-mask.png'

# Luminance -> alpha. Negate so ink (dark) becomes opaque.
& $Magick $source -colorspace gray -negate -alpha off $mask
if ($LASTEXITCODE -ne 0) { throw "Mask build failed (exit $LASTEXITCODE)." }

$size = & $Magick identify -format '%wx%h' $source

foreach ($v in $variants) {
    $out = Join-Path $images $v.Name
    & $Magick -size $size "xc:$($v.Ink)" $mask -alpha off `
        -compose copy_opacity -composite "PNG32:$out"
    if ($LASTEXITCODE -ne 0) { throw "$($v.Name) compose failed (exit $LASTEXITCODE)." }
}

Remove-Item $mask -ErrorAction SilentlyContinue

foreach ($v in $variants) {
    $path = Join-Path $images $v.Name
    Write-Host "$($v.Name) : $(& $Magick identify -format '%wx%h %[channels]' $path)"
}
