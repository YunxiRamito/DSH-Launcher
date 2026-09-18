param(
    [Parameter(Mandatory = $true)]
    [string]$SourceImage,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Bitmap]::FromFile($SourceImage)
try {
    $minX = $source.Width
    $minY = $source.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            $pixel = $source.GetPixel($x, $y)
            if ($pixel.A -gt 12) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt $minX -or $maxY -lt $minY) {
        throw 'Could not find visible artwork in the source image.'
    }

    $padding = 6
    $minX = [Math]::Max(0, $minX - $padding)
    $minY = [Math]::Max(0, $minY - $padding)
    $maxX = [Math]::Min($source.Width - 1, $maxX + $padding)
    $maxY = [Math]::Min($source.Height - 1, $maxY + $padding)

    $cropWidth = $maxX - $minX + 1
    $cropHeight = $maxY - $minY + 1
    $crop = New-Object System.Drawing.Bitmap($cropWidth, $cropHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        for ($y = 0; $y -lt $cropHeight; $y++) {
            for ($x = 0; $x -lt $cropWidth; $x++) {
                $pixel = $source.GetPixel($minX + $x, $minY + $y)
                $crop.SetPixel(
                    $x,
                    $y,
                    [System.Drawing.Color]::FromArgb(
                        $pixel.A,
                        $pixel.R,
                        $pixel.G,
                        $pixel.B))
            }
        }

        $master = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($master)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

                $margin = 14
                $available = 256 - ($margin * 2)
                $scale = [Math]::Min($available / [double]$cropWidth, $available / [double]$cropHeight)
                $drawWidth = [int][Math]::Round($cropWidth * $scale)
                $drawHeight = [int][Math]::Round($cropHeight * $scale)
                $drawX = [int][Math]::Round((256 - $drawWidth) / 2.0)
                $drawY = [int][Math]::Round((256 - $drawHeight) / 2.0)

                $destination = New-Object System.Drawing.Rectangle($drawX, $drawY, $drawWidth, $drawHeight)
                $graphics.DrawImage($crop, $destination)
            }
            finally {
                $graphics.Dispose()
            }

            $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
            $images = @()
            foreach ($size in $sizes) {
                $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                try {
                    $imageGraphics = [System.Drawing.Graphics]::FromImage($bitmap)
                    try {
                        $imageGraphics.Clear([System.Drawing.Color]::Transparent)
                        $imageGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                        $imageGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                        $imageGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                        $imageGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                        $imageGraphics.DrawImage($master, 0, 0, $size, $size)
                    }
                    finally {
                        $imageGraphics.Dispose()
                    }

                    $memory = New-Object System.IO.MemoryStream
                    try {
                        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                        $images += ,@{
                            Size = $size
                            Data = $memory.ToArray()
                        }
                    }
                    finally {
                        $memory.Dispose()
                    }
                }
                finally {
                    $bitmap.Dispose()
                }
            }

            $outputDirectory = Split-Path -Parent $OutputPath
            if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
                New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
            }

            $stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
            $writer = New-Object System.IO.BinaryWriter($stream)
            try {
                $writer.Write([UInt16]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]$images.Count)

                $offset = 6 + (16 * $images.Count)
                foreach ($image in $images) {
                    $size = [int]$image.Size
                    $data = [byte[]]$image.Data
                    if ($size -ge 256) {
                        $writer.Write([byte]0)
                        $writer.Write([byte]0)
                    }
                    else {
                        $writer.Write([byte]$size)
                        $writer.Write([byte]$size)
                    }

                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([UInt16]1)
                    $writer.Write([UInt16]32)
                    $writer.Write([UInt32]$data.Length)
                    $writer.Write([UInt32]$offset)
                    $offset += $data.Length
                }

                foreach ($image in $images) {
                    $writer.Write([byte[]]$image.Data)
                }
            }
            finally {
                $writer.Dispose()
                $stream.Dispose()
            }
        }
        finally {
            $master.Dispose()
        }
    }
    finally {
        $crop.Dispose()
    }
}
finally {
    $source.Dispose()
}
