param([string]$ProjectRoot='G:/UnityProjects/Project213-Testing',
      [string]$EditorRoot='D:/Unity/6000.3.23f1/Editor')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(Get-Process -Name Unity -ErrorAction SilentlyContinue) { throw 'Close Unity before restoring package contents.' }
$projectPath=(Resolve-Path -LiteralPath $ProjectRoot).Path
$cachePath=(Resolve-Path -LiteralPath (Join-Path $projectPath 'Library/PackageCache')).Path
$builtinRoot=(Resolve-Path -LiteralPath (Join-Path $EditorRoot 'Data/Resources/PackageManager/BuiltInPackages')).Path
function AssertOrdinaryTree([string]$path) {
    $root=Get-Item -LiteralPath $path -Force
    if($root.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked directory: $path" }
    # PowerShell does not traverse directory links without FollowSymlink.
    foreach($item in Get-ChildItem -LiteralPath $path -Recurse -Force) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked entry: $($item.FullName)" }
    }
}
AssertOrdinaryTree $cachePath
$manifestPath=Join-Path $projectPath 'Packages/manifest.json'
$lockPath=Join-Path $projectPath 'Packages/packages-lock.json'
$manifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
$lockHash=(Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash
$packageLock=Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$plan=@(foreach($directory in Get-ChildItem -LiteralPath $cachePath -Directory) {
    if(Test-Path -LiteralPath (Join-Path $directory.FullName 'package.json')) { continue }
    if(@(Get-ChildItem -LiteralPath $directory.FullName -Force).Count -ne 0) { throw "Not an empty package: $($directory.FullName)" }
    $packageName=$directory.Name.Split('@')[0]
    $locked=$packageLock.dependencies.$packageName
    if(-not $locked) { throw "Package missing from lock: $packageName" }
    $sourcePath=$null
    if($locked.source -eq 'builtin') {
        $sourcePath=Join-Path $builtinRoot $packageName
        AssertOrdinaryTree $sourcePath
        $sourceManifest=Get-Content -LiteralPath (Join-Path $sourcePath 'package.json') -Raw | ConvertFrom-Json
        if($sourceManifest.name -ne $packageName -or $sourceManifest.version -ne $locked.version) { throw "Builtin version mismatch: $packageName" }
    } elseif($locked.source -ne 'registry') { throw "Unsupported source: $packageName" }
    [pscustomobject]@{Name=$packageName;Version=$locked.version;Source=$locked.source;From=$sourcePath;To=$directory.FullName}
})
if($plan.Count -eq 0) { Write-Output 'No empty package directories to restore.'; exit 0 }
$recoveryParent=Join-Path $projectPath 'Logs/PackageRecovery'
New-Item -ItemType Directory -Path $recoveryParent -Force | Out-Null
AssertOrdinaryTree $recoveryParent
$recoveryPath=Join-Path $recoveryParent ('recovery-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $recoveryPath | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $recoveryPath 'manifest.before.json')
Copy-Item -LiteralPath $lockPath -Destination (Join-Path $recoveryPath 'packages-lock.before.json')
Write-Output "Recovery staging: $recoveryPath"
# Fetch and validate every registry source before writing the project cache.
foreach($package in $plan) {
    if($package.Source -ne 'registry') { continue }
    $metadata=Invoke-RestMethod -Uri ('https://packages.unity.com/'+$package.Name) -TimeoutSec 60
    $version=$metadata.versions.($package.Version)
    if($version.name -ne $package.Name -or $version.version -ne $package.Version) { throw "Registry version mismatch: $($package.Name)" }
    $uri=[Uri]$version.dist.tarball
    if($uri.Scheme -ne 'https' -or $uri.Host -ne 'download.packages.unity.com') { throw 'Unexpected package archive host.' }
    $expectedHash=[string]$version.dist.shasum
    if($expectedHash -notmatch '^[a-fA-F0-9]{40}$') { throw 'Missing archive SHA1 from registry.' }
    $archive=Join-Path $recoveryPath ($package.Name+'-'+$package.Version+'.tgz')
    Write-Output "Downloading $($package.Name) $($package.Version)"
    Invoke-WebRequest -Uri $uri -OutFile $archive -TimeoutSec 600
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash -ne $expectedHash) { throw "Archive checksum mismatch: $($package.Name)" }
    $entries=@(& tar.exe -tzf $archive)
    if($LASTEXITCODE -ne 0) { throw 'Cannot list archive.' }
    foreach($entry in $entries) {
        if($entry -notmatch '^package(/|$)' -or $entry -match '(^|/)\.\.(/|$)' -or $entry.Contains('\')) { throw "Unsafe archive path: $entry" }
    }
    $listing=@(& tar.exe -tvzf $archive)
    if($LASTEXITCODE -ne 0) { throw 'Cannot inspect archive entries.' }
    foreach($entry in $listing) { if($entry -notmatch '^[-d]') { throw "Refusing special archive entry: $entry" } }
    $unpack=Join-Path $recoveryPath $package.Name
    New-Item -ItemType Directory -Path $unpack | Out-Null
    & tar.exe -xzf $archive -C $unpack
    if($LASTEXITCODE -ne 0) { throw "Extraction failed: $($package.Name)" }
    $package.From=Join-Path $unpack 'package'
    AssertOrdinaryTree $package.From
    $unpackedManifest=Get-Content -LiteralPath (Join-Path $package.From 'package.json') -Raw | ConvertFrom-Json
    if($unpackedManifest.name -ne $package.Name -or $unpackedManifest.version -ne $package.Version) { throw 'Extracted manifest mismatch.' }
    Write-Output "Verified archive: $($package.Name) $($package.Version)"
}
if(Get-Process -Name Unity -ErrorAction SilentlyContinue) { throw 'Unity was opened; close it before restoring.' }
foreach($package in $plan) {
    $destination=(Resolve-Path -LiteralPath $package.To).Path
    if(-not $destination.StartsWith($cachePath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Target outside package cache.' }
    AssertOrdinaryTree $destination
    if(@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) { throw "Package target changed: $destination" }
    foreach($child in Get-ChildItem -LiteralPath $package.From -Force) {
        Copy-Item -LiteralPath $child.FullName -Destination $destination -Recurse
    }
    $sourceFiles=@(Get-ChildItem -LiteralPath $package.From -Recurse -File -Force)
    $destinationFiles=@(Get-ChildItem -LiteralPath $destination -Recurse -File -Force)
    if($sourceFiles.Count -ne $destinationFiles.Count) { throw "File count mismatch: $($package.Name)" }
    foreach($file in $sourceFiles) {
        $relative=[IO.Path]::GetRelativePath($package.From,$file.FullName)
        $restored=Join-Path $destination $relative
        if((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash) { throw "Restored file mismatch: $restored" }
    }
    Write-Output "RESTORED $($package.Name) $($package.Version): $($sourceFiles.Count) files verified"
}
if($manifestHash -ne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -or $lockHash -ne (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash) { throw 'Project dependency configuration changed during restoration.' }
Write-Output "PASS: $($plan.Count) packages restored; manifest and lock unchanged. No deletion or directory links used."
