
<#
.SYNOPSIS    script to build different frameworks with dotnet

#>
param(
    [ValidateSet('net472', 'netcoreapp2.2', 'netcoreapp3.1', 'net5')]
    $targetFramework = 'net5',
    [ValidateSet('all', 'debug', 'release')]
    $configuration = 'all',
    [ValidateSet('win-x64', 'ubuntu.18.04-x64')]
    $runtimeIdentifier = 'win-x64',
    [switch]$publish,
    [string]$projectDir = (resolve-path "$psscriptroot\..\src"),
    [string]$nugetFallbackFolder = "$($env:userprofile)\.dotnet\NuGetFallbackFolder",
    [switch]$clean
)

$ErrorActionPreference = 'continue'

$error.Clear()
$global:tempFiles = [collections.arraylist]::new()
$csproj = "$projectDir\CollectSFData\CollectSFData.csproj"
$dllcsproj = "$projectDir\CollectSFDataDll\CollectSFDataDll.csproj"
$frameworksPattern = "\<TargetFrameworks\>(.+?)\</TargetFrameworks\>"
$ignoreCase = [text.regularExpressions.regexOptions]::IgnoreCase

function main() {

    if ($clean) {
        . "$psscriptroot\clean-build.ps1"
    }

    $csproj = create-tempProject -projectFile $csproj
    $dllcsproj = create-tempProject -projectFile $dllcsproj
    
    write-host "dotnet restore $csproj" -ForegroundColor Green
    dotnet restore $csproj

    if($configuration -ieq 'all'){
        build-configuration 'debug'
        build-configuration 'release'
    }
    else{
        build-configuration $configuration
    }

    if ($global:tempFiles) {
        foreach ($file in $global:tempFiles) {
            write-host "removing temp file $file" -ForegroundColor Cyan
            remove-item $file -Force
        }
    }

    return
}

function build-configuration($configuration){
    write-host "dotnet build $csproj -c $configuration" -ForegroundColor Magenta
    dotnet build $csproj -c $configuration

    if ($publish) {
        write-host "dotnet publish $csproj -f $targetFramework -r $runtimeIdentifier -c $configuration --self-contained $true -p:PublishSingleFile=true -p:PublishedTrimmed=true" -ForegroundColor Magenta
        dotnet publish $csproj -f $targetFramework -r $runtimeIdentifier -c $configuration --self-contained $true -p:PublishSingleFile=true -p:PublishedTrimmed=true
    }

    $nugetFile = "$projectDir\bin\$configuration\*.nupkg"
    $nugetFile = (resolve-path $nugetFile)[-1]
    
    if((test-path $nugetFile)){
        write-host "nuget add $nugetFile -source $nugetFallbackFolder" -ForegroundColor Green
        nuget add $nugetFile -source $nugetFallbackFolder
    }
}
function create-tempProject($projectFile) {
    $projContent = Get-Content -raw $projectFile

    if (!([regex]::IsMatch($projContent, $targetFramework, $ignoreCase))) {
        $currentFrameworks = [regex]::Match($projContent, $frameworksPattern, $ignoreCase).Groups[1].Value
        write-host "current frameworks: $currentFrameworks" -ForegroundColor Green
        write-host "copying and adding target framework to csproj $targetFramework" -ForegroundColor Green
        $projContent = [regex]::Replace($projContent, $currentFrameworks, "$currentFrameworks;$targetFramework", $ignoreCase)
        $tempProject = $projectFile.Replace(".csproj", ".$targetFramework.csproj")
        write-host "saving to $tempProject" -ForegroundColor Green
        $projContent | out-file $tempProject
        [void]$global:tempFiles.add($tempProject)
        return $tempProject
    }

    return $projectFile
}

main