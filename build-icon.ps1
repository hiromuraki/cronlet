# 从 runner.svg 生成 runner.ico（多尺寸 PNG 打包进 ICO）
param(
    [string]$Svg = (Join-Path $PSScriptRoot 'runner.svg'),
    [string]$OutIco = (Join-Path $PSScriptRoot 'runner.ico')
)

$ErrorActionPreference = 'Stop'
$sizes = 16, 24, 32, 48, 64, 128, 256
$tmp = Join-Path $env:TEMP ("runner-icons-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null

try {
    $pngs = @()
    foreach ($s in $sizes) {
        $png = Join-Path $tmp "icon_$s.png"
        & resvg -w $s -h $s $Svg $png | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "resvg failed at ${s}px" }
        $pngs += $png
    }

    # ICO 容器：ICONDIR + ICONDIRENTRY*N + PNG 数据
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type = icon
    $bw.Write([UInt16]$sizes.Count)      # count

    $images = @()
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $bytes = [System.IO.File]::ReadAllBytes($pngs[$i])
        $s = $sizes[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)
        $bw.Write([byte]0); $bw.Write([byte]0)          # colorCount, reserved
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)     # planes, bitCount
        $bw.Write([UInt32]$bytes.Length)
        $bw.Write([UInt32]$offset)
        $offset += $bytes.Length
        $images += , $bytes
    }
    foreach ($b in $images) { $bw.Write($b) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($OutIco, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()

    Write-Host ("icon written: {0} ({1} bytes)" -f $OutIco, (Get-Item $OutIco).Length)
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
