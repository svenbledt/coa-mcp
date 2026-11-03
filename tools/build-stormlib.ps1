# Builds StormLib from source into tools/stormlib (gitignored). Only StormLib's own build runs.
# Pinned: a moved tag fails here rather than silently building other code.
$ErrorActionPreference = 'Stop'
$tag = 'v9.40'
$commit = '6bb1882bd00ddbc3729cac5dac0fda81a61e5514'
$root = Join-Path $PSScriptRoot 'stormlib'
$src = Join-Path $root 'src'
$build = Join-Path $root 'build'
$bin = Join-Path $root 'bin'
$cmake = 'C:\Program Files\CMake\bin\cmake.exe'

if (-not (Test-Path $src)) {
    git -c advice.detachedHead=false clone --depth 1 --branch $tag https://github.com/ladislav-zezula/StormLib $src
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
}
$actual = (git -C $src rev-parse HEAD).Trim()
if ($actual -ne $commit) { throw "StormLib $tag is at $actual, expected $commit" }

& $cmake -S $src -B $build -G 'Visual Studio 17 2022' -A x64 -DBUILD_SHARED_LIBS=ON -DSTORM_UNICODE=ON
if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
& $cmake --build $build --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw 'cmake build failed' }

New-Item -ItemType Directory -Force $bin | Out-Null
$dll = Get-ChildItem -Path $build -Recurse -Filter 'StormLib.dll' | Select-Object -First 1
if (-not $dll) { throw 'StormLib.dll was not produced' }
Copy-Item $dll.FullName (Join-Path $bin 'StormLib.dll') -Force
Write-Output "StormLib $tag ($commit) built: $(Join-Path $bin 'StormLib.dll')"
