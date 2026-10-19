# Builds a release zip: dist\OnuradaMapper-v<version>-win-x64.zip
#   OnuradaMapper.exe (self-contained, no .NET install needed), tools\ (the Python pipeline), mod\OnuradaLyrics.dll
# The mod is compiled against your own Beat Saber install (-GameDir), since its game references can't be shipped.
param(
    [string]$GameDir = "$env:USERPROFILE\Desktop\Beat Saber",
    [switch]$SkipMod
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'app\OnuradaMapper\OnuradaMapper.csproj'
$version = (Select-Xml -Path $proj -XPath '//Version').Node.InnerText
$dist = Join-Path $root 'dist\OnuradaMapper'
$zip = Join-Path $root "dist\OnuradaMapper-v$version-win-x64.zip"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force "$dist\tools", "$dist\mod" | Out-Null

if (-not $SkipMod) {
    dotnet build (Join-Path $root 'mod\OnuradaLyrics') -c Release "-p:GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw 'mod build failed' }
}
$dll = Join-Path $root 'mod\OnuradaLyrics\bin\Release\net472\OnuradaLyrics.dll'
if (-not (Test-Path $dll)) { throw "no $dll (build the mod once with -GameDir)" }
Copy-Item $dll "$dist\mod\"

dotnet publish $proj -c Release -r win-x64 --self-contained true -o $dist `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'app publish failed' }

# the tracked pipeline files only (the .gitignore whitelist), never per-song data
$tools = git -C $root ls-files tools
if (-not $tools) { $tools = Get-ChildItem (Join-Path $root 'tools') -Include *.py, requirements.txt -Name | ForEach-Object { "tools/$_" } }
foreach ($t in $tools) { Copy-Item (Join-Path $root $t) "$dist\tools\" }
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $dist

if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $dist -DestinationPath $zip
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
