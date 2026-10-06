# Builds a deterministic QA test arena on the Desktop for the RC checklist
# (docs/qa/rc-qa-checklist.md). Safe: only ever deletes/creates its own folder.
# Run:  powershell -ExecutionPolicy Bypass -File .\qa-arena.ps1
param([string]$Root = "$env:USERPROFILE\Desktop\Quickening-QA")

Add-Type -AssemblyName System.Drawing

if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Force $Root | Out-Null

function New-Photo {
    param([int]$Seed, [string]$Path, [int]$W = 256, [int]$H = 256)
    $rng = New-Object System.Random($Seed)
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $bg = [System.Drawing.Color]::FromArgb(255, $rng.Next(256), $rng.Next(256), $rng.Next(256))
    $g.Clear($bg)
    for ($i = 0; $i -lt 24; $i++) {
        $c = [System.Drawing.Color]::FromArgb(255, $rng.Next(256), $rng.Next(256), $rng.Next(256))
        $b = New-Object System.Drawing.SolidBrush($c)
        $g.FillRectangle($b, $rng.Next($W - 40), $rng.Next($H - 40), 20 + $rng.Next(60), 20 + $rng.Next(60))
        $b.Dispose()
    }
    for ($i = 0; $i -lt 12; $i++) {
        $c = [System.Drawing.Color]::FromArgb(255, $rng.Next(256), $rng.Next(256), $rng.Next(256))
        $p = New-Object System.Drawing.Pen($c, (3 + $rng.Next(5)))
        $g.DrawLine($p, $rng.Next($W), $rng.Next($H), $rng.Next($W), $rng.Next($H))
        $p.Dispose()
    }
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    return $bmp
}

function Save-Resized {
    param([System.Drawing.Bitmap]$Src, [string]$Path, [int]$W, [int]$H)
    $small = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($Src, 0, 0, $W, $H)
    $g.Dispose()
    $small.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose()
}

function Save-Jpeg {
    param([System.Drawing.Bitmap]$Src, [string]$Path, [long]$Quality)
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $params = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, $Quality)
    $Src.Save($Path, $codec, $params)
    $params.Dispose()
}

function New-RandomBytes {
    param([int]$Seed, [int]$Count)
    $rng = New-Object System.Random($Seed)
    $bytes = New-Object byte[] $Count
    $rng.NextBytes($bytes)
    return $bytes
}

# ===== 1. Exact duplicates (and the same-size near-twin trap) =====
$dup = Join-Path $Root 'exact-duplicates'
New-Item -ItemType Directory -Force "$dup\docs", "$dup\backup" | Out-Null
$payload = New-RandomBytes 1 65536
[IO.File]::WriteAllBytes("$dup\docs\report.bin", $payload)
[IO.File]::WriteAllBytes("$dup\backup\report (copy).bin", $payload)
[IO.File]::WriteAllBytes("$dup\backup\report older name.bin", $payload)
$twin = $payload.Clone(); $twin[$twin.Length - 1] = $twin[$twin.Length - 1] -bxor 0xFF
[IO.File]::WriteAllBytes("$dup\docs\NOT-a-dupe-same-size.bin", $twin)   # must NEVER group
'once upon a time in a duplicate folder' | Set-Content "$dup\docs\story.txt"
'once upon a time in a duplicate folder' | Set-Content "$dup\backup\story.txt"

# ===== 2. Similar photos (+ blurry, + solid-colour FP guard) =====
$photos = Join-Path $Root 'photos'
New-Item -ItemType Directory -Force $photos | Out-Null
$hero = New-Photo -Seed 42 -Path "$photos\vacation-original.png"
Save-Resized $hero "$photos\vacation-half-size.png" 128 128            # should group with original
Save-Jpeg $hero "$photos\vacation-low-quality.jpg" 35                   # should group with original
$hero.Dispose()
foreach ($seed in 7, 21, 63) {
    $p = New-Photo -Seed $seed -Path "$photos\distinct-$seed.png"       # must NOT group with anything
    $p.Dispose()
}
# Blurry: a sharp photo squashed to 12px and blown back up = smooth = low sharpness.
$sharp = New-Photo -Seed 99 -Path "$photos\sharp-keeper.png"
$tiny = New-Object System.Drawing.Bitmap($sharp, 12, 12)
$blur = New-Object System.Drawing.Bitmap($tiny, 256, 256)
$blur.Save("$photos\blurry-shot.png", [System.Drawing.Imaging.ImageFormat]::Png)
$blur.Dispose(); $tiny.Dispose(); $sharp.Dispose()
# Two different solid colours: structurally empty - must NOT pair as similar.
foreach ($c in @(@('solid-red', 178, 34, 34), @('solid-blue', 65, 105, 225))) {
    $bmp = New-Object System.Drawing.Bitmap(200, 200)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, $c[1], $c[2], $c[3]))
    $g.Dispose()
    $bmp.Save("$photos\$($c[0]).png", [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# ===== 3. Duplicate folders (+ the hidden-file trap pair) =====
$folders = Join-Path $Root 'folder-copies'
New-Item -ItemType Directory -Force "$folders\album", "$folders\album - Copy", "$folders\trapA", "$folders\trapB" | Out-Null
$f1 = New-RandomBytes 11 4096; $f2 = New-RandomBytes 12 8192
[IO.File]::WriteAllBytes("$folders\album\one.bin", $f1)
[IO.File]::WriteAllBytes("$folders\album\two.bin", $f2)
[IO.File]::WriteAllBytes("$folders\album - Copy\one.bin", $f1)
[IO.File]::WriteAllBytes("$folders\album - Copy\two.bin", $f2)          # album pair: SHOULD be folder dupes
$f3 = New-RandomBytes 13 4096
[IO.File]::WriteAllBytes("$folders\trapA\same.bin", $f3)
[IO.File]::WriteAllBytes("$folders\trapB\same.bin", $f3)
'secret unique notes the scan cannot see' | Set-Content "$folders\trapA\hidden-secret.txt"
(Get-Item "$folders\trapA\hidden-secret.txt").Attributes = 'Hidden'     # trap pair: must NOT be folder dupes
'keeps the root itself from being the reported pair' | Set-Content "$folders\readme.txt"

# ===== 4. Housekeeping (empty folder + zero-byte file) =====
$tidy = Join-Path $Root 'housekeeping'
New-Item -ItemType Directory -Force "$tidy\completely-empty-folder" | Out-Null
New-Item -ItemType File "$tidy\zero-bytes.log" | Out-Null

# ===== 5. Scrapbook scenario (the Ignore feature's reason to exist) =====
$scrap = Join-Path $Root 'scrapbook-scenario'
New-Item -ItemType Directory -Force "$scrap\Photos", "$scrap\Scrapbook\book1\page1" | Out-Null
$wedding = New-Photo -Seed 500 -Path "$scrap\Photos\wedding.png"
$wedding.Dispose()
Copy-Item "$scrap\Photos\wedding.png" "$scrap\Scrapbook\book1\page1\wedding.png"  # deliberate keep-both copy

# ===== 6. Deep audio matching (synthesized tunes, no copyrighted audio) =====
# A tune is a melody that changes pitch every 0.25 s. Same seed = same
# recording, so the quieter copy SHOULD match; a different seed of the same
# length must NOT (same-length different songs are the hard negative).
function New-Tune {
    param([int]$Seed, [string]$Path, [double]$Gain = 1.0, [int]$Seconds = 20)
    $rate = 11025; $n = $Seconds * $rate
    $rng = New-Object System.Random($Seed)
    $freqs = 0..($Seconds * 4) | ForEach-Object { 300 + $rng.NextDouble() * 1500 }
    $buf = New-Object byte[] ($n * 2)
    for ($i = 0; $i -lt $n; $i++) {
        $t = $i / $rate
        $v = [int][Math]::Round([Math]::Sin(2 * [Math]::PI * $freqs[[int][Math]::Floor($t * 4)] * $t) * 9000 * $Gain)
        $buf[2 * $i] = [byte]($v -band 0xFF); $buf[2 * $i + 1] = [byte](($v -shr 8) -band 0xFF)
    }
    $fs = [IO.File]::Create($Path); $w = New-Object IO.BinaryWriter($fs)
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $n * 2)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]($n * 2)); $w.Write($buf); $w.Close()
}
$music = Join-Path $Root 'music'
New-Item -ItemType Directory -Force "$music\Original", "$music\Mastered", "$music\Other" | Out-Null
New-Tune -Seed 7 -Path "$music\Original\Demo Song.wav"
New-Tune -Seed 7 -Path "$music\Mastered\Demo Song (master).wav" -Gain 0.5   # same recording, quieter: SHOULD match
New-Tune -Seed 8 -Path "$music\Other\Different Song.wav"                    # same length, different song: must NOT match

Write-Host ""
Write-Host "QA arena ready: $Root" -ForegroundColor Green
Write-Host "Scan that folder in Quickening and follow docs/qa/rc-qa-checklist.md."
