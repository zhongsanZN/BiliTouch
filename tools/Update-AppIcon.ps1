param(
    [string]$Source = (Join-Path $PSScriptRoot '..\BiliTouch\Assets\app.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\BiliTouch\Assets\app.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourceImage = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
try {
    foreach($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size,$size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($sourceImage,0,0,$size,$size)
            $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($memory.ToArray())
        }
        finally { $graphics.Dispose(); $bitmap.Dispose(); $memory.Dispose() }
    }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create([IO.Path]::GetFullPath($Destination)))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for($index=0;$index -lt $sizes.Count;$index++) {
            $dimension = if($sizes[$index] -eq 256){0}else{$sizes[$index]}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach($frame in $frames) { $writer.Write($frame) }
    }
    finally { $writer.Dispose() }
}
finally { $sourceImage.Dispose() }
Write-Output "Converted app icon into seven Windows sizes."
