param([string]$AndroidJar,[string]$BuildTools,[string]$JavaHome,[string]$OutputDir,[string]$Keystore)
$ErrorActionPreference='Stop'
$AndroidJar=(Resolve-Path $AndroidJar).Path
$BuildTools=(Resolve-Path $BuildTools).Path
$JavaHome=(Resolve-Path $JavaHome).Path
$Keystore=(Resolve-Path $Keystore).Path
$env:JAVA_HOME=$JavaHome
$env:Path="$JavaHome/bin;$env:Path"
$dst=[IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force "$dst/classes","$dst/dex" | Out-Null
function Tool([string]$executable,[string[]]$arguments) {
    & $executable @arguments
    if ($LASTEXITCODE -ne 0) { throw "$executable returned $LASTEXITCODE" }
}
$source=(Get-ChildItem "$PSScriptRoot/src" -Recurse -Filter *.java).FullName
Tool "$JavaHome/bin/javac.exe" @('-encoding','UTF-8','--release','8','-Xlint:-options','-cp',$AndroidJar,'-d',"$dst/classes",$source)
Tool "$JavaHome/bin/jar.exe" @('cf',"$dst/classes.jar",'-C',"$dst/classes",'.')
Tool "$BuildTools/d8.bat" @('--release','--min-api','26','--lib',$AndroidJar,'--output',"$dst/dex","$dst/classes.jar")
Tool "$BuildTools/aapt2.exe" @('link','-I',$AndroidJar,'--manifest',"$PSScriptRoot/AndroidManifest.xml",'-o',"$dst/unsigned.apk")
Tool "$JavaHome/bin/jar.exe" @('uf',"$dst/unsigned.apk",'-C',"$dst/dex",'classes.dex')
Tool "$BuildTools/zipalign.exe" @('-f','4',"$dst/unsigned.apk","$dst/aligned.apk")
Tool "$BuildTools/apksigner.bat" @('sign','--ks',$Keystore,'--ks-pass','pass:android','--key-pass','pass:android','--out',"$dst/TouchReceiver.apk","$dst/aligned.apk")
