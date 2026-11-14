param(
    [Parameter(Mandatory=$true)][string]$AndroidJar,
    [Parameter(Mandatory=$true)][string]$BuildTools,
    [Parameter(Mandatory=$true)][string]$JavaHome,
    [Parameter(Mandatory=$true)][string]$OutputDir,
    [Parameter(Mandatory=$true)][string]$Keystore
)
$ErrorActionPreference = 'Stop'
$AndroidJar = (Resolve-Path -LiteralPath $AndroidJar).Path
$BuildTools = (Resolve-Path -LiteralPath $BuildTools).Path
$JavaHome = (Resolve-Path -LiteralPath $JavaHome).Path
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$Keystore = [IO.Path]::GetFullPath($Keystore)
$env:JAVA_HOME = $JavaHome
$env:Path = (Join-Path $JavaHome 'bin') + ';' + $env:Path
$build = Join-Path $OutputDir 'build'
$classes = Join-Path $build 'classes'
$dex = Join-Path $build 'dex'
$test = Join-Path $build 'test'
New-Item -ItemType Directory -Force -Path $build,$classes,$dex,$test | Out-Null
function Run-Tool([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with code $LASTEXITCODE" }
}
$resources = Join-Path $build 'resources.zip'
$package = Join-Path $build 'resources.apk'
$unsigned = Join-Path $build 'unsigned.apk'
$aligned = Join-Path $build 'aligned.apk'
$apk = Join-Path $OutputDir 'FivePointAndroid.apk'
$source = @(Get-ChildItem (Join-Path $PSScriptRoot 'src') -Recurse -Filter '*.java' | ForEach-Object FullName)
$testSources = @(Get-ChildItem (Join-Path $PSScriptRoot 'test') -Filter '*.java' | ForEach-Object FullName)
$kernel = $source | Where-Object { [IO.Path]::GetFileName($_) -in @('Config.java','Runner.java') }
Run-Tool (Join-Path $JavaHome 'bin/javac.exe') (@('-encoding','UTF-8','--release','8','-d',$test) + $kernel + $testSources)
Run-Tool (Join-Path $JavaHome 'bin/java.exe') @('-cp', $test, 'lan.codex.fivepoint.RunnerTest')
Run-Tool (Join-Path $BuildTools 'aapt2.exe') @('compile','--dir',(Join-Path $PSScriptRoot 'res'),'-o',$resources)
Run-Tool (Join-Path $BuildTools 'aapt2.exe') @('link','-I',$AndroidJar,'--manifest',(Join-Path $PSScriptRoot 'AndroidManifest.xml'),'-o',$package,$resources)
Run-Tool (Join-Path $JavaHome 'bin/javac.exe') (@('-encoding','UTF-8','--release','8','-classpath',$AndroidJar,'-d',$classes) + $source)
$classJar = Join-Path $build 'classes.jar'
Run-Tool (Join-Path $JavaHome 'bin/jar.exe') @('cf',$classJar,'-C',$classes,'.')
Run-Tool (Join-Path $BuildTools 'd8.bat') @('--release','--min-api','26','--lib',$AndroidJar,'--output',$dex,$classJar)
Copy-Item -LiteralPath $package -Destination $unsigned -Force
Run-Tool (Join-Path $JavaHome 'bin/jar.exe') @('uf',$unsigned,'-C',$dex,'classes.dex')
Run-Tool (Join-Path $BuildTools 'zipalign.exe') @('-f','4',$unsigned,$aligned)
if (!(Test-Path -LiteralPath $Keystore)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Keystore) | Out-Null
    Run-Tool (Join-Path $JavaHome 'bin/keytool.exe') @('-genkeypair','-keystore',$Keystore,
        '-storepass','android','-keypass','android','-alias','androiddebugkey','-keyalg','RSA','-keysize','2048',
        '-validity','10000','-dname','CN=FivePoint Android Local Build')
}
Run-Tool (Join-Path $BuildTools 'apksigner.bat') @('sign','--ks',$Keystore,'--ks-pass','pass:android',
    '--key-pass','pass:android','--ks-key-alias','androiddebugkey','--out',$apk,$aligned)
Run-Tool (Join-Path $BuildTools 'apksigner.bat') @('verify','--verbose','--print-certs',$apk)
Get-Item -LiteralPath $apk
